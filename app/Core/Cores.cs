namespace QubitsCast.Core;

/// <summary>
/// Conversão de NV12 (o formato que sai do decodificador) para BGRA (o que a tela desenha).
///
/// Feita aqui, e não pelo ffmpeg, porque assim o que trafega entre os dois processos é
/// 1,5 byte por ponto em vez de 4 — a diferença entre 186 MB/s e 500 MB/s a 1080p60.
/// </summary>
public static class Cores
{
    // Tabelas de BT.601 faixa reduzida, que é o que o codificador entrega.
    // Pré-calculadas para não repetir multiplicação por ponto.
    private static readonly int[] _y = new int[256];
    private static readonly int[] _vParaR = new int[256];
    private static readonly int[] _vParaG = new int[256];
    private static readonly int[] _uParaG = new int[256];
    private static readonly int[] _uParaB = new int[256];
    private static readonly byte[] _limite = new byte[1024];

    /// <summary>
    /// Deslocamento da tabela de corte. Com os extremos das tabelas acima, o resultado do
    /// deslocamento cabe em [-277, 534]; somando 384 tudo cai dentro de [0, 1023], então
    /// nem byte estranho vindo de um quadro corrompido sai do vetor.
    /// </summary>
    private const int Desvio = 384;

    static Cores()
    {
        for (int i = 0; i < 256; i++)
        {
            _y[i] = 298 * (i - 16);
            _vParaR[i] = 409 * (i - 128) + 128;
            _vParaG[i] = -208 * (i - 128);
            _uParaG[i] = -100 * (i - 128) + 128;
            _uParaB[i] = 516 * (i - 128) + 128;
        }
        for (int i = 0; i < 1024; i++)
            _limite[i] = (byte)Math.Clamp(i - Desvio, 0, 255);
    }

    public static int BytesNv12(int largura, int altura) => largura * altura * 3 / 2;

    /// <summary>
    /// Converte um quadro inteiro. As linhas são divididas entre os núcleos porque a
    /// conta é a mesma para todas e não há dependência entre elas.
    /// </summary>
    public static void Nv12ParaBgra(byte[] nv12, byte[] bgra, int largura, int altura)
    {
        int planoCroma = largura * altura;

        // Duas linhas por vez: em NV12 cada par de linhas divide a mesma linha de cor.
        Parallel.For(0, altura / 2, par =>
        {
            int linha = par * 2;
            int posY0 = linha * largura;
            int posY1 = posY0 + largura;
            int posUV = planoCroma + par * largura;
            int saida0 = posY0 * 4;
            int saida1 = posY1 * 4;

            for (int x = 0; x < largura; x += 2)
            {
                int u = nv12[posUV + x];
                int v = nv12[posUV + x + 1];

                int paraR = _vParaR[v];
                int paraG = _vParaG[v] + _uParaG[u];
                int paraB = _uParaB[u];

                // Os quatro pontos que compartilham esta cor.
                Pintar(bgra, saida0, _y[nv12[posY0 + x]], paraR, paraG, paraB);
                Pintar(bgra, saida0 + 4, _y[nv12[posY0 + x + 1]], paraR, paraG, paraB);
                Pintar(bgra, saida1, _y[nv12[posY1 + x]], paraR, paraG, paraB);
                Pintar(bgra, saida1 + 4, _y[nv12[posY1 + x + 1]], paraR, paraG, paraB);

                saida0 += 8;
                saida1 += 8;
            }
        });
    }

    private static void Pintar(byte[] bgra, int em, int y, int paraR, int paraG, int paraB)
    {
        bgra[em] = _limite[((y + paraB) >> 8) + Desvio];
        bgra[em + 1] = _limite[((y + paraG) >> 8) + Desvio];
        bgra[em + 2] = _limite[((y + paraR) >> 8) + Desvio];
        bgra[em + 3] = 255;
    }
}
