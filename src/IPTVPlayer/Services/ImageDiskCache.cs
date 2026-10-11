using System.Security.Cryptography;
using System.Text;

namespace IPTVPlayer.Services;

/// <summary>
/// Cache em disco de logótipos e capas (%AppData%\IPTVPlayer\cache\images), para não voltar a
/// descarregar milhares de imagens em cada arranque. Ficheiros com mais de <see cref="MaxAge"/>
/// são descarregados de novo; o tamanho total é limitado em <see cref="TrimAsync"/>.
/// </summary>
public sealed class ImageDiskCache
{
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(14);
    public const long MaxTotalBytes = 300L * 1024 * 1024;
    public const long TrimTargetBytes = 200L * 1024 * 1024;

    private readonly string _directory;

    public ImageDiskCache(AppPaths paths) : this(Path.Combine(paths.DataDirectory, "cache", "images"))
    {
    }

    internal ImageDiskCache(string directory)
    {
        _directory = directory;
        Directory.CreateDirectory(_directory);
    }

    public byte[]? TryRead(string url)
    {
        try
        {
            var path = PathFor(url);
            var info = new FileInfo(path);
            if (!info.Exists || DateTime.UtcNow - info.LastWriteTimeUtc > MaxAge) return null;
            return File.ReadAllBytes(path);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Write(string url, byte[] data)
    {
        try
        {
            // Escrever para um temporário e mover: um ficheiro a meio nunca é lido como imagem válida.
            var path = PathFor(url);
            var temp = path + "." + Environment.CurrentManagedThreadId + ".tmp";
            File.WriteAllBytes(temp, data);
            File.Move(temp, path, overwrite: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    public long GetSizeBytes()
    {
        try
        {
            return new DirectoryInfo(_directory).GetFiles().Sum(f => f.Length);
        }
        catch (IOException)
        {
            return 0;
        }
    }

    public Task ClearAsync() => Task.Run(() =>
    {
        foreach (var file in new DirectoryInfo(_directory).GetFiles())
        {
            try
            {
                file.Delete();
            }
            catch (IOException)
            {
                // Em uso: fica para a próxima.
            }
        }
    });

    /// <summary>Se a cache passar de 300 MB, apaga os ficheiros mais antigos até ficar com 200 MB.</summary>
    public Task TrimAsync() => Task.Run(() =>
    {
        try
        {
            var files = new DirectoryInfo(_directory).GetFiles();
            var total = files.Sum(f => f.Length);
            if (total <= MaxTotalBytes) return;

            foreach (var file in files.OrderBy(f => f.LastWriteTimeUtc))
            {
                if (total <= TrimTargetBytes) break;
                total -= file.Length;
                file.Delete();
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Não foi possível limpar a cache de imagens.", ex);
        }
    });

    private string PathFor(string url) =>
        Path.Combine(_directory, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url)), 0, 20) + ".img");
}
