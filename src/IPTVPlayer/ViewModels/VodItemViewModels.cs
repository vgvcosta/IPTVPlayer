using CommunityToolkit.Mvvm.ComponentModel;
using IPTVPlayer.Models;
using IPTVPlayer.Services;

namespace IPTVPlayer.ViewModels;

/// <summary>Filme ou série na lista.</summary>
public sealed partial class VodItemViewModel : PlayableItemViewModel
{
    public VodItemViewModel(VodItem model)
    {
        Model = model;
        SearchKey = TextNormalizer.ForSearch(model.Name);
        Initials = TextNormalizer.Initials(model.Name);
        MetaText = string.Join("  ·  ", new[]
        {
            model.Year,
            model.Rating is null ? null : "★ " + model.Rating,
            model.Category,
        }.Where(s => !string.IsNullOrEmpty(s)));
    }

    public VodItem Model { get; }
    public override string Name => Model.Name;
    public override string? Subtitle => MetaText;
    public override bool IsLive => false;
    public bool IsSeries => Model.Kind == VodKind.Series;
    public string Category => Model.Category;
    public string? PosterUrl => Model.PosterUrl;
    public string SearchKey { get; }
    public string Initials { get; }
    public string MetaText { get; }

    /// <summary>Chave nos favoritos ("vm:{id}" filme, "vs:{id}" série).</summary>
    public string FavoriteKey => (IsSeries ? "vs:" : "vm:") + Model.ItemId;

    [ObservableProperty]
    private bool _isFavorite;

    /// <summary>0..1 (filme a meio, ou episódio mais recente de uma série). 0 = não mostrar.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProgress))]
    private double _progress;

    [ObservableProperty]
    private bool _isWatched;

    /// <summary>Última vez que foi visto (ordem de "Continuar a ver").</summary>
    public DateTime LastWatchedUtc { get; set; }

    public bool HasProgress => Progress > 0;
}

/// <summary>Episódio de uma série.</summary>
public sealed partial class EpisodeItemViewModel : PlayableItemViewModel
{
    public EpisodeItemViewModel(SeriesEpisode episode, SeriesDetailViewModel owner)
    {
        Episode = episode;
        Owner = owner;
    }

    public SeriesEpisode Episode { get; }
    public SeriesDetailViewModel Owner { get; }
    public string ProgressKey => Services.Data.WatchProgress.EpisodeKey(Episode.Id);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProgress))]
    private double _progress;

    [ObservableProperty]
    private bool _isWatched;

    public bool HasProgress => Progress > 0;
    public override string Name => Episode.Number > 0 ? $"{Episode.Number}. {Episode.Title}" : Episode.Title;
    public override string Subtitle => $"{Owner.Name} · Temporada {Episode.Season}";
    public override bool IsLive => false;
    public int Season => Episode.Season;
    public string? Plot => Episode.Plot;
    public string? ImageUrl => Episode.ImageUrl ?? Owner.Series.PosterUrl;

    /// <summary>"00:45:12" → "45 min"; outros formatos ficam como vêm.</summary>
    public string? DurationText
    {
        get
        {
            var d = Episode.Duration;
            if (string.IsNullOrWhiteSpace(d)) return null;
            if (TimeSpan.TryParse(d, out var t) && t > TimeSpan.Zero)
                return t.TotalHours >= 1 ? $"{(int)t.TotalHours} h {t.Minutes:00} min" : $"{Math.Max(1, (int)Math.Round(t.TotalMinutes))} min";
            return d;
        }
    }
}

public sealed record SeasonItem(int Number, string Name)
{
    public override string ToString() => Name;
}

/// <summary>Página de uma série: temporadas e episódios (carregados a pedido).</summary>
public sealed partial class SeriesDetailViewModel : ObservableObject
{
    public SeriesDetailViewModel(VodItemViewModel series)
    {
        Series = series;
    }

    public VodItemViewModel Series { get; }
    public string Name => Series.Name;

    public IReadOnlyList<EpisodeItemViewModel> AllEpisodes { get; private set; } = [];

    [ObservableProperty]
    private bool _isLoading = true;

    [ObservableProperty]
    private string? _error;

    [ObservableProperty]
    private string? _plot;

    [ObservableProperty]
    private string? _metaText;

    [ObservableProperty]
    private IReadOnlyList<SeasonItem> _seasons = [];

    [ObservableProperty]
    private SeasonItem? _selectedSeason;

    [ObservableProperty]
    private IReadOnlyList<EpisodeItemViewModel> _episodes = [];

    public void Load(SeriesDetails details)
    {
        AllEpisodes = details.Episodes.Select(e => new EpisodeItemViewModel(e, this)).ToList();
        Plot = details.Plot;
        MetaText = string.Join("  ·  ", new[] { Series.Model.Year, details.Genre, Series.Model.Rating is null ? null : "★ " + Series.Model.Rating }
            .Where(s => !string.IsNullOrWhiteSpace(s)));
        Seasons = details.Seasons.Select(s => new SeasonItem(s.Number, s.Name)).ToList();
        SelectedSeason = Seasons.FirstOrDefault();
        IsLoading = false;
        if (AllEpisodes.Count == 0) Error = "Esta série ainda não tem episódios disponíveis.";
    }

    partial void OnSelectedSeasonChanged(SeasonItem? value) =>
        Episodes = value is null ? [] : AllEpisodes.Where(e => e.Season == value.Number).ToList();
}
