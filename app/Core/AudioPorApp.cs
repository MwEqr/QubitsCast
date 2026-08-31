using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace QubitsCast.Core;

/// <summary>Um programa que está tocando som agora.</summary>
public sealed record AppComSom(int Pid, string Nome)
{
    public override string ToString() => Nome;
}

/// <summary>
/// Captura o som de UM programa só, em vez do computador inteiro.
///
/// Isso depende de uma função do Windows que só existe da compilação 20348 em diante
/// (Windows 11 e Server 2022) — a documentação da Microsoft é explícita quanto a isso.
/// Em Windows 10 comum, <see cref="Suportado"/> devolve falso e o app mostra a opção
/// explicando o motivo, em vez de falhar na hora de transmitir.
/// </summary>
public static class AudioPorApp
{
    /// <summary>Compilação mínima do Windows para a captura por programa.</summary>
    public const int CompilacaoMinima = 20348;

    public static bool Suportado => Environment.OSVersion.Version.Build >= CompilacaoMinima;

    public static string MotivoIndisponivel =>
        $"Só o som de um programa exige Windows 11 (esta máquina é a compilação " +
        $"{Environment.OSVersion.Version.Build}; precisa de {CompilacaoMinima} ou mais nova).";

    /// <summary>
    /// Programas que têm som ativo agora. Funciona em qualquer Windows — é só leitura
    /// das sessões de áudio, e serve para montar a lista mesmo quando a captura por
    /// programa não está disponível.
    /// </summary>
    public static List<AppComSom> Listar()
    {
        var achados = new List<AppComSom>();
        try
        {
            using var enumerador = new MMDeviceEnumerator();
            using var dispositivo = enumerador.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            var sessoes = dispositivo.AudioSessionManager.Sessions;

            var vistos = new HashSet<int>();
            for (int i = 0; i < sessoes.Count; i++)
            {
                try
                {
                    var s = sessoes[i];
                    var pid = (int)s.GetProcessID;
                    if (pid <= 4 || !vistos.Add(pid)) continue;

                    string nome;
                    try { nome = Process.GetProcessById(pid).ProcessName; }
                    catch { continue; }
                    if (string.IsNullOrWhiteSpace(nome)) continue;
                    if (nome.Equals("QubitsCast", StringComparison.OrdinalIgnoreCase)) continue;

                    achados.Add(new AppComSom(pid, nome));
                }
                catch { /* uma sessão problemática não derruba a lista */ }
            }
        }
        catch (Exception e) { Registro.Falha("AudioPorApp.Listar", e); }

        return achados.OrderBy(a => a.Nome, StringComparer.OrdinalIgnoreCase).ToList();
    }

    // ------------------------------------------------------------------ interop

    private const string CaminhoDispositivoVirtual = "VAD\\Process_Loopback";

    private static readonly Guid IID_IAudioClient = new("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");

    private enum TipoAtivacao { Padrao = 0, ProcessLoopback = 1 }
    private enum ModoLoopback { IncluirArvore = 0, ExcluirArvore = 1 }

    [StructLayout(LayoutKind.Sequential)]
    private struct ParametrosAtivacao
    {
        public int Tipo;
        public int PidAlvo;
        public int Modo;
    }

