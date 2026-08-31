using System.IO;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;

namespace QubitsCast.Core;

public sealed class Participante
{
    public int Id { get; set; }
    public string Apelido { get; set; } = "";
    public bool Transmitindo { get; set; }
    public bool Microfone { get; set; }
}

public sealed class InfoTransmissao
{
    public int Id { get; set; }
    public int Largura { get; set; }
    public int Altura { get; set; }
    public int Fps { get; set; }
    public string Codec { get; set; } = "h264";
}

public sealed class EstadoSala
{
    public string Codigo { get; set; } = "";
    public string Nome { get; set; } = "";
    public string Link { get; set; } = "";
    public int Voce { get; set; }
    public List<Participante> Participantes { get; set; } = [];
    public InfoTransmissao? Transmissao { get; set; }
}

public static class Pacote
{
    public const byte VideoParam = 1;
    public const byte VideoChave = 2;
    public const byte VideoInter = 3;
    public const byte AudioTela = 10;
    public const byte AudioVoz = 11;
}

/// <summary>Conversa com o servidor de salas: entra, sai, e leva os pacotes de mídia.</summary>
public sealed class Sinal : IDisposable
{
    private static readonly JsonSerializerOptions _json = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly Uri _endereco;
    private ClientWebSocket? _ws;
    private CancellationTokenSource? _parar;
    private Channel<byte[]>? _fila;
    private int _pendentes;

    public event Action<EstadoSala>? AoEntrar;
    public event Action<EstadoSala>? AoAtualizar;
    public event Action<string>? AoErro;
    public event Action<string, string>? AoRecado;      // apelido, texto
    public event Action<byte, byte, byte[]>? AoMidia;   // tipo, origem, dados (sem o cabeçalho)
    public event Action? AoCair;

    public bool Conectado => _ws?.State == WebSocketState.Open;
    public EstadoSala? Sala { get; private set; }

    public Sinal(string servidor)
    {
        _endereco = MontarEndereco(servidor);
    }

    private static Uri MontarEndereco(string servidor)
    {
        var s = servidor.Trim().TrimEnd('/');
        if (!s.Contains("://")) s = "https://" + s;
        var b = new UriBuilder(s)
        {
            Scheme = s.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ? "ws" : "wss",
            Path = "/ws",
        };
        // UriBuilder repete a porta padrão; deixar em -1 evita "wss://host:443/ws".
        if ((b.Scheme == "wss" && b.Port == 443) || (b.Scheme == "ws" && b.Port == 80)) b.Port = -1;
        return b.Uri;
    }

