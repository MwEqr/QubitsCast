using System.Diagnostics;
using System.IO;
using System.Text;

namespace QubitsCast.Core;

/// <summary>O que esta máquina consegue fazer de verdade — descoberto testando, não supondo.</summary>
/// <param name="Encoder">Codificador H.264 que funciona aqui.</param>
/// <param name="UsarDdagrab">
/// true = captura pela placa (Desktop Duplication, rápida).
/// false = captura por GDI, que funciona em qualquer lugar mas gasta processador.
/// </param>
public sealed record Capacidades(string Encoder, bool UsarDdagrab)
{
    public bool EncoderPorPlaca => Encoder != "libx264";

    public string Resumo =>
        (Encoder switch
        {
            "h264_nvenc" => "placa NVIDIA",
            "h264_qsv" => "gráficos Intel",
            "h264_amf" => "placa AMD",
            _ => "processador",
        }) + (UsarDdagrab ? "" : " · captura simples");
}

/// <summary>
/// Tudo que envolve o ffmpeg: onde ele está, o que a máquina aguenta,
/// e as linhas de comando de captura e de exibição.
/// </summary>
public static class Ffmpeg
{
    private static string? _caminho;
    private static Capacidades? _capacidades;

    /// <summary>Ordem de preferência: placa dedicada, depois gráficos integrados, depois processador.</summary>
    private static readonly string[] _encoders = ["h264_nvenc", "h264_qsv", "h264_amf"];

    /// <summary>Caminho do ffmpeg.exe. Procura ao lado do app antes de tentar o PATH.</summary>
    public static string Caminho
    {
        get
        {
            if (_caminho is not null) return _caminho;

            var baseApp = AppContext.BaseDirectory;
            var candidatos = new[]
            {
                Path.Combine(baseApp, "ffmpeg", "ffmpeg.exe"),
                Path.Combine(baseApp, "ffmpeg.exe"),
            };
            foreach (var c in candidatos)
                if (File.Exists(c)) return _caminho = Path.GetFullPath(c);

            var doPath = Environment.GetEnvironmentVariable("PATH")?.Split(';') ?? [];
            foreach (var dir in doPath)
            {
                try
                {
                    var c = Path.Combine(dir.Trim(), "ffmpeg.exe");
                    if (File.Exists(c)) return _caminho = c;
                }
                catch { }
            }

            return _caminho = Path.Combine(baseApp, "ffmpeg", "ffmpeg.exe");
        }
    }

    public static bool Existe => File.Exists(Caminho);

