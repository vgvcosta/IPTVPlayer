using System.ComponentModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IPTVPlayer.Models;
using IPTVPlayer.Services;
using IPTVPlayer.Services.Data;
using IPTVPlayer.Services.Playback;
using IPTVPlayer.Services.UI;

namespace IPTVPlayer.ViewModels;

/// <summary>
/// Navegação de filmes e séries (Xtream): categorias, lista com pesquisa, página da série,
/// favoritos e "continuar a ver" (a posição é guardada a cada 5 s enquanto se vê).
/// </summary>
public sealed partial class VodBrowserViewModel : ObservableObject
{
    private const string AllName = "Todos";
    private const string ContinueName = "Continuar a ver";
    private const string FavoritesName = "Favoritos";

    private readonly VodService _vod;
    private readonly PlayerViewModel _player;
    private readonly WatchProgressRepository _progressRepository;
    private readonly FavoritesRepository _favorites;
    private readonly DispatcherTimer _trackTimer;
    private Playlist? _playlist;
    private VodKind _kind = VodKind.Movie;
    private List<VodItemViewModel> _all = [];
    private CancellationTokenSource? _loadCts;
    private CancellationTokenSource? _filterCts;
    private string _searchText = "";
    private bool _suppressFilter;

    // Estado por playlist (carregado uma vez).
    private long _stateForPlaylistId = -1;
    private Dictionary<string, WatchProgress> _progress = new(StringComparer.Ordinal);
    private HashSet<string> _favoriteKeys = new(StringComparer.Ordinal);
    private HashSet<string> _lockedMovies = new(StringComparer.Ordinal);
    private HashSet<string> _lockedSeries = new(StringComparer.Ordinal);
    private readonly ParentalControlService _parental;
    private readonly IDialogService _dialogs;

    // O que está a tocar (para guardar a posição).
    private long _playingPlaylistId;
    private VodItemViewModel? _playingSeries;

    public VodBrowserViewModel(VodService vod, PlayerViewModel player, WatchProgressRepository progressRepository,
        FavoritesRepository favorites, ParentalControlService parental, IDialogService dialogs)
    {
        _parental = parental;
        _dialogs = dialogs;
        _vod = vod;
        _player = player;
        _progressRepository = progressRepository;
        _favorites = favorites;
        _trackTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _trackTimer.Tick += (_, _) => TrackProgress(finished: false);
        _trackTimer.Start();
        _player.PropertyChanged += OnPlayerPropertyChanged;
    }

    /// <summary>Mensagens para a barra de estado (mensagem, é erro).</summary>
    public event Action<string, bool>? Notify;

    public VodKind Kind => _kind;