    public async Task<bool> ConectarAsync(CancellationToken ct = default)
    {
        try
        {
            _parar = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _ws = new ClientWebSocket();
            _ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);

            Registro.Escrever($"conectando em {_endereco}");
            using var limite = CancellationTokenSource.CreateLinkedTokenSource(_parar.Token);
            limite.CancelAfter(TimeSpan.FromSeconds(15));
            await _ws.ConnectAsync(_endereco, limite.Token).ConfigureAwait(false);
            Registro.Escrever("conectado");

            _fila = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false,
            });

            _ = Task.Run(() => LacoEnvioAsync(_parar.Token));
            _ = Task.Run(() => LacoRecepcaoAsync(_parar.Token));
            return true;
        }
        catch (Exception e)
        {
            Registro.Falha("Sinal.Conectar", e);
            AoErro?.Invoke("Não consegui falar com o servidor. Confira sua internet.");
            return false;
        }
    }

    // ------------------------------------------------------------------ envio

    private async Task LacoEnvioAsync(CancellationToken ct)
    {
        try
        {
            var leitor = _fila!.Reader;
            while (await leitor.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                while (leitor.TryRead(out var item))
                {
                    Interlocked.Decrement(ref _pendentes);
                    if (_ws is null || _ws.State != WebSocketState.Open) return;

                    // O primeiro byte separa texto (0) de binário (1) — só para esta fila.
                    var tipo = item[0] == 0 ? WebSocketMessageType.Text : WebSocketMessageType.Binary;
                    await _ws.SendAsync(new ArraySegment<byte>(item, 1, item.Length - 1),
                                        tipo, true, ct).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            Registro.Falha("Sinal.LacoEnvio", e);
            AoCair?.Invoke();
        }
    }

    private void Enfileirar(byte marcador, ReadOnlySpan<byte> dados)
    {
        var fila = _fila;
        if (fila is null) return;
        var buf = new byte[dados.Length + 1];
        buf[0] = marcador;
        dados.CopyTo(buf.AsSpan(1));
        if (fila.Writer.TryWrite(buf)) Interlocked.Increment(ref _pendentes);
    }

    public void EnviarJson(object obj)
        => Enfileirar(0, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(obj, _json)));

    /// <summary>
    /// Manda mídia. Quando a rede não dá conta, quadro normal é descartado e
    /// quadro-chave passa — é o que evita a imagem virar borrão e nunca se recuperar.
    /// </summary>
    public bool EnviarMidia(byte tipo, ReadOnlySpan<byte> corpo)
    {
        var fila = _fila;
        if (fila is null) return false;

        var pendentes = Volatile.Read(ref _pendentes);
        if (pendentes > 150) return false;                      // fila entupida: descarta tudo
        // Cerca de meio segundo de vídeo a 60 quadros. Cortar antes disso derruba a
        // fluidez à toa numa oscilação curta da rede; muito depois, a imagem começa a
        // chegar atrasada e o atraso nunca mais volta.
        if (pendentes > 32 && tipo == Pacote.VideoInter) return false;

        var buf = new byte[corpo.Length + 3];
        buf[0] = 1;      // marcador de binário para a fila
        buf[1] = tipo;   // tipo do pacote
        buf[2] = 0;      // origem — o servidor reescreve
        corpo.CopyTo(buf.AsSpan(3));
        if (!fila.Writer.TryWrite(buf)) return false;
        Interlocked.Increment(ref _pendentes);
        return true;
    }

    // ------------------------------------------------------------------ recepção

    private async Task LacoRecepcaoAsync(CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        var acumulado = new MemoryStream();
        try
        {
            while (_ws is not null && _ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                acumulado.SetLength(0);
                WebSocketReceiveResult r;
                do
                {
                    r = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct).ConfigureAwait(false);
                    if (r.MessageType == WebSocketMessageType.Close)
                    {
                        AoCair?.Invoke();
                        return;
                    }
                    acumulado.Write(buffer, 0, r.Count);
                } while (!r.EndOfMessage);

                if (r.MessageType == WebSocketMessageType.Text)
                    TratarTexto(Encoding.UTF8.GetString(acumulado.GetBuffer(), 0, (int)acumulado.Length));
                else
                    TratarBinario(acumulado.GetBuffer(), (int)acumulado.Length);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            Registro.Falha("Sinal.LacoRecepcao", e);
            AoCair?.Invoke();
        }
    }

    private void TratarTexto(string texto)
    {
        try
        {
            using var doc = JsonDocument.Parse(texto);
            var raiz = doc.RootElement;
            var t = raiz.TryGetProperty("t", out var pt) ? pt.GetString() : null;

            switch (t)
            {
                case "entrou":
                    Sala = JsonSerializer.Deserialize<EstadoSala>(texto, _json);
                    if (Sala is not null) AoEntrar?.Invoke(Sala);
                    break;

                case "sala":
                    var anterior = Sala;
                    var nova = JsonSerializer.Deserialize<EstadoSala>(texto, _json);
                    if (nova is not null)
                    {
                        nova.Voce = anterior?.Voce ?? nova.Voce;   // "voce" só vem no "entrou"
                        Sala = nova;
                        AoAtualizar?.Invoke(nova);
                    }
                    break;

                case "recado":
                    AoRecado?.Invoke(
                        raiz.TryGetProperty("apelido", out var a) ? a.GetString() ?? "" : "",
                        raiz.TryGetProperty("texto", out var x) ? x.GetString() ?? "" : "");
                    break;

                case "erro":
                    AoErro?.Invoke(raiz.TryGetProperty("msg", out var m) ? m.GetString() ?? "Erro" : "Erro");
                    break;
            }
        }
        catch (Exception e) { Registro.Falha("Sinal.TratarTexto", e); }
    }

    private void TratarBinario(byte[] dados, int tamanho)
    {
        if (tamanho < 2) return;
        var corpo = new byte[tamanho - 2];
        Buffer.BlockCopy(dados, 2, corpo, 0, corpo.Length);
        AoMidia?.Invoke(dados[0], dados[1], corpo);
    }

    // ------------------------------------------------------------------ comandos

    public void CriarSala(string apelido, string nome)
        => EnviarJson(new { t = "criar", apelido, nome });

    public void EntrarNaSala(string codigo, string apelido)
        => EnviarJson(new { t = "entrar", codigo, apelido });

    public void AnunciarTransmissao(bool ativa, int largura = 0, int altura = 0, int fps = 0)
        => EnviarJson(new { t = "transmitir", ativa, largura, altura, fps, codec = "h264" });

    public void AnunciarMicrofone(bool ativo) => EnviarJson(new { t = "microfone", ativo });

    public void MandarRecado(string texto) => EnviarJson(new { t = "recado", texto });

    /// <summary>Pergunta ao servidor onde fica o WebSocket. Cai na derivação se não responder.</summary>
    public static async Task<bool> ServidorRespondeAsync(string servidor)
    {
        try
        {
            var s = servidor.Trim().TrimEnd('/');
            if (!s.Contains("://")) s = "https://" + s;
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            var r = await http.GetAsync(s + "/saude").ConfigureAwait(false);
            return r.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    public void Dispose()
    {
        try { _parar?.Cancel(); } catch { }
        try
        {
            if (_ws?.State == WebSocketState.Open)
                _ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "tchau", CancellationToken.None)
                   .Wait(1500);
        }
        catch { }
        try { _ws?.Dispose(); } catch { }
        try { _parar?.Dispose(); } catch { }
        _ws = null;
        _fila = null;
    }
}
