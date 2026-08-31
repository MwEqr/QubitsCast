using System.Diagnostics;
using System.IO;

namespace QubitsCast.Core;

/// <summary>Números para mostrar na tela enquanto transmite.</summary>
public sealed record EstatisticaEnvio(int Fps, double Mbps, int Descartados);

/// <summary>
/// Captura o monitor, codifica em H.264 e entrega quadro a quadro.
/// O ffmpeg cospe um fluxo contínuo; aqui ele é recortado em unidades de acesso
/// (um quadro completo) para que cada pacote enviado possa ser decodificado sozinho.
/// </summary>
public sealed class Transmissor : IDisposable
{
    private readonly Sinal _sinal;
    private Process? _proc;
    private CancellationTokenSource? _parar;
    private Task? _laco;

    private byte[]? _sps, _pps;
    private readonly List<byte[]> _naisDoQuadro = [];
    private bool _quadroTemVcl, _quadroTemIdr;

    private int _quadrosNoSegundo, _fpsAtual, _descartados;
    private long _bytesNoSegundo;
    private double _mbpsAtual;
    private long _marcoSegundo;

    // Guardados para poder tentar de novo pelo processador se a placa recusar na hora.
    private Fonte? _fonte;
    private int _larguraAlvo, _bitrateAlvo;
    private bool _noProcessador;
    private long _inicioCaptura;

    /// <summary>Última mensagem de falha mostrada, para não empilhar aviso em cima de aviso.</summary>
    private string? _ultimaQueixa;

    public bool Ativo { get; private set; }
    public int Largura { get; private set; }
    public int Altura { get; private set; }
    public int Fps { get; private set; }

    public event Action<EstatisticaEnvio>? AoMedir;

    /// <summary>A transmissão parou de vez.</summary>
    public event Action<string>? AoFalhar;

    /// <summary>Algo mudou mas a transmissão continua — só avisar o usuário.</summary>
    public event Action<string>? AoAvisar;

    public Transmissor(Sinal sinal) => _sinal = sinal;

    private void Reclamar(string mensagem)
    {
        _ultimaQueixa = mensagem;
        AoFalhar?.Invoke(mensagem);
    }

    public bool Iniciar(Fonte fonte, int larguraAlvo, int fps, int bitrateMbps)
        => Iniciar(fonte, larguraAlvo, fps, bitrateMbps, forcarProcessador: false);

    private bool Iniciar(Fonte fonte, int larguraAlvo, int fps, int bitrateMbps, bool forcarProcessador)
    {
        Parar();

        if (!Ffmpeg.Existe)
        {
            AoFalhar?.Invoke("O componente de vídeo não foi encontrado. Reinstale o QubitsCast.");
            return false;
        }

        // A janela pode ter sido redimensionada ou fechada desde que a lista foi montada.
        var atual = Janelas.Atualizar(fonte);
        if (atual is null)
        {
            Reclamar("Essa janela não está mais aberta. Escolha outra.");
            return false;
        }
        fonte = atual;

        _fonte = fonte;
        _larguraAlvo = larguraAlvo;
        _bitrateAlvo = bitrateMbps;
        _noProcessador = forcarProcessador;

        // Não faz sentido esticar a imagem além do tamanho original: só gastaria banda.
        var largura = Math.Min(larguraAlvo, fonte.Largura);
        var altura = (int)Math.Round((double)largura * fonte.Altura / fonte.Largura);
        largura -= largura % 2;
        altura -= altura % 2;
        if (largura < 16 || altura < 16)
        {
            Reclamar("Essa janela é pequena demais para transmitir.");
            return false;
        }

        Largura = largura;
        Altura = altura;
        Fps = fps;

        var args = Ffmpeg.ArgumentosCaptura(fonte, largura, altura, fps, bitrateMbps, forcarProcessador);
        _proc = Ffmpeg.Iniciar(args, redirecionarSaida: true);
        if (_proc is null)
        {
            Reclamar("Não consegui iniciar a captura de tela.");
            return false;
        }

        _sps = _pps = null;
        _naisDoQuadro.Clear();
        _quadroTemVcl = _quadroTemIdr = false;
        _descartados = 0;
        _marcoSegundo = Stopwatch.GetTimestamp();
        _inicioCaptura = _marcoSegundo;

        _parar = new CancellationTokenSource();
        var ct = _parar.Token;
        _laco = Task.Run(() => LerSaidaAsync(_proc, ct), ct);
        _ = Task.Run(() => LerErrosAsync(_proc, ct), ct);

        Ativo = true;
        _sinal.AnunciarTransmissao(true, largura, altura, fps);
        Registro.Escrever($"transmissão iniciada: {largura}x{altura}@{fps} " +
                          $"({bitrateMbps} Mbps, {Ffmpeg.Detectar().Resumo})");
        return true;
    }