    private static string ArquivoCache => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "QubitsCast", "capacidades.txt");

    /// <summary>
    /// Descobre codificador e forma de captura codificando de verdade e conferindo se saiu byte.
    /// Ler a lista de recursos do ffmpeg não prova nada: o NVENC aparece listado e falha na
    /// hora de abrir quando o driver é mais antigo que o exigido; o QSV aparece em máquina
    /// sem gráficos Intel ligados. O resultado fica guardado para não repetir o teste.
    /// </summary>
    public static Capacidades Detectar(bool forcarNovoTeste = false)
    {
        if (_capacidades is not null && !forcarNovoTeste) return _capacidades;

        var assinatura = AssinaturaFfmpeg();
        if (!forcarNovoTeste && LerCache(assinatura) is { } guardado)
        {
            Registro.Escrever($"capacidades do cache: {guardado.Encoder}, ddagrab={guardado.UsarDdagrab}");
            return _capacidades = guardado;
        }

        string encoder = "libx264";
        foreach (var e in _encoders)
            if (TestarEncoder(e)) { encoder = e; break; }

        // A captura é testada com o codificador por processador: separar as duas coisas
        // evita que uma falha de codificação seja confundida com falha de captura.
        bool ddagrab = TestarCaptura(usarDdagrab: true);
        if (!ddagrab)
        {
            Registro.Escrever("Desktop Duplication não funcionou aqui; tentando captura por GDI");
            if (!TestarCaptura(usarDdagrab: false))
                Registro.Escrever("AVISO: nenhuma forma de captura passou no teste");
        }

        var cap = new Capacidades(encoder, ddagrab);
        Registro.Escrever($"capacidades: codificador={cap.Encoder} ddagrab={cap.UsarDdagrab}");

        // Só guarda o resultado bom. Cair no processador ou na captura simples pode ser azar
        // do momento — a placa aceita um número pequeno de codificações e de capturas ao
        // mesmo tempo, e um teste feito enquanto outra transmissão roda falha sem que a
        // máquina seja incapaz. Guardar isso deixaria o app no modo lento para sempre.
        if (cap.EncoderPorPlaca && cap.UsarDdagrab) GravarCache(assinatura, cap);
        else Registro.Escrever("resultado abaixo do ideal — vou testar de novo na próxima abertura");

        return _capacidades = cap;
    }

    private static Capacidades? LerCache(string assinatura)
    {
        try
        {
            if (!File.Exists(ArquivoCache)) return null;
            var partes = File.ReadAllText(ArquivoCache).Split('|');
            if (partes.Length != 3 || partes[0] != assinatura) return null;
            if (string.IsNullOrWhiteSpace(partes[1])) return null;
            return new Capacidades(partes[1], partes[2] == "1");
        }
        catch (Exception e) { Registro.Falha("Ffmpeg.LerCache", e); return null; }
    }

    private static void GravarCache(string assinatura, Capacidades cap)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ArquivoCache)!);
            File.WriteAllText(ArquivoCache,
                $"{assinatura}|{cap.Encoder}|{(cap.UsarDdagrab ? "1" : "0")}");
        }
        catch { }
    }

    private static string AssinaturaFfmpeg()
    {
        try
        {
            var f = new FileInfo(Caminho);
            // A versão do driver entra na conta: trocar de driver muda o que o NVENC aceita.
            return f.Exists ? $"{f.Length}-{f.LastWriteTimeUtc.Ticks}-{Environment.OSVersion.Version}" : "sem-ffmpeg";
        }
        catch { return "sem-ffmpeg"; }
    }

    private static bool TestarEncoder(string enc)
    {
        var args = $"-hide_banner -loglevel error -nostdin -f lavfi " +
                   $"-i testsrc=size=1280x720:rate=30 -t 0.4 -c:v {enc} -b:v 3M -f h264 ";
        return RodarTeste($"encoder {enc}", args, minimoBytes: 256);
    }

    private static bool TestarCaptura(bool usarDdagrab)
    {
        var entrada = usarDdagrab
            ? "-init_hw_device d3d11va -filter_complex " +
              "\"ddagrab=output_idx=0:framerate=15,hwdownload,format=bgra,scale=320:-2,format=nv12\""
            : "-f gdigrab -framerate 15 -video_size 320x240 -i desktop -vf format=nv12";

        var args = $"-hide_banner -loglevel error -nostdin {entrada} " +
                   "-t 0.6 -c:v libx264 -preset ultrafast -b:v 1M -f h264 ";
        return RodarTeste($"captura {(usarDdagrab ? "ddagrab" : "gdigrab")}", args, minimoBytes: 128);
    }

    private static bool RodarTeste(string nome, string argumentosSemSaida, int minimoBytes)
    {
        // Nome novo a cada teste: sobra de um teste anterior ainda presa pelo ffmpeg fazia
        // o File.Delete lançar, e a exceção era lida como "esta máquina não consegue" —
        // foi assim que a captura pela placa apareceu como indisponível sem motivo.
        var saida = Path.Combine(Path.GetTempPath(),
            $"qcast-teste-{nome.Replace(' ', '-')}-{Guid.NewGuid():N}.h264");
        try
        {
            using var p = Iniciar(argumentosSemSaida + $"-y \"{saida}\"", redirecionarSaida: false);
            if (p is null) return false;
            if (!p.WaitForExit(25_000)) { MatarSilencioso(p); return false; }

            // Prova de conteúdo: o ffmpeg pode sair com 0 e não ter escrito nada.
            long bytes = File.Exists(saida) ? new FileInfo(saida).Length : 0;
            var ok = bytes >= minimoBytes;
            Registro.Escrever($"teste {nome}: saída={p.ExitCode} bytes={bytes} -> {(ok ? "OK" : "não")}");
            return ok;
        }
        catch (Exception e)
        {
            Registro.Falha($"RodarTeste({nome})", e);
            return false;
        }
        finally
        {
            try { if (File.Exists(saida)) File.Delete(saida); } catch { }
        }
    }

    /// <summary>
    /// Pergunta à placa o tamanho de cada saída de vídeo, capturando um quadro de cada uma.
    /// A resolução vem do cabeçalho do PNG gravado — conteúdo, não texto de log.
    /// </summary>
    public static List<(int Indice, int Largura, int Altura)> SondarSaidasDaPlaca(int quantasTentar)
    {
        var achadas = new List<(int, int, int)>();
        if (!Detectar().UsarDdagrab) return achadas;   // sem ddagrab, o índice não é usado

        for (int i = 0; i < Math.Max(1, quantasTentar) + 1; i++)
        {
            var png = Path.Combine(Path.GetTempPath(), $"qcast-saida-{i}-{Environment.ProcessId}.png");
            try
            {
                if (File.Exists(png)) File.Delete(png);

                var args = $"-hide_banner -loglevel error -nostdin -init_hw_device d3d11va " +
                           $"-filter_complex \"ddagrab=output_idx={i}:framerate=5,hwdownload,format=bgra\" " +
                           $"-frames:v 1 -f image2 -y \"{png}\"";

                using var p = Iniciar(args, redirecionarSaida: false);
                if (p is null) break;
                if (!p.WaitForExit(15_000)) { MatarSilencioso(p); break; }

                if (!File.Exists(png) || new FileInfo(png).Length < 32) break;

                var tamanho = LerTamanhoPng(png);
                if (tamanho is null) break;

                achadas.Add((i, tamanho.Value.Largura, tamanho.Value.Altura));
                Registro.Escrever($"saída {i} da placa: {tamanho.Value.Largura}x{tamanho.Value.Altura}");
            }
            catch (Exception e)
            {
                Registro.Falha($"SondarSaidasDaPlaca({i})", e);
                break;
            }
            finally
            {
                try { if (File.Exists(png)) File.Delete(png); } catch { }
            }
        }
        return achadas;
    }

    /// <summary>Largura e altura ficam no bloco IHDR, logo depois da assinatura do PNG.</summary>
    private static (int Largura, int Altura)? LerTamanhoPng(string caminho)
    {
        try
        {
            using var f = File.OpenRead(caminho);
            var cabecalho = new byte[24];
            if (f.Read(cabecalho, 0, 24) < 24) return null;
            if (cabecalho[0] != 0x89 || cabecalho[1] != (byte)'P') return null;

            int Ler(int em) => (cabecalho[em] << 24) | (cabecalho[em + 1] << 16) |
                               (cabecalho[em + 2] << 8) | cabecalho[em + 3];
            var l = Ler(16);
            var a = Ler(20);
            return l is > 0 and <= 16384 && a is > 0 and <= 16384 ? (l, a) : null;
        }
        catch { return null; }
    }

    /// <summary>Linha de comando que captura a fonte escolhida e joga H.264 no stdout.</summary>
    public static string ArgumentosCaptura(Fonte fonte, int larguraSaida, int alturaSaida,
                                           int fps, int bitrateMbps, bool forcarProcessador = false)
    {
        var cap = Detectar();
        if (forcarProcessador) cap = cap with { Encoder = "libx264" };
        var precisaEscalar = larguraSaida != fonte.Largura || alturaSaida != fonte.Altura;
        var escala = precisaEscalar ? $",scale={larguraSaida}:{alturaSaida}:flags=fast_bilinear" : "";

        string entrada;
        if (fonte.EhJanela)
        {
            // Por identificador da janela, e não pelo título: título muda o tempo todo
            // (aba do navegador, arquivo aberto no editor) e a captura se perderia.
            entrada = $"-f gdigrab -framerate {fps} -draw_mouse 1 " +
                      $"-i \"hwnd=0x{fonte.Hwnd.ToInt64():x}\" " +
                      $"-vf \"format=bgra{escala},format=nv12\"";
        }
        else if (cap.UsarDdagrab)
        {
            // ddagrab entrega quadros na memória da placa; o download é obrigatório porque
            // este ffmpeg não deriva contexto CUDA a partir do D3D11 (medido: erro -40).
            entrada = "-init_hw_device d3d11va -filter_complex " +
                      $"\"ddagrab=output_idx={fonte.IndiceDxgi}:framerate={fps}," +
                      $"hwdownload,format=bgra{escala},format=nv12\"";
        }
        else
        {
            // GDI não sabe escolher monitor: recorta pela posição dele na área de trabalho.
            entrada = $"-f gdigrab -framerate {fps} -offset_x {fonte.X} -offset_y {fonte.Y} " +
                      $"-video_size {fonte.Largura}x{fonte.Altura} -draw_mouse 1 -i desktop " +
                      $"-vf \"format=bgra{escala},format=nv12\"";
        }

        var b = bitrateMbps;
        var buf = Math.Max(1, b / 2);
        var opcoesEncoder = cap.Encoder switch
        {
            "h264_nvenc" =>
                $"-c:v h264_nvenc -preset p4 -tune ll -rc cbr -b:v {b}M -maxrate {b}M -bufsize {buf}M " +
                $"-profile:v high -bf 0 -g {fps} -no-scenecut 1",
            "h264_qsv" =>
                $"-c:v h264_qsv -preset veryfast -b:v {b}M -maxrate {b}M -bufsize {buf}M " +
                $"-profile:v high -bf 0 -g {fps} -low_delay_brc 1",
            "h264_amf" =>
                $"-c:v h264_amf -usage lowlatency -quality speed -rc cbr -b:v {b}M -maxrate {b}M " +
                $"-bufsize {buf}M -profile:v high -bf 0 -g {fps}",
            _ =>
                // Sem placa que ajude, o preset acompanha o tamanho: acima de 1080p30 só o
                // mais rápido dá conta de acompanhar a tela em tempo real.
                $"-c:v libx264 -preset {(larguraSaida * alturaSaida * fps > 1920 * 1080 * 32 ? "ultrafast" : "veryfast")} " +
                $"-tune zerolatency -b:v {b}M -maxrate {b}M -bufsize {buf}M " +
                $"-profile:v high -bf 0 -g {fps} -threads 0",
        };

        return $"-hide_banner -loglevel error -nostdin {entrada} " +
               $"{opcoesEncoder} -fps_mode cfr -r {fps} -f h264 -";
    }

    /// <summary>
    /// Linha de comando que lê H.264 do stdin e escreve quadros crus no stdout, em NV12.
    /// <para>
    /// NV12 e não BGRA de propósito: BGRA gasta 4 bytes por ponto e a 1080p60 dá quase
    /// 500 MB/s atravessando o cano entre os dois processos — medido, não passa, e a imagem
    /// chega a 16 quadros por segundo. NV12 gasta 1,5 byte por ponto, corta isso para 186 MB/s
    /// e ainda tira do ffmpeg a conversão de cor. Quem converte é <c>Cores.Nv12ParaBgra</c>.
    /// </para>
    /// </summary>
    public static string ArgumentosExibicao(int largura, int altura)
        => "-hide_banner -loglevel error " +
           "-fflags nobuffer+discardcorrupt -flags low_delay -probesize 32 -analyzeduration 0 " +
           "-f h264 -i pipe:0 " +
           $"-vf scale={largura}:{altura}:flags=fast_bilinear -pix_fmt nv12 -f rawvideo pipe:1";

    /// <summary>Sobe um ffmpeg sem janela de console.</summary>
    public static Process? Iniciar(string argumentos, bool redirecionarSaida, bool redirecionarEntrada = false)
    {
        try
        {
            var info = new ProcessStartInfo
            {
                FileName = Caminho,
                Arguments = argumentos,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = redirecionarSaida,
                RedirectStandardInput = redirecionarEntrada,
                RedirectStandardError = true,
                StandardErrorEncoding = Encoding.UTF8,
            };
            Registro.Escrever("ffmpeg " + argumentos);
            return Process.Start(info);
        }
        catch (Exception e)
        {
            Registro.Falha("Ffmpeg.Iniciar", e);
            return null;
        }
    }

    public static void MatarSilencioso(Process? p)
    {
        if (p is null) return;
        try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
        try { p.Dispose(); } catch { }
    }
}
