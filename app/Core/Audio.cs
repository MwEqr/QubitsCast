using Concentus;
using Concentus.Enums;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace QubitsCast.Core;

/// <summary>
/// Áudio da sala. Duas fontes saem daqui — o som do que está sendo transmitido e a voz
/// do microfone — e tudo que chega dos outros é misturado numa saída só.
///
/// Regra geral deste arquivo: falha de áudio nunca derruba a transmissão de vídeo.
/// Qualquer erro é registrado e a função simplesmente não acontece.
/// </summary>
public static class FormatoAudio
{
    public const int Taxa = 48000;
    public const int AmostrasPorBloco = 960;   // 20 ms
}

/// <summary>Captura uma fonte e entrega blocos Opus prontos para enviar.</summary>
public sealed class CapturaAudio : IDisposable
{
    private readonly bool _doSistema;
    private readonly int _canais;
    private readonly int _bitrate;
    private readonly Action<byte[]> _aoCodificar;

    private IWaveIn? _dispositivo;
    private BufferedWaveProvider? _buffer;
    private ISampleProvider? _cadeia;
    private IOpusEncoder? _opus;
    private CancellationTokenSource? _parar;

    private readonly float[] _lidos;
    private readonly byte[] _saida = new byte[4000];

    public bool Ativa { get; private set; }

    /// <param name="doSistema">true = som que está tocando no PC; false = microfone.</param>
    public CapturaAudio(bool doSistema, Action<byte[]> aoCodificar)
    {
        _doSistema = doSistema;
        _canais = doSistema ? 2 : 1;
        _bitrate = doSistema ? 96_000 : 28_000;
        _aoCodificar = aoCodificar;
        _lidos = new float[FormatoAudio.AmostrasPorBloco * _canais];
    }

    public bool Iniciar()
    {
        Parar();
        try
        {
            _dispositivo = _doSistema ? new WasapiLoopbackCapture() : new WasapiCapture();
            var nativo = _dispositivo.WaveFormat;
            Registro.Escrever($"áudio {(_doSistema ? "do sistema" : "do microfone")}: " +
                              $"{nativo.SampleRate} Hz, {nativo.Channels} canais, {nativo.BitsPerSample} bits");

            _buffer = new BufferedWaveProvider(nativo)
            {
                BufferDuration = TimeSpan.FromSeconds(2),
                DiscardOnBufferOverflow = true,
                ReadFully = false,
            };

            ISampleProvider cadeia = _buffer.ToSampleProvider();

            // Ajusta canais antes da taxa: reamostrar estéreo custa o dobro à toa.
            if (nativo.Channels > _canais && _canais == 1)
                cadeia = new StereoToMonoSampleProvider(cadeia) { LeftVolume = 0.5f, RightVolume = 0.5f };
            else if (nativo.Channels == 1 && _canais == 2)
                cadeia = new MonoToStereoSampleProvider(cadeia);

            if (cadeia.WaveFormat.SampleRate != FormatoAudio.Taxa)
                cadeia = new WdlResamplingSampleProvider(cadeia, FormatoAudio.Taxa);

            _cadeia = cadeia;

            _opus = OpusCodecFactory.CreateEncoder(FormatoAudio.Taxa, _canais,
                _doSistema ? OpusApplication.OPUS_APPLICATION_AUDIO
                           : OpusApplication.OPUS_APPLICATION_VOIP);
            _opus.Bitrate = _bitrate;
            _opus.UseVBR = true;

            _dispositivo.DataAvailable += (_, e) =>
            {
                try { _buffer?.AddSamples(e.Buffer, 0, e.BytesRecorded); } catch { }
            };
            _dispositivo.RecordingStopped += (_, e) =>
            {
                if (e.Exception is not null) Registro.Falha("CapturaAudio.parada", e.Exception);
            };

            _dispositivo.StartRecording();

            _parar = new CancellationTokenSource();
            _ = Task.Run(() => LacoAsync(_parar.Token));

            Ativa = true;
            return true;
        }
        catch (Exception e)
        {
            Registro.Falha($"CapturaAudio.Iniciar({(_doSistema ? "sistema" : "mic")})", e);
            Parar();
            return false;
        }
    }

    private async Task LacoAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var cadeia = _cadeia;
                if (cadeia is null) return;

                int lidos = cadeia.Read(_lidos, 0, _lidos.Length);
                if (lidos < _lidos.Length)
                {
                    // Silêncio ou fonte ainda enchendo: espera um bloco e tenta de novo.
                    await Task.Delay(10, ct).ConfigureAwait(false);
                    continue;
                }

                int bytes = _opus!.Encode(_lidos, FormatoAudio.AmostrasPorBloco, _saida, _saida.Length);
                if (bytes > 0)
                {
                    var pacote = new byte[bytes];
                    Buffer.BlockCopy(_saida, 0, pacote, 0, bytes);
                    _aoCodificar(pacote);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { Registro.Falha("CapturaAudio.Laco", e); }
    }

