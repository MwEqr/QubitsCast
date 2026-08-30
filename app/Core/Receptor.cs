using System.Diagnostics;
using System.IO;

namespace QubitsCast.Core;

public sealed record EstatisticaRecepcao(int Fps, double Mbps);

/// <summary>
/// Recebe os pacotes de vídeo da sala, decodifica e deixa o último quadro pronto
/// para a tela pegar. A tela lê no ritmo do monitor; aqui só se guarda o mais recente.
/// </summary>
public sealed class Receptor : IDisposable
{
    private readonly object _trava = new();
    private Process? _proc;
    private CancellationTokenSource? _parar;
    private Stream? _entradaFfmpeg;

    private byte[]? _quadroPronto;   // último quadro completo, para a tela
    private byte[]? _quadroEmUso;    // o que a tela pegou por último (volta para reuso)
    private bool _temNovo;

    private bool _viuChave;
    private int _quadrosNoSegundo;
    private long _bytesNoSegundo;
    private long _marcoSegundo;

    public int Largura { get; private set; }
    public int Altura { get; private set; }
    public bool Ativo { get; private set; }

    public event Action<EstatisticaRecepcao>? AoMedir;
    public event Action? AoPrimeiroQuadro;

    private bool _avisouPrimeiro;

    public bool Iniciar(int largura, int altura)
    {
        Parar();

        if (largura < 16 || altura < 16 || largura > 7680 || altura > 4320)
        {
            Registro.Escrever($"tamanho de vídeo recusado: {largura}x{altura}");
            return false;
        }

        largura -= largura % 2;
        altura -= altura % 2;
        Largura = largura;
        Altura = altura;

        _proc = Ffmpeg.Iniciar(Ffmpeg.ArgumentosExibicao(largura, altura),
                               redirecionarSaida: true, redirecionarEntrada: true);
        if (_proc is null) return false;

        _entradaFfmpeg = _proc.StandardInput.BaseStream;
        _viuChave = false;
        _avisouPrimeiro = false;
        _quadroPronto = null;
        _quadroEmUso = null;
        _temNovo = false;
        _marcoSegundo = Stopwatch.GetTimestamp();

        _parar = new CancellationTokenSource();
        var ct = _parar.Token;
        _ = Task.Run(() => LerQuadrosAsync(_proc, largura, altura, ct), ct);
        _ = Task.Run(() => LerErrosAsync(_proc, ct), ct);

        Ativo = true;
        Registro.Escrever($"exibição iniciada em {largura}x{altura}");
        return true;
    }

    public void Parar()
    {
        if (!Ativo && _proc is null) return;
        Ativo = false;
        try { _parar?.Cancel(); } catch { }
        try { _entradaFfmpeg?.Dispose(); } catch { }
        _entradaFfmpeg = null;
        Ffmpeg.MatarSilencioso(_proc);
        _proc = null;
        lock (_trava) { _quadroPronto = null; _temNovo = false; }
        Registro.Escrever("exibição parada");
    }

    /// <summary>Entrega um pacote vindo da sala ao decodificador.</summary>
    public void Alimentar(byte tipo, byte[] dados)
    {
        var saida = _entradaFfmpeg;
        if (saida is null || !Ativo) return;

        // Antes do primeiro quadro-chave, tudo que chega é pedaço de imagem sem começo:
        // mandar isso ao decodificador só produziria borrão verde.
        if (!_viuChave)
        {
            if (tipo == Pacote.VideoChave) _viuChave = true;
            else if (tipo != Pacote.VideoParam) return;
        }

        try
        {
            saida.Write(dados, 0, dados.Length);
            saida.Flush();
            _bytesNoSegundo += dados.Length;
        }
        catch (Exception e)
        {
            Registro.Falha("Receptor.Alimentar", e);
            Ativo = false;
        }
    }

    /// <summary>
    /// Pega o quadro mais recente, se houver um novo desde a última chamada.
    /// O buffer devolvido volta para reuso na chamada seguinte — a tela deve copiá-lo na hora.
    /// </summary>
    public byte[]? PegarQuadro()
    {
        lock (_trava)
        {
            if (!_temNovo || _quadroPronto is null) return null;
            _temNovo = false;
            var q = _quadroPronto;
            _quadroPronto = _quadroEmUso;   // devolve o antigo para o laço de leitura reusar
            _quadroEmUso = q;
            return q;
        }
    }

    private async Task LerQuadrosAsync(Process proc, int largura, int altura, CancellationToken ct)
    {
        int bytesNv12 = Cores.BytesNv12(largura, altura);
        int bytesBgra = largura * altura * 4;
        var nv12 = new byte[bytesNv12];
        var buffer = new byte[bytesBgra];
        try
        {
            var fonte = proc.StandardOutput.BaseStream;
            while (!ct.IsCancellationRequested)
            {
                int lido = 0;
                while (lido < bytesNv12)
                {
                    int n = await fonte.ReadAsync(nv12.AsMemory(lido, bytesNv12 - lido), ct)
                                       .ConfigureAwait(false);
                    if (n <= 0) return;
                    lido += n;
                }

                Cores.Nv12ParaBgra(nv12, buffer, largura, altura);

                lock (_trava)
                {
                    // Troca o cheio pelo vazio; nunca há duas escritas no mesmo array.
                    var vazio = _quadroPronto ?? new byte[bytesBgra];
                    _quadroPronto = buffer;
                    buffer = vazio.Length == bytesBgra ? vazio : new byte[bytesBgra];
                    _temNovo = true;
                }

                _quadrosNoSegundo++;
                Medir();

                if (!_avisouPrimeiro)
                {
                    _avisouPrimeiro = true;
                    AoPrimeiroQuadro?.Invoke();
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { Registro.Falha("Receptor.LerQuadros", e); }
    }

    private async Task LerErrosAsync(Process proc, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var linha = await proc.StandardError.ReadLineAsync(ct).ConfigureAwait(false);
                if (linha is null) break;
                // Enquanto o fluxo não sincroniza, o decodificador reclama de propósito.
                if (linha.Length > 0) Registro.Escrever("ffmpeg(exibição): " + linha);
            }
        }
        catch (OperationCanceledException) { }
        catch { }
    }

    private void Medir()
    {
        var agora = Stopwatch.GetTimestamp();
        var decorrido = (agora - _marcoSegundo) / (double)Stopwatch.Frequency;
        if (decorrido < 1.0) return;

        var fps = (int)Math.Round(_quadrosNoSegundo / decorrido);
        var mbps = _bytesNoSegundo * 8.0 / decorrido / 1_000_000.0;
        _quadrosNoSegundo = 0;
        _bytesNoSegundo = 0;
        _marcoSegundo = agora;
        AoMedir?.Invoke(new EstatisticaRecepcao(fps, mbps));
    }

    public void Dispose() => Parar();
}
