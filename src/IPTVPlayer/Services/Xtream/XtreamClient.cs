using System.Globalization;
using System.Text.Json;
using IPTVPlayer.Models;
using IPTVPlayer.Services.Net;

namespace IPTVPlayer.Services.Xtream;

/// <summary>Cliente da API Xtream Codes (player_api.php).</summary>
public sealed class XtreamClient
{
    private static readonly string[] KnownEndpoints = ["/player_api.php", "/get.php", "/xmltv.php", "/panel_api.php"];

    private readonly HttpClient _http;

    public XtreamClient(HttpClients clients) : this(clients.Api)
    {
    }

    internal XtreamClient(HttpClient http)
    {
        _http = http;
    }

    /// <summary>
    /// Aceita "servidor.com:8080", "http://servidor.com:8080/" ou até o URL do player_api.php,
    /// e devolve "http://servidor.com:8080".
    /// </summary>
    public static string NormalizeServer(string? input)
    {
        var s = input?.Trim() ?? "";
        if (s.Length == 0) throw new UserFacingException("Indica o endereço do servidor.");
        if (!s.Contains("://", StringComparison.Ordinal)) s = "http://" + s;

        if (!Uri.TryCreate(s, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            string.IsNullOrEmpty(uri.Host))
        {
            throw new UserFacingException("O endereço do servidor não é válido. Exemplo: http://servidor.com:8080");
        }

        var path = uri.AbsolutePath;
        foreach (var endpoint in KnownEndpoints)
        {
            if (path.EndsWith(endpoint, StringComparison.OrdinalIgnoreCase))
            {
                path = path[..^endpoint.Length];
                break;
            }
        }
        return $"{uri.Scheme}://{uri.Authority}{path.TrimEnd('/')}";
    }

    public async Task<XtreamAccount> AuthenticateAsync(XtreamCredentials credentials, CancellationToken ct)
    {
        using var doc = await GetDocumentAsync(credentials, null, ct).ConfigureAwait(false);
        var root = doc.RootElement;

        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("user_info", out var userInfoElement) ||
            userInfoElement.ValueKind != JsonValueKind.Object)
        {
            throw new UserFacingException("Login falhado: utilizador ou password incorretos, ou o servidor não é Xtream Codes.");
        }

        var userInfo = userInfoElement.Deserialize<XtreamUserInfo>() ?? new XtreamUserInfo();
        if (userInfo.Auth != 1)
            throw new UserFacingException("Login falhado: utilizador ou password incorretos.");

        var status = userInfo.Status ?? "Active";
        if (!status.Equals("Active", StringComparison.OrdinalIgnoreCase))
            throw new UserFacingException($"A conta não está ativa (estado: {TranslateStatus(status)}).");

        DateTime? expires = null;
        if (long.TryParse(userInfo.ExpDate, NumberStyles.Integer, CultureInfo.InvariantCulture, out var unix) && unix > 0)
            expires = DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime;

        string? timezone = null;
        if (root.TryGetProperty("server_info", out var serverInfo) && serverInfo.ValueKind == JsonValueKind.Object &&
            serverInfo.TryGetProperty("timezone", out var tz) && tz.ValueKind == JsonValueKind.String)
            timezone = tz.GetString();

        return new XtreamAccount(status, expires, PickExtension(userInfo.AllowedOutputFormats), timezone);
    }

    public Task<List<XtreamCategory>> GetLiveCategoriesAsync(XtreamCredentials credentials, CancellationToken ct) =>
        GetListAsync<XtreamCategory>(credentials, "get_live_categories", ct);

    public Task<List<XtreamLiveStream>> GetLiveStreamsAsync(XtreamCredentials credentials, CancellationToken ct) =>
        GetListAsync<XtreamLiveStream>(credentials, "get_live_streams", ct);

    public Task<List<XtreamCategory>> GetVodCategoriesAsync(XtreamCredentials credentials, CancellationToken ct) =>
        GetListAsync<XtreamCategory>(credentials, "get_vod_categories", ct);

    public Task<List<XtreamVodStream>> GetVodStreamsAsync(XtreamCredentials credentials, CancellationToken ct) =>
        GetListAsync<XtreamVodStream>(credentials, "get_vod_streams", ct);

