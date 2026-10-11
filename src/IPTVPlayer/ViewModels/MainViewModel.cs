using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IPTVPlayer.Models;
using IPTVPlayer.Services;
using System.Windows.Threading;
using IPTVPlayer.Services.Data;
using IPTVPlayer.Services.Epg;
using IPTVPlayer.Services.Playback;
using IPTVPlayer.Services.UI;

namespace IPTVPlayer.ViewModels;

public enum BrowseMode
{
    Live,
    Movies,
    Series,
}

public sealed partial class MainViewModel : ObservableObject
{
    private const string AllChannelsName = "Todos os canais";
    private const string FavoritesName = "Favoritos";
    private static readonly TimeSpan AutoRefreshAge = TimeSpan.FromHours(24);

    private readonly PlaylistService _playlistService;
    private readonly FavoritesRepository _favorites;
    private readonly SettingsRepository _settings;
    private readonly IDialogService _dialogs;
    private readonly EpgService _epgService;
    private readonly ParentalControlService _parental;
    private readonly RecordingService _recordings;
    private readonly LibVlcHost _vlcHost;
    private HashSet<string> _lockedGroups = new(StringComparer.Ordinal);
    private readonly DispatcherTimer _epgTimer;
    private EpgGuide _epg = EpgGuide.Empty;

    private Playlist? _loadedPlaylist;
    private List<ChannelItemViewModel> _allChannels = [];
    /// <summary>Lista onde o canal atual foi escolhido; usada para canal seguinte/anterior.</summary>
    private List<ChannelItemViewModel> _zapList = [];
    private CancellationTokenSource? _loadCts;
    private CancellationTokenSource? _filterCts;
    private CancellationTokenSource? _notificationCts;
    private bool _suppressFilter;
    private bool _autoPlayOnNextLoad;

