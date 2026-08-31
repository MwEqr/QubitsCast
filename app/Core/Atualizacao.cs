using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace QubitsCast.Core;

/// <summary>O que o servidor diz sobre a versão mais nova.</summary>
public sealed class VersaoPublicada
{
    public string Versao { get; set; } = "";
    public string Url { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public string Notas { get; set; } = "";
    public string Data { get; set; } = "";
    public long Tamanho { get; set; }
}

/// <summary>
/// Busca, baixa e instala versão nova, do jeito que o usuário espera: ele abre o app,
/// aparece um aviso, clica em atualizar e pronto.
///
/// O arquivo baixado é conferido pelo resumo SHA-256 antes de rodar. Isso não é
/// formalidade: instalar um executável que veio pela rede sem conferir é abrir a porta
/// para qualquer coisa que tenha chegado no lugar dele.
/// </summary>
public static class Atualizacao
{
    public static Version VersaoAtual =>
        Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0, 0);

    public static string VersaoAtualTexto
    {
        get
        {
            var v = VersaoAtual;
            return $"{v.Major}.{v.Minor}.{v.Build}";
        }
    }

    private static string PastaBaixados => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "QubitsCast", "atualizacao");

    /// <summary>Consulta o servidor. Devolve nulo quando já está na versão mais nova.</summary>
    public static async Task<VersaoPublicada?> ProcurarAsync(string servidor,
                                                             CancellationToken ct = default)
    {
        try
        {
            var s = servidor.Trim().TrimEnd('/');
            if (!s.Contains("://")) s = "https://" + s;

            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            var texto = await http.GetStringAsync(s + "/versao", ct).ConfigureAwait(false);

            var publicada = JsonSerializer.Deserialize<VersaoPublicada>(texto,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (publicada is null || string.IsNullOrWhiteSpace(publicada.Versao)) return null;
            if (!Version.TryParse(NormalizarVersao(publicada.Versao), out var nova)) return null;

            var atual = VersaoAtual;
            if (nova <= new Version(atual.Major, atual.Minor, atual.Build))
            {
                Registro.Escrever($"já estou na versão mais nova ({VersaoAtualTexto})");
                return null;
            }

            if (string.IsNullOrWhiteSpace(publicada.Url) ||
                !publicada.Url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                Registro.Escrever("atualização anunciada sem endereço seguro; ignorando");
                return null;
            }

            Registro.Escrever($"versão nova disponível: {publicada.Versao} (tenho {VersaoAtualTexto})");
            return publicada;
        }
        catch (Exception e)
        {
            // Ficar sem internet não é erro que mereça incomodar quem está usando.
            Registro.Escrever("não consegui procurar atualização: " + e.Message);
            return null;
        }
    }

    private static string NormalizarVersao(string v)
    {
        var limpo = new string(v.Where(c => char.IsDigit(c) || c == '.').ToArray());
        var partes = limpo.Split('.', StringSplitOptions.RemoveEmptyEntries);
        return partes.Length switch
        {
            0 => "0.0.0",
            1 => partes[0] + ".0.0",
            2 => partes[0] + "." + partes[1] + ".0",
            _ => string.Join('.', partes.Take(3)),
        };
    }

    /// <summary>
    /// Baixa o instalador e confere o resumo. Devolve o caminho do arquivo, ou nulo se algo
    /// não bateu — e nesse caso o arquivo é apagado, para não ficar lixo executável em disco.
    /// </summary>
    public static async Task<string?> BaixarAsync(VersaoPublicada versao,
                                                  IProgress<double>? andamento = null,
                                                  CancellationToken ct = default)
    {
        var destino = Path.Combine(PastaBaixados, $"QubitsCast-{NormalizarVersao(versao.Versao)}.exe");
        try
        {
            Directory.CreateDirectory(PastaBaixados);

            // Já baixado e íntegro numa tentativa anterior: não baixa de novo.
            if (File.Exists(destino) && await ConfereAsync(destino, versao.Sha256).ConfigureAwait(false))
            {
                Registro.Escrever("instalador já estava baixado e íntegro");
                return destino;
            }

            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(20) };
            using var resposta = await http.GetAsync(versao.Url, HttpCompletionOption.ResponseHeadersRead, ct)
                                           .ConfigureAwait(false);
            resposta.EnsureSuccessStatusCode();

            var total = resposta.Content.Headers.ContentLength ?? versao.Tamanho;
            await using (var origem = await resposta.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var arquivo = File.Create(destino))
            {
                var buffer = new byte[128 * 1024];
                long baixado = 0;
                int lidos;
                while ((lidos = await origem.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    await arquivo.WriteAsync(buffer.AsMemory(0, lidos), ct).ConfigureAwait(false);
                    baixado += lidos;
                    if (total > 0) andamento?.Report(baixado * 100.0 / total);
                }
            }

            if (!await ConfereAsync(destino, versao.Sha256).ConfigureAwait(false))
            {
                Registro.Escrever("o arquivo baixado não confere com o resumo publicado; descartado");
                try { File.Delete(destino); } catch { }
                return null;
            }

            Registro.Escrever($"atualização baixada e conferida: {destino}");
            return destino;
        }
        catch (Exception e)
        {
            Registro.Falha("Atualizacao.Baixar", e);
            try { if (File.Exists(destino)) File.Delete(destino); } catch { }
            return null;
        }
    }

    private static async Task<bool> ConfereAsync(string arquivo, string esperado)
    {
        if (string.IsNullOrWhiteSpace(esperado)) return false;
        try
        {
            await using var f = File.OpenRead(arquivo);
            var resumo = await SHA256.HashDataAsync(f).ConfigureAwait(false);
            var texto = Convert.ToHexString(resumo);
            return texto.Equals(esperado.Replace("-", "").Trim(), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception e)
        {
            Registro.Falha("Atualizacao.Confere", e);
            return false;
        }
    }

    /// <summary>
    /// Roda o instalador em silêncio e devolve true se ele começou. O app precisa fechar em
    /// seguida — o instalador substitui os arquivos que estão em uso.
    /// </summary>
    public static bool Instalar(string instalador)
    {
        try
        {
            var info = new ProcessStartInfo
            {
                FileName = instalador,
                Arguments = "/SILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS /RESTARTAPPLICATIONS",
                UseShellExecute = true,
            };
            Process.Start(info);
            Registro.Escrever("instalador da atualização iniciado");
            return true;
        }
        catch (Exception e)
        {
            Registro.Falha("Atualizacao.Instalar", e);
            return false;
        }
    }

    /// <summary>Apaga instaladores de versões já passadas.</summary>
    public static void LimparAntigos()
    {
        try
        {
            if (!Directory.Exists(PastaBaixados)) return;
            foreach (var f in Directory.GetFiles(PastaBaixados, "QubitsCast-*.exe"))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddDays(-7)) File.Delete(f);
                }
                catch { }
            }
        }
        catch { }
    }
}
