using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace QubitsCast.Core;

/// <summary>Registro em arquivo. É a única forma de saber o que houve depois que o usuário fecha.</summary>
public static class Registro
{
    private static readonly object _trava = new();
    private static string? _arquivo;

    public static string Arquivo
    {
        get
        {
            if (_arquivo is null)
            {
                var pasta = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "QubitsCast");
                Directory.CreateDirectory(pasta);
                _arquivo = Path.Combine(pasta, "registro.txt");

                // Não deixa o arquivo crescer para sempre.
                try
                {
                    var f = new FileInfo(_arquivo);
                    if (f.Exists && f.Length > 4 * 1024 * 1024)
                        File.Move(_arquivo, _arquivo + ".old", overwrite: true);
                }
                catch { /* registro nunca derruba o app */ }
            }
            return _arquivo;
        }
    }

    public static void Escrever(string mensagem)
    {
        var linha = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {mensagem}";
        Debug.WriteLine(linha);
        try
        {
            lock (_trava) File.AppendAllText(Arquivo, linha + Environment.NewLine, Encoding.UTF8);
        }
        catch { }
    }

    public static void Falha(string onde, Exception e) =>
        Escrever($"ERRO em {onde}: {e.GetType().Name}: {e.Message}\n{e.StackTrace}");
}

/// <summary>Preferências que sobrevivem ao fechamento do app.</summary>
public sealed class Ajustes
{
    public string Apelido { get; set; } = Environment.UserName;
    public string Servidor { get; set; } = Padroes.ServidorPadrao;
    public int Largura { get; set; } = 1920;
    public int Fps { get; set; } = 60;
    public int Bitrate { get; set; } = 8;          // Mbps
    public int MonitorIndice { get; set; }
    public bool CapturarSomDoSistema { get; set; } = true;
    public bool MicrofoneLigado { get; set; }
    public int VolumeSaida { get; set; } = 100;

    [JsonIgnore]
    public static string CaminhoArquivo => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "QubitsCast", "ajustes.json");

    public static Ajustes Carregar()
    {
        try
        {
            if (File.Exists(CaminhoArquivo))
            {
                var texto = File.ReadAllText(CaminhoArquivo, Encoding.UTF8);
                var a = JsonSerializer.Deserialize<Ajustes>(texto);
                if (a is not null) return a.Validado();
            }
        }
        catch (Exception e) { Registro.Falha("Ajustes.Carregar", e); }
        return new Ajustes();
    }

    public void Salvar()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CaminhoArquivo)!);
            File.WriteAllText(CaminhoArquivo,
                JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }),
                Encoding.UTF8);
        }
        catch (Exception e) { Registro.Falha("Ajustes.Salvar", e); }
    }

    /// <summary>Arquivo editado à mão não pode deixar o app em estado impossível.</summary>
    private Ajustes Validado()
    {
        if (string.IsNullOrWhiteSpace(Apelido)) Apelido = Environment.UserName;
        if (Apelido.Length > 20) Apelido = Apelido[..20];
        // Endereço de servidor que já saiu de uso vira o atual: sem isso, quem instalou uma
        // versão antiga fica preso num endereço morto e o app diz "servidor fora do ar".
        if (string.IsNullOrWhiteSpace(Servidor) ||
            Padroes.ServidoresAposentados.Any(
                v => Servidor.TrimEnd('/').Equals(v, StringComparison.OrdinalIgnoreCase)))
            Servidor = Padroes.ServidorPadrao;
        if (Largura is not (960 or 1280 or 1920 or 2560 or 3840)) Largura = 1920;
        if (Fps is not (30 or 60)) Fps = 60;
        Bitrate = Math.Clamp(Bitrate, 1, 60);
        VolumeSaida = Math.Clamp(VolumeSaida, 0, 100);
        MonitorIndice = Math.Max(0, MonitorIndice);
        return this;
    }
}

public static class Padroes
{
    /// <summary>Endereço público do servidor de salas. Trocável no arquivo de ajustes.</summary>
    public const string ServidorPadrao = "https://cast.qubitslab.com.br";

    public const string EsquemaLink = "qubitscast";

    /// <summary>Endereços que já foram o padrão. Ajuste antigo apontando para um deles é migrado.</summary>
    public static readonly string[] ServidoresAposentados =
    {
        "https://cast.encrypthost.com.br",
    };

    /// <summary>
    /// Da mais leve para a mais pesada — a ordem importa: o app escolhe a última que
    /// couber na internet de quem transmite.
    /// </summary>
    public static readonly (string Rotulo, int Largura, int Fps, int Bitrate)[] Qualidades =
    {
        ("540p · 30 fps",   960, 30,  1),
        ("720p · 30 fps",  1280, 30,  2),
        ("1080p · 30 fps", 1920, 30,  5),
        ("1080p · 60 fps", 1920, 60,  8),
        ("1440p · 60 fps", 2560, 60, 14),
        ("4K · 60 fps",    3840, 60, 25),
    };
}

/// <summary>
/// Um monitor que pode ser transmitido. X e Y são a posição na área de trabalho.
/// <para>
/// <see cref="IndiceDxgi"/> é o número que a placa usa para esta tela, que NÃO é
/// necessariamente o mesmo que o Windows usa: com dois monitores de tamanhos diferentes,
/// trocar os dois faz o app capturar uma tela e redimensionar com a medida da outra.
/// Ele é descoberto perguntando à placa, não deduzido.
/// </para>
/// </summary>
public sealed record Tela(int Indice, int IndiceDxgi, int X, int Y, int Largura, int Altura, bool Principal)
{
    public string Rotulo => $"Monitor {Indice + 1} · {Largura}×{Altura}" + (Principal ? " (principal)" : "");
}

