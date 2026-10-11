using System.Text;
using IPTVPlayer.Models;
using IPTVPlayer.Services.Data;
using LibVLCSharp.Shared;

namespace IPTVPlayer.Services.Playback;

/// <summary>
/// Grava canais em direto para ficheiros .ts (o VLC copia o stream sem recodificar, por isso quase não
/// gasta CPU). Cada gravação usa um player próprio sem imagem; as agendadas começam e acabam sozinhas
/// enquanto a app está aberta. Se a ligação cair, tenta continuar num ficheiro "parte 2", "parte 3"…
/// O evento <see cref="Changed"/> é disparado fora da thread de UI.
/// </summary>
public sealed class RecordingService : IDisposable
{
    public const string FolderSetting = "recordings_folder";
    private const int MaxParts = 4;

    private readonly LibVLC _libVlc;
    private readonly RecordingRepository _repository;
    private readonly PlaylistRepository _playlists;
    private readonly ChannelRepository _channels;
    private readonly PlaylistService _playlistService;
    private readonly SettingsRepository _settings;
    private readonly object _sync = new();
    private readonly Dictionary<long, Active> _active = new();
    private readonly Timer _timer;
    private bool _disposed;

    public RecordingService(LibVlcHost host, RecordingRepository repository, PlaylistRepository playlists,
        ChannelRepository channels, PlaylistService playlistService, SettingsRepository settings)
    {
        _libVlc = host.LibVLC;
        _repository = repository;
        _playlists = playlists;
        _channels = channels;
        _playlistService = playlistService;
        _settings = settings;
        _timer = new Timer(_ => Tick(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public event EventHandler? Changed;

    /// <summary>Vídeos\PepinoTv; em modo portátil (IPTVPLAYER_DATA) fica dentro dessa pasta.</summary>
    public static string DefaultFolder =>
        Environment.GetEnvironmentVariable(AppPaths.DataDirectoryVariable) is { Length: > 0 } portable
            ? Path.Combine(portable, "recordings")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "PepinoTv");

    public string Folder
    {
        get => _settings.Get(FolderSetting) is { Length: > 0 } f ? f : DefaultFolder;
        set => _settings.Set(FolderSetting, string.IsNullOrWhiteSpace(value) ? null : value.Trim());
    }

    /// <summary>Quantas gravações estão a decorrer agora.</summary>
    public int ActiveCount
    {
        get
        {
            lock (_sync) return _active.Count;
        }
    }

    /// <summary>Arranque: limpa estados deixados por um fecho inesperado e começa a vigiar a agenda.</summary>
    public void Start()
    {
        var now = DateTime.UtcNow;
        foreach (var r in _repository.GetAll())
        {
            if (r.Status == RecordingStatus.Recording)
            {
                r.Status = HasContent(r.FilePath) ? RecordingStatus.Completed : RecordingStatus.Failed;
                r.Error = "A app foi fechada durante a gravação.";
                _repository.Update(r);
            }
            else if (r.Status == RecordingStatus.Scheduled && r.StopUtc <= now)
            {
                r.Status = RecordingStatus.Failed;
                r.Error = "A app estava fechada à hora marcada.";
                _repository.Update(r);
            }
        }
        _timer.Change(TimeSpan.Zero, TimeSpan.FromSeconds(5));
    }

    public IReadOnlyList<Recording> GetAll() => _repository.GetAll();

    /// <summary>Id da gravação em curso deste canal (ou null).</summary>
    public long? FindActive(long playlistId, string channelKey)
    {
        lock (_sync)
            return _active.Values.FirstOrDefault(a => a.Recording.PlaylistId == playlistId && a.Recording.ChannelKey == channelKey)?.Recording.Id;
    }

    public bool IsRecording(long playlistId, string channelKey)
    {
        lock (_sync) return _active.Values.Any(a => a.Recording.PlaylistId == playlistId && a.Recording.ChannelKey == channelKey);
    }

    /// <summary>Agenda (ou começa já, se a hora de início já passou) a gravação de um canal.</summary>
    public Recording Schedule(Playlist playlist, Channel channel, string title, DateTime startUtc, DateTime stopUtc)
    {
        if (stopUtc <= DateTime.UtcNow) throw new UserFacingException("Esse programa já terminou.");
        if (stopUtc <= startUtc) throw new UserFacingException("A hora de fim tem de ser depois da hora de início.");

        var overlapping = _repository.GetAll().Any(r =>
            r.PlaylistId == playlist.Id && r.ChannelKey == channel.Key &&
            r.Status is RecordingStatus.Scheduled or RecordingStatus.Recording &&
            r.StartUtc < stopUtc && startUtc < r.StopUtc);
        if (overlapping) throw new UserFacingException("Já há uma gravação deste canal a essa hora.");

        var recording = new Recording
        {
            PlaylistId = playlist.Id,
            ChannelKey = channel.Key,
            ChannelName = channel.Name,
            Title = string.IsNullOrWhiteSpace(title) ? channel.Name : title.Trim(),
            StartUtc = startUtc,
            StopUtc = stopUtc,
            Status = RecordingStatus.Scheduled,
            CreatedUtc = DateTime.UtcNow,
        };
        recording.Id = _repository.Insert(recording);
        RaiseChanged();
        if (startUtc <= DateTime.UtcNow.AddSeconds(5)) ThreadPool.QueueUserWorkItem(_ => Tick());
        return recording;
    }

    /// <summary>Para uma gravação em curso (fica concluída) ou cancela uma agendada.</summary>
    public void Stop(long id)
    {
        Active? active;
        lock (_sync) _active.Remove(id, out active);
        if (active is not null)
        {
            Finish(active, RecordingStatus.Completed, null);
            return;
        }

        var r = _repository.Get(id);
        if (r is { Status: RecordingStatus.Scheduled })
        {
            r.Status = RecordingStatus.Cancelled;
            _repository.Update(r);
            RaiseChanged();
        }
    }

    /// <summary>Apaga o registo e, se pedido, os ficheiros (todas as partes).</summary>
    public void Delete(long id, bool deleteFiles)
    {
        Stop(id);
        var r = _repository.Get(id);
        if (r is null) return;
        if (deleteFiles)
        {
            foreach (var file in PartFiles(r.FilePath))
            {
                try
                {
                    File.Delete(file);
                }
                catch (IOException ex)
                {
                    throw new UserFacingException($"Não foi possível apagar o ficheiro (está a ser usado?): {Path.GetFileName(file)}", ex);
                }
            }
        }
        _repository.Delete(id);
        RaiseChanged();
    }

    /// <summary>Ficheiro principal e partes seguintes ("… (parte 2).ts").</summary>
    public static IEnumerable<string> PartFiles(string? path)
    {
        if (string.IsNullOrEmpty(path)) yield break;
        if (File.Exists(path)) yield return path;
        for (var part = 2; part <= MaxParts; part++)
        {
            var p = PartPath(path, part);
            if (File.Exists(p)) yield return p;
        }
    }

    public static long TotalSize(string? path) => PartFiles(path).Sum(f => new FileInfo(f).Length);

    public void Dispose()
    {
        _disposed = true;
        _timer.Dispose();
        List<Active> active;
        lock (_sync)
        {
            active = _active.Values.ToList();
            _active.Clear();
        }
        foreach (var a in active) Finish(a, RecordingStatus.Completed, "Interrompida ao fechar a app.");
    }

    // ───────────────────────────── Interno ─────────────────────────────

    private sealed class Active
    {
        public required Recording Recording { get; init; }
        public required MediaPlayer Player { get; set; }
        public Media? Media { get; set; }
        public int Part { get; set; } = 1;
        public bool Restarting { get; set; }
    }

    private void Tick()
    {
        if (_disposed) return;
        try
        {
            var now = DateTime.UtcNow;

            // Terminar as que chegaram à hora de fim.
            List<Active> due;
            lock (_sync) due = _active.Values.Where(a => a.Recording.StopUtc <= now).ToList();
            foreach (var a in due)
            {
                lock (_sync) _active.Remove(a.Recording.Id);
                Finish(a, RecordingStatus.Completed, null);
            }

            // Começar as agendadas.
            foreach (var r in _repository.GetAll().Where(r => r.Status == RecordingStatus.Scheduled && r.StartUtc <= now.AddSeconds(5)))
            {
                lock (_sync)
                {
                    if (_active.ContainsKey(r.Id)) continue;
                }
                if (r.StopUtc <= now)
                {
                    r.Status = RecordingStatus.Failed;
                    r.Error = "A hora marcada já tinha passado.";
                    _repository.Update(r);
                    RaiseChanged();
                    continue;
                }
                Begin(r);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Erro na agenda de gravações.", ex);
        }
    }

    private void Begin(Recording r)
    {
        try
        {
            var playlist = _playlists.Get(r.PlaylistId) ?? throw new UserFacingException("A playlist desta gravação já não existe.");
            var channel = _channels.GetByKey(r.PlaylistId, r.ChannelKey) ?? throw new UserFacingException("O canal já não existe na playlist.");

            Directory.CreateDirectory(Folder);
            r.FilePath = UniquePath(Path.Combine(Folder, FileName(r)));
            var active = new Active { Recording = r, Player = new MediaPlayer(_libVlc) };
            lock (_sync) _active[r.Id] = active;

            StartPart(active, playlist, channel);
            r.Status = RecordingStatus.Recording;
            r.Error = null;
            _repository.Update(r);
            Log.Info($"Gravação iniciada ({r.Id}).");
        }
        catch (Exception ex)
        {
            lock (_sync) _active.Remove(r.Id);
            r.Status = RecordingStatus.Failed;
            r.Error = ex is UserFacingException ? ex.Message : "Não foi possível iniciar a gravação.";
            if (ex is not UserFacingException) Log.Error("Erro ao iniciar gravação.", ex);
            _repository.Update(r);
        }
        RaiseChanged();
    }

    private void StartPart(Active active, Playlist playlist, Channel channel)
    {
        var url = _playlistService.ResolveStreamUrl(playlist, channel);
        var path = active.Part == 1 ? active.Recording.FilePath! : PartPath(active.Recording.FilePath!, active.Part);
        var options = new List<string>
        {
            // Copiar o stream para o ficheiro, sem imagem nem som no PC.
            $":sout=#std{{access=file,mux=ts,dst=\"{path}\"}}",
            ":sout-all",
            ":network-caching=3000",
        };
        if (!string.IsNullOrWhiteSpace(channel.UserAgent)) options.Add($":http-user-agent={channel.UserAgent}");
        if (!string.IsNullOrWhiteSpace(channel.Referrer)) options.Add($":http-referrer={channel.Referrer}");

        var media = new Media(_libVlc, new Uri(url), options.ToArray());
        var player = active.Player;
        player.EncounteredError += (_, _) => OnProblem(active, playlist, channel);
        player.EndReached += (_, _) => OnProblem(active, playlist, channel);
        active.Media?.Dispose();
        active.Media = media;
        player.Play(media);
    }

    /// <summary>A ligação caiu antes da hora de fim: tentar continuar numa nova parte.</summary>
    private void OnProblem(Active active, Playlist playlist, Channel channel)
    {
        // Eventos do VLC: nunca chamar o MediaPlayer daqui (deadlock) → thread pool.
        ThreadPool.QueueUserWorkItem(async _ =>
        {
            lock (_sync)
            {
                if (active.Restarting || !_active.ContainsKey(active.Recording.Id)) return;
                active.Restarting = true;
            }
            await Task.Delay(5000);

            lock (_sync)
            {
                if (!_active.ContainsKey(active.Recording.Id)) return;
                active.Restarting = false;
            }
            if (active.Recording.StopUtc <= DateTime.UtcNow.AddSeconds(10) || active.Part >= MaxParts)
            {
                lock (_sync) _active.Remove(active.Recording.Id);
                Finish(active, RecordingStatus.Completed, active.Part >= MaxParts ? "A ligação caiu várias vezes." : null);
                return;
            }

            try
            {
                active.Player.Stop();
                active.Player.Dispose();
                active.Player = new MediaPlayer(_libVlc);
                active.Part++;
                Log.Info($"Gravação {active.Recording.Id}: ligação perdida, a continuar na parte {active.Part}.");
                StartPart(active, playlist, channel);
                active.Recording.Error = $"A ligação caiu; gravado em {active.Part} partes.";
                _repository.Update(active.Recording);
                RaiseChanged();
            }
            catch (Exception ex)
            {
                Log.Error("Erro ao retomar gravação.", ex);
                lock (_sync) _active.Remove(active.Recording.Id);
                Finish(active, RecordingStatus.Failed, "A ligação caiu e não foi possível continuar.");
            }
        });
    }

    private void Finish(Active active, RecordingStatus status, string? error)
    {
        try
        {
            active.Player.Stop();
            active.Player.Dispose();
            active.Media?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Warn("Erro ao parar a gravação.", ex);
        }

        var r = active.Recording;
        if (status == RecordingStatus.Completed && !HasContent(r.FilePath))
        {
            status = RecordingStatus.Failed;
            error = "Não foi gravado nada (o canal pode estar offline ou o fornecedor não permite mais ligações).";
        }
        r.Status = status;
        r.Error = error ?? r.Error;
        if (status == RecordingStatus.Completed && r.StopUtc > DateTime.UtcNow) r.StopUtc = DateTime.UtcNow;
        _repository.Update(r);
        Log.Info($"Gravação {r.Id} terminada: {status}.");
        RaiseChanged();
    }

    private void RaiseChanged()
    {
        try
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            Log.Warn("Erro ao notificar gravações.", ex);
        }
    }

    private static bool HasContent(string? path) => TotalSize(path) > 0;

    internal static string FileName(Recording r)
    {
        var local = r.StartUtc.ToLocalTime();
        var name = r.Title == r.ChannelName
            ? $"{r.ChannelName} - {local:yyyy-MM-dd HH-mm}"
            : $"{r.ChannelName} - {r.Title} - {local:yyyy-MM-dd HH-mm}";
        return Sanitize(name) + ".ts";
    }

    internal static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(name.Length);
        foreach (var ch in name) sb.Append(invalid.Contains(ch) ? '_' : ch);
        var s = sb.ToString().Trim().TrimEnd('.');
        return s.Length > 150 ? s[..150] : s;
    }

    private static string PartPath(string path, int part) =>
        Path.Combine(Path.GetDirectoryName(path)!, $"{Path.GetFileNameWithoutExtension(path)} (parte {part}){Path.GetExtension(path)}");

    private static string UniquePath(string path)
    {
        if (!File.Exists(path)) return path;
        for (var i = 2; ; i++)
        {
            var candidate = Path.Combine(Path.GetDirectoryName(path)!, $"{Path.GetFileNameWithoutExtension(path)} ({i}){Path.GetExtension(path)}");
            if (!File.Exists(candidate)) return candidate;
        }
    }
}
