using System.Globalization;
using LibVLCSharp.Shared;

namespace IPTVPlayer.Services.Playback;

public enum PlaybackState
{
    Idle,
    Opening,
    Buffering,
    Playing,
    Paused,
    Reconnecting,
    Ended,
    Error,
}

public sealed class PlaybackStatusEventArgs : EventArgs
{
    public PlaybackStatusEventArgs(PlaybackState state, string? message = null)
    {
        State = state;
        Message = message;
    }

    public PlaybackState State { get; }
    public string? Message { get; }
}

/// <summary>Faixa de áudio ou de legendas (Id -1 = desligar).</summary>
public sealed record TrackInfo(int Id, string Name);

/// <summary>O que reproduzir. IsLive = canal em direto (sem fim, reconecta); senão VOD.</summary>
public sealed record PlayRequest(string Url, string? UserAgent, string? Referrer, bool IsLive, long StartTimeMs = 0);

/// <summary>
/// Encapsula o LibVLC. Regras importantes:
/// - os eventos do VLC chegam numa thread do VLC e nunca podem chamar métodos do MediaPlayer
///   diretamente (deadlock), por isso tudo é reencaminhado para o thread pool;
/// - Play/Stop podem bloquear segundos num stream que não responde, por isso correm fora da UI;
/// - se um stream que já estava a tocar cair (erro, fim inesperado ou imagem parada), religa
///   automaticamente com espera crescente; nos filmes retoma na mesma posição.
/// O evento <see cref="StatusChanged"/> é disparado fora da thread de UI.
/// </summary>
public sealed class PlayerService : IDisposable
{
    public static readonly TimeSpan OpenTimeout = TimeSpan.FromSeconds(20);
    public const int MaxReconnectAttempts = 5;

    /// <summary>Um direto "a tocar" cujo tempo não avança há este tempo considera-se caído.</summary>
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(15);

    /// <summary>Depois de tocar bem durante este tempo, o contador de tentativas recomeça.</summary>
    private static readonly TimeSpan StableAfter = TimeSpan.FromSeconds(60);

    private readonly LibVLC _libVlc;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _sync = new();
    private readonly Timer _monitor;
    private Media? _currentMedia;
    private Timer? _watchdog;
    private int _generation;
    private PlayRequest? _request;
    private volatile bool _reachedPlaying;
    private bool _everPlayed;
    private int _attempt;
    private DateTime _playingSinceUtc;
    private long _lastTimeMs = -1;
    private DateTime _lastTimeChangeUtc;
    private long _lastKnownTimeMs;
    private volatile PlaybackState _state = PlaybackState.Idle;
    private int _volume = 80;
    private bool _muted;

