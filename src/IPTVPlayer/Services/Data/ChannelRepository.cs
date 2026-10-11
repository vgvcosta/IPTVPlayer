using Dapper;
using IPTVPlayer.Models;
using Microsoft.Data.Sqlite;

namespace IPTVPlayer.Services.Data;

public sealed class ChannelRepository
{
    private readonly AppDatabase _db;

    public ChannelRepository(AppDatabase db)
    {
        _db = db;
    }

    public List<Channel> GetByPlaylist(long playlistId)
    {
        using var c = _db.Open();
        return c.Query<Channel>("""
            SELECT id, playlist_id, channel_key AS [Key], name, tvg_id, tvg_name, logo_url, group_title,
                   url_sealed, sort_order, user_agent, referrer, catchup_days, catchup_mode, catchup_source_sealed
            FROM channels
            WHERE playlist_id = @playlistId
            ORDER BY sort_order
            """, new { playlistId }).AsList();
    }

    /// <summary>
    /// Substitui todos os canais de uma playlist numa única transação, com um comando preparado
    /// (20 000 canais demoram poucas centenas de ms).
    /// </summary>
    public void ReplaceAll(long playlistId, IReadOnlyList<Channel> channels, CancellationToken ct = default)
    {
        using var c = _db.Open();
        using var tx = c.BeginTransaction();

        c.Execute("DELETE FROM channels WHERE playlist_id = @playlistId", new { playlistId }, tx);

        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO channels (playlist_id, channel_key, name, tvg_id, tvg_name, logo_url, group_title,
                                  url_sealed, sort_order, user_agent, referrer, catchup_days, catchup_mode, catchup_source_sealed)
            VALUES ($pid, $key, $name, $tvgId, $tvgName, $logo, $group, $url, $order, $ua, $ref, $cdays, $cmode, $csrc)
            """;
        var pPid = cmd.Parameters.Add("$pid", SqliteType.Integer);
        var pKey = cmd.Parameters.Add("$key", SqliteType.Text);
        var pName = cmd.Parameters.Add("$name", SqliteType.Text);
        var pTvgId = cmd.Parameters.Add("$tvgId", SqliteType.Text);
        var pTvgName = cmd.Parameters.Add("$tvgName", SqliteType.Text);
        var pLogo = cmd.Parameters.Add("$logo", SqliteType.Text);
        var pGroup = cmd.Parameters.Add("$group", SqliteType.Text);
        var pUrl = cmd.Parameters.Add("$url", SqliteType.Blob);
        var pOrder = cmd.Parameters.Add("$order", SqliteType.Integer);
        var pUa = cmd.Parameters.Add("$ua", SqliteType.Text);
        var pRef = cmd.Parameters.Add("$ref", SqliteType.Text);
        var pCDays = cmd.Parameters.Add("$cdays", SqliteType.Integer);
        var pCMode = cmd.Parameters.Add("$cmode", SqliteType.Text);
        var pCSrc = cmd.Parameters.Add("$csrc", SqliteType.Blob);
        cmd.Prepare();

        for (var i = 0; i < channels.Count; i++)
        {
            if ((i & 1023) == 0) ct.ThrowIfCancellationRequested();

            var ch = channels[i];
            ch.PlaylistId = playlistId;
            pPid.Value = playlistId;
            pKey.Value = ch.Key;
            pName.Value = ch.Name;
            pTvgId.Value = (object?)ch.TvgId ?? DBNull.Value;
            pTvgName.Value = (object?)ch.TvgName ?? DBNull.Value;
            pLogo.Value = (object?)ch.LogoUrl ?? DBNull.Value;
            pGroup.Value = ch.GroupTitle;
            pUrl.Value = ch.UrlSealed;
            pOrder.Value = ch.SortOrder;
            pUa.Value = (object?)ch.UserAgent ?? DBNull.Value;
            pRef.Value = (object?)ch.Referrer ?? DBNull.Value;
            pCDays.Value = ch.CatchupDays;
            pCMode.Value = (object?)ch.CatchupMode ?? DBNull.Value;
            pCSrc.Value = (object?)ch.CatchupSourceSealed ?? DBNull.Value;
            cmd.ExecuteNonQuery();
        }

        tx.Commit();
    }

    public Channel? GetByKey(long playlistId, string key)
    {
        using var c = _db.Open();
        return c.QueryFirstOrDefault<Channel>("""
            SELECT id, playlist_id, channel_key AS [Key], name, tvg_id, tvg_name, logo_url, group_title,
                   url_sealed, sort_order, user_agent, referrer, catchup_days, catchup_mode, catchup_source_sealed
            FROM channels WHERE playlist_id = @playlistId AND channel_key = @key
            """, new { playlistId, key });
    }

    /// <summary>Nomes dos grupos pela ordem da playlist.</summary>
    public List<string> GetGroupNames(long playlistId)
    {
        using var c = _db.Open();
        return c.Query<string>("""
            SELECT group_title FROM channels WHERE playlist_id = @playlistId
            GROUP BY group_title ORDER BY MIN(sort_order)
            """, new { playlistId }).AsList();
    }

    public void DeleteAll()
    {
        using var c = _db.Open();
        c.Execute("DELETE FROM channels");
    }
}
