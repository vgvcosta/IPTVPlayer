using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IPTVPlayer.Services.Playback;
using LibVLCSharp.Shared;

namespace IPTVPlayer.ViewModels;

/// <summary>Faixa na lista do painel (áudio ou legendas).</summary>
public sealed record TrackItem(int Id, string Name, bool IsSelected)
{
    public override string ToString() => Name;
}

public sealed partial class PlayerViewModel : ObservableObject
{
    private readonly PlayerService _player;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _positionTimer;
    private (PlayRequest Request, PlayableItemViewModel Item)? _last;
    private int _osdGeneration;

    public PlayerViewModel(PlayerService player)
    {
        _player = player;
        _dispatcher = Dispatcher.CurrentDispatcher;
        _volume = player.Volume;
        _player.StatusChanged += OnStatusChanged;
        _positionTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _positionTimer.Tick += (_, _) => UpdatePosition();
    }

    public MediaPlayer MediaPlayer => _player.MediaPlayer;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPlaying), nameof(IsBusy), nameof(HasError), nameof(IsIdle), nameof(HasEnded))]
    private PlaybackState _state = PlaybackState.Idle;

    [ObservableProperty]
    private string? _statusMessage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentChannel), nameof(IsVod))]
    private PlayableItemViewModel? _currentItem;

    /// <summary>Mostra número e nome durante alguns segundos após mudar de canal.</summary>
    [ObservableProperty]
    private bool _isOsdVisible;

    [ObservableProperty]
    private int _volume;

    [ObservableProperty]
    private bool _isMuted;

    // ── Posição (só VOD) ──
    [ObservableProperty]
    private bool _isSeekable;

    [ObservableProperty]
    private double _positionMs;

    [ObservableProperty]
    private double _lengthMs = 1;

    [ObservableProperty]
    private string _timeText = "";

    /// <summary>"A retomar em 12:34" (aparece alguns segundos, com "Começar do início").</summary>
    [ObservableProperty]
    private string? _resumeText;

    [ObservableProperty]
    private bool _isResumeNoticeVisible;

    private int _resumeGeneration;

    // ── Faixas de áudio e legendas ──

    /// <summary>Idioma de áudio preferido ("pt", "en"…; vazio = o que o canal trouxer).</summary>
    public string? PreferredAudioLanguage { get; set; }

    /// <summary>Legendas preferidas: "off", um idioma, ou vazio = o que o ficheiro trouxer.</summary>
    public string? PreferredSubtitleLanguage { get; set; }

    [ObservableProperty]
    private bool _isTracksPanelOpen;

    [ObservableProperty]
    private IReadOnlyList<TrackItem> _audioTracks = [];

    [ObservableProperty]
    private IReadOnlyList<TrackItem> _subtitleTracks = [];

    /// <summary>Aviso curto no canto do vídeo ("Áudio: Inglês").</summary>
    [ObservableProperty]
    private string? _toastText;

    private int _toastGeneration;
    private int _playGeneration;
    private int _preferencesAppliedFor = -1;

    [RelayCommand]
    private void ToggleTracksPanel()
    {
        if (!IsTracksPanelOpen) RefreshTracks();
        IsTracksPanelOpen = !IsTracksPanelOpen;
    }

    [RelayCommand]
    private void SelectAudio(TrackItem? track)
    {
        if (track is null) return;
        _player.SetAudioTrack(track.Id);
        RefreshTracks();
    }

    [RelayCommand]
    private void SelectSubtitle(TrackItem? track)
    {
        if (track is null) return;
        _player.SetSubtitleTrack(track.Id);
        RefreshTracks();
    }

    public async void AddSubtitleFile(string path)
    {
        if (!_player.AddSubtitleFile(path))
        {
            ShowToast("Não foi possível abrir as legendas.");
            return;
        }
        ShowToast("Legendas: " + Path.GetFileName(path));
        await Task.Delay(800); // o VLC demora um pouco a acrescentar a faixa
        RefreshTracks();
    }

    /// <summary>Tecla A: próxima faixa de áudio.</summary>
    public void CycleAudio() => Cycle(_player.GetAudioTracks().Where(t => t.Id >= 0).ToList(), _player.CurrentAudioTrack,
        _player.SetAudioTrack, "Áudio");

    /// <summary>Tecla S: próximas legendas (incluindo desligadas).</summary>
    public void CycleSubtitles() => Cycle(_player.GetSubtitleTracks().ToList(), _player.CurrentSubtitleTrack,
        _player.SetSubtitleTrack, "Legendas");

    private void Cycle(List<TrackInfo> tracks, int current, Action<int> set, string label)
    {
        if (tracks.Count == 0)
        {
            ShowToast($"{label}: não há faixas.");
            return;
        }
        var index = tracks.FindIndex(t => t.Id == current);
        var next = tracks[(index + 1) % tracks.Count];
        set(next.Id);
        ShowToast($"{label}: {(next.Id < 0 ? "desligadas" : next.Name)}");
        if (IsTracksPanelOpen) RefreshTracks();
    }

    private void RefreshTracks()
    {
        var audio = _player.CurrentAudioTrack;
        var spu = _player.CurrentSubtitleTrack;
        AudioTracks = _player.GetAudioTracks().Where(t => t.Id >= 0).Select(t => new TrackItem(t.Id, t.Name, t.Id == audio)).ToList();
        SubtitleTracks = _player.GetSubtitleTracks()
            .Select(t => new TrackItem(t.Id, t.Id < 0 ? "Desligadas" : t.Name, t.Id == spu)).ToList();
    }

    /// <summary>Aplica os idiomas preferidos uma vez por reprodução (as faixas só existem depois de começar).</summary>
    private async void ApplyTrackPreferences()
    {
        var generation = _playGeneration;
        if (_preferencesAppliedFor == generation) return;
        _preferencesAppliedFor = generation;
        await Task.Delay(1500);
        if (generation != _playGeneration) return;

        if (!string.IsNullOrEmpty(PreferredAudioLanguage) &&
            TrackLanguages.Find(_player.GetAudioTracks(), PreferredAudioLanguage) is { } audio && audio.Id != _player.CurrentAudioTrack)
            _player.SetAudioTrack(audio.Id);

        if (PreferredSubtitleLanguage == "off")
            _player.SetSubtitleTrack(-1);
        else if (!string.IsNullOrEmpty(PreferredSubtitleLanguage) &&
                 TrackLanguages.Find(_player.GetSubtitleTracks(), PreferredSubtitleLanguage) is { } sub)
            _player.SetSubtitleTrack(sub.Id);
    }

    private async void ShowToast(string text)
    {
        var generation = ++_toastGeneration;
        ToastText = text;
        await Task.Delay(2500);
        if (generation == _toastGeneration) ToastText = null;
    }

    /// <summary>Posição real do VLC (para guardar o progresso).</summary>
    public long CurrentTimeMs => _player.TimeMs;
    public long CurrentLengthMs => _player.LengthMs;

    /// <summary>true enquanto o utilizador arrasta a barra (não atualizar a posição por baixo dele).</summary>
    public bool IsSeeking { get; set; }

    public ChannelItemViewModel? CurrentChannel => CurrentItem as ChannelItemViewModel;
    public bool IsVod => CurrentItem is { IsLive: false };
    public bool IsPlaying => State == PlaybackState.Playing;
    public bool IsBusy => State is PlaybackState.Opening or PlaybackState.Buffering or PlaybackState.Reconnecting;
    public bool HasError => State == PlaybackState.Error;
    public bool HasEnded => State == PlaybackState.Ended;
    public bool IsIdle => State == PlaybackState.Idle;

    public async Task PlayAsync(PlayableItemViewModel item, PlayRequest request)
    {
        _last = (request, item);
        _playGeneration++;
        IsTracksPanelOpen = false;
        CurrentItem = item;
        IsSeekable = false;
        TimeText = "";
        ShowOsd();
        if (request.StartTimeMs > 0) ShowResumeNotice(request.StartTimeMs);
        else IsResumeNoticeVisible = false;
        await _player.PlayAsync(request);
    }

    /// <summary>Após atualizar a playlist, aponta para o novo objeto do mesmo canal sem reiniciar o stream.</summary>
    public void ReplaceCurrentChannel(ChannelItemViewModel channel)
    {
        if (CurrentChannel?.Key != channel.Key) return;
        CurrentItem = channel;
        if (_last is { } last) _last = (last.Request, channel);
    }

    public void ShowError(PlayableItemViewModel? item, string message)
    {
        CurrentItem = item;
        State = PlaybackState.Error;
        StatusMessage = message;
    }

    public async Task StopAsync()
    {
        _last = null;
        CurrentItem = null;
        await _player.StopAsync();
    }

    [RelayCommand]
    private void TogglePause()
    {
        if (State is PlaybackState.Error or PlaybackState.Ended && _last is not null)
            _ = RetryAsync();
        else
            _player.TogglePause();
    }

    [RelayCommand]
    private void ToggleMute() => IsMuted = !IsMuted;

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task RetryAsync()
    {
        if (_last is not { } last) return;
        // "Ver de novo" depois de terminar um filme começa do início.
        await PlayAsync(last.Item, last.Request with { StartTimeMs = 0 });
    }

    public void ChangeVolume(int delta) => Volume = Math.Clamp(Volume + delta, 0, 100);

    /// <summary>Avançar/recuar (setas ←/→ nos filmes).</summary>
    public void SeekBy(int seconds)
    {
        if (!IsSeekable) return;
        _player.Seek(_player.TimeMs + seconds * 1000L);
        UpdatePosition();
    }

    public void SeekTo(double positionMs)
    {
        _player.Seek((long)positionMs);
        UpdatePosition();
    }

    partial void OnVolumeChanged(int value)
    {
        _player.Volume = value;
        if (value > 0 && IsMuted) IsMuted = false;
    }

    partial void OnIsMutedChanged(bool value) => _player.IsMuted = value;

    partial void OnStateChanged(PlaybackState value)
    {
        if (value == PlaybackState.Playing) ApplyTrackPreferences();
        if (value is PlaybackState.Playing or PlaybackState.Paused && IsVod)
            _positionTimer.Start();
        else if (value is not PlaybackState.Buffering)
            _positionTimer.Stop();
        UpdatePosition();
    }

    private void UpdatePosition()
    {
        var seekable = _player.IsSeekable && State is PlaybackState.Playing or PlaybackState.Paused;
        IsSeekable = seekable;
        if (!seekable || IsSeeking) return;

        var length = _player.LengthMs;
        var time = _player.TimeMs;
        if (length <= 0) return;
        LengthMs = length;
        PositionMs = Math.Clamp(time, 0, length);
        TimeText = $"{Format(time)} / {Format(length)}";
    }

    private static string Format(long ms)
    {
        var t = TimeSpan.FromMilliseconds(Math.Max(0, ms));
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
    }

    /// <summary>Recomeçar do início o filme/episódio retomado.</summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task StartOverAsync()
    {
        IsResumeNoticeVisible = false;
        return RetryAsync();
    }

    private async void ShowResumeNotice(long positionMs)
    {
        var generation = ++_resumeGeneration;
        ResumeText = $"A retomar em {Format(positionMs)}";
        IsResumeNoticeVisible = true;
        await Task.Delay(10_000);
        if (generation == _resumeGeneration) IsResumeNoticeVisible = false;
    }

    private async void ShowOsd()
    {
        var generation = ++_osdGeneration;
        IsOsdVisible = true;
        await Task.Delay(4000);
        if (generation == _osdGeneration) IsOsdVisible = false;
    }

    private void OnStatusChanged(object? sender, PlaybackStatusEventArgs e)
    {
        _dispatcher.BeginInvoke(() =>
        {
            State = e.State;
            StatusMessage = e.Message;
        });
    }
}
