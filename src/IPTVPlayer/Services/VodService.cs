using System.Globalization;
using System.Text.RegularExpressions;
using IPTVPlayer.Models;
using IPTVPlayer.Services.Data;
using IPTVPlayer.Services.Playback;
using IPTVPlayer.Services.Xtream;

namespace IPTVPlayer.Services;

/// <summary>Filmes e séries do Xtream Codes: listas em cache (SQLite) e episódios a pedido.</summary>
public sealed partial class VodService
{
    /// <summary>As listas de filmes/séries são atualizadas automaticamente quando têm mais do que isto.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromHours(24);

    private readonly XtreamClient _xtream;
    private readonly PlaylistService _playlists;
    private readonly VodRepository _repository;

    public VodService(XtreamClient xtream, PlaylistService playlists, VodRepository repository)
    {
        _xtream = xtream;
        _playlists = playlists;
        _repository = repository;
    }

    /// <summary>
    /// Lista da cache se existir e for recente; senão descarrega do servidor. Se o download falhar
    /// e houver cache, usa a cache (e avisa via <paramref name="onStale"/>).
    /// </summary>
    public Task<List<VodItem>> GetAsync(Playlist playlist, VodKind kind, bool forceRefresh,
        IProgress<LoadProgress>? progress, Action<string>? onStale, CancellationToken ct) =>
        Task.Run(async () =>
        {
            var (cached, loaded) = _repository.Get(playlist.Id, kind);
            var fresh = loaded is not null && DateTime.UtcNow - loaded < MaxAge;
            if (!forceRefresh && cached.Count > 0 && fresh) return cached;

            try
            {
                var items = await FetchAsync(playlist, kind, progress, ct).ConfigureAwait(false);
                progress?.Report(new LoadProgress($"A guardar {items.Count:N0} títulos…"));
                _repository.ReplaceAll(playlist.Id, kind, items, ct);
                return items;
            }
            catch (UserFacingException ex) when (cached.Count > 0 && !forceRefresh)
            {
                onStale?.Invoke($"Não foi possível atualizar (a mostrar a lista guardada). {ex.Message}");
                return cached;
            }
        }, ct);

    public Task<SeriesDetails> GetSeriesDetailsAsync(Playlist playlist, long seriesId, CancellationToken ct) =>
        Task.Run(() => _xtream.GetSeriesInfoAsync(_playlists.GetXtreamCredentials(playlist), seriesId, ct), ct);

    public PlayRequest BuildMovieRequest(Playlist playlist, VodItem movie) =>
        new(XtreamClient.BuildMovieUrl(_playlists.GetXtreamCredentials(playlist), movie.ItemId, movie.Extension),
            null, null, IsLive: false);

    public PlayRequest BuildEpisodeRequest(Playlist playlist, SeriesEpisode episode) =>
        new(XtreamClient.BuildEpisodeUrl(_playlists.GetXtreamCredentials(playlist), episode.Id, episode.Extension),
            null, null, IsLive: false);

    private async Task<List<VodItem>> FetchAsync(Playlist playlist, VodKind kind, IProgress<LoadProgress>? progress, CancellationToken ct)
    {
        var credentials = _playlists.GetXtreamCredentials(playlist);
        var what = kind == VodKind.Movie ? "filmes" : "séries";

        progress?.Report(new LoadProgress($"A obter categorias de {what}…"));
        var categories = kind == VodKind.Movie
            ? await _xtream.GetVodCategoriesAsync(credentials, ct).ConfigureAwait(false)
            : await _xtream.GetSeriesCategoriesAsync(credentials, ct).ConfigureAwait(false);

        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        var order = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var c in categories)
        {
            if (c.CategoryId is null || names.ContainsKey(c.CategoryId)) continue;
            names[c.CategoryId] = string.IsNullOrWhiteSpace(c.CategoryName) ? "Sem categoria" : c.CategoryName.Trim();
            order[c.CategoryId] = order.Count;
        }

        progress?.Report(new LoadProgress($"A obter a lista de {what}…"));
        var raw = new List<(VodItem Item, string? CategoryId)>();
        if (kind == VodKind.Movie)
        {
            foreach (var m in await _xtream.GetVodStreamsAsync(credentials, ct).ConfigureAwait(false))
            {
                if (m.StreamId <= 0) continue;
                raw.Add((new VodItem
                {
                    Kind = kind,
                    ItemId = m.StreamId,
                    Name = CleanName(m.Name, m.StreamId),
                    PosterUrl = NullIfBlank(m.StreamIcon),
                    Rating = FormatRating(m.Rating),
                    Year = YearOf(m.Year, m.ReleaseDate, m.Name),
                    Extension = NullIfBlank(m.ContainerExtension),
                }, m.CategoryId));
            }
        }
        else
        {
            foreach (var s in await _xtream.GetSeriesAsync(credentials, ct).ConfigureAwait(false))
            {
                if (s.SeriesId <= 0) continue;
                raw.Add((new VodItem
                {
                    Kind = kind,
                    ItemId = s.SeriesId,
                    Name = CleanName(s.Name, s.SeriesId),
                    PosterUrl = NullIfBlank(s.Cover),
                    Rating = FormatRating(s.Rating),
                    Year = YearOf(s.Year, s.ReleaseDate, s.Name),
                }, s.CategoryId));
            }
        }

        // Ordem das categorias do servidor e, dentro de cada uma, a ordem original.
        var items = raw
            .Select((x, index) => (x.Item, x.CategoryId, Index: index))
            .OrderBy(x => x.CategoryId is not null && order.TryGetValue(x.CategoryId, out var o) ? o : int.MaxValue)
            .ThenBy(x => x.Index)
            .Select((x, i) =>
            {
                x.Item.Category = x.CategoryId is not null && names.TryGetValue(x.CategoryId, out var n) ? n : "Sem categoria";
                x.Item.SortOrder = i;
                return x.Item;
            })
            .ToList();

        if (items.Count == 0)
            throw new UserFacingException(kind == VodKind.Movie ? "O servidor não tem filmes nesta conta." : "O servidor não tem séries nesta conta.");
        return items;
    }

    private static string CleanName(string? name, long id) =>
        string.IsNullOrWhiteSpace(name) ? $"#{id}" : name.Trim();

    private static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    /// <summary>"7.4" / "7,4" / "0" → "7.4" (ou null se não houver).</summary>
    private static string? FormatRating(string? rating)
    {
        if (string.IsNullOrWhiteSpace(rating)) return null;
        return double.TryParse(rating.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var r) && r > 0
            ? r.ToString("0.0", CultureInfo.InvariantCulture)
            : null;
    }

    /// <summary>Ano a partir do campo "year", da data de estreia ou de "(2021)" no nome.</summary>
    internal static string? YearOf(string? year, string? releaseDate, string? name)
    {
        foreach (var candidate in new[] { year, releaseDate, name })
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            var m = YearPattern().Match(candidate);
            if (m.Success) return m.Value;
        }
        return null;
    }

    [GeneratedRegex(@"\b(19[2-9]\d|20[0-4]\d)\b")]
    private static partial Regex YearPattern();
}
