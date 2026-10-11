using System.Diagnostics;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;

namespace IPTVPlayer.Services.Net;

/// <summary>Versão publicada no GitHub com o instalador .msi.</summary>
public sealed record UpdateInfo(Version Version, string MsiUrl, string MsiName, long MsiSize, string PageUrl);

/// <summary>
/// Atualizações a partir das Releases do GitHub: compara a versão instalada com a última release,
/// descarrega o .msi e corre-o (o MSI substitui a versão anterior e mantém os dados do utilizador).
/// </summary>
public sealed class UpdateService
{
    public const string ReleasesPage = "https://github.com/vgvcosta/IPTVPlayer/releases/latest";
    private const string LatestApi = "https://api.github.com/repos/vgvcosta/IPTVPlayer/releases/latest";

    private readonly HttpClient _http;

    public UpdateService(HttpClients clients)
    {
        _http = clients.Api;
    }

    public static Version CurrentVersion { get; } = Normalize(Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0));

    /// <summary>Instalada pelo .msi (em Program Files). A versão .zip é portátil e atualiza-se à mão.</summary>
    public static bool IsInstalled
    {
        get
        {
            var dir = AppContext.BaseDirectory;
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            return !string.IsNullOrEmpty(programFiles) && dir.StartsWith(programFiles, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>Devolve a release mais recente se for mais nova do que esta versão; senão null.</summary>
    public async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, LatestApi);
        request.Headers.UserAgent.Clear();
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("PepinoTv", CurrentVersion.ToString()));
        request.Headers.Accept.Clear();
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new UserFacingException("O GitHub não respondeu a tempo. Tenta mais tarde.", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new UserFacingException($"Não foi possível ligar ao GitHub: {HttpHelper.Describe(ex)}.", ex);
        }

        using (response)
        {
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                throw new UserFacingException("Não há versões publicadas (ou o repositório no GitHub não é público).");
            if (!response.IsSuccessStatusCode)
                throw new UserFacingException(HttpHelper.DescribeStatus((int)response.StatusCode));

            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var json = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            return ParseRelease(json.RootElement, CurrentVersion);
        }
    }

    /// <summary>Lê a resposta de /releases/latest. Público para os testes.</summary>
    internal static UpdateInfo? ParseRelease(JsonElement release, Version current)
    {
        var tag = release.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var latest)) return null;
        latest = Normalize(latest);
        if (latest <= Normalize(current)) return null;

        if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) return null;
        foreach (var asset in assets.EnumerateArray())
        {
            var name = asset.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            if (!name.EndsWith(".msi", StringComparison.OrdinalIgnoreCase)) continue;
            var url = asset.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
            if (url is null || !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) continue;
            var size = asset.TryGetProperty("size", out var s) && s.TryGetInt64(out var sz) ? sz : 0;
            var page = release.TryGetProperty("html_url", out var h) ? h.GetString() ?? ReleasesPage : ReleasesPage;
            return new UpdateInfo(latest, url, Path.GetFileName(name), size, page);
        }
        return null;
    }

    /// <summary>Descarrega o .msi para a pasta temporária e devolve o caminho.</summary>
    public async Task<string> DownloadAsync(UpdateInfo update, IProgress<double>? progress, CancellationToken ct = default)
    {
        var folder = Path.Combine(Path.GetTempPath(), "PepinoTv-update");
        Directory.CreateDirectory(folder);
        var target = Path.Combine(folder, update.MsiName);
        var partial = target + ".part";

        using var response = await HttpHelper.GetAsync(_http, new Uri(update.MsiUrl), ct).ConfigureAwait(false);
        var total = response.Content.Headers.ContentLength ?? update.MsiSize;
        await using (var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
        await using (var file = File.Create(partial))
        {
            var buffer = new byte[81920];
            long done = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await file.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                done += read;
                if (total > 0) progress?.Report((double)done / total);
            }
        }

        File.Move(partial, target, overwrite: true);
        return target;
    }

    /// <summary>
    /// Corre o instalador na mesma pasta e volta a abrir a app no fim.
    /// O chamador tem de fechar a app logo a seguir (os ficheiros não podem estar em uso).
    /// </summary>
    public static void LaunchInstaller(string msiPath)
    {
        var installDir = AppContext.BaseDirectory.TrimEnd('\\');
        var exe = Environment.ProcessPath ?? Path.Combine(installDir, "IPTVPlayer.exe");
        // cmd espera pelo msiexec (que pede permissão de administrador) e reabre a app se correu bem.
        var command = $"/c msiexec /i \"{msiPath}\" /passive /norestart INSTALLFOLDER=\"{installDir}\" && start \"\" \"{exe}\"";
        Process.Start(new ProcessStartInfo("cmd.exe", command)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        });
    }

    public static void OpenReleasesPage(string? url = null) =>
        Process.Start(new ProcessStartInfo(url ?? ReleasesPage) { UseShellExecute = true });

    private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0));
}