    public void Parar()
    {
        if (!Ativo && _proc is null) return;
        Ativo = false;
        try { _parar?.Cancel(); } catch { }
        Ffmpeg.MatarSilencioso(_proc);
        _proc = null;
        try { _sinal.AnunciarTransmissao(false); } catch { }
        Registro.Escrever("transmissão parada");
    }

    // ------------------------------------------------------------------ leitura do ffmpeg

    private async Task LerSaidaAsync(Process proc, CancellationToken ct)
    {
        var buffer = new byte[1 << 20];
        int fim = 0;

        try
        {
            var entrada = proc.StandardOutput.BaseStream;
            while (!ct.IsCancellationRequested)
            {
                if (fim == buffer.Length)
                {
                    // Quadro maior que o buffer: dobra até um teto sensato.
                    if (buffer.Length >= 16 << 20) { fim = 0; continue; }
                    Array.Resize(ref buffer, buffer.Length * 2);
                }

                int lidos = await entrada.ReadAsync(buffer.AsMemory(fim, buffer.Length - fim), ct)
                                         .ConfigureAwait(false);
                if (lidos <= 0) break;
                fim += lidos;

                int consumido = ProcessarBuffer(buffer, fim);
                if (consumido > 0)
                {
                    Buffer.BlockCopy(buffer, consumido, buffer, 0, fim - consumido);
                    fim -= consumido;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { Registro.Falha("Transmissor.LerSaida", e); }

        if (ct.IsCancellationRequested || !Ativo) return;

        var durou = (Stopwatch.GetTimestamp() - _inicioCaptura) / (double)Stopwatch.Frequency;
        Registro.Escrever($"a captura terminou sozinha depois de {durou:0.0}s");

        // Morrer logo no começo com a placa quase sempre é a placa recusando mais uma
        // codificação ao mesmo tempo (jogo gravando, outra transmissão aberta). Nesse caso
        // vale tentar pelo processador antes de desistir e reclamar com o usuário.
        if (durou < 6 && !_noProcessador && _fonte is not null && Ffmpeg.Detectar().EncoderPorPlaca)
        {
            Registro.Escrever("tentando de novo pelo processador");
            Ativo = false;
            _ultimaQueixa = null;

            if (Iniciar(_fonte, _larguraAlvo, Fps, _bitrateAlvo, forcarProcessador: true))
            {
                AoAvisar?.Invoke("A placa de vídeo estava ocupada. Continuei pelo processador.");
                return;
            }

            // A tentativa já explicou o que houve (janela fechada, por exemplo). Repetir uma
            // mensagem genérica por cima só apagaria a informação boa da tela.
            if (_ultimaQueixa is not null) return;
        }

        Ativo = false;
        Reclamar("A captura de tela parou. Tente iniciar de novo.");
    }

    private async Task LerErrosAsync(Process proc, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var linha = await proc.StandardError.ReadLineAsync(ct).ConfigureAwait(false);
                if (linha is null) break;
                if (linha.Length > 0) Registro.Escrever("ffmpeg(captura): " + linha);
            }
        }
        catch (OperationCanceledException) { }
        catch { }
    }

    // ------------------------------------------------------------------ recorte do fluxo H.264

    /// <summary>Devolve quantos bytes do começo do buffer já foram aproveitados.</summary>
    private int ProcessarBuffer(byte[] buf, int fim)
    {
        int pos = 0;
        int inicioNal = -1;

        // Primeiro start code
        int p = AcharStartCode(buf, 0, fim, out int tamCodigo);
        if (p < 0) return 0;
        inicioNal = p + tamCodigo;

        while (true)
        {
            int prox = AcharStartCode(buf, inicioNal, fim, out int tamProx);
            if (prox < 0)
            {
                // O resto pode estar incompleto: guarda a partir do start code atual.
                return Math.Max(0, inicioNal - tamCodigo);
            }

            int fimNal = prox;
            // Um start code de 4 bytes é "00 00 00 01": o zero extra pertence ao delimitador.
            while (fimNal > inicioNal && buf[fimNal - 1] == 0) fimNal--;

            int tamanho = fimNal - inicioNal;
            if (tamanho > 0)
            {
                var nal = new byte[tamanho];
                Buffer.BlockCopy(buf, inicioNal, nal, 0, tamanho);
                TratarNal(nal);
            }

            pos = prox;
            tamCodigo = tamProx;
            inicioNal = prox + tamProx;
        }
    }

    private static int AcharStartCode(byte[] buf, int de, int ate, out int tamanho)
    {
        for (int i = de; i + 2 < ate; i++)
        {
            if (buf[i] != 0 || buf[i + 1] != 0) continue;
            if (buf[i + 2] == 1) { tamanho = 3; return i; }
            if (buf[i + 2] == 0 && i + 3 < ate && buf[i + 3] == 1) { tamanho = 4; return i; }
        }
        tamanho = 0;
        return -1;
    }

    private void TratarNal(byte[] nal)
    {
        int tipo = nal[0] & 0x1F;

        switch (tipo)
        {
            case 7: // SPS
                if (!Iguais(_sps, nal)) { _sps = nal; EnviarParametros(); }
                return;
            case 8: // PPS
                if (!Iguais(_pps, nal)) { _pps = nal; EnviarParametros(); }
                return;
        }

        bool ehVcl = tipo is >= 1 and <= 5;

        // Um quadro pode vir partido em vários pedaços (slices) — o codificador de placa
        // costuma usar oito. Só é quadro NOVO quando o pedaço começa no primeiro macrobloco.
        if (ehVcl && _quadroTemVcl && ComecaQuadro(nal)) DespacharQuadro();

        _naisDoQuadro.Add(nal);
        if (ehVcl)
        {
            _quadroTemVcl = true;
            if (tipo == 5) _quadroTemIdr = true;
        }
    }

    /// <summary>
    /// O primeiro campo do cabeçalho de um slice é first_mb_in_slice, em Exp-Golomb.
    /// Valor zero é escrito como um único bit 1, então o bit mais alto ligado no primeiro
    /// byte depois do cabeçalho NAL significa "este slice abre um quadro".
    /// </summary>
    private static bool ComecaQuadro(byte[] nal)
        => nal.Length > 1 && (nal[1] & 0x80) != 0;

    private static bool Iguais(byte[]? a, byte[] b)
        => a is not null && a.AsSpan().SequenceEqual(b);

    private void EnviarParametros()
    {
        if (_sps is null || _pps is null) return;
        var corpo = MontarAnnexB([_sps, _pps]);
        _sinal.EnviarMidia(Pacote.VideoParam, corpo);
    }

    private void DespacharQuadro()
    {
        if (_naisDoQuadro.Count == 0) { _quadroTemVcl = _quadroTemIdr = false; return; }

        List<byte[]> partes;
        if (_quadroTemIdr && _sps is not null && _pps is not null)
        {
            // Quadro-chave sai sempre com os parâmetros junto: assim quem acabou de
            // entrar consegue decodificar sem depender de ter visto o começo.
            partes = [_sps, _pps, .. _naisDoQuadro];
        }
        else partes = _naisDoQuadro;

        var corpo = MontarAnnexB(partes);
        var tipo = _quadroTemIdr ? Pacote.VideoChave : Pacote.VideoInter;

        if (!_sinal.EnviarMidia(tipo, corpo)) _descartados++;

        _quadrosNoSegundo++;
        _bytesNoSegundo += corpo.Length;
        AtualizarMedidas();

        _naisDoQuadro.Clear();
        _quadroTemVcl = _quadroTemIdr = false;
    }

    private static byte[] MontarAnnexB(IReadOnlyList<byte[]> nais)
    {
        int total = 0;
        foreach (var n in nais) total += n.Length + 4;

        var saida = new byte[total];
        int i = 0;
        foreach (var n in nais)
        {
            saida[i + 2] = 0;
            saida[i + 3] = 1;
            i += 4;
            Buffer.BlockCopy(n, 0, saida, i, n.Length);
            i += n.Length;
        }
        return saida;
    }

    private void AtualizarMedidas()
    {
        var agora = Stopwatch.GetTimestamp();
        var decorrido = (agora - _marcoSegundo) / (double)Stopwatch.Frequency;
        if (decorrido < 1.0) return;

        _fpsAtual = (int)Math.Round(_quadrosNoSegundo / decorrido);
        _mbpsAtual = _bytesNoSegundo * 8.0 / decorrido / 1_000_000.0;
        _quadrosNoSegundo = 0;
        _bytesNoSegundo = 0;
        _marcoSegundo = agora;

        AoMedir?.Invoke(new EstatisticaEnvio(_fpsAtual, _mbpsAtual, _descartados));
    }

    public void Dispose() => Parar();
}
