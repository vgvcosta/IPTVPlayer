using Dapper;
using IPTVPlayer.Services.Security;
using Microsoft.Data.Sqlite;

namespace IPTVPlayer.Services.Data;

public sealed class AppDatabase
{
    internal const int CurrentVersion = 4;

    internal const string SchemaV1 = """
        CREATE TABLE IF NOT EXISTS playlists (
            id                  INTEGER PRIMARY KEY AUTOINCREMENT,
            name                TEXT    NOT NULL,
            kind                INTEGER NOT NULL,
            source_protected    BLOB    NOT NULL,
            username_protected  BLOB,
            password_protected  BLOB,
            epg_url             TEXT,
            last_loaded_utc     TEXT,
            created_utc         TEXT    NOT NULL
        );

        CREATE TABLE IF NOT EXISTS channels (
            id           INTEGER PRIMARY KEY AUTOINCREMENT,
            playlist_id  INTEGER NOT NULL REFERENCES playlists(id) ON DELETE CASCADE,
            channel_key  TEXT    NOT NULL,
            name         TEXT    NOT NULL,
            tvg_id       TEXT,
            tvg_name     TEXT,
            logo_url     TEXT,
            group_title  TEXT    NOT NULL,
            url_sealed   BLOB    NOT NULL,
            sort_order   INTEGER NOT NULL,
            user_agent   TEXT,
            referrer     TEXT
        );
        CREATE INDEX IF NOT EXISTS ix_channels_playlist ON channels(playlist_id, sort_order);

        CREATE TABLE IF NOT EXISTS favorites (
            playlist_id  INTEGER NOT NULL REFERENCES playlists(id) ON DELETE CASCADE,
            channel_key  TEXT    NOT NULL,
            added_utc    TEXT    NOT NULL,
            PRIMARY KEY (playlist_id, channel_key)
        );

        CREATE TABLE IF NOT EXISTS settings (
            key    TEXT PRIMARY KEY,
            value  TEXT
        );
        """;

    /// <summary>
    /// v2 (fase 2): EPG, VOD e URL do EPG encriptado. Na v1 o epg_url ficava em texto simples,
    /// mas o url-tvg de um fornecedor pode trazer credenciais (xmltv.php?username=…).
    /// </summary>
    private const string SchemaV2 = """
        ALTER TABLE playlists ADD COLUMN epg_url_protected BLOB;
        ALTER TABLE playlists ADD COLUMN custom_epg_url_protected BLOB;
        ALTER TABLE playlists ADD COLUMN epg_loaded_utc TEXT;

        CREATE TABLE IF NOT EXISTS epg_map (
            playlist_id  INTEGER NOT NULL REFERENCES playlists(id) ON DELETE CASCADE,
            channel_key  TEXT    NOT NULL,
            epg_id       TEXT    NOT NULL,
            PRIMARY KEY (playlist_id, channel_key)
        );

        CREATE TABLE IF NOT EXISTS epg_programmes (
            playlist_id  INTEGER NOT NULL REFERENCES playlists(id) ON DELETE CASCADE,
            epg_id       TEXT    NOT NULL,
            start_utc    INTEGER NOT NULL,
            stop_utc     INTEGER NOT NULL,
            title        TEXT    NOT NULL,
            description  TEXT
        );
        CREATE INDEX IF NOT EXISTS ix_epg_programmes ON epg_programmes(playlist_id, epg_id, start_utc);

        CREATE TABLE IF NOT EXISTS vod_items (
            id           INTEGER PRIMARY KEY AUTOINCREMENT,
            playlist_id  INTEGER NOT NULL REFERENCES playlists(id) ON DELETE CASCADE,
            kind         INTEGER NOT NULL,
            item_id      INTEGER NOT NULL,
            name         TEXT    NOT NULL,
            category     TEXT    NOT NULL,
            poster_url   TEXT,
            rating       TEXT,
            year         TEXT,
            extension    TEXT,
            sort_order   INTEGER NOT NULL
        );
        CREATE INDEX IF NOT EXISTS ix_vod_items ON vod_items(playlist_id, kind, sort_order);

        CREATE TABLE IF NOT EXISTS vod_state (
            playlist_id  INTEGER NOT NULL REFERENCES playlists(id) ON DELETE CASCADE,
            kind         INTEGER NOT NULL,
            loaded_utc   TEXT    NOT NULL,
            PRIMARY KEY (playlist_id, kind)
        );
        """;

