namespace IPTVPlayer.Models;

public enum VodKind
{
    Movie = 1,
    Series = 2,
}

/// <summary>Filme ou série do Xtream (guardado em cache na BD).</summary>
public sealed class VodItem
{
    public long Id { get; set; }
    public long PlaylistId { get; set; }
    public VodKind Kind { get; set; }

    /// <summary>stream_id (filme) ou series_id (série).</summary>
    public long ItemId { get; set; }

    public string Name { get; set; } = "";
    public string Category { get; set; } = "";
    public string? PosterUrl { get; set; }
    public string? Rating { get; set; }
    public string? Year { get; set; }

    /// <summary>container_extension do filme (mp4, mkv…). Nas séries é por episódio.</summary>
    public string? Extension { get; set; }

    public int SortOrder { get; set; }
}

public sealed record SeriesSeason(int Number, string Name);

public sealed record SeriesEpisode(
    long Id,
    int Season,
    int Number,
    string Title,
    string? Extension,
    string? Plot,
    string? ImageUrl,
    string? Duration);

public sealed record SeriesDetails(
    string? Plot,
    string? Genre,
    string? Cast,
    string? ReleaseDate,
    IReadOnlyList<SeriesSeason> Seasons,
    IReadOnlyList<SeriesEpisode> Episodes);
