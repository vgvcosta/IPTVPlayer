using System.Diagnostics;
using IPTVPlayer.Models;
using IPTVPlayer.Services.Data;
using IPTVPlayer.Services.Net;

namespace IPTVPlayer.Services.Epg;

public sealed record EpgImportResult(int MatchedChannels, int Programmes, int FailedSources);

/// <summary>Descarrega/lê o XMLTV, associa aos canais e guarda na BD.</summary>
public sealed class EpgService
{
    /// <summary>O EPG é atualizado automaticamente quando tem mais do que isto.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromHours(12);

    private readonly HttpClient _http;
    private readonly PlaylistService _playlists;
    private readonly EpgRepository _repository;

    public EpgService(HttpClients clients, PlaylistService playlists, EpgRepository repository)
    {
        _http = clients.Api;
        _playlists = playlists;
        _repository = repository;
    }

    public bool HasSource(Playlist playlist) => _playlists.GetEpgSources(playlist).Count > 0;

    public bool IsStale(Playlist playlist) =>
        playlist.EpgLoadedUtc is null || DateTime.UtcNow - playlist.EpgLoadedUtc > MaxAge;

    public Task<EpgGuide> LoadAsync(long playlistId) => Task.Run(() => _repository.Load(playlistId));

    public Task<string?> GetDescriptionAsync(long playlistId, EpgProgramme programme) =>
        Task.Run(() => _repository.GetDescription(playlistId, programme.EpgId, programme.Start));

    public Task<EpgImportResult> ImportAsync(Playlist playlist, IReadOnlyList<Channel> channels,
        IProgress<LoadProgress>? progress, CancellationToken ct) =>
        Task.Run(async () =>
        {
            var sources = _playlists.GetEpgSources(playlist);
            if (sources.Count == 0)
                throw new UserFacingException("Esta playlist não indica um EPG. Define um URL de EPG em \"Editar playlist\".");

            var parser = new XmltvParser(channels.Select(c => new EpgChannelRef(c.Key, c.TvgId, c.TvgName, c.Name)));
            var failed = 0;
            UserFacingException? lastError = null;

            for (var i = 0; i < sources.Count; i++)
            {
                var label = sources.Count > 1 ? $" ({i + 1}/{sources.Count})" : "";
                try
                {
                    await ReadSourceAsync(sources[i], parser, label, progress, ct).ConfigureAwait(false);
                }
                catch (UserFacingException ex)
                {
                    failed++;
                    lastError = ex;
                    Log.Warn($"Fonte de EPG {i + 1} falhou: {ex.Message}");
                }
            }

            if (failed == sources.Count)
                throw new UserFacingException($"Não foi possível carregar o EPG. {lastError?.Message}", lastError);

            progress?.Report(new LoadProgress("A guardar o EPG…"));
            var result = parser.Complete();
            _repository.ReplaceAll(playlist.Id, result, ct);

            playlist.EpgLoadedUtc = DateTime.UtcNow;
            await _playlists.SaveAsync(playlist).ConfigureAwait(false);

            var matched = result.KeyToEpgId.Count;
            Log.Info($"EPG carregado: {matched} canais, {result.Programmes.Count} programas.");
            return new EpgImportResult(matched, result.Programmes.Count, failed);
        }, ct);

    private async Task ReadSourceAsync(string source, XmltvParser parser, string label,
        IProgress<LoadProgress>? progress, CancellationToken ct)
    {
        Stream raw;
        long? length;
        HttpResponseMessage? response = null;

        if (File.Exists(source))
        {
            raw = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 81920);
            length = raw.Length;
        }
        else
        {
            progress?.Report(new LoadProgress($"A ligar ao servidor de EPG{label}…"));
            response = await HttpHelper.GetAsync(_http, HttpHelper.ValidateHttpUrl(source, "URL do EPG"), ct).ConfigureAwait(false);
            length = response.Content.Headers.ContentLength;
            raw = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        }

        var throttle = Stopwatch.StartNew();
        void OnBytes(long read)
        {
            if (throttle.ElapsedMilliseconds < 150) return;
            throttle.Restart();
            var mb = read / 1024d / 1024d;
            progress?.Report(length is > 0
                ? new LoadProgress($"A carregar EPG{label}… {mb:0.0} MB", Math.Min(1, (double)read / length.Value))
                : new LoadProgress($"A carregar EPG{label}… {mb:0.0} MB"));
        }

        try
        {
            using (response)
            using (var stream = new ProgressStream(raw, OnBytes))
            {
                parser.Parse(stream, ct);
            }
        }
        catch (IOException ex)
        {
            throw new UserFacingException("A ligação foi interrompida durante o download do EPG.", ex);
        }
        catch (InvalidDataException ex)
        {
            throw new UserFacingException("O ficheiro de EPG comprimido (.gz) está corrompido.", ex);
        }
    }
}
