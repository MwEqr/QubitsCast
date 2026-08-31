using System.IO;
using System.Text;

namespace QubitsCast.Core;

/// <summary>
/// Roda o caminho real — servidor, captura, codificação, rede, decodificação — sem
/// interface e sem ninguém clicando. Serve para provar que a transmissão funciona
/// de ponta a ponta, não só que o programa compila.
///
///   QubitsCast.exe --autoteste anfitriao   &lt;servidor&gt; &lt;relatorio&gt;
///   QubitsCast.exe --autoteste espectador  &lt;servidor&gt; &lt;codigo&gt; &lt;relatorio&gt;
/// </summary>
public static class Autoteste
{
    public static bool Pedido(string[] args)
        => args.Length >= 2 && args[0].Equals("--autoteste", StringComparison.OrdinalIgnoreCase);

    public static async Task<int> ExecutarAsync(string[] args)
    {
        var papel = args[1].ToLowerInvariant();
        try
        {
            return papel switch
            {
                "anfitriao" => await AnfitriaoAsync(args[2], args[3],
                    args.Length > 4 ? int.Parse(args[4]) : 1280,
                    args.Length > 5 ? int.Parse(args[5]) : 30,
                    args.Length > 6 ? int.Parse(args[6]) : 4,
                    args.Length > 7 ? int.Parse(args[7]) : 26),
                "espectador" => await EspectadorAsync(args[2], args[3], args[4]),
                _ => Erro("papel desconhecido: " + papel),
            };
        }
        catch (Exception e)
        {
            Registro.Falha("Autoteste", e);
            return Erro(e.Message);
        }
    }

    private static int Erro(string msg)
    {
        Registro.Escrever("AUTOTESTE FALHOU: " + msg);
        return 2;
    }

    private static void Anotar(string arquivo, string linha)
    {
        Registro.Escrever("[autoteste] " + linha);
        try { File.AppendAllText(arquivo, linha + Environment.NewLine, Encoding.UTF8); } catch { }
    }

    // ------------------------------------------------------------------ anfitrião

    private static async Task<int> AnfitriaoAsync(string servidor, string relatorio,
                                                   int largura, int fps, int mbps, int segundos)
    {
        File.WriteAllText(relatorio, "", Encoding.UTF8);
        Anotar(relatorio, "papel=anfitriao");
        var cap = Ffmpeg.Detectar();
        Anotar(relatorio, $"codificador={cap.Encoder} ddagrab={cap.UsarDdagrab} ({cap.Resumo})");

        using var sinal = new Sinal(servidor);
        var entrou = new TaskCompletionSource<EstadoSala>();
        sinal.AoEntrar += s => entrou.TrySetResult(s);
        sinal.AoErro += m => Anotar(relatorio, "erro-servidor=" + m);

        if (!await sinal.ConectarAsync()) return Erro("não conectou no servidor");
        Anotar(relatorio, "conectado=sim");

        sinal.CriarSala("teste-anfitriao", "Sala de teste");

        var sala = await ComLimite(entrou.Task, 15_000);
        if (sala is null) return Erro("o servidor não devolveu a sala");
        Anotar(relatorio, "codigo=" + sala.Codigo);
        Anotar(relatorio, "link=" + sala.Link);

        var telas = Telas.CasarComSaidasDaPlaca(Telas.Listar());
        var tela = telas.FirstOrDefault(t => t.Principal) ?? telas[0];
        Anotar(relatorio, $"tela={tela.Largura}x{tela.Altura} (saída {tela.IndiceDxgi} da placa, " +
                          $"{telas.Count} monitor(es))");

        using var transmissor = new Transmissor(sinal);
        int amostras = 0, somaFps = 0;
        double somaMbps = 0;
        transmissor.AoMedir += s =>
        {
            amostras++;
            somaFps += s.Fps;
            somaMbps += s.Mbps;
            Anotar(relatorio, $"medida fps={s.Fps} mbps={s.Mbps:0.00} descartados={s.Descartados}");
        };
        transmissor.AoFalhar += m => Anotar(relatorio, "falha-captura=" + m);

        if (!transmissor.Iniciar(tela, largura, fps, mbps)) return Erro("a captura não iniciou");
        Anotar(relatorio, $"transmitindo={transmissor.Largura}x{transmissor.Altura}@{transmissor.Fps}");

        // Fica no ar tempo suficiente para o espectador entrar, sincronizar e medir.
        await Task.Delay(Math.Clamp(segundos, 5, 600) * 1000);

        transmissor.Parar();
        Anotar(relatorio, $"media-fps={(amostras > 0 ? somaFps / amostras : 0)}");
        Anotar(relatorio, $"media-mbps={(amostras > 0 ? somaMbps / amostras : 0):0.00}");
        Anotar(relatorio, amostras > 0 ? "RESULTADO=ok" : "RESULTADO=nenhum quadro saiu");
        return amostras > 0 ? 0 : 3;
    }

