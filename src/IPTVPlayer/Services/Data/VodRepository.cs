using System.Globalization;
using Dapper;
using IPTVPlayer.Models;
using Microsoft.Data.Sqlite;

namespace IPTVPlayer.Services.Data;

public sealed class VodRepository
{
    private readonly AppDatabase _db;

    public VodRepository(AppDatabase db)
    {
        _db = db;
    }

    public (List<VodItem> Items, DateTime? LoadedUtc) Get(long playlistId, VodKind kind)
    {
        using var c = _db.Open();
        var items = c.Query<VodItem>("""
            SELECT id, playlist_id, kind, item_id, name, category, poster_url, rating, year, extension, sort_order
            FROM vod_items WHERE playlist_id = @playlistId AND kind = @kind ORDER BY sort_order
            """, new { playlistId, kind = (int)kind }).AsList();
        var loaded = c.ExecuteScalar<string?>("SELECT loaded_utc FROM vod_state WHERE playlist_id = @playlistId AND kind = @kind",
            new { playlistId, kind = (int)kind });
        return (items, DateTime.TryParse(loaded, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var d) ? d.ToUniversalTime() : null);
    }

    /// <summary>Categorias (pela ordem do servidor) da lista em cache.</summary>
    public List<string> GetCategories(long playlistId, VodKind kind)
    {
        using var c = _db.Open();
        return c.Query<string>("""
            SELECT category FROM vod_items WHERE playlist_id = @playlistId AND kind = @kind
            GROUP BY category ORDER BY MIN(sort_order)
            """, new { playlistId, kind = (int)kind }).AsList();
    }

    public void ReplaceAll(long playlistId, VodKind kind, IReadOnlyList<VodItem> items, CancellationToken ct = default)
    {
        using var c = _db.Open();
        using var tx = c.BeginTransaction();
        c.Execute("DELETE FROM vod_items WHERE playlist_id = @playlistId AND kind = @kind", new { playlistId, kind = (int)kind }, tx);

        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO vod_items (playlist_id, kind, item_id, name, category, poster_url, rating, year, extension, sort_order)
            VALUES ($p, $k, $i, $n, $c, $poster, $r, $y, $e, $o)
            """;
        var pP = cmd.Parameters.Add("$p", SqliteType.Integer);
        var pK = cmd.Parameters.Add("$k", SqliteType.Integer);
        var pI = cmd.Parameters.Add("$i", SqliteType.Integer);
        var pN = cmd.Parameters.Add("$n", SqliteType.Text);
        var pC = cmd.Parameters.Add("$c", SqliteType.Text);
        var pPoster = cmd.Parameters.Add("$poster", SqliteType.Text);
        var pR = cmd.Parameters.Add("$r", SqliteType.Text);
        var pY = cmd.Parameters.Add("$y", SqliteType.Text);
        var pE = cmd.Parameters.Add("$e", SqliteType.Text);
        var pO = cmd.Parameters.Add("$o", SqliteType.Integer);
        cmd.Prepare();

        for (var i = 0; i < items.Count; i++)
        {
            if ((i & 1023) == 0) ct.ThrowIfCancellationRequested();
            var v = items[i];
            v.PlaylistId = playlistId;
            pP.Value = playlistId;
            pK.Value = (int)kind;
            pI.Value = v.ItemId;
            pN.Value = v.Name;
            pC.Value = v.Category;
            pPoster.Value = (object?)v.PosterUrl ?? DBNull.Value;
            pR.Value = (object?)v.Rating ?? DBNull.Value;
            pY.Value = (object?)v.Year ?? DBNull.Value;
            pE.Value = (object?)v.Extension ?? DBNull.Value;
            pO.Value = v.SortOrder;
            cmd.ExecuteNonQuery();
        }

        c.Execute("""
            INSERT INTO vod_state (playlist_id, kind, loaded_utc) VALUES (@playlistId, @kind, @now)
            ON CONFLICT(playlist_id, kind) DO UPDATE SET loaded_utc = excluded.loaded_utc
            """, new { playlistId, kind = (int)kind, now = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture) }, tx);
        tx.Commit();
    }
}
