using Dapper;
using IPTVPlayer.Services.Epg;
using Microsoft.Data.Sqlite;

namespace IPTVPlayer.Services.Data;

public sealed class EpgRepository
{
    private readonly AppDatabase _db;

    public EpgRepository(AppDatabase db)
    {
        _db = db;
    }

    /// <summary>Substitui o EPG de uma playlist numa única transação.</summary>
    public void ReplaceAll(long playlistId, XmltvResult data, CancellationToken ct = default)
    {
        using var c = _db.Open();
        using var tx = c.BeginTransaction();
        c.Execute("DELETE FROM epg_map WHERE playlist_id = @playlistId", new { playlistId }, tx);
        c.Execute("DELETE FROM epg_programmes WHERE playlist_id = @playlistId", new { playlistId }, tx);

        using (var map = c.CreateCommand())
        {
            map.Transaction = tx;
            map.CommandText = "INSERT INTO epg_map (playlist_id, channel_key, epg_id) VALUES ($p, $k, $e)";
            var pP = map.Parameters.Add("$p", SqliteType.Integer);
            var pK = map.Parameters.Add("$k", SqliteType.Text);
            var pE = map.Parameters.Add("$e", SqliteType.Text);
            map.Prepare();
            foreach (var (key, epgId) in data.KeyToEpgId)
            {
                pP.Value = playlistId;
                pK.Value = key;
                pE.Value = epgId;
                map.ExecuteNonQuery();
            }
        }

        using (var cmd = c.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO epg_programmes (playlist_id, epg_id, start_utc, stop_utc, title, description)
                VALUES ($p, $e, $s, $t, $title, $d)
                """;
            var pP = cmd.Parameters.Add("$p", SqliteType.Integer);
            var pE = cmd.Parameters.Add("$e", SqliteType.Text);
            var pS = cmd.Parameters.Add("$s", SqliteType.Integer);
            var pT = cmd.Parameters.Add("$t", SqliteType.Integer);
            var pTitle = cmd.Parameters.Add("$title", SqliteType.Text);
            var pD = cmd.Parameters.Add("$d", SqliteType.Text);
            cmd.Prepare();

            for (var i = 0; i < data.Programmes.Count; i++)
            {
                if ((i & 4095) == 0) ct.ThrowIfCancellationRequested();
                var p = data.Programmes[i];
                pP.Value = playlistId;
                pE.Value = p.EpgId;
                pS.Value = p.Start;
                pT.Value = p.Stop;
                pTitle.Value = p.Title;
                pD.Value = (object?)p.Description ?? DBNull.Value;
                cmd.ExecuteNonQuery();
            }
        }

        tx.Commit();
    }

    public EpgGuide Load(long playlistId)
    {
        using var c = _db.Open();
        var map = c.Query<(string Key, string EpgId)>(
                "SELECT channel_key, epg_id FROM epg_map WHERE playlist_id = @playlistId", new { playlistId })
            .ToDictionary(x => x.Key, x => x.EpgId, StringComparer.Ordinal);

        var byEpgId = new Dictionary<string, List<EpgProgramme>>(StringComparer.Ordinal);
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = """
                SELECT epg_id, start_utc, stop_utc, title FROM epg_programmes
                WHERE playlist_id = $p ORDER BY epg_id, start_utc
                """;
            cmd.Parameters.AddWithValue("$p", playlistId);
            using var r = cmd.ExecuteReader();
            string? lastId = null;
            List<EpgProgramme>? current = null;
            while (r.Read())
            {
                var id = r.GetString(0);
                if (!string.Equals(id, lastId, StringComparison.Ordinal))
                {
                    lastId = id;
                    if (!byEpgId.TryGetValue(id, out current)) byEpgId[id] = current = [];
                }
                // O mesmo id partilha a mesma instância de string (poupa memória com 100 000+ programas).
                current!.Add(new EpgProgramme(lastId!, r.GetInt64(1), r.GetInt64(2), r.GetString(3)));
            }
        }

        return new EpgGuide(map, byEpgId.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray(), StringComparer.Ordinal));
    }

    public string? GetDescription(long playlistId, string epgId, long start)
    {
        using var c = _db.Open();
        return c.ExecuteScalar<string?>("""
            SELECT description FROM epg_programmes
            WHERE playlist_id = @playlistId AND epg_id = @epgId AND start_utc = @start
            """, new { playlistId, epgId, start });
    }
}
