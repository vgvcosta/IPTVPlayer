namespace IPTVPlayer.Services.Parsing;

public sealed class M3uEntry
{
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
    public string? TvgId { get; set; }
    public string? TvgName { get; set; }
    public string? TvgLogo { get; set; }
    public string? GroupTitle { get; set; }
    public string? UserAgent { get; set; }
    public string? Referrer { get; set; }

    // Catch-up: catchup / catchup-type, catchup-days / tvg-rec / timeshift, catchup-source
    public string? CatchupMode { get; set; }
    public int CatchupDays { get; set; }
    public string? CatchupSource { get; set; }
}

public sealed class M3uPlaylist
{
    /// <summary>url-tvg / x-tvg-url do cabeçalho #EXTM3U (EPG, fase 2).</summary>
    public string? EpgUrl { get; set; }
    public List<M3uEntry> Entries { get; } = [];

    /// <summary>Atributos de catch-up no #EXTM3U, herdados pelos canais que não os definem.</summary>
    internal M3uEntry Defaults { get; } = new();
}

public sealed class M3uFormatException : UserFacingException
{
    public M3uFormatException(string message) : base(message)
    {
    }
}

/// <summary>
/// Parser M3U/M3U8 em streaming (linha a linha, sem carregar o ficheiro todo em memória).
/// Suporta #EXTINF com atributos (tvg-id, tvg-name, tvg-logo, group-title), #EXTGRP e
/// #EXTVLCOPT (http-user-agent, http-referrer).
/// </summary>
public sealed class M3uParser
{
    public async Task<M3uPlaylist> ParseAsync(TextReader reader, CancellationToken ct = default)
    {
        var result = new M3uPlaylist();
        var sawHeader = false;
        M3uEntry? pending = null;
        string? pendingGroup = null, pendingUserAgent = null, pendingReferrer = null;
        var lineNumber = 0;

        while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } raw)
        {
            if ((++lineNumber & 1023) == 0) ct.ThrowIfCancellationRequested();

            var line = raw.Trim();
            if (line.Length == 0) continue;

            if (line[0] == '#')
            {
                if (StartsWith(line, "#EXTM3U"))
                {
                    sawHeader = true;
                    var header = result.Defaults;
                    string? epg = null;
                    ParseAttributes(line.AsSpan(7), header, ref epg);
                    result.EpgUrl ??= epg;
                }
                else if (StartsWith(line, "#EXTINF:"))
                {
                    pending = ParseExtInf(line);
                }
                else if (StartsWith(line, "#EXTGRP:"))
                {
                    pendingGroup = NullIfEmpty(line[8..].Trim());
                }
                else if (StartsWith(line, "#EXTVLCOPT:"))
                {
                    var option = line[11..].Trim();
                    var eq = option.IndexOf('=');
                    if (eq > 0)
                    {
                        var key = option[..eq].Trim();
                        var value = NullIfEmpty(option[(eq + 1)..].Trim().Trim('"'));
                        if (key.Equals("http-user-agent", StringComparison.OrdinalIgnoreCase))
                            pendingUserAgent = value;
                        else if (key.Equals("http-referrer", StringComparison.OrdinalIgnoreCase) ||
                                 key.Equals("http-referer", StringComparison.OrdinalIgnoreCase))
                            pendingReferrer = value;
                    }
                }
                continue;
            }

            // Linha de URL. Sem cabeçalho #EXTM3U só aceitamos algo que pareça um URL/caminho,
            // para não transformar uma página HTML de erro em "canais".
            if (!LooksLikeStreamLocation(line))
            {
                pending = null;
                continue;
            }

            var entry = pending ?? new M3uEntry();
            entry.Url = line;
            if (string.IsNullOrWhiteSpace(entry.GroupTitle)) entry.GroupTitle = pendingGroup;
            entry.UserAgent ??= pendingUserAgent;
            entry.Referrer ??= pendingReferrer;
            if (string.IsNullOrWhiteSpace(entry.Name)) entry.Name = entry.TvgName ?? line;
            entry.CatchupMode ??= result.Defaults.CatchupMode;
            entry.CatchupSource ??= result.Defaults.CatchupSource;
            if (entry.CatchupDays == 0) entry.CatchupDays = result.Defaults.CatchupDays;
            // catchup-source sem catchup-days: os fornecedores costumam dar 7 dias.
            if (entry.CatchupDays == 0 && entry.CatchupMode is not null) entry.CatchupDays = 7;
            result.Entries.Add(entry);

            pending = null;
            pendingGroup = pendingUserAgent = pendingReferrer = null;
        }

        if (!sawHeader && result.Entries.Count == 0)
            throw new M3uFormatException("O conteúdo não parece ser uma playlist M3U.");

        return result;
    }

    internal static M3uEntry ParseExtInf(string line)
    {
        var entry = new M3uEntry();
        var content = line.AsSpan(8); // depois de "#EXTINF:"

        // A vírgula que separa os atributos do nome é a primeira fora de aspas.
        var inQuotes = false;
        var comma = -1;
        for (var i = 0; i < content.Length; i++)
        {
            var c = content[i];
            if (c == '"') inQuotes = !inQuotes;
            else if (c == ',' && !inQuotes)
            {
                comma = i;
                break;
            }
        }

        var attributes = comma >= 0 ? content[..comma] : content;
        string? ignored = null;
        ParseAttributes(attributes, entry, ref ignored);

        if (comma >= 0) entry.Name = content[(comma + 1)..].Trim().ToString();
        return entry;
    }

    private static void ParseAttributes(ReadOnlySpan<char> s, M3uEntry entry, ref string? epgUrl)
    {
        var i = 0;
        while (i < s.Length)
        {
            while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
            var keyStart = i;
            while (i < s.Length && s[i] != '=' && !char.IsWhiteSpace(s[i])) i++;
            if (i >= s.Length || s[i] != '=') continue; // token sem valor (ex.: duração "-1")

            var key = s[keyStart..i];
            i++; // '='

            ReadOnlySpan<char> value;
            if (i < s.Length && (s[i] == '"' || s[i] == '\''))
            {
                var quote = s[i++];
                var valueStart = i;
                while (i < s.Length && s[i] != quote) i++;
                value = s[valueStart..i];
                if (i < s.Length) i++;
            }
            else
            {
                var valueStart = i;
                while (i < s.Length && !char.IsWhiteSpace(s[i])) i++;
                value = s[valueStart..i];
            }

            Assign(entry, key, value.Trim(), ref epgUrl);
        }
    }

    private static void Assign(M3uEntry entry, ReadOnlySpan<char> key, ReadOnlySpan<char> value, ref string? epgUrl)
    {
        if (value.IsEmpty) return;

        if (Is(key, "tvg-id")) entry.TvgId = value.ToString();
        else if (Is(key, "tvg-name")) entry.TvgName = value.ToString();
        else if (Is(key, "tvg-logo")) entry.TvgLogo = value.ToString();
        else if (Is(key, "group-title")) entry.GroupTitle = value.ToString();
        else if (Is(key, "http-user-agent") || Is(key, "user-agent")) entry.UserAgent = value.ToString();
        else if (Is(key, "http-referrer") || Is(key, "http-referer")) entry.Referrer = value.ToString();
        else if (Is(key, "url-tvg") || Is(key, "x-tvg-url") || Is(key, "tvg-url")) epgUrl ??= value.ToString();
        else if (Is(key, "catchup") || Is(key, "catchup-type")) entry.CatchupMode = value.ToString().ToLowerInvariant();
        else if (Is(key, "catchup-source")) entry.CatchupSource = value.ToString();
        else if ((Is(key, "catchup-days") || Is(key, "tvg-rec") || Is(key, "timeshift")) &&
                 int.TryParse(value, out var days) && days > 0)
            entry.CatchupDays = Math.Min(days, 30);
    }

    private static bool Is(ReadOnlySpan<char> key, string name) =>
        key.Equals(name, StringComparison.OrdinalIgnoreCase);

    private static bool StartsWith(string line, string prefix) =>
        line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikeStreamLocation(string line) =>
        line.Contains("://", StringComparison.Ordinal) || Path.IsPathRooted(line);

    private static string? NullIfEmpty(string s) => s.Length == 0 ? null : s;
}