public static class Telas
{
    private delegate bool ProcMonitor(IntPtr hMonitor, IntPtr hdc, IntPtr rect, IntPtr dados);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr rect, ProcMonitor proc, IntPtr dados);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfoW(IntPtr hMonitor, ref INFO_MONITOR info);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct INFO_MONITOR
    {
        public int cbSize;
        public RETANGULO rcMonitor;
        public RETANGULO rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RETANGULO { public int left, top, right, bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MODO_VIDEO
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public ushort dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public uint dmFields;
        public int dmPositionX, dmPositionY;          // union POINTL com dmOrientation
        public uint dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public ushort dmLogPixels;
        public uint dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public uint dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2;
        public uint dmPanningWidth, dmPanningHeight;
    }

    private const int ENUM_CONFIGURACAO_ATUAL = -1;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettingsW(string? nomeDispositivo, int modo, ref MODO_VIDEO dm);

    /// <summary>
    /// Lista os monitores em pixels reais.
    /// A resolução vem do modo de vídeo (EnumDisplaySettings), e não do retângulo do monitor,
    /// porque o retângulo já vem dividido pela escala do Windows quando o processo não é
    /// DPI-aware: numa tela 1920x1080 a 120% ele responde 1600x900, e a transmissão sairia
    /// menor e borrada. O modo de vídeo é imune a isso.
    /// </summary>
    public static List<Tela> Listar()
    {
        var telas = new List<Tela>();
        try
        {
            var encontrados = new List<(RETANGULO r, bool principal, string dispositivo)>();
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (h, _, _, _) =>
            {
                var info = new INFO_MONITOR { cbSize = Marshal.SizeOf<INFO_MONITOR>() };
                if (GetMonitorInfoW(h, ref info))
                    encontrados.Add((info.rcMonitor, (info.dwFlags & 1) != 0, info.szDevice));
                return true;
            }, IntPtr.Zero);

            // Da esquerda para a direita, de cima para baixo — mesma ordem que o usuário vê
            // no painel de vídeo do Windows.
            encontrados.Sort((a, b) =>
                a.r.left != b.r.left ? a.r.left.CompareTo(b.r.left) : a.r.top.CompareTo(b.r.top));

            for (int i = 0; i < encontrados.Count; i++)
            {
                var (r, principal, dispositivo) = encontrados[i];
                int largura = r.right - r.left, altura = r.bottom - r.top;
                int x = r.left, y = r.top;

                var dm = new MODO_VIDEO();
                dm.dmSize = (ushort)Marshal.SizeOf<MODO_VIDEO>();
                if (EnumDisplaySettingsW(dispositivo, ENUM_CONFIGURACAO_ATUAL, ref dm) &&
                    dm.dmPelsWidth > 0 && dm.dmPelsHeight > 0)
                {
                    largura = (int)dm.dmPelsWidth;
                    altura = (int)dm.dmPelsHeight;
                    x = dm.dmPositionX;
                    y = dm.dmPositionY;
                }

                // IndiceDxgi provisório; quem corrige é CasarComSaidasDaPlaca().
                telas.Add(new Tela(i, i, x, y, largura, altura, principal));
                Registro.Escrever($"monitor {i}: {dispositivo} {largura}x{altura} em ({x},{y})" +
                                  (principal ? " principal" : ""));
            }
        }
        catch (Exception e) { Registro.Falha("Telas.Listar", e); }

        if (telas.Count == 0) telas.Add(new Tela(0, 0, 0, 0, 1920, 1080, true));
        return telas;
    }

    /// <summary>
    /// Descobre qual saída da placa corresponde a cada monitor e devolve a lista corrigida.
    /// Chama o ffmpeg uma vez por saída — é lento o bastante para não fazer isso a toda hora,
    /// e é a única forma honesta de saber: a ordem da placa não segue a do Windows.
    /// </summary>
    public static List<Tela> CasarComSaidasDaPlaca(List<Tela> telas)
    {
        try
        {
            var saidas = Ffmpeg.SondarSaidasDaPlaca(telas.Count);
            if (saidas.Count == 0) return telas;

            var sobrando = new List<Tela>(telas);
            var resultado = new List<Tela>();

            foreach (var (indiceDxgi, largura, altura) in saidas)
            {
                // Casa pelo tamanho; com telas iguais, a primeira que sobrar serve.
                var achado = sobrando.FirstOrDefault(t => t.Largura == largura && t.Altura == altura);
                if (achado is null) continue;
                sobrando.Remove(achado);
                resultado.Add(achado with { IndiceDxgi = indiceDxgi });
            }

            // Alguma tela que a placa não listou continua na lista, com o índice que tinha.
            resultado.AddRange(sobrando);
            resultado.Sort((a, b) => a.Indice.CompareTo(b.Indice));

            foreach (var t in resultado)
                Registro.Escrever($"monitor {t.Indice} ({t.Largura}x{t.Altura}) = saída {t.IndiceDxgi} da placa");

            return resultado.Count == telas.Count ? resultado : telas;
        }
        catch (Exception e)
        {
            Registro.Falha("Telas.CasarComSaidasDaPlaca", e);
            return telas;
        }
    }
}