    [ObservableProperty]
    private IReadOnlyList<GroupItemViewModel> _categories = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HeaderText))]
    private GroupItemViewModel? _selectedCategory;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CountText), nameof(IsEmpty))]
    private List<VodItemViewModel> _items = [];

    [ObservableProperty]
    private VodItemViewModel? _selectedItem;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    private bool _isLoading;

    [ObservableProperty]
    private string? _loadingMessage;

    [ObservableProperty]
    private double _loadingProgress;

    [ObservableProperty]
    private bool _isLoadingIndeterminate = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HeaderText), nameof(IsSeriesOpen))]
    private SeriesDetailViewModel? _openSeries;

    public bool IsSeriesOpen => OpenSeries is not null;

    public string HeaderText => SelectedCategory?.Name ?? "";

    public string CountText => _kind == VodKind.Movie
        ? (Items.Count == 1 ? "1 filme" : $"{Items.Count:N0} filmes")
        : (Items.Count == 1 ? "1 série" : $"{Items.Count:N0} séries");

    public bool IsEmpty => !IsLoading && Items.Count == 0;

    public string EmptyMessage =>
        !string.IsNullOrWhiteSpace(_searchText) ? "Nada encontrado."
        : SelectedCategory?.Kind == GroupKind.Favorites ? "Ainda não tens favoritos.\nUsa a estrela ao lado de um título."
        : SelectedCategory?.Kind == GroupKind.ContinueWatching ? "Nada a meio. Os filmes e episódios que começares a ver aparecem aqui."
        : _kind == VodKind.Movie ? "Sem filmes." : "Sem séries.";

    /// <summary>Mostra filmes ou séries da playlist (da cache, se for recente).</summary>
    public async Task ShowAsync(Playlist playlist, VodKind kind, bool forceRefresh = false)
    {
        var same = ReferenceEquals(playlist, _playlist) && kind == _kind && _all.Count > 0;
        if (same && !forceRefresh) return;

        _loadCts?.Cancel();
        var cts = new CancellationTokenSource();
        _loadCts = cts;
        _playlist = playlist;
        _kind = kind;
        if (!same) Clear();

        IsLoading = true;
        IsLoadingIndeterminate = true;
        LoadingMessage = kind == VodKind.Movie ? "A carregar filmes…" : "A carregar séries…";
        var progress = new Progress<LoadProgress>(p =>
        {
            if (cts.IsCancellationRequested) return;
            LoadingMessage = p.Message;
            IsLoadingIndeterminate = p.Fraction is null;
            LoadingProgress = p.Fraction ?? 0;
        });

        try
        {
            await EnsureStateAsync(playlist);
            var items = await _vod.GetAsync(playlist, kind, forceRefresh, progress, m => Notify?.Invoke(m, true), cts.Token);
            // Cópias: o registo de progresso altera os originais na thread de UI.
            var progressSnapshot = new Dictionary<string, WatchProgress>(_progress, StringComparer.Ordinal);
            var favoritesSnapshot = new HashSet<string>(_favoriteKeys, StringComparer.Ordinal);
            var (vms, categories) = await Task.Run(() => Build(items, progressSnapshot, favoritesSnapshot), cts.Token);
            if (cts.IsCancellationRequested) return;

            var previousCategory = SelectedCategory?.Name;
            _all = vms;
            ApplyLockFlags(categories);
            _suppressFilter = true;
            Categories = categories;
            var restored = categories.FirstOrDefault(c => c.Name == previousCategory) ?? categories[0];
            SelectedCategory = restored.IsLocked && _parental.IsActive ? categories[0] : restored;
            _suppressFilter = false;
            await RunFilterAsync(0);
            if (forceRefresh) Notify?.Invoke($"Lista atualizada: {CountText}.", false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (UserFacingException ex)
        {
            Notify?.Invoke(ex.Message, true);
        }
        catch (Exception ex)
        {
            Log.Error("Erro ao carregar VOD.", ex);
            Notify?.Invoke("Ocorreu um erro inesperado ao carregar a lista.", true);
        }
        finally
        {
            if (ReferenceEquals(_loadCts, cts)) IsLoading = false;
            OnPropertyChanged(nameof(EmptyMessage));
        }
    }

    /// <summary>Outra playlist: esquecer tudo.</summary>
    public void Reset()
    {
        _loadCts?.Cancel();
        _playlist = null;
        _stateForPlaylistId = -1;
        Clear();
        IsLoading = false;
    }

    public void SetSearch(string text)
    {
        _searchText = text;
        OnPropertyChanged(nameof(EmptyMessage));
        if (OpenSeries is not null && !string.IsNullOrWhiteSpace(text)) OpenSeries = null;
        _ = RunFilterAsync(150);
    }

    /// <summary>Clique / Enter: filme → reproduzir (retomando onde ficou); série → abrir episódios.</summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task ActivateAsync(VodItemViewModel? item)
    {
        if (item is null || _playlist is null) return;
        SelectedItem = item;
        if (item.IsSeries)
        {
            await OpenSeriesAsync(item);
            return;
        }

        try
        {
            var request = _vod.BuildMovieRequest(_playlist, item.Model);
            _playingPlaylistId = _playlist.Id;
            _playingSeries = null;
            await _player.PlayAsync(item, WithResume(request, WatchProgress.MovieKey(item.Model.ItemId)));
        }
        catch (UserFacingException ex)
        {
            _player.ShowError(item, ex.Message);
        }
    }

    [RelayCommand]
    private void CloseSeries() => OpenSeries = null;

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task PlayEpisodeAsync(EpisodeItemViewModel? episode)
    {
        if (episode is null || _playlist is null) return;
        try
        {
            var request = _vod.BuildEpisodeRequest(_playlist, episode.Episode);
            _playingPlaylistId = _playlist.Id;
            _playingSeries = episode.Owner.Series;
            await _player.PlayAsync(episode, WithResume(request, episode.ProgressKey));
        }
        catch (UserFacingException ex)
        {
            _player.ShowError(episode, ex.Message);
        }
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task ToggleFavoriteAsync(VodItemViewModel? item)
    {
        if (item is null || _playlist is null) return;
        var playlistId = _playlist.Id;
        var value = !item.IsFavorite;
        item.IsFavorite = value;
        if (value) _favoriteKeys.Add(item.FavoriteKey);
        else _favoriteKeys.Remove(item.FavoriteKey);
        UpdateVirtualCounts();

        try
        {
            await Task.Run(() =>
            {
                if (value) _favorites.Add(playlistId, item.FavoriteKey);
                else _favorites.Remove(playlistId, item.FavoriteKey);
            });
        }
        catch (Exception ex)
        {
            Log.Error("Erro ao guardar favorito.", ex);
            item.IsFavorite = !value;
            Notify?.Invoke("Não foi possível guardar o favorito.", true);
            return;
        }

        if (SelectedCategory?.Kind == GroupKind.Favorites) await RunFilterAsync(0);
    }

    /// <summary>Episódio seguinte/anterior (PgDn/PgUp e botões do player), passando de temporada.</summary>
    public Task PlayAdjacentEpisodeAsync(EpisodeItemViewModel current, int delta)
    {
        var list = current.Owner.AllEpisodes;
        var index = -1;
        for (var i = 0; i < list.Count; i++)
        {
            if (ReferenceEquals(list[i], current)) index = i;
        }
        var next = index + delta;
        if (index < 0 || next < 0 || next >= list.Count) return Task.CompletedTask;

        var episode = list[next];
        var season = current.Owner.Seasons.FirstOrDefault(s => s.Number == episode.Season);
        if (season is not null) current.Owner.SelectedSeason = season;
        return PlayEpisodeAsync(episode);
    }

    private PlayRequest WithResume(PlayRequest request, string key) =>
        _progress.TryGetValue(key, out var p) && p.CanResume
            ? request with { StartTimeMs = Math.Max(0, p.PositionMs - 3000) }
            : request;

    private async Task OpenSeriesAsync(VodItemViewModel series)
    {
        var detail = new SeriesDetailViewModel(series);
        OpenSeries = detail;
        try
        {
            var details = await _vod.GetSeriesDetailsAsync(_playlist!, series.Model.ItemId, CancellationToken.None);
            detail.Load(details);

            // Progresso por episódio; abre na temporada do episódio visto mais recentemente.
            EpisodeItemViewModel? latest = null;
            DateTime latestTime = default;
            foreach (var e in detail.AllEpisodes)
            {
                if (!_progress.TryGetValue(e.ProgressKey, out var p)) continue;
                e.IsWatched = p.Finished;
                e.Progress = p.Finished ? 1 : p.Fraction;
                if (p.UpdatedUtc > latestTime)
                {
                    latestTime = p.UpdatedUtc;
                    latest = e;
                }
            }
            if (latest is not null)
                detail.SelectedSeason = detail.Seasons.FirstOrDefault(s => s.Number == latest.Season) ?? detail.SelectedSeason;
        }
        catch (UserFacingException ex)
        {
            detail.IsLoading = false;
            detail.Error = ex.Message;
        }
        catch (Exception ex)
        {
            Log.Error("Erro ao abrir série.", ex);
            detail.IsLoading = false;
            detail.Error = "Ocorreu um erro inesperado ao carregar os episódios.";
        }
    }

    // ───────────── Progresso ("continuar a ver") ─────────────

    private void OnPlayerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PlayerViewModel.State) && _player.State == PlaybackState.Ended)
            TrackProgress(finished: true);
    }

    /// <summary>Guarda a posição do filme/episódio que está a tocar.</summary>
    private void TrackProgress(bool finished)
    {
        if (!finished && _player.State is not (PlaybackState.Playing or PlaybackState.Paused)) return;

        string key;
        long? seriesId = null;
        switch (_player.CurrentItem)
        {
            case VodItemViewModel { IsSeries: false } movie:
                key = WatchProgress.MovieKey(movie.Model.ItemId);
                break;
            case EpisodeItemViewModel episode:
                key = episode.ProgressKey;
                seriesId = episode.Owner.Series.Model.ItemId;
                break;
            default:
                return;
        }

        var length = _player.CurrentLengthMs;
        var position = finished ? length : _player.CurrentTimeMs;
        if (length <= 0 || position <= 0) return;

        var p = new WatchProgress(key, seriesId, position, length, finished || WatchProgress.IsFinished(position, length), DateTime.UtcNow);
        _progress[key] = p;
        ApplyProgress(_player.CurrentItem, p);

        var playlistId = _playingPlaylistId;
        _ = Task.Run(() =>
        {
            try
            {
                _progressRepository.Save(playlistId, p);
            }
            catch (Exception ex)
            {
                Log.Warn("Não foi possível guardar o progresso.", ex);
            }
        });
    }

    private void ApplyProgress(PlayableItemViewModel? item, WatchProgress p)
    {
        var fraction = p.Finished ? 1 : p.Fraction;
        switch (item)
        {
            case VodItemViewModel movie:
                movie.Progress = p.Finished ? 0 : fraction;
                movie.IsWatched = p.Finished;
                movie.LastWatchedUtc = p.UpdatedUtc;
                break;
            case EpisodeItemViewModel episode:
                episode.Progress = fraction;
                episode.IsWatched = p.Finished;
                if (_playingSeries is { } series)
                {
                    series.Progress = p.Finished ? 0 : fraction;
                    series.LastWatchedUtc = p.UpdatedUtc;
                }
                break;
        }
        UpdateVirtualCounts();
    }

    private async Task EnsureStateAsync(Playlist playlist)
    {
        if (_stateForPlaylistId == playlist.Id) return;
        var (progress, favorites, lockedMovies, lockedSeries) = await Task.Run(() => (
            _progressRepository.GetAll(playlist.Id),
            _favorites.GetKeys(playlist.Id),
            _parental.GetLockedGroups(playlist.Id, LockScope.Movies),
            _parental.GetLockedGroups(playlist.Id, LockScope.Series)));
        _progress = progress;
        _favoriteKeys = favorites;
        _lockedMovies = lockedMovies;
        _lockedSeries = lockedSeries;
        _stateForPlaylistId = playlist.Id;
    }

    // ───────────── Lista, categorias e filtro ─────────────

    private HashSet<string> LockedForKind => _kind == VodKind.Movie ? _lockedMovies : _lockedSeries;

    private void ApplyLockFlags(IEnumerable<GroupItemViewModel> categories)
    {
        foreach (var g in categories) g.IsLocked = g.Kind == GroupKind.Group && LockedForKind.Contains(g.Name);
    }

    /// <summary>PIN/grupos bloqueados mudaram.</summary>
    public void OnParentalChanged()
    {
        if (_playlist is { } playlist)
        {
            _lockedMovies = _parental.GetLockedGroups(playlist.Id, LockScope.Movies);
            _lockedSeries = _parental.GetLockedGroups(playlist.Id, LockScope.Series);
        }
        ApplyLockFlags(Categories);
        if (SelectedCategory is { IsLocked: true } && _parental.IsActive)
        {
            _suppressFilter = true;
            SelectedCategory = Categories.FirstOrDefault();
            _suppressFilter = false;
            OpenSeries = null;
        }
        _ = RunFilterAsync(0);
    }

    partial void OnSelectedCategoryChanged(GroupItemViewModel? oldValue, GroupItemViewModel? newValue)
    {
        var value = newValue;
        OnPropertyChanged(nameof(EmptyMessage));
        if (_suppressFilter || value is null) return;

        if (value.IsLocked && _parental.IsActive)
        {
            System.Windows.Application.Current.Dispatcher.BeginInvoke(() =>
            {
                if (_dialogs.AskPin("Controlo parental", "Esta categoria está bloqueada. Introduz o PIN para continuar.", _parental.Unlock)) return;
                _suppressFilter = true;
                SelectedCategory = oldValue is { IsLocked: false } ? oldValue : Categories.FirstOrDefault();
                _suppressFilter = false;
            });
            return;
        }

        OpenSeries = null;
        _ = RunFilterAsync(0);
    }

    private void Clear()
    {
        _filterCts?.Cancel();
        _suppressFilter = true;
        _all = [];
        Categories = [];
        SelectedCategory = null;
        Items = [];
        SelectedItem = null;
        OpenSeries = null;
        _suppressFilter = false;
    }

    private static (List<VodItemViewModel>, List<GroupItemViewModel>) Build(IReadOnlyList<VodItem> items,
        Dictionary<string, WatchProgress> progress, HashSet<string> favoriteKeys)
    {
        // Progresso das séries = episódio visto mais recentemente.
        var latestBySeries = new Dictionary<long, WatchProgress>();
        foreach (var p in progress.Values)
        {
            if (p.SeriesId is not { } sid) continue;
            if (!latestBySeries.TryGetValue(sid, out var current) || p.UpdatedUtc > current.UpdatedUtc) latestBySeries[sid] = p;
        }

        var vms = new List<VodItemViewModel>(items.Count);
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (var item in items)
        {
            var vm = new VodItemViewModel(item);
            vm.IsFavorite = favoriteKeys.Contains(vm.FavoriteKey);
            var p = item.Kind == VodKind.Movie
                ? progress.GetValueOrDefault(WatchProgress.MovieKey(item.ItemId))
                : latestBySeries.GetValueOrDefault(item.ItemId);
            if (p is not null)
            {
                vm.IsWatched = p.Finished && item.Kind == VodKind.Movie;
                vm.Progress = p.Finished ? 0 : p.Fraction;
                vm.LastWatchedUtc = p.UpdatedUtc;
            }
            vms.Add(vm);

            if (counts.TryGetValue(item.Category, out var n)) counts[item.Category] = n + 1;
            else
            {
                counts[item.Category] = 1;
                order.Add(item.Category);
            }
        }

        var groups = new List<GroupItemViewModel>(order.Count + 3)
        {
            new(GroupKind.All, AllName, vms.Count),
            new(GroupKind.ContinueWatching, ContinueName, vms.Count(IsInProgress)),
            new(GroupKind.Favorites, FavoritesName, vms.Count(v => v.IsFavorite)),
        };
        groups.AddRange(order.Select(name => new GroupItemViewModel(GroupKind.Group, name, counts[name])));
        return (vms, groups);
    }

    /// <summary>Filme a meio, ou série com algum episódio visto.</summary>
    private static bool IsInProgress(VodItemViewModel v) =>
        v.IsSeries ? v.LastWatchedUtc > DateTime.MinValue : v.Progress > 0;

    private void UpdateVirtualCounts()
    {
        foreach (var g in Categories)
        {
            if (g.Kind == GroupKind.ContinueWatching) g.Count = _all.Count(IsInProgress);
            else if (g.Kind == GroupKind.Favorites) g.Count = _all.Count(v => v.IsFavorite);
        }
    }

    private async Task RunFilterAsync(int delayMs)
    {
        _filterCts?.Cancel();
        var cts = new CancellationTokenSource();
        _filterCts = cts;
        try
        {
            if (delayMs > 0) await Task.Delay(delayMs, cts.Token);
            var source = _all;
            var category = SelectedCategory;
            var hidden = _parental.IsActive && LockedForKind.Count > 0 ? new HashSet<string>(LockedForKind, StringComparer.Ordinal) : null;
            var terms = TextNormalizer.ForSearch(_searchText)
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            var result = await Task.Run(() =>
            {
                var query = source.Where(v => category?.Kind switch
                {
                    GroupKind.Favorites => v.IsFavorite,
                    GroupKind.ContinueWatching => IsInProgress(v),
                    GroupKind.Group => string.Equals(v.Category, category.Name, StringComparison.Ordinal),
                    _ => true,
                } && (hidden is null || !hidden.Contains(v.Category))
                  && terms.All(t => v.SearchKey.Contains(t, StringComparison.Ordinal)));

                // "Continuar a ver": o mais recente primeiro.
                if (category?.Kind == GroupKind.ContinueWatching) query = query.OrderByDescending(v => v.LastWatchedUtc);
                return query.ToList();
            }, cts.Token);
            if (cts.IsCancellationRequested) return;

            var selected = SelectedItem;
            Items = result;
            SelectedItem = selected is not null && result.Contains(selected) ? selected : null;
        }
        catch (OperationCanceledException)
        {
        }
    }
}
