using System.Globalization;
using Dapper;

namespace IPTVPlayer.Services.Data;

/// <summary>Posição guardada de um filme ("m:{id}") ou episódio ("e:{id}", com a série).</summary>
public sealed record WatchProgress(string ItemKey, long? SeriesId, long PositionMs, long DurationMs, bool Finished, DateTime UpdatedUtc)
{
    /// <summary>Menos do que isto não vale a pena retomar.</summary>
    public const long MinResumeMs = 30_000;

    public double Fraction => DurationMs > 0 ? Math.Clamp((double)PositionMs / DurationMs, 0, 1) : 0;

    public bool CanResume => !Finished && PositionMs >= MinResumeMs;

    /// <summary>Visto até aos últimos 5% (ou aos últimos 3 minutos): conta como visto.</summary>
    public static bool IsFinished(long positionMs, long durationMs) =>
        durationMs > 0 && (positionMs >= durationMs * 0.95 || durationMs - positionMs <= 180_000 && durationMs > 600_000);

    public static string MovieKey(long streamId) => "m:" + streamId;
    public static string EpisodeKey(long episodeId) => "e:" + episodeId;
}

public sealed class WatchProgressRepository
{
    private readonly AppDatabase _db;

    public WatchProgressRepository(AppDatabase db)
    {
        _db = db;
    }

    public Dictionary<string, WatchProgress> GetAll(long playlistId)
    {
        using var c = _db.Open();
        return c.Query<Row>("""
                SELECT item_key, series_id, position_ms, duration_ms, finished, updated_utc
                FROM watch_progress WHERE playlist_id = @playlistId
                """, new { playlistId })
            .Select(r => new WatchProgress(r.ItemKey, r.SeriesId, r.PositionMs, r.DurationMs, r.Finished != 0,
                DateTime.TryParse(r.UpdatedUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var d) ? d.ToUniversalTime() : DateTime.MinValue))
            .ToDictionary(p => p.ItemKey, StringComparer.Ordinal);
    }

    public void Save(long playlistId, WatchProgress p)
    {
        using var c = _db.Open();
        c.Execute("""
            INSERT INTO watch_progress (playlist_id, item_key, series_id, position_ms, duration_ms, finished, updated_utc)
            VALUES (@playlistId, @ItemKey, @SeriesId, @PositionMs, @DurationMs, @finished, @updated)
            ON CONFLICT(playlist_id, item_key) DO UPDATE SET
                series_id = excluded.series_id, position_ms = excluded.position_ms, duration_ms = excluded.duration_ms,
                finished = excluded.finished, updated_utc = excluded.updated_utc
            """, new
        {
            playlistId,
            p.ItemKey,
            p.SeriesId,
            p.PositionMs,
            p.DurationMs,
            finished = p.Finished ? 1 : 0,
            updated = p.UpdatedUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
        });
    }

    private sealed class Row
    {
        public string ItemKey { get; set; } = "";
        public long? SeriesId { get; set; }
        public long PositionMs { get; set; }
        public long DurationMs { get; set; }
        public long Finished { get; set; }
        public string? UpdatedUtc { get; set; }
    }
}
