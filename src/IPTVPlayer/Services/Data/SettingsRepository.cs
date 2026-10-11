using System.Globalization;
using Dapper;

namespace IPTVPlayer.Services.Data;

/// <summary>Definições chave/valor (última playlist, último canal, volume…).</summary>
public sealed class SettingsRepository
{
    public const string LastPlaylistId = "last_playlist_id";
    public const string Volume = "volume";
    public const string NetworkCachingMs = "network_caching_ms";
    public const string HardwareDecoding = "hw_decoding";
    public const string PreferredAudio = "pref_audio_lang";
    public const string PreferredSubtitle = "pref_sub_lang";
    public const string GamepadEnabled = "gamepad_enabled";
    public static string LastChannelKey(long playlistId) => $"last_channel.{playlistId}";
    public static string LastGroup(long playlistId) => $"last_group.{playlistId}";

    private readonly AppDatabase _db;

    public SettingsRepository(AppDatabase db)
    {
        _db = db;
    }

    public string? Get(string key)
    {
        using var c = _db.Open();
        return c.ExecuteScalar<string?>("SELECT value FROM settings WHERE key = @key", new { key });
    }

    public long? GetInt64(string key) =>
        long.TryParse(Get(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;

    public void Set(string key, string? value)
    {
        using var c = _db.Open();
        if (value is null)
        {
            c.Execute("DELETE FROM settings WHERE key = @key", new { key });
            return;
        }
        c.Execute("""
            INSERT INTO settings (key, value) VALUES (@key, @value)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value
            """, new { key, value });
    }

    public void Set(string key, long value) => Set(key, value.ToString(CultureInfo.InvariantCulture));
}
