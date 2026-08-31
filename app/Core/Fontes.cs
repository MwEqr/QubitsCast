using System.Runtime.InteropServices;
using System.Text;

namespace QubitsCast.Core;

/// <summary>
/// O que está sendo transmitido: um monitor inteiro ou uma janela específica.
///
/// Monitor passa pela placa (Desktop Duplication), que é rápido e aguenta 60 quadros.
/// Janela passa por GDI, que é mais pesado e não enxerga jogos em tela cheia exclusiva —
/// em compensação mostra só aquele programa, e o resto da tela fica fora.
/// </summary>
public sealed record Fonte
{
    public required string Rotulo { get; init; }

    /// <summary>Identificador estável para guardar nas preferências.</summary>
    public required string Chave { get; init; }

    public bool EhJanela { get; init; }

    // Monitor
    public int IndiceDxgi { get; init; }
    public int X { get; init; }
    public int Y { get; init; }

    // Janela
    public IntPtr Hwnd { get; init; }
    public string Programa { get; init; } = "";

    public int Largura { get; init; }
    public int Altura { get; init; }

    public static Fonte DeTela(Tela t) => new()
    {
        Rotulo = t.Rotulo,
        Chave = "monitor:" + t.Indice,
        EhJanela = false,
        IndiceDxgi = t.IndiceDxgi,
        X = t.X,
        Y = t.Y,
        Largura = t.Largura,
        Altura = t.Altura,
    };
}

public static class Janelas
{
    private delegate bool ProcJanela(IntPtr hwnd, IntPtr dados);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(ProcJanela proc, IntPtr dados);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(IntPtr hwnd, StringBuilder texto, int max);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(IntPtr hwnd, StringBuilder texto, int max);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out RETANGULO r);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hwnd, uint comando);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int atributo, out int valor, int tamanho);

    [StructLayout(LayoutKind.Sequential)]
    private struct RETANGULO { public int left, top, right, bottom; }

    private const int DWMWA_CLOAKED = 14;
    private const uint GW_OWNER = 4;

    /// <summary>
    /// Janelas que fazem sentido transmitir: visíveis, com título, de tamanho útil,
    /// e que não sejam a própria janela do QubitsCast (transmitir a si mesmo vira
    /// aquele efeito de espelho infinito).
    /// </summary>
    public static List<Fonte> Listar()
    {
        var achadas = new List<Fonte>();
        var meuPid = (uint)Environment.ProcessId;

        try
        {
            EnumWindows((h, _) =>
            {
                try
                {
                    if (!IsWindowVisible(h) || IsIconic(h)) return true;

                    // Janela auxiliar (caixa de diálogo de outra) não interessa.
                    if (GetWindow(h, GW_OWNER) != IntPtr.Zero) return true;

                    var titulo = new StringBuilder(512);
                    if (GetWindowTextW(h, titulo, 512) <= 0) return true;
                    var nome = titulo.ToString().Trim();
                    if (nome.Length == 0) return true;

                    GetWindowThreadProcessId(h, out var pid);
                    if (pid == meuPid) return true;

                    // Aplicativos da loja deixam janelas escondidas para trás; o DWM sabe quais.
                    if (DwmGetWindowAttribute(h, DWMWA_CLOAKED, out int escondida, sizeof(int)) == 0
                        && escondida != 0) return true;

                    var classe = new StringBuilder(256);
                    GetClassNameW(h, classe, 256);
                    var nomeClasse = classe.ToString();
                    if (nomeClasse is "Progman" or "WorkerW" or "Shell_TrayWnd" or
                                      "Windows.UI.Core.CoreWindow") return true;

                    if (!GetWindowRect(h, out var r)) return true;
                    int largura = r.right - r.left, altura = r.bottom - r.top;
                    if (largura < 200 || altura < 150) return true;

                    var programa = "";
                    try { programa = System.Diagnostics.Process.GetProcessById((int)pid).ProcessName; }
                    catch { }

                    achadas.Add(new Fonte
                    {
                        Rotulo = Encurtar(nome, 46) + (programa.Length > 0 ? $"  ({programa})" : ""),
                        Chave = "janela:" + programa + ":" + Encurtar(nome, 30),
                        EhJanela = true,
                        Hwnd = h,
                        Programa = programa,
                        Largura = largura,
                        Altura = altura,
                        X = r.left,
                        Y = r.top,
                    });
                }
                catch { /* uma janela problemática não pode derrubar a lista inteira */ }
                return true;
            }, IntPtr.Zero);
        }
        catch (Exception e) { Registro.Falha("Janelas.Listar", e); }

        return achadas;
    }

    /// <summary>Relê o tamanho atual — a pessoa pode ter redimensionado desde que a lista foi montada.</summary>
    public static Fonte? Atualizar(Fonte fonte)
    {
        if (!fonte.EhJanela) return fonte;
        try
        {
            if (!IsWindowVisible(fonte.Hwnd) || !GetWindowRect(fonte.Hwnd, out var r)) return null;
            int largura = r.right - r.left, altura = r.bottom - r.top;
            if (largura < 16 || altura < 16) return null;
            return fonte with { Largura = largura, Altura = altura, X = r.left, Y = r.top };
        }
        catch { return null; }
    }

    private static string Encurtar(string s, int max)
        => s.Length <= max ? s : s[..(max - 1)].TrimEnd() + "…";
}

public static class Fontes
{
    /// <summary>Monitores primeiro, janelas depois — é a ordem em que a pessoa procura.</summary>
    public static List<Fonte> Listar(List<Tela> telas)
    {
        var lista = telas.Select(Fonte.DeTela).ToList();
        lista.AddRange(Janelas.Listar().OrderBy(j => j.Programa).ThenBy(j => j.Rotulo));
        return lista;
    }
}
