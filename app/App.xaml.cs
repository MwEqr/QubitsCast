using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using QubitsCast.Core;

namespace QubitsCast;

public partial class App : Application
{
    private const string NomeMutex = @"Local\QubitsCast.InstanciaUnica";
    private const string NomeCanal = "QubitsCast.Convite";

    private Mutex? _mutex;

    /// <summary>Código de sala vindo do link, quando o app foi aberto pelo navegador.</summary>
    public static string? ConviteInicial { get; private set; }

    /// <summary>Disparado quando chega um convite com o app já aberto.</summary>
    public static event Action<string>? AoReceberConvite;

    protected override void OnStartup(StartupEventArgs e)
    {
        // Modo de verificação: roda o caminho real sem interface e sai com código de saída.
        if (Autoteste.Pedido(e.Args))
        {
            Task.Run(async () =>
            {
                var codigo = await Autoteste.ExecutarAsync(e.Args);
                _ = Dispatcher.BeginInvoke(() => Shutdown(codigo));
            });
            return;
        }

        ConviteInicial = LerCodigoDosArgumentos(e.Args);

        _mutex = new Mutex(true, NomeMutex, out bool souOPrimeiro);
        if (!souOPrimeiro)
        {
            // Já existe uma janela aberta: entrega o convite a ela e sai de fininho.
            EnviarParaInstanciaAberta(ConviteInicial);
            Shutdown();
            return;
        }

        Registro.Escrever("=== QubitsCast iniciando ===");
        Registro.Escrever("argumentos: " + string.Join(' ', e.Args));

        RegistrarProtocolo();
        _ = Task.Run(EscutarConvitesAsync);

        DispatcherUnhandledException += (_, ev) =>
        {
            Registro.Falha("erro não tratado", ev.Exception);
            MessageBox.Show(
                "Aconteceu um erro inesperado.\n\nO registro foi salvo em:\n" + Registro.Arquivo,
                "QubitsCast", MessageBoxButton.OK, MessageBoxImage.Warning);
            ev.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (_, ev) =>
        {
            if (ev.ExceptionObject is Exception ex) Registro.Falha("erro fatal", ex);
        };

        base.OnStartup(e);

        // A janela é criada aqui, e não por StartupUri no App.xaml, porque o StartupUri é
        // processado pelo Run() depois deste método: no modo sem interface, sair mais cedo
        // daqui não impediria a janela de abrir do mesmo jeito.
        var janela = new JanelaPrincipal();
        MainWindow = janela;
        janela.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Registro.Escrever("=== QubitsCast encerrando ===");
        try { _mutex?.ReleaseMutex(); } catch { }
        try { _mutex?.Dispose(); } catch { }
        base.OnExit(e);
    }

    /// <summary>Aceita "qubitscast://entrar/ABC123", "qubitscast://ABC123" ou só o código.</summary>
    public static string? LerCodigoDosArgumentos(string[] args)
    {
        foreach (var bruto in args)
        {
            var a = bruto.Trim().Trim('"');
            if (a.StartsWith(Padroes.EsquemaLink + ":", StringComparison.OrdinalIgnoreCase))
            {
                var resto = a[(Padroes.EsquemaLink.Length + 1)..].TrimStart('/');
                if (resto.StartsWith("entrar/", StringComparison.OrdinalIgnoreCase))
                    resto = resto["entrar/".Length..];
                var codigo = new string(resto.TakeWhile(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
                if (codigo.Length is >= 4 and <= 12) return codigo;
            }
        }
        return null;
    }

    // ------------------------------------------------------------------ instância única

    private static void EnviarParaInstanciaAberta(string? codigo)
    {
        try
        {
            using var cano = new NamedPipeClientStream(".", NomeCanal, PipeDirection.Out);
            cano.Connect(2000);
            var dados = Encoding.UTF8.GetBytes(codigo ?? "acordar");
            cano.Write(dados, 0, dados.Length);
            cano.Flush();
        }
        catch (Exception e) { Registro.Falha("EnviarParaInstanciaAberta", e); }
    }

    private static async Task EscutarConvitesAsync()
    {
        while (true)
        {
            try
            {
                using var cano = new NamedPipeServerStream(NomeCanal, PipeDirection.In, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await cano.WaitForConnectionAsync().ConfigureAwait(false);

                using var leitor = new StreamReader(cano, Encoding.UTF8);
                var texto = (await leitor.ReadToEndAsync().ConfigureAwait(false)).Trim();

                Current?.Dispatcher.BeginInvoke(DispatcherPriority.Normal, () =>
                {
                    var janela = Current.MainWindow;
                    if (janela is not null)
                    {
                        if (janela.WindowState == WindowState.Minimized)
                            janela.WindowState = WindowState.Normal;
                        janela.Activate();
                    }
                    if (texto.Length is >= 4 and <= 12 && texto != "acordar")
                        AoReceberConvite?.Invoke(texto.ToUpperInvariant());
                });
            }
            catch (Exception e)
            {
                Registro.Falha("EscutarConvites", e);
                await Task.Delay(1000).ConfigureAwait(false);
            }
        }
    }

    // ------------------------------------------------------------------ link qubitscast://

    /// <summary>
    /// Registra o esquema do link só para o usuário atual (HKCU), sem pedir administrador.
    /// Reescreve sempre porque o caminho do executável muda quando o app é reinstalado.
    /// </summary>
    private static void RegistrarProtocolo()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe) || !File.Exists(exe)) return;

            using var raiz = Registry.CurrentUser.CreateSubKey(
                $@"Software\Classes\{Padroes.EsquemaLink}", writable: true);
            if (raiz is null) return;

            raiz.SetValue("", "URL:QubitsCast");
            raiz.SetValue("URL Protocol", "");

            using (var icone = raiz.CreateSubKey("DefaultIcon"))
                icone?.SetValue("", $"\"{exe}\",0");

            using var comando = raiz.CreateSubKey(@"shell\open\command");
            comando?.SetValue("", $"\"{exe}\" \"%1\"");

            Registro.Escrever($"link {Padroes.EsquemaLink}:// apontando para {exe}");
        }
        catch (Exception e) { Registro.Falha("RegistrarProtocolo", e); }
    }
}
