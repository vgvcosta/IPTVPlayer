using System.Globalization;
using System.Text.RegularExpressions;

namespace IPTVPlayer.Services.Playback;

/// <summary>
/// URLs de catch-up (ver programas que já passaram). Formatos suportados:
/// <list type="bullet">
/// <item><b>xc</b> (Xtream Codes): /timeshift/{user}/{pass}/{minutos}/{AAAA-MM-DD:HH-mm}/{id}.ts, na hora do servidor;</item>
/// <item><b>default</b>: catchup-source é um URL completo com marcadores ({utc}, ${start}, {duration}…);</item>
/// <item><b>append</b>: catchup-source é acrescentado ao URL do canal;</item>
/// <item><b>shift</b>: acrescenta ?utc={início}&amp;lutc={agora} ao URL do canal;</item>
/// <item><b>flussonic</b>: index-{início}-{duração}.m3u8 / timeshift_abs-{início}.ts.</item>
/// </list>
/// </summary>
public static partial class CatchupUrlBuilder
{
    /// <summary>URL Xtream (o canal tem de vir de um servidor Xtream).</summary>
    public static string Xtream(string server, string username, string password, long streamId,
        DateTime startUtc, DateTime stopUtc, TimeZoneInfo serverZone)
    {
        var start = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(startUtc, DateTimeKind.Utc), serverZone);
        return $"{server.TrimEnd('/')}/timeshift/{Uri.EscapeDataString(username)}/{Uri.EscapeDataString(password)}/" +
               $"{DurationMinutes(startUtc, stopUtc)}/{start.ToString("yyyy-MM-dd:HH-mm", CultureInfo.InvariantCulture)}/{streamId}.ts";
    }

    /// <summary>URL de catch-up de um canal M3U.</summary>
    public static string M3u(string mode, string streamUrl, string? source, DateTime startUtc, DateTime stopUtc, DateTime nowUtc)
    {
        switch (mode.Trim().ToLowerInvariant())
        {
            case "xc":
                var m = XtreamStreamUrl().Match(streamUrl);
                if (!m.Success)
                    throw new UserFacingException("Este canal indica catch-up \"xc\", mas o endereço não tem o formato Xtream.");
                var server = m.Groups["server"].Value;
                return Xtream(server, Uri.UnescapeDataString(m.Groups["user"].Value), Uri.UnescapeDataString(m.Groups["pass"].Value),
                    long.Parse(m.Groups["id"].Value, CultureInfo.InvariantCulture), startUtc, stopUtc, TimeZoneInfo.Utc);

            case "append":
                return streamUrl + Fill(source ?? "", startUtc, stopUtc, nowUtc);

            case "shift" or "timeshift":
                return streamUrl + (streamUrl.Contains('?') ? "&" : "?") +
                       $"utc={Unix(startUtc)}&lutc={Unix(nowUtc)}";

            case "flussonic" or "flussonic-hls" or "flussonic-ts" or "fs":
                return Flussonic(streamUrl, startUtc, stopUtc);

            default: // "default" e desconhecidos
                if (string.IsNullOrWhiteSpace(source))
                    throw new UserFacingException("Este canal não indica o endereço de catch-up (catchup-source).");
                var filled = Fill(source, startUtc, stopUtc, nowUtc);
                // Alguns fornecedores põem só o sufixo ("?utc=…") mesmo em modo default.
                return filled.StartsWith('?') || filled.StartsWith('&') ? streamUrl + filled : filled;
        }
    }

    /// <summary>Substitui os marcadores mais comuns ({utc}, ${start}, {duration}, {Y}…).</summary>
    internal static string Fill(string template, DateTime startUtc, DateTime stopUtc, DateTime nowUtc)
    {
        var start = Unix(startUtc);
        var stop = Unix(stopUtc);
        var now = Unix(nowUtc);
        var duration = Math.Max(60, stop - start);

        var s = template
            .Replace("${start}", start.ToString(CultureInfo.InvariantCulture))
            .Replace("${end}", stop.ToString(CultureInfo.InvariantCulture))
            .Replace("${now}", now.ToString(CultureInfo.InvariantCulture))
            .Replace("${timestamp}", now.ToString(CultureInfo.InvariantCulture))
            .Replace("${duration}", duration.ToString(CultureInfo.InvariantCulture))
            .Replace("{utcend}", stop.ToString(CultureInfo.InvariantCulture))
            .Replace("{utc}", start.ToString(CultureInfo.InvariantCulture))
            .Replace("{start}", start.ToString(CultureInfo.InvariantCulture))
            .Replace("{end}", stop.ToString(CultureInfo.InvariantCulture))
            .Replace("{lutc}", now.ToString(CultureInfo.InvariantCulture))
            .Replace("{now}", now.ToString(CultureInfo.InvariantCulture))
            .Replace("{timestamp}", now.ToString(CultureInfo.InvariantCulture))
            .Replace("{Y}", startUtc.ToString("yyyy", CultureInfo.InvariantCulture))
            .Replace("{m}", startUtc.ToString("MM", CultureInfo.InvariantCulture))
            .Replace("{d}", startUtc.ToString("dd", CultureInfo.InvariantCulture))
            .Replace("{H}", startUtc.ToString("HH", CultureInfo.InvariantCulture))
            .Replace("{M}", startUtc.ToString("mm", CultureInfo.InvariantCulture))
            .Replace("{S}", startUtc.ToString("ss", CultureInfo.InvariantCulture));

        // {duration}, {duration:60} (minutos), {offset:1} (segundos desde o início)
        s = DurationToken().Replace(s, m => (duration / Divisor(m)).ToString(CultureInfo.InvariantCulture));
        s = OffsetToken().Replace(s, m => (Math.Max(0, now - start) / Divisor(m)).ToString(CultureInfo.InvariantCulture));
        return s;
    }

    private static string Flussonic(string url, DateTime startUtc, DateTime stopUtc)
    {
        var start = Unix(startUtc);
        var duration = Math.Max(60, Unix(stopUtc) - start);
        var m = FlussonicUrl().Match(url);
        if (!m.Success) throw new UserFacingException("O endereço deste canal não tem o formato Flussonic esperado.");
        var basePath = m.Groups["base"].Value;
        return m.Groups["file"].Value.StartsWith("mpegts", StringComparison.OrdinalIgnoreCase)
            ? $"{basePath}/timeshift_abs-{start}.ts{m.Groups["query"].Value}"
            : $"{basePath}/index-{start}-{duration}.m3u8{m.Groups["query"].Value}";
    }

    private static long Divisor(Match m) =>
        m.Groups["div"].Success && long.TryParse(m.Groups["div"].Value, out var d) && d > 0 ? d : 1;

    internal static int DurationMinutes(DateTime startUtc, DateTime stopUtc) =>
        (int)Math.Max(1, Math.Ceiling((stopUtc - startUtc).TotalMinutes));

    private static long Unix(DateTime utc) => new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeSeconds();

    /// <summary>Fuso do servidor Xtream ("Europe/Lisbon"); se não for conhecido, o do PC.</summary>
    public static TimeZoneInfo ResolveZone(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return TimeZoneInfo.Local;
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (Exception) when (TimeZoneInfo.TryConvertIanaIdToWindowsId(id, out var windowsId))
        {
            return TimeZoneInfo.FindSystemTimeZoneById(windowsId);
        }
        catch (Exception)
        {
            return TimeZoneInfo.Local;
        }
    }

    [GeneratedRegex(@"\{duration(?::(?<div>\d+))?\}")]
    private static partial Regex DurationToken();

    [GeneratedRegex(@"\{offset(?::(?<div>\d+))?\}")]
    private static partial Regex OffsetToken();

    [GeneratedRegex(@"^(?<server>https?://[^/]+)/(?:live/)?(?<user>[^/]+)/(?<pass>[^/]+)/(?<id>\d+)(?:\.\w+)?(?:\?.*)?$", RegexOptions.IgnoreCase)]
    private static partial Regex XtreamStreamUrl();

    [GeneratedRegex(@"^(?<base>https?://.+)/(?<file>[^/?]+)(?<query>\?.*)?$", RegexOptions.IgnoreCase)]
    private static partial Regex FlussonicUrl();
}