    public PlayerService(LibVlcHost host)
    {
        _libVlc = host.LibVLC;
        MediaPlayer = new MediaPlayer(_libVlc) { EnableHardwareDecoding = true };
        // Eventos do MediaPlayer (os do Media, como StateChanged, não são fiáveis no LibVLC 3).
        MediaPlayer.Playing += OnPlaying;
        MediaPlayer.Paused += OnPaused;
        MediaPlayer.EndReached += OnEndReached;
        MediaPlayer.EncounteredError += OnEncounteredError;
        MediaPlayer.Buffering += OnBuffering;
        _monitor = new Timer(_ => Monitor(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    public event EventHandler<PlaybackStatusEventArgs>? StatusChanged;

    public MediaPlayer MediaPlayer { get; }

    public PlaybackState State => _state;

    public bool IsLive => _request?.IsLive ?? true;

    public bool IsSeekable => _request is { IsLive: false } && MediaPlayer.IsSeekable;

    public long LengthMs => MediaPlayer.Length;

    public long TimeMs => MediaPlayer.Time;

    /// <summary>Buffer de rede para diretos (os filmes usam pelo menos 3 s).</summary>
    public int NetworkCachingMs { get; set; } = 1500;

    public bool HardwareDecoding
    {
        get => MediaPlayer.EnableHardwareDecoding;
        set => MediaPlayer.EnableHardwareDecoding = value;
    }

    public int Volume
    {
        get => _volume;
        set
        {
            _volume = Math.Clamp(value, 0, 100);
            MediaPlayer.Volume = _volume;
        }
    }

    public bool IsMuted
    {
        get => _muted;
        set
        {
            _muted = value;
            MediaPlayer.Mute = value;
        }
    }

    /// <summary>Novo pedido do utilizador: recomeça o contador de tentativas.</summary>
    public Task PlayAsync(PlayRequest request)
    {
        var generation = Interlocked.Increment(ref _generation);
        lock (_sync)
        {
            _request = request;
            _attempt = 0;
            _everPlayed = false;
            _lastKnownTimeMs = request.StartTimeMs;
        }
        Raise(PlaybackState.Opening, request.IsLive ? "A abrir canal…" : "A abrir…");
        return OpenAsync(request, generation);
    }

    public async Task StopAsync()
    {
        Interlocked.Increment(ref _generation);
        lock (_sync) _request = null;
        StopWatchdog();
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await Task.Run(StopCore).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
        Raise(PlaybackState.Idle);
    }

    public void TogglePause()
    {
        switch (_state)
        {
            case PlaybackState.Playing when MediaPlayer.CanPause:
                MediaPlayer.SetPause(true);
                break;
            case PlaybackState.Paused:
                MediaPlayer.SetPause(false);
                break;
        }
    }

    // ── Faixas de áudio e legendas ──

    /// <summary>Faixas de áudio do que está a tocar (Id -1 = "Desativar").</summary>
    public IReadOnlyList<TrackInfo> GetAudioTracks() => Describe(MediaPlayer.AudioTrackDescription);

    /// <summary>Legendas do que está a tocar (Id -1 = desligadas).</summary>
    public IReadOnlyList<TrackInfo> GetSubtitleTracks() => Describe(MediaPlayer.SpuDescription);

    public int CurrentAudioTrack => MediaPlayer.AudioTrack;
    public int CurrentSubtitleTrack => MediaPlayer.Spu;

    public void SetAudioTrack(int id) => MediaPlayer.SetAudioTrack(id);
    public void SetSubtitleTrack(int id) => MediaPlayer.SetSpu(id);

    /// <summary>Legendas de um ficheiro (.srt, .ass, .vtt…), selecionadas logo.</summary>
    public bool AddSubtitleFile(string path) =>
        MediaPlayer.AddSlave(MediaSlaveType.Subtitle, new Uri(path).AbsoluteUri, true);

    private static IReadOnlyList<TrackInfo> Describe(LibVLCSharp.Shared.Structures.TrackDescription[] tracks) =>
        tracks.Select(t => new TrackInfo(t.Id, string.IsNullOrWhiteSpace(t.Name) ? $"Faixa {t.Id}" : t.Name)).ToList();

    /// <summary>Saltar para uma posição (só VOD).</summary>
    public void Seek(long timeMs)
    {
        if (!IsSeekable) return;
        var length = MediaPlayer.Length;
        MediaPlayer.Time = Math.Clamp(timeMs, 0, length > 0 ? length - 1000 : long.MaxValue);
    }

    public void Dispose()
    {
        StopWatchdog();
        _monitor.Dispose();
        try
        {
            StopCore();
            MediaPlayer.Playing -= OnPlaying;
            MediaPlayer.Paused -= OnPaused;
            MediaPlayer.EndReached -= OnEndReached;
            MediaPlayer.EncounteredError -= OnEncounteredError;
            MediaPlayer.Buffering -= OnBuffering;
            MediaPlayer.Dispose();
        }
        catch (Exception ex)
        {
            Log.Warn("Erro ao libertar o LibVLC.", ex);
        }
    }

    private async Task OpenAsync(PlayRequest request, int generation)
    {
        StopWatchdog();
        _reachedPlaying = false;

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            // Zapping rápido: se entretanto foi pedido outro canal, este já não interessa.
            if (generation != Volatile.Read(ref _generation)) return;

            await Task.Run(() =>
            {
                var caching = request.IsLive ? NetworkCachingMs : Math.Max(3000, NetworkCachingMs);
                var options = new List<string> { $":network-caching={caching}" };
                if (!string.IsNullOrWhiteSpace(request.UserAgent)) options.Add($":http-user-agent={request.UserAgent}");
                if (!string.IsNullOrWhiteSpace(request.Referrer)) options.Add($":http-referrer={request.Referrer}");
                if (request.StartTimeMs > 0)
                    options.Add(":start-time=" + (request.StartTimeMs / 1000.0).ToString("0.###", CultureInfo.InvariantCulture));

                var media = new Media(_libVlc, new Uri(request.Url), options.ToArray());
                var previous = _currentMedia;
                _currentMedia = media;
                MediaPlayer.Play(media);
                previous?.Dispose();
            }).ConfigureAwait(false);

            StartWatchdog(generation);
        }
        catch (UriFormatException)
        {
            Fail(generation, "O endereço deste conteúdo não é válido.");
        }
        catch (Exception ex)
        {
            Log.Error("Falha ao iniciar a reprodução.", ex);
            Fail(generation, "Não foi possível iniciar a reprodução.");
        }
        finally
        {
            _gate.Release();
        }
    }

    private void StopCore()
    {
        MediaPlayer.Stop();
        _currentMedia?.Dispose();
        _currentMedia = null;
    }

    private void OnPlaying(object? sender, EventArgs e)
    {
        lock (_sync)
        {
            _reachedPlaying = true;
            _everPlayed = true;
            _playingSinceUtc = DateTime.UtcNow;
            _lastTimeChangeUtc = DateTime.UtcNow;
            _lastTimeMs = -1;
        }
        StopWatchdog();
        // O VLC repõe o volume quando cria a saída de áudio: reaplicar fora da thread do VLC.
        ThreadPool.QueueUserWorkItem(_ =>
        {
            MediaPlayer.Volume = _volume;
            MediaPlayer.Mute = _muted;
        });
        Raise(PlaybackState.Playing);
    }

    private void OnPaused(object? sender, EventArgs e) => Raise(PlaybackState.Paused);

    private void OnEndReached(object? sender, EventArgs e)
    {
        if (_request is { IsLive: false })
        {
            // Num filme/episódio, chegar ao fim é normal... a não ser que tenha acabado muito antes.
            var length = MediaPlayer.Length;
            if (length <= 0 || _lastKnownTimeMs >= length - 30_000)
            {
                StopWatchdog();
                Raise(PlaybackState.Ended, "Terminou.");
                return;
            }
        }
        ConnectionLost("A transmissão terminou ou a ligação caiu.");
    }

    /// <summary>Stream offline, endereço inválido, ligação recusada…</summary>
    private void OnEncounteredError(object? sender, EventArgs e) =>
        ConnectionLost("Não foi possível reproduzir. O stream pode estar offline ou indisponível.");

    private void OnBuffering(object? sender, MediaPlayerBufferingEventArgs e)
    {
        // Em HLS o VLC continua a emitir Buffering (< 100%) a cada segmento durante a reprodução
        // normal: só mostramos a percentagem enquanto ainda não começou a tocar.
        if (_reachedPlaying || _state is not (PlaybackState.Opening or PlaybackState.Buffering)) return;
        if (e.Cache < 100f) Raise(PlaybackState.Buffering, $"A carregar… {e.Cache:0}%");
    }

    /// <summary>
    /// Ligação perdida. Se nunca chegou a tocar → erro (o canal está mesmo em baixo).
    /// Se já estava a tocar → religar até <see cref="MaxReconnectAttempts"/> vezes (1, 2, 4, 8, 15 s).
    /// </summary>
    private void ConnectionLost(string finalMessage)
    {
        var generation = Volatile.Read(ref _generation);
        PlayRequest retry;
        TimeSpan delay;
        int attempt;

        lock (_sync)
        {
            var request = _request;
            if (request is null || _state is PlaybackState.Idle or PlaybackState.Error or PlaybackState.Ended) return;
            StopWatchdog();

            if (!_everPlayed)
            {
                Fail(generation, finalMessage);
                return;
            }

            if (_state == PlaybackState.Playing && DateTime.UtcNow - _playingSinceUtc > StableAfter) _attempt = 0;
            attempt = ++_attempt;
            if (attempt > MaxReconnectAttempts)
            {
                Fail(generation, $"Ligação perdida. Não foi possível religar após {MaxReconnectAttempts} tentativas.");
                return;
            }

            delay = TimeSpan.FromSeconds(Math.Min(15, Math.Pow(2, attempt - 1)));
            retry = request.IsLive ? request : request with { StartTimeMs = Math.Max(0, _lastKnownTimeMs - 2000) };
        }

        Log.Info($"Ligação perdida; a religar (tentativa {attempt}).");
        Raise(PlaybackState.Reconnecting, $"Ligação perdida. A religar… ({attempt}/{MaxReconnectAttempts})");
        _ = Task.Run(async () =>
        {
            await Task.Delay(delay).ConfigureAwait(false);
            if (generation == Volatile.Read(ref _generation)) await OpenAsync(retry, generation).ConfigureAwait(false);
        });
    }

    private void Fail(int generation, string message)
    {
        StopWatchdog();
        Raise(PlaybackState.Error, message);
        // Libertar a ligação ao servidor (alguns fornecedores só permitem 1 ligação).
        _ = Task.Run(async () =>
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (generation == Volatile.Read(ref _generation)) StopCore();
            }
            finally
            {
                _gate.Release();
            }
        });
    }

