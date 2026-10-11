using System.Globalization;
using Dapper;
using IPTVPlayer.Models;

namespace IPTVPlayer.Services.Data;

/// <summary>Acesso síncrono à tabela de playlists (os chamadores usam Task.Run).</summary>
public sealed class PlaylistRepository
{
    private const string SelectSql = """
        SELECT p.id, p.name, p.kind, p.source_protected, p.username_protected, p.password_protected,
               p.epg_url_protected, p.custom_epg_url_protected, p.epg_loaded_utc, p.server_timezone,
               p.last_loaded_utc, p.created_utc,
               (SELECT COUNT(*) FROM channels c WHERE c.playlist_id = p.id) AS channel_count
        FROM playlists p
        """;

    private readonly AppDatabase _db;

    public PlaylistRepository(AppDatabase db)
    {
        _db = db;
    }

    public List<Playlist> GetAll()
    {
        using var c = _db.Open();
        return c.Query<Row>(SelectSql + " ORDER BY p.name COLLATE NOCASE").Select(ToModel).ToList();
    }

    public Playlist? Get(long id)
    {
        using var c = _db.Open();
        var row = c.QuerySingleOrDefault<Row>(SelectSql + " WHERE p.id = @id", new { id });
        return row is null ? null : ToModel(row);
    }

    public long Insert(Playlist p)
    {
        using var c = _db.Open();
        return c.ExecuteScalar<long>("""
            INSERT INTO playlists (name, kind, source_protected, username_protected, password_protected,
                                   epg_url_protected, custom_epg_url_protected, epg_loaded_utc, server_timezone,
                                   last_loaded_utc, created_utc)
            VALUES (@Name, @Kind, @SourceProtected, @UsernameProtected, @PasswordProtected,
                    @EpgUrlProtected, @CustomEpgUrlProtected, @EpgLoaded, @ServerTimezone,
                    @LastLoaded, @Created);
            SELECT last_insert_rowid();
            """, ToParameters(p));
    }

    public void Update(Playlist p)
    {
        using var c = _db.Open();
        c.Execute("""
            UPDATE playlists SET name = @Name, kind = @Kind, source_protected = @SourceProtected,
                   username_protected = @UsernameProtected, password_protected = @PasswordProtected,
                   epg_url_protected = @EpgUrlProtected, custom_epg_url_protected = @CustomEpgUrlProtected,
                   epg_loaded_utc = @EpgLoaded, server_timezone = @ServerTimezone, last_loaded_utc = @LastLoaded
            WHERE id = @Id
            """, ToParameters(p));
    }

    public void Delete(long id)
    {
        using var c = _db.Open();
        c.Execute("DELETE FROM playlists WHERE id = @id", new { id });
    }

    private static object ToParameters(Playlist p) => new
    {
        p.Id,
        p.Name,
        Kind = (int)p.Kind,
        p.SourceProtected,
        p.UsernameProtected,
        p.PasswordProtected,
        p.EpgUrlProtected,
        p.CustomEpgUrlProtected,
        p.ServerTimezone,
        EpgLoaded = FormatUtc(p.EpgLoadedUtc),
        LastLoaded = FormatUtc(p.LastLoadedUtc),
        Created = FormatUtc(p.CreatedUtc),
    };

    private static Playlist ToModel(Row r) => new()
    {
        Id = r.Id,
        Name = r.Name,
        Kind = (PlaylistKind)r.Kind,
        SourceProtected = r.SourceProtected,
        UsernameProtected = r.UsernameProtected,
        PasswordProtected = r.PasswordProtected,
        EpgUrlProtected = r.EpgUrlProtected,
        CustomEpgUrlProtected = r.CustomEpgUrlProtected,
        EpgLoadedUtc = ParseUtc(r.EpgLoadedUtc),
        ServerTimezone = r.ServerTimezone,
        LastLoadedUtc = ParseUtc(r.LastLoadedUtc),
        CreatedUtc = ParseUtc(r.CreatedUtc) ?? DateTime.UtcNow,
        ChannelCount = (int)r.ChannelCount,
    };

    private static string? FormatUtc(DateTime? value) =>
        value?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTime? ParseUtc(string? value) =>
        DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var d)
            ? d.ToUniversalTime()
            : null;

    private sealed class Row
    {
        public long Id { get; set; }
        public string Name { get; set; } = "";
        public long Kind { get; set; }
        public byte[] SourceProtected { get; set; } = [];
        public byte[]? UsernameProtected { get; set; }
        public byte[]? PasswordProtected { get; set; }
        public byte[]? EpgUrlProtected { get; set; }
        public byte[]? CustomEpgUrlProtected { get; set; }
        public string? EpgLoadedUtc { get; set; }
        public string? ServerTimezone { get; set; }
        public string? LastLoadedUtc { get; set; }
        public string? CreatedUtc { get; set; }
        public long ChannelCount { get; set; }
    }
}
