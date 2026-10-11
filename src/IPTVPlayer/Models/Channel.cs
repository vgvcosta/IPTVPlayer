namespace IPTVPlayer.Models;

public sealed class Channel
{
    public long Id { get; set; }
    public long PlaylistId { get; set; }

    /// <summary>
    /// Chave estável entre atualizações da playlist (usada nos favoritos e no "último canal"):
    /// "x:{stream_id}" no Xtream, hash do URL no M3U.
    /// </summary>
    public string Key { get; set; } = "";

    public string Name { get; set; } = "";
    public string? TvgId { get; set; }
    public string? TvgName { get; set; }
    public string? LogoUrl { get; set; }
    public string GroupTitle { get; set; } = "";

    /// <summary>
    /// URL do stream encriptado (AES-GCM). No Xtream guarda só "{stream_id}.{ext}";
    /// o URL completo (com credenciais) é montado em memória no momento de reproduzir.
    /// </summary>
    public byte[] UrlSealed { get; set; } = [];

    public int SortOrder { get; set; }
    public string? UserAgent { get; set; }
    public string? Referrer { get; set; }

    /// <summary>Dias de catch-up disponíveis (0 = sem catch-up).</summary>
    public int CatchupDays { get; set; }

    /// <summary>"xc" (Xtream), "default", "append", "shift" ou "flussonic".</summary>
    public string? CatchupMode { get; set; }

    /// <summary>Modelo do URL de catch-up (M3U catchup-source), encriptado: pode trazer credenciais.</summary>
    public byte[]? CatchupSourceSealed { get; set; }

    public bool HasCatchup => CatchupDays > 0;
}
