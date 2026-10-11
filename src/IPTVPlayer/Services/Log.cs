namespace IPTVPlayer.Services;

/// <summary>
/// Log simples em ficheiro (%AppData%\IPTVPlayer\logs). Nunca registar URLs de streams
/// nem credenciais: só mensagens e exceções.
/// </summary>
public static class Log
{
    private static readonly object Sync = new();
    private static string? _directory;

    public static void Initialize(string directory, int keepDays = 14)
    {
        _directory = directory;
        try
        {
            foreach (var file in Directory.EnumerateFiles(directory, "app-*.log"))
            {
                if (File.GetLastWriteTimeUtc(file) < DateTime.UtcNow.AddDays(-keepDays))
                    File.Delete(file);
            }
        }
        catch
        {
            // Limpeza de logs antigos é opcional.
        }
    }

    public static void Info(string message) => Write("INFO", message, null);
    public static void Warn(string message, Exception? ex = null) => Write("WARN", message, ex);
    public static void Error(string message, Exception? ex = null) => Write("ERROR", message, ex);

    private static void Write(string level, string message, Exception? ex)
    {
        if (_directory is null) return;
        try
        {
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}";
            if (ex is not null) line += Environment.NewLine + ex;
            lock (Sync)
            {
                File.AppendAllText(Path.Combine(_directory, $"app-{DateTime.Now:yyyyMMdd}.log"),
                    line + Environment.NewLine);
            }
        }
        catch
        {
            // O log nunca pode derrubar a aplicação.
        }
    }
}