    // ------------------------------------------------------------------ espectador

    private static async Task<int> EspectadorAsync(string servidor, string codigo, string relatorio)
    {
        File.WriteAllText(relatorio, "", Encoding.UTF8);
        Anotar(relatorio, "papel=espectador");
        Anotar(relatorio, "codigo=" + codigo);

        using var sinal = new Sinal(servidor);
        using var receptor = new Receptor();

        var entrou = new TaskCompletionSource<EstadoSala>();
        var primeiroQuadro = new TaskCompletionSource<bool>();

        sinal.AoEntrar += s => entrou.TrySetResult(s);
        sinal.AoErro += m => Anotar(relatorio, "erro-servidor=" + m);
        sinal.AoMidia += (tipo, _, dados) =>
        {
            if (tipo is Pacote.VideoParam or Pacote.VideoChave or Pacote.VideoInter)
                receptor.Alimentar(tipo, dados);
        };

        int medidas = 0, somaFps = 0;
        double somaMbps = 0;
        receptor.AoMedir += s =>
        {
            medidas++;
            somaFps += s.Fps;
            somaMbps += s.Mbps;
            Anotar(relatorio, $"medida fps={s.Fps} mbps={s.Mbps:0.00}");
        };
        receptor.AoPrimeiroQuadro += () => primeiroQuadro.TrySetResult(true);

        if (!await sinal.ConectarAsync()) return Erro("não conectou no servidor");
        sinal.EntrarNaSala(codigo, "teste-espectador");

        var sala = await ComLimite(entrou.Task, 15_000);
        if (sala is null) return Erro("não entrei na sala");
        Anotar(relatorio, $"participantes={sala.Participantes.Count}");

        // Espera o anúncio de quem está transmitindo.
        InfoTransmissao? t = sala.Transmissao;
        for (int i = 0; i < 60 && t is null; i++)
        {
            await Task.Delay(250);
            t = sinal.Sala?.Transmissao;
        }
        if (t is null) return Erro("ninguém está transmitindo nesta sala");
        Anotar(relatorio, $"transmissao={t.Largura}x{t.Altura}@{t.Fps}");

        if (!receptor.Iniciar(t.Largura, t.Altura)) return Erro("o decodificador não abriu");

        if (!await Esperou(primeiroQuadro.Task, 20_000))
            return Erro("nenhum quadro chegou em 20 s");
        Anotar(relatorio, "primeiro-quadro=chegou");

        // Deixa correr para medir taxa em regime.
        await Task.Delay(9_000);

        // Guarda um quadro em disco: é a prova de que a imagem chegou de verdade.
        var quadro = receptor.PegarQuadro();
        for (int i = 0; i < 120 && quadro is null; i++)
        {
            await Task.Delay(25);
            quadro = receptor.PegarQuadro();
        }

        if (quadro is not null)
        {
            var bruto = Path.ChangeExtension(relatorio, ".bgra");
            File.WriteAllBytes(bruto, quadro);
            Anotar(relatorio, $"quadro-salvo={bruto}");
            Anotar(relatorio, $"quadro-tamanho={receptor.Largura}x{receptor.Altura}");

            // Um quadro todo preto significaria captura vazia; conta quantos pixels têm cor.
            long naoPretos = 0;
            for (int i = 0; i + 3 < quadro.Length; i += 4)
                if (quadro[i] > 8 || quadro[i + 1] > 8 || quadro[i + 2] > 8) naoPretos++;
            var total = (long)receptor.Largura * receptor.Altura;
            Anotar(relatorio, $"pixels-com-cor={naoPretos} de {total} " +
                              $"({(total > 0 ? 100.0 * naoPretos / total : 0):0.0}%)");
        }
        else Anotar(relatorio, "quadro-salvo=não consegui pegar um quadro");

        Anotar(relatorio, $"media-fps={(medidas > 0 ? somaFps / medidas : 0)}");
        Anotar(relatorio, $"media-mbps={(medidas > 0 ? somaMbps / medidas : 0):0.00}");

        bool ok = medidas > 0 && quadro is not null;
        Anotar(relatorio, ok ? "RESULTADO=ok" : "RESULTADO=falhou");
        return ok ? 0 : 4;
    }

    private static async Task<T?> ComLimite<T>(Task<T> tarefa, int ms) where T : class
    {
        var vencedor = await Task.WhenAny(tarefa, Task.Delay(ms));
        return vencedor == (Task)tarefa ? await tarefa : null;
    }

    private static async Task<bool> Esperou(Task tarefa, int ms)
        => await Task.WhenAny(tarefa, Task.Delay(ms)) == tarefa;
}
