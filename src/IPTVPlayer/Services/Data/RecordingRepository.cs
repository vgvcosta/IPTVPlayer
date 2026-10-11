using System.Globalization;
using Dapper;

namespace IPTVPlayer.Services.Data;

public enum RecordingStatus
{
    Scheduled = 0,
    Recording = 1,
    Completed = 2,
    Failed = 3,
    Cancelled = 4,
}

public sealed class Recording
{
    public long Id { get; set; }
    public long PlaylistId { get; set; }
    public string ChannelKey { get; set; } = "";
    public string ChannelName { get; set; } = "";
    public string Title { get; set; } = "";
    public DateTime StartUtc { get; set; }
    public DateTime StopUtc { get; set; }
    public string? FilePath { get; set; }
    public RecordingStatus Status { get; set; }
    public string? Error { get; set; }
    public DateTime CreatedUtc { get; set; }
}

public sealed class RecordingRepository
{
    private readonly AppDatabase _db;

    public RecordingRepository(AppDatabase db)
    {
        _db = db;
    }

    public List<Recording> GetAll()
    {
        using var c = _db.Open();
        return c.Query<Row>("""
                SELECT id, playlist_id, channel_key, channel_name, title, start_utc, stop_utc, file_path, status, error, created_utc
                FROM recordings ORDER BY start_utc DESC
                """)
            .Select(ToModel).ToList();
    }

    public Recording? Get(long id) => GetAll().FirstOrDefault(r => r.Id == id);

    public long Insert(Recording r)
    {
        using var c = _db.Open();
        return c.ExecuteScalar<long>("""
            INSERT INTO recordings (playlist_id, channel_key, channel_name, title, start_utc, stop_utc, file_path, status, error, created_utc)
            VALUES (@PlaylistId, @ChannelKey, @ChannelName, @Title, @Start, @Stop, @FilePath, @Status, @Error, @Created);
            SELECT last_insert_rowid();
            """, Params(r));
    }

    public void Update(Recording r)
    {
        using var c = _db.Open();
        c.Execute("""
            UPDATE recordings SET title = @Title, start_utc = @Start, stop_utc = @Stop, file_path = @FilePath,
                   status = @Status, error = @Error
            WHERE id = @Id
            """, Params(r));
    }

    public void Delete(long id)
    {
        using var c = _db.Open();
        c.Execute("DELETE FROM recordings WHERE id = @id", new { id });
    }

    private static object Params(Recording r) => new
    {
        r.Id,
        r.PlaylistId,
        r.ChannelKey,
        r.ChannelName,
        r.Title,
        Start = Utc(r.StartUtc),
        Stop = Utc(r.StopUtc),
        r.FilePath,
        Status = (int)r.Status,
        r.Error,
        Created = Utc(r.CreatedUtc),
    };

    private static string Utc(DateTime d) => DateTime.SpecifyKind(d, DateTimeKind.Utc).ToString("O", CultureInfo.InvariantCulture);

    private static DateTime Parse(string? s) =>
        DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var d) ? d.ToUniversalTime() : DateTime.MinValue;

    private static Recording ToModel(Row r) => new()
    {
        Id = r.Id,
        PlaylistId = r.PlaylistId,
        ChannelKey = r.ChannelKey,
        ChannelName = r.ChannelName,
        Title = r.Title,
        StartUtc = Parse(r.StartUtc),
        StopUtc = Parse(r.StopUtc),
        FilePath = r.FilePath,
        Status = (RecordingStatus)r.Status,
        Error = r.Error,
        CreatedUtc = Parse(r.CreatedUtc),
    };

    private sealed class Row
    {
        public long Id { get; set; }
        public long PlaylistId { get; set; }
        public string ChannelKey { get; set; } = "";
        public string ChannelName { get; set; } = "";
        public string Title { get; set; } = "";
        public string? StartUtc { get; set; }
        public string? StopUtc { get; set; }
        public string? FilePath { get; set; }
        public long Status { get; set; }
        public string? Error { get; set; }
        public string? CreatedUtc { get; set; }
    }
}
