using System.Diagnostics;
using System.IO;
using System.Threading.Channels;

namespace QubitsCast.Core;

public sealed record EstatisticaRecepcao(int Fps, double Mbps, int Atrasados);

/// <summary>
/// Recebe os pacotes de vídeo da sala, decodifica e deixa o último quadro pronto
/// para a tela pegar. A tela lê no ritmo do monitor; aqui só se guarda o mais recente.
///
/// <para>
/// Entre a rede e o decodificador existe uma fila, e ela é o ponto mais importante desta
/// classe. Escrever direto no decodificador prende a thread que lê a rede quando a máquina
/// não dá conta: o cano enche, a escrita trava, a leitura da rede para junto e o servidor
/// acaba descartando tudo — inclusive quadro-chave, sem o qual a imagem nunca se recupera.
/// Com a fila, quem está atrasado perde quadro solto e continua vendo.
/// </para>
/// </summary>
public sealed class Receptor : IDisposable
{
    /// <summary>Meio segundo a 60 quadros: daqui para cima, quadro comum começa a ser pulado.</summary>
    private const int FilaConfortavel = 30;

    /// <summary>Um segundo e meio: daqui para cima, a fila é esvaziada e espera-se quadro-chave.</summary>
    private const int FilaCheia = 90;

    private readonly object _trava = new();
    private Process? _proc;
    private CancellationTokenSource? _parar;
    private Stream? _entradaFfmpeg;

    private Channel<(byte Tipo, byte[] Dados)>? _fila;
    private int _pendentes;

    private byte[]? _quadroPronto;   // último quadro completo, para a tela
    private byte[]? _quadroEmUso;    // o que a tela pegou por último (volta para reuso)
    private bool _temNovo;

    private bool _viuChave;
    private int _quadrosNoSegundo;
    private int _atrasados;
    private long _bytesNoSegundo;
    private long _marcoSegundo;

    public int Largura { get; private set; }
    public int Altura { get; private set; }
    public bool Ativo { get; private set; }

    /// <summary>Quantos pacotes foram pulados por não dar tempo de mostrar.</summary>
    public int Atrasados => _atrasados;

    public event Action<EstatisticaRecepcao>? AoMedir;
    public event Action? AoPrimeiroQuadro;

    /// <summary>Disparado quando a máquina não está acompanhando e vale reduzir o tamanho.</summary>
    public event Action? AoNaoAcompanhar;

    private bool _avisouPrimeiro;
    private bool _avisouLento;

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
        _avisouLento = false;
        _quadroPronto = null;
        _quadroEmUso = null;
        _temNovo = false;
        _pendentes = 0;
        _atrasados = 0;
        _marcoSegundo = Stopwatch.GetTimestamp();

        _fila = Channel.CreateUnbounded<(byte, byte[])>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });

        _parar = new CancellationTokenSource();
        var ct = _parar.Token;
        _ = Task.Run(() => LacoAlimentacaoAsync(ct), ct);
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
        try { _fila?.Writer.TryComplete(); } catch { }
        _fila = null;
        try { _entradaFfmpeg?.Dispose(); } catch { }
        _entradaFfmpeg = null;
        Ffmpeg.MatarSilencioso(_proc);
        _proc = null;
        lock (_trava) { _quadroPronto = null; _temNovo = false; }
        Registro.Escrever("exibição parada");
    }

    /// <summary>
    /// Entrega um pacote vindo da sala. Nunca bloqueia: no pior caso o pacote é
    /// descartado aqui mesmo, o que é muito melhor do que segurar a thread da rede.
    /// </summary>
    public void Alimentar(byte tipo, byte[] dados)
    {
        var fila = _fila;
        if (fila is null || !Ativo) return;

        // Antes do primeiro quadro-chave, tudo que chega é pedaço de imagem sem começo:
        // mandar isso ao decodificador só produziria borrão verde.
        if (!_viuChave)
        {
            if (tipo == Pacote.VideoChave) _viuChave = true;
            else if (tipo != Pacote.VideoParam) return;
        }

        var pendentes = Volatile.Read(ref _pendentes);

        if (pendentes > FilaCheia)
        {
            // Atraso grande demais para recuperar quadro a quadro: joga fora o que estava
            // esperando e recomeça do próximo quadro-chave. Melhor pular para o agora do
            // que arrastar meio minuto de atraso até o fim da transmissão.
            if (tipo == Pacote.VideoInter) { _atrasados++; return; }
            EsvaziarFila();
            if (tipo != Pacote.VideoChave && tipo != Pacote.VideoParam) return;
        }
        else if (pendentes > FilaConfortavel && tipo == Pacote.VideoInter)
        {
            _atrasados++;
            if (!_avisouLento)
            {
                _avisouLento = true;
                Registro.Escrever("a máquina não está acompanhando a transmissão");
                AoNaoAcompanhar?.Invoke();
            }
            return;
        }

        if (fila.Writer.TryWrite((tipo, dados)))
            Interlocked.Increment(ref _pendentes);
    }

    private void EsvaziarFila()
    {
        var fila = _fila;
        if (fila is null) return;
        while (fila.Reader.TryRead(out _))
        {
            Interlocked.Decrement(ref _pendentes);
            _atrasados++;
        }
        _viuChave = false;   // o que vier antes do próximo quadro-chave não serve
        Registro.Escrever("fila de exibição esvaziada: esperando o próximo quadro-chave");
    }

    private async Task LacoAlimentacaoAsync(CancellationToken ct)
    {
        try
        {
            var fila = _fila;
            if (fila is null) return;

            await foreach (var item in fila.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                Interlocked.Decrement(ref _pendentes);

                var saida = _entradaFfmpeg;
                if (saida is null) return;

                await saida.WriteAsync(item.Dados, ct).ConfigureAwait(false);
                await saida.FlushAsync(ct).ConfigureAwait(false);
                _bytesNoSegundo += item.Dados.Length;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            Registro.Falha("Receptor.LacoAlimentacao", e);
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
        AoMedir?.Invoke(new EstatisticaRecepcao(fps, mbps, _atrasados));
    }

    public void Dispose() => Parar();
}