    public MainViewModel(PlaylistService playlistService, FavoritesRepository favorites, SettingsRepository settings,
        IDialogService dialogs, PlayerViewModel player, EpgService epgService, VodBrowserViewModel vod,
        ParentalControlService parental, RecordingService recordings, LibVlcHost vlcHost, UpdateViewModel updates)
    {
        Updates = updates;
        _vlcHost = vlcHost;
        _recordings = recordings;
        recordings.Changed += (_, _) => System.Windows.Application.Current?.Dispatcher.BeginInvoke(UpdateRecordingState);
        _parental = parental;
        parental.Changed += (_, _) => OnParentalChanged();
        Vod = vod;
        vod.Notify += (message, isError) => ShowNotification(message, isError);
        player.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(PlayerViewModel.CurrentItem)) return;
            MarkPlaying(player.CurrentItem);
            UpdateCatchupState();
            UpdateRecordingState();
        };
        _playlistService = playlistService;
        _favorites = favorites;
        _settings = settings;
        _dialogs = dialogs;
        _epgService = epgService;
        Player = player;

        // "Agora / a seguir" avança sozinho.
        _epgTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _epgTimer.Tick += (_, _) => UpdateNowNext();
        _epgTimer.Start();
    }

    public VodBrowserViewModel Vod { get; }

    private PlayableItemViewModel? _playingItem;

    /// <summary>Destaca nas listas o item que está a tocar (canal, filme ou episódio).</summary>
    private void MarkPlaying(PlayableItemViewModel? item)
    {
        if (ReferenceEquals(_playingItem, item)) return;
        if (_playingItem is not null) _playingItem.IsPlaying = false;
        _playingItem = item;
        if (item is not null) item.IsPlaying = true;
    }

    // ───────────────────────────── Modo: Direto / Filmes / Séries ─────────────────────────────

    /// <summary>Filmes e séries só existem em playlists Xtream Codes.</summary>
    [ObservableProperty]
    private bool _canBrowseVod;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLiveMode), nameof(IsMoviesMode), nameof(IsSeriesMode), nameof(SearchPlaceholder))]
    private BrowseMode _mode = BrowseMode.Live;

    public bool IsLiveMode
    {
        get => Mode == BrowseMode.Live;
        set { if (value) Mode = BrowseMode.Live; }
    }

    public bool IsMoviesMode
    {
        get => Mode == BrowseMode.Movies;
        set { if (value) Mode = BrowseMode.Movies; }
    }

    public bool IsSeriesMode
    {
        get => Mode == BrowseMode.Series;
        set { if (value) Mode = BrowseMode.Series; }
    }

    public string SearchPlaceholder => Mode switch
    {
        BrowseMode.Movies => "Pesquisar filmes…  (Ctrl+F)",
        BrowseMode.Series => "Pesquisar séries…  (Ctrl+F)",
        _ => "Pesquisar canais…  (Ctrl+F)",
    };

    partial void OnModeChanged(BrowseMode value)
    {
        _suppressFilter = true;
        SearchText = "";
        _suppressFilter = false;
        if (value == BrowseMode.Live)
        {
            _ = RunFilterAsync(0);
            return;
        }
        Vod.SetSearch("");
        if (_loadedPlaylist is { } playlist)
            _ = Vod.ShowAsync(playlist, value == BrowseMode.Movies ? VodKind.Movie : VodKind.Series);
    }

    /// <summary>A playlist carregada tem EPG (para ativar o botão do Guia TV).</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenGuideCommand))]
    private bool _hasEpg;

    public PlayerViewModel Player { get; }

    public ObservableCollection<PlaylistItemViewModel> Playlists { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsChannelListEmpty), nameof(EmptyListMessage))]
    private bool _hasPlaylists;

    [ObservableProperty]
    private PlaylistItemViewModel? _selectedPlaylist;

    [ObservableProperty]
    private IReadOnlyList<GroupItemViewModel> _groups = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EmptyListMessage))]
    private GroupItemViewModel? _selectedGroup;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EmptyListMessage))]
    private string _searchText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ChannelCountText), nameof(IsChannelListEmpty), nameof(EmptyListMessage))]
    private List<ChannelItemViewModel> _channels = [];

    [ObservableProperty]
    private ChannelItemViewModel? _selectedChannel;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EmptyListMessage))]
    private bool _isBusy;

    [ObservableProperty]
    private string? _busyMessage;

    [ObservableProperty]
    private double _busyProgress;

    [ObservableProperty]
    private bool _isBusyIndeterminate = true;

    [ObservableProperty]
    private string? _notification;

    [ObservableProperty]
    private bool _isNotificationError;

    [ObservableProperty]
    private bool _isFullscreen;

    public string ChannelCountText => Channels.Count == 1 ? "1 canal" : $"{Channels.Count:N0} canais";

    public bool IsChannelListEmpty => HasPlaylists && Channels.Count == 0;

    public string EmptyListMessage =>
        IsBusy ? "A carregar canais…"
        : !string.IsNullOrWhiteSpace(SearchText) ? "Nenhum canal encontrado."
        : SelectedGroup?.Kind == GroupKind.Favorites ? "Ainda não tens favoritos.\nUsa a estrela ao lado de um canal para o adicionar."
        : "Sem canais.";

    public UpdateViewModel Updates { get; }

    // ───────────────────────────── Arranque e estado ─────────────────────────────

    public async Task InitializeAsync()
    {
        try
        {
            var volume = await Task.Run(() => _settings.GetInt64(SettingsRepository.Volume));
            Player.Volume = (int)Math.Clamp(volume ?? 80, 0, 100);

            var playlists = await _playlistService.GetPlaylistsAsync();
            foreach (var p in playlists) Playlists.Add(new PlaylistItemViewModel(p));
            HasPlaylists = Playlists.Count > 0;
            if (!HasPlaylists) return;

            var lastId = await Task.Run(() => _settings.GetInt64(SettingsRepository.LastPlaylistId));
            _autoPlayOnNextLoad = true;
            SelectedPlaylist = Playlists.FirstOrDefault(p => p.Model.Id == lastId) ?? Playlists[0];
        }
        catch (Exception ex)
        {
            Log.Error("Erro ao iniciar.", ex);
            ShowNotification("Não foi possível ler os dados guardados.", isError: true);
        }
    }

    /// <summary>Chamado ao fechar a janela.</summary>
    public void SaveState()
    {
        try
        {
            _settings.Set(SettingsRepository.Volume, Player.Volume);
        }
        catch (Exception ex)
        {
            Log.Warn("Não foi possível guardar o estado.", ex);
        }
    }

    // ───────────────────────────── Playlists ─────────────────────────────

    partial void OnSelectedPlaylistChanged(PlaylistItemViewModel? value)
    {
        if (value is null || ReferenceEquals(value.Model, _loadedPlaylist)) return;
        var autoPlay = _autoPlayOnNextLoad;
        _autoPlayOnNextLoad = false;
        _ = LoadPlaylistAsync(value.Model, autoPlay);
    }

    private async Task LoadPlaylistAsync(Playlist playlist, bool autoPlay)
    {
        var ct = BeginOperation();
        if (Player.CurrentChannel is not null) await Player.StopAsync();

        _loadedPlaylist = playlist;
        ClearChannels();
        Vod.Reset();
        Mode = BrowseMode.Live;
        CanBrowseVod = playlist.Kind == PlaylistKind.Xtream;
        SetBusy("A carregar canais…");

        var refreshInBackground = false;
        try
        {
            IReadOnlyList<Channel> channels = await _playlistService.LoadCachedChannelsAsync(playlist.Id);
            ct.ThrowIfCancellationRequested();

            if (channels.Count == 0)
            {
                var data = await _playlistService.RefreshAsync(playlist, CreateProgress(ct), ct);
                channels = data.Channels;
                UpdatePlaylistItem(playlist);
            }
            else
            {
                refreshInBackground = playlist.LastLoadedUtc is null ||
                                      DateTime.UtcNow - playlist.LastLoadedUtc > AutoRefreshAge;
            }

            var (groupKey, channelKey, locked) = await Task.Run(() => (
                _settings.Get(SettingsRepository.LastGroup(playlist.Id)),
                _settings.Get(SettingsRepository.LastChannelKey(playlist.Id)),
                _parental.GetLockedGroups(playlist.Id, LockScope.Live)), ct);
            _lockedGroups = locked;

            await ShowChannelsAsync(playlist, channels, ct, groupKey, channelKey, autoPlay);
            SaveSetting(SettingsRepository.LastPlaylistId, playlist.Id.ToString());
            await LoadEpgAsync(playlist);
        }
        catch (OperationCanceledException)
        {
            refreshInBackground = false;
        }
        catch (UserFacingException ex)
        {
            refreshInBackground = false;
            ShowNotification(ex.Message, isError: true);
        }
        catch (Exception ex)
        {
            refreshInBackground = false;
            Log.Error("Erro ao carregar a playlist.", ex);
            ShowNotification("Ocorreu um erro inesperado ao carregar a playlist.", isError: true);
        }
        finally
        {
            EndBusy(ct);
        }

        if (!ReferenceEquals(_loadedPlaylist, playlist) || IsBusy || _allChannels.Count == 0) return;
        if (refreshInBackground)
            await RefreshAsync(silent: true);
        else if (_epgService.IsStale(playlist) && _epgService.HasSource(playlist))
            await ImportEpgAsync(playlist, silent: true);
    }

    [RelayCommand]
    private void AddPlaylist()
    {
        var data = _dialogs.ShowAddPlaylist();
        if (data is null) return;

        var item = new PlaylistItemViewModel(data.Playlist);
        Playlists.Add(item);
        HasPlaylists = true;
        ShowNotification($"Playlist \"{data.Playlist.Name}\" adicionada com {data.Channels.Count:N0} canais.");
        SelectedPlaylist = item;
    }

    [RelayCommand]
    private async Task EditPlaylistAsync()
    {
        var item = SelectedPlaylist;
        if (item is null) return;

        PlaylistEditResult? result;
        try
        {
            result = _dialogs.ShowEditPlaylist(item.Model);
        }
        catch (UserFacingException ex)
        {
            ShowNotification(ex.Message, isError: true);
            return;
        }
        if (result is null) return;

        item.Refresh();
        ShowNotification($"Playlist \"{item.Name}\" guardada.");
        if (!ReferenceEquals(_loadedPlaylist, item.Model)) return;

        if (result.NewChannels is { } channels)
        {
            // Origem/credenciais mudaram: a cache de filmes/séries em memória já não é válida.
            Vod.Reset();
            Mode = BrowseMode.Live;
            var ct = BeginOperation();
            try
            {
                await ShowChannelsAsync(item.Model, channels, ct, GroupKeyOf(SelectedGroup), SelectedChannel?.Key, autoPlay: false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }

        if (result.EpgSourceChanged)
        {
            _epg = EpgGuide.Empty;
            ApplyEpg();
            if (_epgService.HasSource(item.Model)) await ImportEpgAsync(item.Model, silent: false);
        }
    }

    [RelayCommand]
    private Task RefreshPlaylistAsync()
    {
        if (Mode != BrowseMode.Live && _loadedPlaylist is { } playlist)
            return Vod.ShowAsync(playlist, Mode == BrowseMode.Movies ? VodKind.Movie : VodKind.Series, forceRefresh: true);
        return RefreshAsync(silent: false);
    }

    private async Task RefreshAsync(bool silent)
    {
        var playlist = _loadedPlaylist;
        if (playlist is null) return;
        var refreshEpg = false;

        var ct = BeginOperation();
        SetBusy("A atualizar playlist…");
        try
        {
            var data = await _playlistService.RefreshAsync(playlist, CreateProgress(ct), ct);
            await ShowChannelsAsync(playlist, data.Channels, ct, GroupKeyOf(SelectedGroup), SelectedChannel?.Key, autoPlay: false);
            UpdatePlaylistItem(playlist);
            ApplyEpg();
            if (!silent) ShowNotification($"Playlist atualizada: {data.Channels.Count:N0} canais.");
            refreshEpg = !silent || _epgService.IsStale(playlist);
        }
        catch (OperationCanceledException)
        {
        }
        catch (UserFacingException ex)
        {
            ShowNotification(silent
                ? $"Não foi possível atualizar a playlist (a usar a versão guardada). {ex.Message}"
                : ex.Message, isError: true);
        }
        catch (Exception ex)
        {
            Log.Error("Erro ao atualizar a playlist.", ex);
            ShowNotification("Ocorreu um erro inesperado ao atualizar a playlist.", isError: true);
        }
        finally
        {
            EndBusy(ct);
        }

        if (refreshEpg && ReferenceEquals(_loadedPlaylist, playlist) && _epgService.HasSource(playlist))
            await ImportEpgAsync(playlist, silent);
    }

    [RelayCommand]
    private async Task RemovePlaylistAsync()
    {
        var item = SelectedPlaylist;
        if (item is null) return;
        if (!_dialogs.Confirm("Remover playlist",
                $"Remover a playlist \"{item.Name}\"?\nOs favoritos desta playlist também serão apagados."))
            return;

        _loadCts?.Cancel();
        if (ReferenceEquals(_loadedPlaylist, item.Model))
        {
            await Player.StopAsync();
            _loadedPlaylist = null;
            ClearChannels();
        }

        try
        {
            await _playlistService.DeleteAsync(item.Model.Id);
        }
        catch (Exception ex)
        {
            Log.Error("Erro ao remover playlist.", ex);
            ShowNotification("Não foi possível remover a playlist.", isError: true);
            return;
        }

        Playlists.Remove(item);
        HasPlaylists = Playlists.Count > 0;
        SelectedPlaylist = Playlists.FirstOrDefault();
    }

    [RelayCommand]
    private void CancelOperation() => _loadCts?.Cancel();

    private void UpdatePlaylistItem(Playlist playlist)
    {
        var item = Playlists.FirstOrDefault(p => ReferenceEquals(p.Model, playlist));
        if (item is not null) item.ChannelCount = playlist.ChannelCount;
    }

    // ───────────────────────────── EPG ─────────────────────────────

    private async Task LoadEpgAsync(Playlist playlist)
    {
        try
        {
            var guide = await _epgService.LoadAsync(playlist.Id);
            if (!ReferenceEquals(_loadedPlaylist, playlist)) return;
            _epg = guide;
            ApplyEpg();
        }
        catch (Exception ex)
        {
            Log.Error("Erro ao ler o EPG guardado.", ex);
        }
    }

    [RelayCommand]
    private Task RefreshEpgAsync() => _loadedPlaylist is { } p ? ImportEpgAsync(p, silent: false) : Task.CompletedTask;

    private async Task ImportEpgAsync(Playlist playlist, bool silent)
    {
        var ct = BeginOperation();
        SetBusy("A carregar EPG…");
        try
        {
            var channels = _allChannels.Select(c => c.Model).ToList();
            var result = await _epgService.ImportAsync(playlist, channels, CreateProgress(ct), ct);
            var guide = await _epgService.LoadAsync(playlist.Id);
            if (!ReferenceEquals(_loadedPlaylist, playlist)) return;
            _epg = guide;
            ApplyEpg();

            var message = result.MatchedChannels == 0
                ? "EPG carregado, mas nenhum canal corresponde (verifica os tvg-id da playlist)."
                : $"EPG carregado: {result.MatchedChannels:N0} canais com programação.";
            if (!silent || result.MatchedChannels == 0) ShowNotification(message, isError: result.MatchedChannels == 0);
        }
        catch (OperationCanceledException)
        {
        }
        catch (UserFacingException ex)
        {
            ShowNotification(ex.Message, isError: true);
        }
        catch (Exception ex)
        {
            Log.Error("Erro ao carregar o EPG.", ex);
            ShowNotification("Ocorreu um erro inesperado ao carregar o EPG.", isError: true);
        }
        finally
        {
            EndBusy(ct);
        }
    }

    /// <summary>Liga cada canal ao seu id EPG e atualiza "agora / a seguir".</summary>
    private void ApplyEpg()
    {
        foreach (var c in _allChannels)
            c.EpgId = _epg.KeyToEpgId.TryGetValue(c.Key, out var id) ? id : null;
        HasEpg = _epg.ChannelCount > 0;
        UpdateNowNext();
    }

    private void UpdateNowNext()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        foreach (var c in _allChannels)
        {
            if (c.EpgId is null)
            {
                c.NowTitle = null;
                c.EpgToolTip = null;
                continue;
            }

            var (current, next) = _epg.GetNowNext(c.EpgId, now);
            c.NowTitle = current?.Title;
            c.NowProgress = current is null ? 0 : Math.Clamp((now - current.Start) / (double)(current.Stop - current.Start), 0, 1);
            c.EpgToolTip = BuildToolTip(current, next);
        }
        UpdateCatchupState();
    }

    private static string? BuildToolTip(EpgProgramme? current, EpgProgramme? next)
    {
        if (current is null && next is null) return null;
        var lines = new List<string>(2);
        if (current is not null) lines.Add($"Agora ({current.StartLocal:HH:mm}–{current.StopLocal:HH:mm}): {current.Title}");
        if (next is not null) lines.Add($"A seguir ({next.StartLocal:HH:mm}): {next.Title}");
        return string.Join("\n", lines);
    }

    [RelayCommand(CanExecute = nameof(HasEpg))]
    private async Task OpenGuideAsync()
    {
        if (_loadedPlaylist is not { } playlist) return;
        var guide = new GuideViewModel(_epg, Channels, _epgService, playlist.Id, SelectedGroup?.Name ?? "")
        {
            ScheduleRecording = ScheduleFromGuide,
        };
        var result = _dialogs.ShowGuide(guide);
        if (result is null) return;
        if (result.Catchup is { } programme) await PlayCatchupAsync(result.Channel, programme);
        else await PlayChannelAsync(result.Channel);
    }

    // ───────────────────────────── Controlo parental e definições ─────────────────────────────

    /// <summary>Há PIN e a sessão está desbloqueada (mostra o botão "Bloquear").</summary>
    public bool CanLockNow => _parental.HasPin && _parental.IsUnlocked;

    private bool IsHidden(ChannelItemViewModel channel) => _parental.IsActive && _lockedGroups.Contains(channel.GroupTitle);

    /// <summary>Pede o PIN; true se ficou desbloqueado (ou se não há PIN).</summary>
    private bool RequestUnlock(string reason)
    {
        if (!_parental.IsActive) return true;
        return _dialogs.AskPin("Controlo parental", reason + " Introduz o PIN para continuar.", _parental.Unlock);
    }

    private void OnParentalChanged()
    {
        OnPropertyChanged(nameof(CanLockNow));
        if (_loadedPlaylist is { } playlist)
        {
            _lockedGroups = _parental.GetLockedGroups(playlist.Id, LockScope.Live);
            foreach (var g in Groups) g.IsLocked = g.Kind == GroupKind.Group && _lockedGroups.Contains(g.Name);
            if (SelectedGroup is { IsLocked: true } && _parental.IsActive)
            {
                _suppressFilter = true;
                SelectedGroup = Groups.FirstOrDefault();
                _suppressFilter = false;
            }
        }
        _ = RunFilterAsync(0);
        Vod.OnParentalChanged();

        // A tocar algo que acabou de ficar bloqueado: parar.
        if (Player.CurrentChannel is { } playing && IsHidden(playing)) _ = Player.StopAsync();
    }

    [RelayCommand]
    private void LockNow()
    {
        _parental.Lock();
        ShowNotification("Conteúdo bloqueado. Para o voltar a ver é preciso o PIN.");
    }

    [RelayCommand]
    private void OpenSettings()
    {
        // As definições permitem desbloquear grupos: pedir o PIN primeiro.
        if (!RequestUnlock("As definições estão protegidas.")) return;
        _dialogs.ShowSettings(_loadedPlaylist);
    }

    // ───────────────────────────── Gravações ─────────────────────────────

    /// <summary>O canal em direto que está a tocar está a ser gravado.</summary>
    [ObservableProperty]
    private bool _isRecordingCurrent;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RecordingsButtonText))]
    private int _activeRecordings;

    public string RecordingsButtonText => ActiveRecordings > 0 ? $"Gravações ({ActiveRecordings} a gravar)" : "Gravações";

    private bool _warnedAboutConnections;

    private void UpdateRecordingState()
    {
        ActiveRecordings = _recordings.ActiveCount;
        IsRecordingCurrent = Player.CurrentChannel is { } c && _loadedPlaylist is { } p && _recordings.IsRecording(p.Id, c.Key);
    }

    /// <summary>● no player: grava o canal até ao fim do programa (ou 2 h sem EPG); de novo = parar.</summary>
    [RelayCommand]
    private async Task ToggleRecordAsync()
    {
        if (Player.CurrentChannel is not { } channel || _loadedPlaylist is not { } playlist)
        {
            ShowNotification("Escolhe um canal em direto para gravar.", isError: true);
            return;
        }

        if (_recordings.FindActive(playlist.Id, channel.Key) is { } activeId)
        {
            await Task.Run(() => _recordings.Stop(activeId));
            ShowNotification("Gravação parada. Está em \"Gravações\".");
            return;
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var (programme, _) = channel.EpgId is { } epgId ? _epg.GetNowNext(epgId, now) : (null, null);
        var title = programme?.Title ?? channel.Name;
        var stop = programme is not null && programme.Stop - now > 120
            ? DateTimeOffset.FromUnixTimeSeconds(programme.Stop).UtcDateTime
            : DateTime.UtcNow.AddHours(2);

        try
        {
            await Task.Run(() => _recordings.Schedule(playlist, channel.Model, title, DateTime.UtcNow, stop));
            var warning = _warnedAboutConnections ? "" : " Nota: a gravação usa mais uma ligação ao fornecedor.";
            _warnedAboutConnections = true;
            ShowNotification($"A gravar \"{title}\" até às {stop.ToLocalTime():HH:mm}.{warning}");
        }
        catch (UserFacingException ex)
        {
            ShowNotification(ex.Message, isError: true);
        }
    }

    [RelayCommand]
    private async Task OpenRecordingsAsync()
    {
        var recording = _dialogs.ShowRecordings();
        if (recording?.FilePath is not { } path) return;
        if (!File.Exists(path))
        {
            ShowNotification("O ficheiro desta gravação já não existe.", isError: true);
            return;
        }
        await Player.PlayAsync(new RecordedItemViewModel(recording), new PlayRequest(path, null, null, IsLive: false));
    }

    /// <summary>Gravar a partir do Guia TV (programa atual ou futuro).</summary>
    private string ScheduleFromGuide(ChannelItemViewModel channel, EpgProgramme programme)
    {
        if (_loadedPlaylist is not { } playlist) return "Sem playlist.";
        try
        {
            var start = DateTimeOffset.FromUnixTimeSeconds(programme.Start).UtcDateTime;
            var stop = DateTimeOffset.FromUnixTimeSeconds(programme.Stop).UtcDateTime;
            if (start < DateTime.UtcNow) start = DateTime.UtcNow;
            _recordings.Schedule(playlist, channel.Model, programme.Title, start, stop);
            return start <= DateTime.UtcNow.AddSeconds(5)
                ? $"A gravar \"{programme.Title}\" até às {programme.StopLocal:HH:mm}."
                : $"Gravação agendada: \"{programme.Title}\" ({programme.StartLocal:dd/MM HH:mm}). A app tem de estar aberta.";
        }
        catch (UserFacingException ex)
        {
            return ex.Message;
        }
    }

    // ───────────────────────────── Multi-ecrã ─────────────────────────────

    /// <summary>Até 4 canais ao mesmo tempo (tecla V). O player principal para, para libertar a ligação.</summary>
    [RelayCommand]
    private async Task OpenMultiViewAsync()
    {
        if (_loadedPlaylist is not { } playlist || _allChannels.Count == 0)
        {
            ShowNotification("Abre uma playlist com canais para usar o multi-ecrã.", isError: true);
            return;
        }

        var current = Player.CurrentChannel;
        if (Player.CurrentItem is not null) await Player.StopAsync();

        var channels = _allChannels.Where(c => !IsHidden(c)).ToList();
        var caching = (int)Math.Clamp(_settings.GetInt64(SettingsRepository.NetworkCachingMs) ?? 1500, 300, 20000);
        var vm = new MultiViewViewModel(_vlcHost, _playlistService, playlist, channels, Player.Volume, caching);
        vm.InitialChannel = current;
        _dialogs.ShowMultiView(vm);

        // De volta ao canal que estava a ver.
        if (current is not null && ReferenceEquals(_loadedPlaylist, playlist)) await PlayCoreAsync(current);
    }

    // ───────────────────────────── Catch-up ─────────────────────────────

    /// <summary>O canal em direto tem arquivo e EPG: pode ver-se o programa atual desde o início.</summary>
    [ObservableProperty]
    private bool _canRestartProgramme;

    /// <summary>Está a tocar um programa do arquivo (mostra "Voltar ao direto").</summary>
    [ObservableProperty]
    private bool _isCatchupPlaying;

    private void UpdateCatchupState()
    {
        IsCatchupPlaying = Player.CurrentItem is CatchupItemViewModel;
        CanRestartProgramme = Player.CurrentChannel is { Model.HasCatchup: true, EpgId: not null, NowTitle: not null };
    }

    private async Task PlayCatchupAsync(ChannelItemViewModel channel, EpgProgramme programme)
    {
        if (_loadedPlaylist is not { } playlist) return;
        var item = new CatchupItemViewModel(channel, programme);
        if (Channels.Contains(channel))
        {
            _zapList = Channels;
            SelectedChannel = channel;
        }

        try
        {
            var url = await Task.Run(() => _playlistService.ResolveCatchupUrl(playlist, channel.Model,
                DateTimeOffset.FromUnixTimeSeconds(programme.Start).UtcDateTime,
                DateTimeOffset.FromUnixTimeSeconds(programme.Stop).UtcDateTime));
            await Player.PlayAsync(item, new PlayRequest(url, channel.Model.UserAgent, channel.Model.Referrer, IsLive: false));
        }
        catch (UserFacingException ex)
        {
            ShowNotification(ex.Message, isError: true);
        }
    }

    /// <summary>Ver o programa que está a dar desde o início (tecla R).</summary>
    [RelayCommand]
    private Task RestartProgrammeAsync()
    {
        if (Player.CurrentChannel is not { EpgId: { } epgId } channel || !channel.Model.HasCatchup) return Task.CompletedTask;
        var (now, _) = _epg.GetNowNext(epgId, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        return now is null ? Task.CompletedTask : PlayCatchupAsync(channel, now);
    }

    /// <summary>Do arquivo de volta à emissão em direto.</summary>
    [RelayCommand]
    private Task BackToLiveAsync() =>
        Player.CurrentItem is CatchupItemViewModel catchup ? PlayCoreAsync(catchup.Channel) : Task.CompletedTask;

    // ───────────────────────────── Canais, grupos e pesquisa ─────────────────────────────

    private async Task ShowChannelsAsync(Playlist playlist, IReadOnlyList<Channel> channels, CancellationToken ct,
        string? groupKey, string? channelKey, bool autoPlay)
    {
        var favoriteKeys = await Task.Run(() => _favorites.GetKeys(playlist.Id), ct);
        var (items, groups) = await Task.Run(() => Build(channels, favoriteKeys), ct);
        ct.ThrowIfCancellationRequested();

        // Atualização com um canal a tocar: manter o stream e apontar para o novo objeto.
        if (Player.CurrentChannel is { } playing)
        {
            var same = items.FirstOrDefault(c => c.Key == playing.Key);
            if (same is not null) Player.ReplaceCurrentChannel(same);
        }

        _allChannels = items;
        _zapList = [];
        foreach (var g in groups) g.IsLocked = g.Kind == GroupKind.Group && _lockedGroups.Contains(g.Name);
        _suppressFilter = true;
        Groups = groups;
        var restored = FindGroup(groups, groupKey);
        SelectedGroup = restored.IsLocked && _parental.IsActive ? groups[0] : restored;
        _suppressFilter = false;
        await RunFilterAsync(0);

        var target = channelKey is null ? null : items.FirstOrDefault(c => c.Key == channelKey);
        // Último canal num grupo bloqueado: não o mostrar nem o pôr a tocar.
        if (target is null || IsHidden(target)) return;

        if (!Channels.Contains(target))
        {
            _suppressFilter = true;
            SelectedGroup = groups[0];
            _suppressFilter = false;
            await RunFilterAsync(0);
        }
        SelectedChannel = target;

        if (autoPlay)
        {
            _zapList = Channels;
            await PlayCoreAsync(target);
        }
    }

    private static (List<ChannelItemViewModel>, List<GroupItemViewModel>) Build(
        IReadOnlyList<Channel> channels, HashSet<string> favoriteKeys)
    {
        var items = new List<ChannelItemViewModel>(channels.Count);
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var order = new List<string>();
        var favoriteCount = 0;

        for (var i = 0; i < channels.Count; i++)
        {
            var ch = channels[i];
            var vm = new ChannelItemViewModel(ch, i + 1) { IsFavorite = favoriteKeys.Contains(ch.Key) };
            if (vm.IsFavorite) favoriteCount++;
            items.Add(vm);

            if (counts.TryGetValue(ch.GroupTitle, out var n))
            {
                counts[ch.GroupTitle] = n + 1;
            }
            else
            {
                counts[ch.GroupTitle] = 1;
                order.Add(ch.GroupTitle);
            }
        }

        var groups = new List<GroupItemViewModel>(order.Count + 2)
        {
            new(GroupKind.All, AllChannelsName, items.Count),
            new(GroupKind.Favorites, FavoritesName, favoriteCount),
        };
        groups.AddRange(order.Select(name => new GroupItemViewModel(GroupKind.Group, name, counts[name])));
        return (items, groups);
    }

    private void ClearChannels()
    {
        _filterCts?.Cancel();
        _suppressFilter = true;
        _allChannels = [];
        _zapList = [];
        Groups = [];
        SelectedGroup = null;
        Channels = [];
        SelectedChannel = null;
        _suppressFilter = false;
    }

    partial void OnSelectedGroupChanged(GroupItemViewModel? oldValue, GroupItemViewModel? newValue)
    {
        var value = newValue;
        if (_suppressFilter || value is null) return;

        // Grupo bloqueado: pedir o PIN (depois da atualização da seleção, fora do binding).
        if (value.IsLocked && _parental.IsActive)
        {
            System.Windows.Application.Current.Dispatcher.BeginInvoke(() =>
            {
                if (RequestUnlock("Este grupo está bloqueado.")) return;
                _suppressFilter = true;
                SelectedGroup = oldValue is { IsLocked: false } ? oldValue : Groups.FirstOrDefault();
                _suppressFilter = false;
            });
            return;
        }

        _ = RunFilterAsync(0);
        if (_loadedPlaylist is { } playlist)
            SaveSetting(SettingsRepository.LastGroup(playlist.Id), GroupKeyOf(value));
    }

    partial void OnSearchTextChanged(string value)
    {
        if (_suppressFilter) return;
        if (Mode == BrowseMode.Live) _ = RunFilterAsync(150);
        else Vod.SetSearch(value);
    }

    [RelayCommand]
    private void ClearSearch() => SearchText = "";

    /// <summary>Filtra em background com debounce; um novo pedido cancela o anterior.</summary>
    private async Task RunFilterAsync(int delayMs)
    {
        _filterCts?.Cancel();
        var cts = new CancellationTokenSource();
        _filterCts = cts;

        try
        {
            if (delayMs > 0) await Task.Delay(delayMs, cts.Token);

            var source = _allChannels;
            var group = SelectedGroup;
            var hidden = _parental.IsActive && _lockedGroups.Count > 0 ? _lockedGroups : null;
            var terms = TextNormalizer.ForSearch(SearchText)
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            var result = await Task.Run(() => Filter(source, group, terms, hidden, cts.Token), cts.Token);
            if (cts.IsCancellationRequested) return;

            var selected = SelectedChannel;
            Channels = result;
            SelectedChannel = selected is not null && result.Contains(selected) ? selected : null;
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static List<ChannelItemViewModel> Filter(List<ChannelItemViewModel> source, GroupItemViewModel? group,
        string[] terms, HashSet<string>? hidden, CancellationToken ct)
    {
        var result = new List<ChannelItemViewModel>(group?.Kind == GroupKind.All && terms.Length == 0 ? source.Count : 256);
        for (var i = 0; i < source.Count; i++)
        {
            if ((i & 4095) == 0) ct.ThrowIfCancellationRequested();
            var c = source[i];

            if (group is not null)
            {
                if (group.Kind == GroupKind.Favorites && !c.IsFavorite) continue;
                if (group.Kind == GroupKind.Group && !string.Equals(c.GroupTitle, group.Name, StringComparison.Ordinal)) continue;
            }

            if (hidden is not null && hidden.Contains(c.GroupTitle)) continue;
            if (terms.Length > 0 && !MatchesAll(c.SearchKey, terms)) continue;
            result.Add(c);
        }
        return result;
    }

    private static bool MatchesAll(string text, string[] terms)
    {
        foreach (var term in terms)
        {
            if (!text.Contains(term, StringComparison.Ordinal)) return false;
        }
        return true;
    }

    private static string? GroupKeyOf(GroupItemViewModel? g) => g?.Kind switch
    {
        GroupKind.All => "*all",
        GroupKind.Favorites => "*fav",
        GroupKind.Group => "g:" + g.Name,
        _ => null,
    };

    private static GroupItemViewModel FindGroup(IReadOnlyList<GroupItemViewModel> groups, string? key)
    {
        GroupItemViewModel? found = key switch
        {
            "*fav" => groups.FirstOrDefault(g => g.Kind == GroupKind.Favorites),
            not null when key.StartsWith("g:", StringComparison.Ordinal) =>
                groups.FirstOrDefault(g => g.Kind == GroupKind.Group && g.Name == key[2..]),
            _ => null,
        };
        return found ?? groups[0];
    }

    // ───────────────────────────── Reprodução ─────────────────────────────

    /// <summary>Canal escolhido pelo utilizador na lista (clique ou Enter).</summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task PlayChannelAsync(ChannelItemViewModel? channel)
    {
        if (channel is null) return;
        _zapList = Channels;
        await PlayCoreAsync(channel);
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task NextChannelAsync() => ZapAsync(+1);

    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task PreviousChannelAsync() => ZapAsync(-1);

    private Task ZapAsync(int delta)
    {
        // A ver uma série: episódio seguinte/anterior.
        if (Player.CurrentItem is EpisodeItemViewModel episode) return Vod.PlayAdjacentEpisodeAsync(episode, delta);
        if (Player.CurrentItem is VodItemViewModel) return Task.CompletedTask;
        if (Player.CurrentItem is CatchupItemViewModel catchup && Channels.Count > 0 && _zapList.Count == 0) _zapList = Channels;
        var reference = (Player.CurrentItem as CatchupItemViewModel)?.Channel;

        var list = _zapList.Count > 0 ? _zapList : Channels;
        if (list.Count == 0) return Task.CompletedTask;

        var current = Player.CurrentChannel ?? reference ?? SelectedChannel;
        var index = current is null ? -1 : list.IndexOf(current);
        var next = index < 0 ? 0 : (index + delta + list.Count) % list.Count;
        if (_zapList.Count == 0) _zapList = list;
        return PlayCoreAsync(list[next]);
    }

    private async Task PlayCoreAsync(ChannelItemViewModel channel)
    {
        var playlist = _loadedPlaylist;
        if (playlist is null) return;

        if (Channels.Contains(channel)) SelectedChannel = channel;
        SaveSetting(SettingsRepository.LastChannelKey(playlist.Id), channel.Key);

        string url;
        try
        {
            url = await Task.Run(() => _playlistService.ResolveStreamUrl(playlist, channel.Model));
        }
        catch (UserFacingException ex)
        {
            await Player.StopAsync();
            Player.ShowError(channel, ex.Message);
            return;
        }

        await Player.PlayAsync(channel, new PlayRequest(url, channel.Model.UserAgent, channel.Model.Referrer, IsLive: true));
    }

    // ───────────────────────────── Favoritos ─────────────────────────────

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task ToggleFavoriteAsync(ChannelItemViewModel? channel)
    {
        channel ??= SelectedChannel ?? Player.CurrentChannel;
        if (channel is null || _loadedPlaylist is null) return;

        var playlistId = _loadedPlaylist.Id;
        var isFavorite = !channel.IsFavorite;
        SetFavorite(channel, isFavorite);

        try
        {
            await Task.Run(() =>
            {
                if (isFavorite) _favorites.Add(playlistId, channel.Key);
                else _favorites.Remove(playlistId, channel.Key);
            });
        }
        catch (Exception ex)
        {
            Log.Error("Erro ao guardar favorito.", ex);
            SetFavorite(channel, !isFavorite);
            ShowNotification("Não foi possível guardar o favorito.", isError: true);
            return;
        }

        if (SelectedGroup?.Kind == GroupKind.Favorites) await RunFilterAsync(0);
    }

    private void SetFavorite(ChannelItemViewModel channel, bool value)
    {
        if (channel.IsFavorite == value) return;
        channel.IsFavorite = value;
        var group = Groups.FirstOrDefault(g => g.Kind == GroupKind.Favorites);
        if (group is not null) group.Count += value ? 1 : -1;
    }

    // ───────────────────────────── Ecrã inteiro e volume ─────────────────────────────

    [RelayCommand]
    private void ToggleFullscreen() => IsFullscreen = !IsFullscreen;

    [RelayCommand]
    private void ExitFullscreen() => IsFullscreen = false;

    // ───────────────────────────── Auxiliares ─────────────────────────────

    [RelayCommand]
    private void DismissNotification() => Notification = null;

    private async void ShowNotification(string message, bool isError = false)
    {
        _notificationCts?.Cancel();
        var cts = new CancellationTokenSource();
        _notificationCts = cts;

        Notification = message;
        IsNotificationError = isError;
        try
        {
            await Task.Delay(isError ? 10_000 : 5_000, cts.Token);
            Notification = null;
        }
        catch (OperationCanceledException)
        {
        }
    }

    private CancellationToken BeginOperation()
    {
        _loadCts?.Cancel();
        _loadCts = new CancellationTokenSource();
        return _loadCts.Token;
    }

    private void SetBusy(string message)
    {
        IsBusy = true;
        BusyMessage = message;
        IsBusyIndeterminate = true;
        BusyProgress = 0;
    }

    private void EndBusy(CancellationToken ct)
    {
        if (_loadCts is not null && _loadCts.Token == ct) IsBusy = false;
    }

    private IProgress<LoadProgress> CreateProgress(CancellationToken ct) => new Progress<LoadProgress>(p =>
    {
        if (ct.IsCancellationRequested) return;
        BusyMessage = p.Message;
        IsBusyIndeterminate = p.Fraction is null;
        BusyProgress = p.Fraction ?? 0;
    });

    private void SaveSetting(string key, string? value)
    {
        _ = Task.Run(() =>
        {
            try
            {
                _settings.Set(key, value);
            }
            catch (Exception ex)
            {
                Log.Warn($"Não foi possível guardar a definição {key}.", ex);
            }
        });
    }
}