    [ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IOperacaoAtivacao
    {
        void GetActivateResult(out int resultado,
            [MarshalAs(UnmanagedType.IUnknown)] out object? interfaceAtivada);
    }

    [ComImport, Guid("41D949AB-9862-444A-80F6-C261334DA5EB"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAvisoDeAtivacao
    {
        void ActivateCompleted(IOperacaoAtivacao operacao);
    }

    [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IClienteAudio
    {
        [PreserveSig]
        int Initialize(int modo, int flags, long duracao, long periodo,
                       [In] byte[] formato, [In] IntPtr sessao);
        [PreserveSig] int GetBufferSize(out int quadros);
        [PreserveSig] int GetStreamLatency(out long latencia);
        [PreserveSig] int GetCurrentPadding(out int quadros);
        [PreserveSig] int IsFormatSupported(int modo, [In] byte[] formato, out IntPtr maisProximo);
        [PreserveSig] int GetMixFormat(out IntPtr formato);
        [PreserveSig] int GetDevicePeriod(out long padrao, out long minimo);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(IntPtr evento);
        [PreserveSig] int GetService([In, MarshalAs(UnmanagedType.LPStruct)] Guid id,
                                     [MarshalAs(UnmanagedType.IUnknown)] out object servico);
    }

    [ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IClienteCaptura
    {
        [PreserveSig]
        int GetBuffer(out IntPtr dados, out int quadros, out int flags,
                      out long posicao, out long horario);
        [PreserveSig] int ReleaseBuffer(int quadros);
        [PreserveSig] int GetNextPacketSize(out int quadros);
    }

    [DllImport("Mmdevapi.dll", ExactSpelling = true, PreserveSig = false)]
    private static extern void ActivateAudioInterfaceAsync(
        [MarshalAs(UnmanagedType.LPWStr)] string caminhoDispositivo,
        [MarshalAs(UnmanagedType.LPStruct)] Guid id,
        IntPtr parametros,
        IAvisoDeAtivacao aviso,
        out IOperacaoAtivacao operacao);

    private sealed class Aviso : IAvisoDeAtivacao
    {
        public readonly ManualResetEventSlim Pronto = new(false);
        public IOperacaoAtivacao? Operacao;

        public void ActivateCompleted(IOperacaoAtivacao operacao)
        {
            Operacao = operacao;
            Pronto.Set();
        }
    }

    /// <summary>
    /// Abre o fluxo de captura do programa indicado. Devolve nulo (e registra o motivo)
    /// quando o Windows não tem a função ou a ativação é recusada — quem chama cai para
    /// o som do computador inteiro.
    /// </summary>
    internal static IClienteAudioAberto? Abrir(int pid, WaveFormat formato)
    {
        if (!Suportado)
        {
            Registro.Escrever("captura por programa indisponível: " + MotivoIndisponivel);
            return null;
        }

        IntPtr blocoParametros = IntPtr.Zero, blocoPropvariant = IntPtr.Zero;
        try
        {
            var parametros = new ParametrosAtivacao
            {
                Tipo = (int)TipoAtivacao.ProcessLoopback,
                PidAlvo = pid,
                Modo = (int)ModoLoopback.IncluirArvore,
            };

            int tamanho = Marshal.SizeOf<ParametrosAtivacao>();
            blocoParametros = Marshal.AllocHGlobal(tamanho);
            Marshal.StructureToPtr(parametros, blocoParametros, false);

            // PROPVARIANT do tipo BLOB: vt em 0, cbSize em 8, ponteiro em 16 (x64).
            blocoPropvariant = Marshal.AllocHGlobal(32);
            for (int i = 0; i < 32; i++) Marshal.WriteByte(blocoPropvariant, i, 0);
            Marshal.WriteInt16(blocoPropvariant, 0, 0x0041);            // VT_BLOB
            Marshal.WriteInt32(blocoPropvariant, 8, tamanho);
            Marshal.WriteIntPtr(blocoPropvariant, 16, blocoParametros);

            var aviso = new Aviso();
            ActivateAudioInterfaceAsync(CaminhoDispositivoVirtual, IID_IAudioClient,
                                        blocoPropvariant, aviso, out _);

            if (!aviso.Pronto.Wait(TimeSpan.FromSeconds(5)) || aviso.Operacao is null)
            {
                Registro.Escrever("captura por programa: o Windows não respondeu a tempo");
                return null;
            }

            aviso.Operacao.GetActivateResult(out int resultado, out var obj);
            if (resultado != 0 || obj is not IClienteAudio cliente)
            {
                Registro.Escrever($"captura por programa recusada pelo Windows (0x{resultado:X8})");
                return null;
            }

            // Para este caminho o Windows exige duração e período zerados, e o formato
            // precisa ser o que vamos ler — não há mistura automática aqui.
            const int compartilhado = 0;
            const int flagsLoopbackComEvento = 0x00020000 | 0x00040000; // LOOPBACK | EVENTCALLBACK

            var evento = CriarEvento();
            var bytesFormato = FormatoEmBytes(formato);

            int hr = cliente.Initialize(compartilhado, flagsLoopbackComEvento, 0, 0,
                                        bytesFormato, IntPtr.Zero);
            if (hr != 0)
            {
                Registro.Escrever($"captura por programa: Initialize falhou (0x{hr:X8})");
                FecharEvento(evento);
                return null;
            }

            hr = cliente.SetEventHandle(evento);
            if (hr != 0)
            {
                Registro.Escrever($"captura por programa: SetEventHandle falhou (0x{hr:X8})");
                FecharEvento(evento);
                return null;
            }

            hr = cliente.GetService(new Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), out var servico);
            if (hr != 0 || servico is not IClienteCaptura captura)
            {
                Registro.Escrever($"captura por programa: GetService falhou (0x{hr:X8})");
                FecharEvento(evento);
                return null;
            }

            cliente.Start();
            Registro.Escrever($"captura por programa aberta para o pid {pid}");
            return new IClienteAudioAberto(cliente, captura, evento, formato);
        }
        catch (Exception e)
        {
            Registro.Falha("AudioPorApp.Abrir", e);
            return null;
        }
        finally
        {
            if (blocoPropvariant != IntPtr.Zero) Marshal.FreeHGlobal(blocoPropvariant);
            if (blocoParametros != IntPtr.Zero) Marshal.FreeHGlobal(blocoParametros);
        }
    }

    private static byte[] FormatoEmBytes(WaveFormat f)
    {
        using var fluxo = new MemoryStream();
        using var escritor = new BinaryWriter(fluxo);
        f.Serialize(escritor);
        return fluxo.ToArray();
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateEventW(IntPtr atributos, bool manual, bool inicial, string? nome);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr h);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr h, uint ms);

    private static IntPtr CriarEvento() => CreateEventW(IntPtr.Zero, false, false, null);
    private static void FecharEvento(IntPtr h) { if (h != IntPtr.Zero) CloseHandle(h); }

    /// <summary>Fluxo aberto de captura por programa, pronto para ser lido em blocos.</summary>
    internal sealed class IClienteAudioAberto : IDisposable
    {
        private readonly IClienteAudio _cliente;
        private readonly IClienteCaptura _captura;
        private readonly IntPtr _evento;
        public WaveFormat Formato { get; }

        internal IClienteAudioAberto(IClienteAudio cliente, IClienteCaptura captura,
                                     IntPtr evento, WaveFormat formato)
        {
            _cliente = cliente;
            _captura = captura;
            _evento = evento;
            Formato = formato;
        }

        /// <summary>Espera e entrega o próximo bloco. Devolve zero bytes quando não há nada.</summary>
        public int Ler(byte[] destino)
        {
            WaitForSingleObject(_evento, 200);

            int total = 0;
            while (_captura.GetNextPacketSize(out int quadros) == 0 && quadros > 0)
            {
                if (_captura.GetBuffer(out var dados, out int lidos, out int flags, out _, out _) != 0) break;
                int bytes = lidos * Formato.BlockAlign;
                if (bytes > 0 && total + bytes <= destino.Length)
                {
                    // Silêncio vem sinalizado sem dados: preencher com zero mantém o ritmo.
                    if ((flags & 0x2) != 0) Array.Clear(destino, total, bytes);
                    else Marshal.Copy(dados, destino, total, bytes);
                    total += bytes;
                }
                _captura.ReleaseBuffer(lidos);
                if (total >= destino.Length) break;
            }
            return total;
        }

        public void Dispose()
        {
            try { _cliente.Stop(); } catch { }
            try { Marshal.ReleaseComObject(_captura); } catch { }
            try { Marshal.ReleaseComObject(_cliente); } catch { }
            FecharEvento(_evento);
        }
    }
}
