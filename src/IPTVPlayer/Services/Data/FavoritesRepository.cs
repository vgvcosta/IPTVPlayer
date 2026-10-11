using System.Globalization;
using Dapper;

namespace IPTVPlayer.Services.Data;

public sealed class FavoritesRepository
{
    private readonly AppDatabase _db;

    public FavoritesRepository(AppDatabase db)
    {
        _db = db;
    }

    public HashSet<string> GetKeys(long playlistId)
    {
        using var c = _db.Open();
        return c.Query<string>("SELECT channel_key FROM favorites WHERE playlist_id = @playlistId",
            new { playlistId }).ToHashSet(StringComparer.Ordinal);
    }

    public void Add(long playlistId, string channelKey)
    {
        using var c = _db.Open();
        c.Execute("INSERT OR IGNORE INTO favorites (playlist_id, channel_key, added_utc) VALUES (@playlistId, @channelKey, @now)",
            new { playlistId, channelKey, now = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture) });
    }

    public void Remove(long playlistId, string channelKey)
    {
        using var c = _db.Open();
        c.Execute("DELETE FROM favorites WHERE playlist_id = @playlistId AND channel_key = @channelKey",
            new { playlistId, channelKey });
    }
}