    public void Parar()
    {
        Ativa = false;
        try { _parar?.Cancel(); } catch { }
        try { _dispositivo?.StopRecording(); } catch { }
        try { _dispositivo?.Dispose(); } catch { }
        _dispositivo = null;
        _cadeia = null;
        _buffer = null;
        try { _opus?.Dispose(); } catch { }
        _opus = null;
    }

    public void Dispose() => Parar();
}

/// <summary>Toca tudo que chega da sala: som da transmissão e vozes, misturados.</summary>
public sealed class ReproducaoAudio : IDisposable
{
    private sealed class Origem
    {
        public required IOpusDecoder Decodificador;
        public required BufferedWaveProvider Buffer;
        public required VolumeSampleProvider Volume;
        public int Canais;
    }

    private readonly object _trava = new();
    private readonly Dictionary<int, Origem> _origens = [];
    private WasapiOut? _saida;
    private MixingSampleProvider? _mistura;
    private float _volume = 1f;

    public bool Ativa { get; private set; }

    public float Volume
    {
        get => _volume;
        set
        {
            _volume = Math.Clamp(value, 0f, 1f);
            lock (_trava)
                foreach (var o in _origens.Values) o.Volume.Volume = _volume;
        }
    }

    public bool Iniciar()
    {
        Parar();
        try
        {
            _mistura = new MixingSampleProvider(
                WaveFormat.CreateIeeeFloatWaveFormat(FormatoAudio.Taxa, 2))
            {
                ReadFully = true,   // sem isso a saída para quando todo mundo fica em silêncio
            };

            _saida = new WasapiOut(AudioClientShareMode.Shared, useEventSync: true, latency: 80);
            _saida.Init(_mistura);
            _saida.Play();

            Ativa = true;
            Registro.Escrever("saída de áudio pronta");
            return true;
        }
        catch (Exception e)
        {
            Registro.Falha("ReproducaoAudio.Iniciar", e);
            Parar();
            return false;
        }
    }

    /// <summary>Entrega um bloco Opus. A chave separa cada pessoa (e o som da tela).</summary>
    public void Alimentar(int chave, bool estereo, byte[] opus)
    {
        if (!Ativa) return;
        try
        {
            Origem origem;
            lock (_trava)
            {
                if (!_origens.TryGetValue(chave, out var existente))
                {
                    int canais = estereo ? 2 : 1;
                    var buffer = new BufferedWaveProvider(
                        WaveFormat.CreateIeeeFloatWaveFormat(FormatoAudio.Taxa, canais))
                    {
                        BufferDuration = TimeSpan.FromMilliseconds(600),
                        DiscardOnBufferOverflow = true,
                        ReadFully = true,
                    };

                    ISampleProvider cadeia = buffer.ToSampleProvider();
                    if (canais == 1) cadeia = new MonoToStereoSampleProvider(cadeia);
                    var volume = new VolumeSampleProvider(cadeia) { Volume = _volume };

                    existente = new Origem
                    {
                        Decodificador = OpusCodecFactory.CreateDecoder(FormatoAudio.Taxa, canais),
                        Buffer = buffer,
                        Volume = volume,
                        Canais = canais,
                    };
                    _origens[chave] = existente;
                    _mistura!.AddMixerInput(volume);
                    Registro.Escrever($"nova origem de áudio: {chave} ({canais} canais)");
                }
                origem = existente;
            }

            // Buffer alto significa que o som está atrasando; melhor pular do que acumular.
            if (origem.Buffer.BufferedDuration.TotalMilliseconds > 450)
                origem.Buffer.ClearBuffer();

            // 120 ms de folga: um pacote Opus nunca passa disso.
            var pcm = new float[FormatoAudio.AmostrasPorBloco * 6 * origem.Canais];
            int amostras = origem.Decodificador.Decode(opus, pcm, pcm.Length / origem.Canais, false);
            if (amostras <= 0) return;

            int totalBytes = amostras * origem.Canais * sizeof(float);
            var bytes = new byte[totalBytes];
            Buffer.BlockCopy(pcm, 0, bytes, 0, totalBytes);
            origem.Buffer.AddSamples(bytes, 0, totalBytes);
        }
        catch (Exception e) { Registro.Falha("ReproducaoAudio.Alimentar", e); }
    }

    public void Remover(int chave)
    {
        lock (_trava)
        {
            if (!_origens.Remove(chave, out var o)) return;
            try { _mistura?.RemoveMixerInput(o.Volume); } catch { }
            try { o.Decodificador.Dispose(); } catch { }
        }
    }

    public void Parar()
    {
        Ativa = false;
        lock (_trava)
        {
            foreach (var o in _origens.Values)
            {
                try { _mistura?.RemoveMixerInput(o.Volume); } catch { }
                try { o.Decodificador.Dispose(); } catch { }
            }
            _origens.Clear();
        }
        try { _saida?.Stop(); } catch { }
        try { _saida?.Dispose(); } catch { }
        _saida = null;
        _mistura = null;
    }

    public void Dispose() => Parar();
}