    /// <summary>A cada segundo: guarda a posição (para retomar filmes) e deteta diretos parados.</summary>
    private void Monitor()
    {
        if (_state != PlaybackState.Playing) return;
        try
        {
            var time = MediaPlayer.Time;
            var stalled = false;
            lock (_sync)
            {
                if (time > 0) _lastKnownTimeMs = time;
                if (time != _lastTimeMs)
                {
                    _lastTimeMs = time;
                    _lastTimeChangeUtc = DateTime.UtcNow;
                }
                else if (_request is { IsLive: true } && DateTime.UtcNow - _lastTimeChangeUtc > StallTimeout)
                {
                    _lastTimeChangeUtc = DateTime.UtcNow;
                    stalled = true;
                }
            }
            if (stalled) ConnectionLost("A imagem parou e não foi possível religar.");
        }
        catch (Exception ex)
        {
            Log.Warn("Erro no monitor de reprodução.", ex);
        }
    }

    private void StartWatchdog(int generation)
    {
        StopWatchdog();
        _watchdog = new Timer(_ =>
        {
            if (generation != Volatile.Read(ref _generation) || _reachedPlaying) return;
            ConnectionLost("Não respondeu a tempo. O stream pode estar offline.");
        }, null, OpenTimeout, Timeout.InfiniteTimeSpan);
    }

    private void StopWatchdog()
    {
        Interlocked.Exchange(ref _watchdog, null)?.Dispose();
    }

    private void Raise(PlaybackState state, string? message = null)
    {
        _state = state;
        StatusChanged?.Invoke(this, new PlaybackStatusEventArgs(state, message));
    }
}
