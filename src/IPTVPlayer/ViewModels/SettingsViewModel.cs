using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IPTVPlayer.Models;
using IPTVPlayer.Services;
using IPTVPlayer.Services.Data;
using IPTVPlayer.Services.Playback;

namespace IPTVPlayer.ViewModels;

/// <summary>Opção de idioma ("" = automático, "off" = legendas desligadas).</summary>
public sealed record LanguageOption(string Code, string Label)
{
    public override string ToString() => Label;
}

public sealed partial class LockableGroup : ObservableObject
{
    public LockableGroup(string name, bool isLocked)
    {
        Name = name;
        _isLocked = isLocked;
    }

    public string Name { get; }

    [ObservableProperty]
    private bool _isLocked;

    public override string ToString() => Name;
}

/// <summary>Definições: controlo parental (PIN e grupos bloqueados), reprodução e cache.</summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly ParentalControlService _parental;
    private readonly SettingsRepository _settings;
    private readonly PlayerService _player;
    private readonly ImageDiskCache _imageCache;
    private readonly ChannelRepository _channels;
    private readonly VodRepository _vod;
    private readonly PlayerViewModel _playerViewModel;
    private readonly RecordingService _recordings;
    private readonly IPTVPlayer.Views.GamepadService _gamepad;
    private Playlist? _playlist;

    public SettingsViewModel(ParentalControlService parental, SettingsRepository settings, PlayerService player,
        ImageDiskCache imageCache, ChannelRepository channels, VodRepository vod, PlayerViewModel playerViewModel,
        RecordingService recordings, IPTVPlayer.Views.GamepadService gamepad)
    {
        _playerViewModel = playerViewModel;
        _recordings = recordings;
        _gamepad = gamepad;
        _parental = parental;
        _settings = settings;
        _player = player;
        _imageCache = imageCache;
        _channels = channels;
        _vod = vod;
    }

    public event EventHandler? RequestClose;

    /// <summary>A view limpa as caixas de PIN quando isto é disparado.</summary>
    public event EventHandler? PinFieldsReset;

    // Definidos pela view (PasswordBox não suporta binding).
    public string CurrentPin { private get; set; } = "";
    public string NewPin { private get; set; } = "";
    public string ConfirmPin { private get; set; } = "";

    // ── Controlo parental ──
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PinStatus))]
    private bool _hasPin;

    [ObservableProperty]
    private string? _pinMessage;

    [ObservableProperty]
    private bool _pinMessageIsError;

    public string PinStatus => HasPin
        ? "PIN ativo. Os grupos bloqueados ficam escondidos até introduzires o PIN."
        : "Sem PIN. Define um PIN para os bloqueios terem efeito.";

    public string PlaylistName => _playlist?.Name ?? "";
    public bool HasPlaylist => _playlist is not null;
    public bool IsXtream => _playlist?.Kind == PlaylistKind.Xtream;

    public List<LockableGroup> LiveGroups { get; private set; } = [];
    public List<LockableGroup> MovieCategories { get; private set; } = [];
    public List<LockableGroup> SeriesCategories { get; private set; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLiveScope), nameof(IsMoviesScope), nameof(IsSeriesScope), nameof(VisibleGroups))]
    private LockScope _scope = LockScope.Live;

    public bool IsLiveScope { get => Scope == LockScope.Live; set { if (value) Scope = LockScope.Live; } }
    public bool IsMoviesScope { get => Scope == LockScope.Movies; set { if (value) Scope = LockScope.Movies; } }
    public bool IsSeriesScope { get => Scope == LockScope.Series; set { if (value) Scope = LockScope.Series; } }

    public List<LockableGroup> VisibleGroups => Scope switch
    {
        LockScope.Movies => MovieCategories,
        LockScope.Series => SeriesCategories,
        _ => LiveGroups,
    };

    // ── Reprodução ──
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BufferText))]
    private double _bufferSeconds = 1.5;

    public string BufferText => $"{BufferSeconds:0.0} s";

    [ObservableProperty]
    private bool _hardwareDecoding = true;

    // ── Idiomas, gravações e comando ──
    public IReadOnlyList<LanguageOption> AudioOptions { get; } =
        [new("", "Automático"), .. TrackLanguages.Options.Select(o => new LanguageOption(o.Code, o.Label))];

    public IReadOnlyList<LanguageOption> SubtitleOptions { get; } =
        [new("", "Automático"), new("off", "Desligadas"), .. TrackLanguages.Options.Select(o => new LanguageOption(o.Code, o.Label))];

    [ObservableProperty]
    private string _preferredAudio = "";

    [ObservableProperty]
    private string _preferredSubtitle = "";

    [ObservableProperty]
    private string _recordingsFolder = "";

    [ObservableProperty]
    private bool _gamepadEnabled = true;

    // ── Cache ──
    [ObservableProperty]
    private string _cacheSizeText = "";

    public async Task LoadAsync(Playlist? playlist)
    {
        _playlist = playlist;
        HasPin = _parental.HasPin;
        BufferSeconds = Math.Round((_settings.GetInt64(SettingsRepository.NetworkCachingMs) ?? 1500) / 1000.0, 1);
        HardwareDecoding = _settings.Get(SettingsRepository.HardwareDecoding) != "0";
        PreferredAudio = _settings.Get(SettingsRepository.PreferredAudio) ?? "";
        PreferredSubtitle = _settings.Get(SettingsRepository.PreferredSubtitle) ?? "";
        GamepadEnabled = _settings.Get(SettingsRepository.GamepadEnabled) != "0";
        RecordingsFolder = _recordings.Folder;

        if (playlist is not null)
        {
            var (live, movies, series, lockedLive, lockedMovies, lockedSeries) = await Task.Run(() => (
                _channels.GetGroupNames(playlist.Id),
                _vod.GetCategories(playlist.Id, VodKind.Movie),
                _vod.GetCategories(playlist.Id, VodKind.Series),
                _parental.GetLockedGroups(playlist.Id, LockScope.Live),
                _parental.GetLockedGroups(playlist.Id, LockScope.Movies),
                _parental.GetLockedGroups(playlist.Id, LockScope.Series)));
            LiveGroups = live.Select(n => new LockableGroup(n, lockedLive.Contains(n))).ToList();
            MovieCategories = movies.Select(n => new LockableGroup(n, lockedMovies.Contains(n))).ToList();
            SeriesCategories = series.Select(n => new LockableGroup(n, lockedSeries.Contains(n))).ToList();
        }

        OnPropertyChanged(string.Empty);
        await UpdateCacheSizeAsync();
    }

    [RelayCommand]
    private void SavePin()
    {
        if (HasPin && !_parental.Verify(CurrentPin))
        {
            SetPinMessage("O PIN atual está errado.", true);
            return;
        }
        if (!ParentalControlService.IsValidPin(NewPin))
        {
            SetPinMessage("O novo PIN tem de ter entre 4 e 8 algarismos.", true);
            return;
        }
        if (NewPin != ConfirmPin)
        {
            SetPinMessage("A confirmação não coincide com o novo PIN.", true);
            return;
        }

        _parental.SetPin(NewPin);
        HasPin = true;
        PinFieldsReset?.Invoke(this, EventArgs.Empty);
        SetPinMessage("PIN guardado.", false);
    }

    [RelayCommand]
    private void RemovePin()
    {
        if (!HasPin) return;
        if (!_parental.Verify(CurrentPin))
        {
            SetPinMessage("Para remover o PIN, escreve o PIN atual.", true);
            return;
        }
        _parental.RemovePin();
        HasPin = false;
        PinFieldsReset?.Invoke(this, EventArgs.Empty);
        SetPinMessage("PIN removido. Os grupos marcados deixam de estar escondidos.", false);
    }

    /// <summary>Marca os grupos com nomes típicos de conteúdo para adultos.</summary>
    [RelayCommand]
    private void SuggestAdult()
    {
        var count = 0;
        foreach (var g in LiveGroups.Concat(MovieCategories).Concat(SeriesCategories))
        {
            if (g.IsLocked || !ParentalControlService.LooksAdult(g.Name)) continue;
            g.IsLocked = true;
            count++;
        }
        SetPinMessage(count == 0 ? "Não encontrei grupos com nomes de conteúdo para adultos." : $"{count} grupo(s) marcado(s). Carrega em Guardar.", false);
    }

    [RelayCommand]
    private async Task ClearCacheAsync()
    {
        await _imageCache.ClearAsync();
        await UpdateCacheSizeAsync();
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        var cachingMs = (int)Math.Round(BufferSeconds * 1000);
        _settings.Set(SettingsRepository.NetworkCachingMs, cachingMs);
        _settings.Set(SettingsRepository.HardwareDecoding, HardwareDecoding ? "1" : "0");
        _player.NetworkCachingMs = cachingMs;
        _player.HardwareDecoding = HardwareDecoding;

        _settings.Set(SettingsRepository.PreferredAudio, PreferredAudio is { Length: > 0 } a ? a : null);
        _settings.Set(SettingsRepository.PreferredSubtitle, PreferredSubtitle is { Length: > 0 } s ? s : null);
        _playerViewModel.PreferredAudioLanguage = PreferredAudio;
        _playerViewModel.PreferredSubtitleLanguage = PreferredSubtitle;

        _settings.Set(SettingsRepository.GamepadEnabled, GamepadEnabled ? "1" : "0");
        _gamepad.Enabled = GamepadEnabled;

        if (!string.IsNullOrWhiteSpace(RecordingsFolder))
        {
            try
            {
                Directory.CreateDirectory(RecordingsFolder);
                _recordings.Folder = RecordingsFolder;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                SetPinMessage("Não foi possível usar essa pasta para as gravações.", true);
                return;
            }
        }

        if (_playlist is { } playlist)
        {
            var id = playlist.Id;
            var live = LiveGroups.Where(g => g.IsLocked).Select(g => g.Name).ToList();
            var movies = MovieCategories.Where(g => g.IsLocked).Select(g => g.Name).ToList();
            var series = SeriesCategories.Where(g => g.IsLocked).Select(g => g.Name).ToList();
            // Na thread de UI: o evento Changed volta a filtrar as listas (são poucas escritas, rápidas).
            _parental.SetLockedGroups(id, LockScope.Live, live);
            _parental.SetLockedGroups(id, LockScope.Movies, movies);
            _parental.SetLockedGroups(id, LockScope.Series, series);
        }

        RequestClose?.Invoke(this, EventArgs.Empty);
        await Task.CompletedTask;
    }

    private async Task UpdateCacheSizeAsync()
    {
        var bytes = await Task.Run(_imageCache.GetSizeBytes);
        CacheSizeText = $"{bytes / 1024d / 1024d:0.0} MB";
    }

    private void SetPinMessage(string message, bool isError)
    {
        PinMessage = message;
        PinMessageIsError = isError;
    }
}