    public Task<List<XtreamCategory>> GetSeriesCategoriesAsync(XtreamCredentials credentials, CancellationToken ct) =>
        GetListAsync<XtreamCategory>(credentials, "get_series_categories", ct);

    public Task<List<XtreamSeries>> GetSeriesAsync(XtreamCredentials credentials, CancellationToken ct) =>
        GetListAsync<XtreamSeries>(credentials, "get_series", ct);

    /// <summary>Temporadas e episódios de uma série (get_series_info).</summary>
    public async Task<SeriesDetails> GetSeriesInfoAsync(XtreamCredentials credentials, long seriesId, CancellationToken ct)
    {
        using var doc = await GetDocumentAsync(credentials, $"get_series_info&series_id={seriesId}", ct).ConfigureAwait(false);
        return ParseSeriesInfo(doc.RootElement);
    }

    /// <summary>
    /// O formato varia entre painéis: "episodes" pode ser um objeto { "1": [...], "2": [...] }
    /// ou uma lista (de listas ou de episódios). Números podem vir como texto.
    /// </summary>
    internal static SeriesDetails ParseSeriesInfo(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            throw new UserFacingException("O servidor não devolveu informação desta série.");

        var info = root.TryGetProperty("info", out var i) && i.ValueKind == JsonValueKind.Object ? i : default;

        var episodes = new List<SeriesEpisode>();
        if (root.TryGetProperty("episodes", out var eps))
        {
            if (eps.ValueKind == JsonValueKind.Object)
            {
                foreach (var season in eps.EnumerateObject())
                    AddEpisodes(season.Value, int.TryParse(season.Name, out var n) ? n : 0, episodes);
            }
            else if (eps.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in eps.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.Array) AddEpisodes(item, 0, episodes);
                    else if (item.ValueKind == JsonValueKind.Object) AddEpisode(item, 0, episodes);
                }
            }
        }

        var seasonNames = new Dictionary<int, string>();
        if (root.TryGetProperty("seasons", out var seasons) && seasons.ValueKind == JsonValueKind.Array)
        {
            foreach (var s in seasons.EnumerateArray())
            {
                var number = (int)Long(s, "season_number");
                var name = Str(s, "name");
                if (number > 0 && !string.IsNullOrWhiteSpace(name)) seasonNames[number] = name.Trim();
            }
        }

        // Só temporadas com episódios (muitos painéis listam temporadas vazias).
        var seasonList = episodes.Select(e => e.Season).Distinct().OrderBy(n => n)
            .Select(n => new SeriesSeason(n, seasonNames.TryGetValue(n, out var name) && !name.Equals($"Season {n}", StringComparison.OrdinalIgnoreCase)
                ? name
                : $"Temporada {n}"))
            .ToList();

        return new SeriesDetails(
            Str(info, "plot"), Str(info, "genre"), Str(info, "cast"), Str(info, "releaseDate") ?? Str(info, "releasedate"),
            seasonList,
            episodes.OrderBy(e => e.Season).ThenBy(e => e.Number).ToList());
    }

    private static void AddEpisodes(JsonElement array, int season, List<SeriesEpisode> into)
    {
        if (array.ValueKind != JsonValueKind.Array) return;
        foreach (var e in array.EnumerateArray())
            if (e.ValueKind == JsonValueKind.Object) AddEpisode(e, season, into);
    }

    private static void AddEpisode(JsonElement e, int seasonFromKey, List<SeriesEpisode> into)
    {
        var id = Long(e, "id");
        if (id <= 0) return;
        var season = (int)Long(e, "season");
        if (season <= 0) season = seasonFromKey > 0 ? seasonFromKey : 1;
        var number = (int)Long(e, "episode_num");
        var info = e.TryGetProperty("info", out var i) && i.ValueKind == JsonValueKind.Object ? i : default;
        var title = Str(e, "title");
        into.Add(new SeriesEpisode(id, season, number,
            string.IsNullOrWhiteSpace(title) ? $"Episódio {number}" : title.Trim(),
            Str(e, "container_extension"), Str(info, "plot"), Str(info, "movie_image"), Str(info, "duration")));
    }

    private static string? Str(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.String => string.IsNullOrWhiteSpace(v.GetString()) ? null : v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            _ => null,
        };
    }

    private static long Long(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v)) return 0;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n)) return n;
        return v.ValueKind == JsonValueKind.String && long.TryParse(v.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) ? s : 0;
    }

    /// <summary>URL de um canal em direto (contém credenciais: nunca guardar nem registar em log).</summary>
    public static string BuildLiveUrl(XtreamCredentials c, string streamFile) =>
        $"{c.Server}/live/{Uri.EscapeDataString(c.Username)}/{Uri.EscapeDataString(c.Password)}/{streamFile}";

    /// <summary>Filme (VOD): /movie/{user}/{pass}/{stream_id}.{ext}.</summary>
    public static string BuildMovieUrl(XtreamCredentials c, long streamId, string? extension) =>
        $"{c.Server}/movie/{Uri.EscapeDataString(c.Username)}/{Uri.EscapeDataString(c.Password)}/{streamId}.{ExtensionOrDefault(extension)}";

    /// <summary>Episódio de série: /series/{user}/{pass}/{episode_id}.{ext}.</summary>
    public static string BuildEpisodeUrl(XtreamCredentials c, long episodeId, string? extension) =>
        $"{c.Server}/series/{Uri.EscapeDataString(c.Username)}/{Uri.EscapeDataString(c.Password)}/{episodeId}.{ExtensionOrDefault(extension)}";

    /// <summary>Guia XMLTV do próprio servidor.</summary>
    public static string BuildXmltvUrl(XtreamCredentials c) =>
        $"{c.Server}/xmltv.php?username={Uri.EscapeDataString(c.Username)}&password={Uri.EscapeDataString(c.Password)}";

    private static string ExtensionOrDefault(string? ext)
    {
        var e = ext?.Trim().TrimStart('.');
        return string.IsNullOrEmpty(e) || e.Any(ch => !char.IsLetterOrDigit(ch)) ? "mp4" : e;
    }

    private static Uri ApiUrl(XtreamCredentials c, string? action)
    {
        var url = $"{c.Server}/player_api.php?username={Uri.EscapeDataString(c.Username)}&password={Uri.EscapeDataString(c.Password)}";
        if (action is not null) url += "&action=" + action;
        return new Uri(url);
    }

    private async Task<JsonDocument> GetDocumentAsync(XtreamCredentials c, string? action, CancellationToken ct)
    {
        using var response = await HttpHelper.GetAsync(_http, ApiUrl(c, action), ct).ConfigureAwait(false);
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        try
        {
            return await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            throw InvalidResponse(ex);
        }
    }

    private async Task<List<T>> GetListAsync<T>(XtreamCredentials c, string action, CancellationToken ct)
    {
        using var response = await HttpHelper.GetAsync(_http, ApiUrl(c, action), ct).ConfigureAwait(false);
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        try
        {
            return await JsonSerializer.DeserializeAsync<List<T>>(stream, cancellationToken: ct).ConfigureAwait(false) ?? [];
        }
        catch (JsonException ex)
        {
            throw InvalidResponse(ex);
        }
        catch (IOException ex)
        {
            throw new UserFacingException("A ligação ao servidor foi interrompida durante o download.", ex);
        }
    }

    private static UserFacingException InvalidResponse(Exception ex) =>
        new("O servidor respondeu com dados inválidos. Confirma que é um servidor Xtream Codes.", ex);

    /// <summary>Preferimos MPEG-TS (mais estável no VLC); se o servidor não o permitir, HLS.</summary>
    private static string PickExtension(JsonElement formats)
    {
        if (formats.ValueKind != JsonValueKind.Array) return "ts";

        var list = formats.EnumerateArray()
            .Where(e => e.ValueKind == JsonValueKind.String)
            .Select(e => e.GetString()!)
            .ToList();

        if (list.Count == 0 || list.Contains("ts", StringComparer.OrdinalIgnoreCase)) return "ts";
        return list.Contains("m3u8", StringComparer.OrdinalIgnoreCase) ? "m3u8" : "ts";
    }

    private static string TranslateStatus(string status) => status.ToLowerInvariant() switch
    {
        "expired" => "expirada",
        "banned" => "bloqueada",
        "disabled" => "desativada",
        _ => status,
    };
}