    /// <summary>
    /// v3 (fase 3): catch-up nos canais, fuso horário do servidor Xtream, progresso de
    /// visualização (continuar a ver) e grupos bloqueados (controlo parental).
    /// </summary>
    private const string SchemaV3 = """
        ALTER TABLE channels ADD COLUMN catchup_days INTEGER NOT NULL DEFAULT 0;
        ALTER TABLE channels ADD COLUMN catchup_mode TEXT;
        ALTER TABLE channels ADD COLUMN catchup_source_sealed BLOB;
        ALTER TABLE playlists ADD COLUMN server_timezone TEXT;

        CREATE TABLE IF NOT EXISTS watch_progress (
            playlist_id   INTEGER NOT NULL REFERENCES playlists(id) ON DELETE CASCADE,
            item_key      TEXT    NOT NULL,
            series_id     INTEGER,
            position_ms   INTEGER NOT NULL,
            duration_ms   INTEGER NOT NULL,
            finished      INTEGER NOT NULL DEFAULT 0,
            updated_utc   TEXT    NOT NULL,
            PRIMARY KEY (playlist_id, item_key)
        );

        CREATE TABLE IF NOT EXISTS locked_groups (
            playlist_id  INTEGER NOT NULL REFERENCES playlists(id) ON DELETE CASCADE,
            scope        INTEGER NOT NULL,
            name         TEXT    NOT NULL,
            PRIMARY KEY (playlist_id, scope, name)
        );
        """;

    /// <summary>v4 (fase 4): gravações (agendadas, em curso e concluídas).</summary>
    private const string SchemaV4 = """
        CREATE TABLE IF NOT EXISTS recordings (
            id            INTEGER PRIMARY KEY AUTOINCREMENT,
            playlist_id   INTEGER NOT NULL REFERENCES playlists(id) ON DELETE CASCADE,
            channel_key   TEXT    NOT NULL,
            channel_name  TEXT    NOT NULL,
            title         TEXT    NOT NULL,
            start_utc     TEXT    NOT NULL,
            stop_utc      TEXT    NOT NULL,
            file_path     TEXT,
            status        INTEGER NOT NULL,
            error         TEXT,
            created_utc   TEXT    NOT NULL
        );
        """;

    private readonly string _connectionString;
    private readonly CredentialProtector _protector;

    static AppDatabase()
    {
        DefaultTypeMap.MatchNamesWithUnderscores = true;
    }

    public AppDatabase(AppPaths paths, CredentialProtector protector)
        : this(new SqliteConnectionStringBuilder { DataSource = paths.DatabasePath }.ToString(), protector)
    {
    }

    public AppDatabase(string connectionString, CredentialProtector protector)
    {
        _connectionString = connectionString;
        _protector = protector;
    }

    public SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        connection.Execute("PRAGMA foreign_keys = ON;");
        return connection;
    }

    public void Initialize()
    {
        using var connection = Open();
        connection.Execute("PRAGMA journal_mode = WAL;");

        var version = connection.ExecuteScalar<long>("PRAGMA user_version;");
        if (version >= CurrentVersion) return;

        using var tx = connection.BeginTransaction();
        if (version < 1) connection.Execute(SchemaV1, transaction: tx);
        if (version < 2)
        {
            connection.Execute(SchemaV2, transaction: tx);
            EncryptLegacyEpgUrls(connection, tx);
        }
        if (version < 3) connection.Execute(SchemaV3, transaction: tx);
        if (version < 4) connection.Execute(SchemaV4, transaction: tx);
        connection.Execute($"PRAGMA user_version = {CurrentVersion};", transaction: tx);
        tx.Commit();
    }

    private void EncryptLegacyEpgUrls(SqliteConnection connection, SqliteTransaction tx)
    {
        var rows = connection.Query<(long Id, string Url)>(
            "SELECT id, epg_url FROM playlists WHERE epg_url IS NOT NULL AND epg_url <> ''", transaction: tx).AsList();
        foreach (var (id, url) in rows)
        {
            connection.Execute("UPDATE playlists SET epg_url_protected = @p, epg_url = NULL WHERE id = @id",
                new { p = _protector.Protect(url), id }, tx);
        }
    }
}
