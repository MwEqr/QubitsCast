using System.Diagnostics;
using System.Net.Http;

namespace QubitsCast.Core;

/// <summary>Velocidade da internet, em megabits por segundo.</summary>
public sealed record Velocidade(double SubidaMbps, double DescidaMbps)
{
    public bool Vazia => SubidaMbps <= 0;
}

/// <summary>
/// Mede quanto a internet de quem transmite sobe.
///
/// Existe porque escolher a qualidade no escuro é a diferença entre uma imagem lisa e
/// uma imagem travando sem explicação: quem transmite precisa ter a subida (upload) que
/// a qualidade pede, e subida costuma ser muitas vezes menor que a descida.
/// </summary>
public static class Medidor
{
    private static Velocidade? _ultima;

    public static Velocidade? Ultima => _ultima;

    public static async Task<Velocidade?> MedirAsync(string servidor, CancellationToken ct = default)
    {
        try
        {
            var s = servidor.Trim().TrimEnd('/');
            if (!s.Contains("://")) s = "https://" + s;

            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(45) };

            // Subida primeiro: é a que decide a qualidade. Dois megabytes dão uma leitura
            // estável sem prender a pessoa esperando em link lento.
            var amostra = new byte[2 * 1024 * 1024];
            Random.Shared.NextBytes(amostra);   // dado repetido comprimiria e mentiria

            var relogio = Stopwatch.StartNew();
            using (var conteudo = new ByteArrayContent(amostra))
            {
                conteudo.Headers.ContentType = new("application/octet-stream");
                var r = await http.PostAsync(s + "/medir", conteudo, ct).ConfigureAwait(false);
                if (!r.IsSuccessStatusCode) return null;
            }
            var segundosSubida = relogio.Elapsed.TotalSeconds;
            var subida = segundosSubida > 0.05 ? amostra.Length * 8.0 / segundosSubida / 1e6 : 0;

            relogio.Restart();
            var baixado = await http.GetByteArrayAsync(s + "/medir?bytes=2000000", ct)
                                    .ConfigureAwait(false);
            var segundosDescida = relogio.Elapsed.TotalSeconds;
            var descida = segundosDescida > 0.05 ? baixado.Length * 8.0 / segundosDescida / 1e6 : 0;

            var v = new Velocidade(Math.Round(subida, 1), Math.Round(descida, 1));
            Registro.Escrever($"velocidade medida: sobe {v.SubidaMbps} Mb/s, baixa {v.DescidaMbps} Mb/s");
            return _ultima = v;
        }
        catch (Exception e)
        {
            Registro.Falha("Medidor.Medir", e);
            return null;
        }
    }

    /// <summary>
    /// Maior qualidade que cabe na subida medida, deixando folga.
    /// A folga não é capricho: link cheio até a borda faz a fila crescer e a imagem
    /// atrasar cada vez mais, em vez de simplesmente ficar menos nítida.
    /// </summary>
    public static int MelhorQualidade(double subidaMbps)
    {
        var teto = subidaMbps * 0.75;
        int melhor = 0;
        for (int i = 0; i < Padroes.Qualidades.Length; i++)
            if (Padroes.Qualidades[i].Bitrate <= teto) melhor = i;
        return melhor;
    }

    public static bool Cabe(int indiceQualidade, double subidaMbps)
        => Padroes.Qualidades[indiceQualidade].Bitrate <= subidaMbps * 0.75;
}
