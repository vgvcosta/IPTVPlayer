namespace IPTVPlayer.Services;

/// <summary>Localizações dos dados da aplicação (%AppData%\IPTVPlayer).</summary>
public sealed class AppPaths
{
    /// <summary>Permite usar outra pasta de dados (testes, modo portátil).</summary>
    public const string DataDirectoryVariable = "IPTVPLAYER_DATA";

    public AppPaths(string? dataDirectory = null)
    {
        DataDirectory = dataDirectory
            ?? NullIfEmpty(Environment.GetEnvironmentVariable(DataDirectoryVariable))
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "IPTVPlayer");
        LogsDirectory = Path.Combine(DataDirectory, "logs");
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(LogsDirectory);
    }

    public string DataDirectory { get; }
    public string LogsDirectory { get; }
    public string DatabasePath => Path.Combine(DataDirectory, "iptv.db");
    public string KeyPath => Path.Combine(DataDirectory, "key.bin");

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
}
