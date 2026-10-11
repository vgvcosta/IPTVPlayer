using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using IPTVPlayer.Models;
using IPTVPlayer.Services.Data;
using IPTVPlayer.Services.Net;
using IPTVPlayer.Services.Parsing;
using IPTVPlayer.Services.Security;
using IPTVPlayer.Services.Xtream;

namespace IPTVPlayer.Services;

/// <summary>
/// Adicionar, atualizar, ler e remover playlists. Todo o trabalho pesado (download, parsing,
/// encriptação, SQLite) corre fora da thread de UI.
/// </summary>
public sealed class PlaylistService
{
    private const string NoGroup = "Sem grupo";

    private readonly HttpClient _http;
    private readonly M3uParser _parser;
    private readonly XtreamClient _xtream;
    private readonly CredentialProtector _protector;
    private readonly StreamUrlCipher _cipher;
    private readonly PlaylistRepository _playlists;
    private readonly ChannelRepository _channels;

    public PlaylistService(HttpClients clients, M3uParser parser, XtreamClient xtream, CredentialProtector protector,
        StreamUrlCipher cipher, PlaylistRepository playlists, ChannelRepository channels)
    {
        _http = clients.Api;
        _parser = parser;
        _xtream = xtream;
        _protector = protector;
        _cipher = cipher;
        _playlists = playlists;
        _channels = channels;
    }

    public Task<List<Playlist>> GetPlaylistsAsync() => Task.Run(_playlists.GetAll);

    public Task<List<Channel>> LoadCachedChannelsAsync(long playlistId) =>
        Task.Run(() => _channels.GetByPlaylist(playlistId));

    public Task DeleteAsync(long playlistId) => Task.Run(() => _playlists.Delete(playlistId));

    /// <summary>Descarrega e valida a playlist; só depois a guarda.</summary>
    public Task<PlaylistData> AddAsync(NewPlaylistRequest request, IProgress<LoadProgress>? progress, CancellationToken ct) =>
        Task.Run(async () =>
        {
            var source = Normalize(request);
            var (channels, epgUrl, timezone) = await FetchAsync(source, progress, ct).ConfigureAwait(false);

            progress?.Report(new LoadProgress($"A guardar {channels.Count:N0} canais…"));
            var now = DateTime.UtcNow;
            var playlist = new Playlist
            {
                Name = string.IsNullOrWhiteSpace(request.Name) ? DefaultName(source) : request.Name.Trim(),
                Kind = source.Kind,
                SourceProtected = _protector.Protect(source.Location),
                UsernameProtected = source.Username is null ? null : _protector.Protect(source.Username),
                PasswordProtected = source.Password is null ? null : _protector.Protect(source.Password),
                EpgUrlProtected = ProtectOrNull(epgUrl),
                ServerTimezone = timezone,
                CustomEpgUrlProtected = ProtectOrNull(NormalizeEpgUrl(request.CustomEpgUrl)),
                CreatedUtc = now,
                LastLoadedUtc = now,
            };

            ct.ThrowIfCancellationRequested();
            playlist.Id = _playlists.Insert(playlist);
            try
            {
                _channels.ReplaceAll(playlist.Id, channels, ct);
            }
            catch
            {
                _playlists.Delete(playlist.Id);
                throw;
            }

            playlist.ChannelCount = channels.Count;
            Log.Info($"Playlist adicionada ({playlist.KindLabel}, {channels.Count} canais).");
            return new PlaylistData(playlist, channels);
        }, ct);

    /// <summary>Volta a descarregar a playlist e substitui a cache de canais.</summary>
    public Task<PlaylistData> RefreshAsync(Playlist playlist, IProgress<LoadProgress>? progress, CancellationToken ct) =>
        Task.Run(async () =>
        {
            var source = GetSource(playlist);
            var (channels, epgUrl, timezone) = await FetchAsync(source, progress, ct).ConfigureAwait(false);

            progress?.Report(new LoadProgress($"A guardar {channels.Count:N0} canais…"));
            _channels.ReplaceAll(playlist.Id, channels, ct);

            playlist.LastLoadedUtc = DateTime.UtcNow;
            if (epgUrl is not null) playlist.EpgUrlProtected = _protector.Protect(epgUrl);
            playlist.ServerTimezone = timezone ?? playlist.ServerTimezone;
            playlist.ChannelCount = channels.Count;
            _playlists.Update(playlist);
            return new PlaylistData(playlist, channels);
        }, ct);

    /// <summary>
    /// URL real do stream, montado em memória no momento de reproduzir.
    /// Pode lançar <see cref="UserFacingException"/> se os dados não puderem ser desencriptados.
    /// </summary>
    public string ResolveStreamUrl(Playlist playlist, Channel channel)
    {
        var stored = _cipher.Open(channel.UrlSealed);
        if (playlist.Kind != PlaylistKind.Xtream) return stored;

        var source = GetSource(playlist);
        return XtreamClient.BuildLiveUrl(new XtreamCredentials(source.Location, source.Username!, source.Password!), stored);
    }

    /// <summary>
    /// URL de catch-up de um programa que já começou (ou já passou). Lança <see cref="UserFacingException"/>
    /// se o canal não tiver catch-up ou o programa for mais antigo do que o arquivo do canal.
    /// </summary>
    public string ResolveCatchupUrl(Playlist playlist, Channel channel, DateTime startUtc, DateTime stopUtc)
    {
        if (!channel.HasCatchup || channel.CatchupMode is null)
            throw new UserFacingException("Este canal não tem catch-up (arquivo de programas).");
        var now = DateTime.UtcNow;
        if (startUtc > now)
            throw new UserFacingException("Este programa ainda não começou.");
        if (startUtc < now.AddDays(-channel.CatchupDays))
            throw new UserFacingException($"Este programa já não está disponível (o arquivo do canal guarda {channel.CatchupDays} dias).");

        var stored = _cipher.Open(channel.UrlSealed);
        if (playlist.Kind == PlaylistKind.Xtream)
        {
            var c = GetXtreamCredentials(playlist);
            var id = long.Parse(stored.Split('.')[0], System.Globalization.CultureInfo.InvariantCulture);
            return Playback.CatchupUrlBuilder.Xtream(c.Server, c.Username, c.Password, id, startUtc, stopUtc,
                Playback.CatchupUrlBuilder.ResolveZone(playlist.ServerTimezone));
        }

        var source = channel.CatchupSourceSealed is null ? null : _cipher.Open(channel.CatchupSourceSealed);
        return Playback.CatchupUrlBuilder.M3u(channel.CatchupMode, stored, source, startUtc, stopUtc, now);
    }

    /// <summary>Credenciais Xtream (para VOD e EPG). Só existem em memória.</summary>
    public XtreamCredentials GetXtreamCredentials(Playlist playlist)
    {
        if (playlist.Kind != PlaylistKind.Xtream)
            throw new InvalidOperationException("A playlist não é Xtream Codes.");
        var s = GetSource(playlist);
        return new XtreamCredentials(s.Location, s.Username!, s.Password!);
    }

    /// <summary>
    /// Fontes XMLTV por ordem de prioridade: URL manual; senão o url-tvg da playlist (pode ter vários,
    /// separados por vírgulas); senão, no Xtream, o xmltv.php do próprio servidor.
    /// </summary>
    public IReadOnlyList<string> GetEpgSources(Playlist playlist)
    {
        if (playlist.CustomEpgUrlProtected is not null)
            return [_protector.Unprotect(playlist.CustomEpgUrlProtected)];

        if (playlist.EpgUrlProtected is not null)
        {
            var urls = _protector.Unprotect(playlist.EpgUrlProtected)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (urls.Length > 0) return urls;
        }

        return playlist.Kind == PlaylistKind.Xtream
            ? [XtreamClient.BuildXmltvUrl(GetXtreamCredentials(playlist))]
            : [];
    }

    public PlaylistEditInfo GetEditInfo(Playlist p)
    {
        var s = GetSource(p);
        var custom = p.CustomEpgUrlProtected is null ? null : _protector.Unprotect(p.CustomEpgUrlProtected);
        return new PlaylistEditInfo(p.Kind, p.Name, s.Location, s.Username, custom);
    }

    /// <summary>
    /// Edita nome, origem e EPG manual. Se a origem/credenciais mudarem, a playlist é descarregada
    /// primeiro (validação) e só depois guardada. Password vazia = manter a atual.
    /// </summary>
    public Task<PlaylistEditResult> UpdateAsync(Playlist playlist, NewPlaylistRequest request,
        IProgress<LoadProgress>? progress, CancellationToken ct) =>
        Task.Run(async () =>
        {
            var current = GetSource(playlist);
            var effective = request.Kind == PlaylistKind.Xtream && string.IsNullOrEmpty(request.Password)
                ? request with { Password = current.Password }
                : request;
            var source = Normalize(effective with { Kind = playlist.Kind });
            var customEpg = NormalizeEpgUrl(request.CustomEpgUrl);
            var oldCustomEpg = playlist.CustomEpgUrlProtected is null ? null : _protector.Unprotect(playlist.CustomEpgUrlProtected);

            List<Channel>? channels = null;
            if (source != current)
            {
                var (fetched, epgUrl, timezone) = await FetchAsync(source, progress, ct).ConfigureAwait(false);
                channels = fetched;
                progress?.Report(new LoadProgress($"A guardar {channels.Count:N0} canais…"));
                _channels.ReplaceAll(playlist.Id, channels, ct);

                playlist.SourceProtected = _protector.Protect(source.Location);
                playlist.UsernameProtected = ProtectOrNull(source.Username);
                playlist.PasswordProtected = ProtectOrNull(source.Password);
                playlist.EpgUrlProtected = ProtectOrNull(epgUrl);
                playlist.ServerTimezone = timezone;
                playlist.LastLoadedUtc = DateTime.UtcNow;
                playlist.ChannelCount = channels.Count;
            }

            var epgChanged = channels is not null || customEpg != oldCustomEpg;
            playlist.Name = string.IsNullOrWhiteSpace(request.Name) ? playlist.Name : request.Name.Trim();
            playlist.CustomEpgUrlProtected = ProtectOrNull(customEpg);
            if (epgChanged) playlist.EpgLoadedUtc = null;
            _playlists.Update(playlist);

            return new PlaylistEditResult(playlist, channels, epgChanged);
        }, ct);

    public Task SaveAsync(Playlist playlist) => Task.Run(() => _playlists.Update(playlist));

    private byte[]? ProtectOrNull(string? value) =>
        string.IsNullOrEmpty(value) ? null : _protector.Protect(value);

    /// <summary>Aceita URL http(s) ou ficheiro local (.xml / .xml.gz). Vazio = sem EPG manual.</summary>
    private static string? NormalizeEpgUrl(string? input)
    {
        var s = input?.Trim().Trim('"');
        if (string.IsNullOrEmpty(s)) return null;
        if (File.Exists(s)) return Path.GetFullPath(s);
        return HttpHelper.ValidateHttpUrl(s, "URL do EPG").ToString();
    }

    private sealed record Source(PlaylistKind Kind, string Location, string? Username, string? Password);

    private Source GetSource(Playlist p) => new(
        p.Kind,
        _protector.Unprotect(p.SourceProtected),
        p.UsernameProtected is null ? null : _protector.Unprotect(p.UsernameProtected),
        p.PasswordProtected is null ? null : _protector.Unprotect(p.PasswordProtected));

    private static Source Normalize(NewPlaylistRequest r)
    {
        switch (r.Kind)
        {
            case PlaylistKind.M3uUrl:
                return new Source(r.Kind, HttpHelper.ValidateHttpUrl(r.Location, "URL da playlist").ToString(), null, null);

            case PlaylistKind.M3uFile:
                var path = r.Location?.Trim().Trim('"') ?? "";
                if (path.Length == 0) throw new UserFacingException("Escolhe o ficheiro da playlist.");
                if (!File.Exists(path)) throw new UserFacingException("O ficheiro indicado não existe.");
                return new Source(r.Kind, Path.GetFullPath(path), null, null);

            case PlaylistKind.Xtream:
                var server = XtreamClient.NormalizeServer(r.Location);
                if (string.IsNullOrWhiteSpace(r.Username)) throw new UserFacingException("Indica o utilizador.");
                if (string.IsNullOrEmpty(r.Password)) throw new UserFacingException("Indica a password.");
                return new Source(r.Kind, server, r.Username.Trim(), r.Password);

            default:
                throw new ArgumentOutOfRangeException(nameof(r));
        }
    }

    private static string DefaultName(Source s) => s.Kind switch
    {
        PlaylistKind.M3uFile => Path.GetFileNameWithoutExtension(s.Location),
        _ => Uri.TryCreate(s.Location, UriKind.Absolute, out var uri) ? uri.Host : "Playlist",
    };

    private async Task<(List<Channel> Channels, string? EpgUrl, string? ServerTimezone)> FetchAsync(
        Source source, IProgress<LoadProgress>? progress, CancellationToken ct)
    {
        var result = source.Kind == PlaylistKind.Xtream
            ? await FetchXtreamAsync(source, progress, ct).ConfigureAwait(false)
            : await FetchM3uAsync(source, progress, ct).ConfigureAwait(false);

        if (result.Channels.Count == 0)
            throw new UserFacingException("A playlist não contém canais.");
        return result;
    }

    private async Task<(List<Channel> Channels, string? EpgUrl, string? ServerTimezone)> FetchM3uAsync(Source source, IProgress<LoadProgress>? progress, CancellationToken ct)
    {
        Stream raw;
        long? length;
        HttpResponseMessage? response = null;

        if (source.Kind == PlaylistKind.M3uUrl)
        {
            progress?.Report(new LoadProgress("A ligar ao servidor…"));
            response = await HttpHelper.GetAsync(_http, new Uri(source.Location), ct).ConfigureAwait(false);
            length = response.Content.Headers.ContentLength;
            raw = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        }
        else
        {
            try
            {
                raw = new FileStream(source.Location, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new UserFacingException($"Não foi possível abrir o ficheiro: {ex.Message}", ex);
            }
            length = raw.Length;
        }

        var verb = source.Kind == PlaylistKind.M3uUrl ? "A descarregar" : "A ler";
        var throttle = Stopwatch.StartNew();
        void OnBytes(long read)
        {
            if (throttle.ElapsedMilliseconds < 100) return;
            throttle.Restart();
            var mb = read / 1024d / 1024d;
            progress?.Report(length is > 0
                ? new LoadProgress($"{verb} playlist… {mb:0.0} MB", Math.Min(1, (double)read / length.Value))
                : new LoadProgress($"{verb} playlist… {mb:0.0} MB"));
        }

        M3uPlaylist parsed;
        try
        {
            using (response)
            await using (var stream = new ProgressStream(raw, OnBytes))
            using (var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
            {
                parsed = await _parser.ParseAsync(reader, ct).ConfigureAwait(false);
            }
        }
        catch (IOException ex)
        {
            throw new UserFacingException("A ligação foi interrompida durante o download da playlist.", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new UserFacingException($"Erro durante o download da playlist: {HttpHelper.Describe(ex)}.", ex);
        }

        progress?.Report(new LoadProgress($"A processar {parsed.Entries.Count:N0} canais…"));
        var channels = new List<Channel>(parsed.Entries.Count);
        for (var i = 0; i < parsed.Entries.Count; i++)
        {
            if ((i & 1023) == 0) ct.ThrowIfCancellationRequested();
            var e = parsed.Entries[i];
            channels.Add(new Channel
            {
                Key = HashKey(e.Url),
                Name = e.Name.Trim(),
                TvgId = e.TvgId,
                TvgName = e.TvgName,
                LogoUrl = e.TvgLogo,
                GroupTitle = string.IsNullOrWhiteSpace(e.GroupTitle) ? NoGroup : e.GroupTitle.Trim(),
                UrlSealed = _cipher.Seal(e.Url),
                SortOrder = i,
                UserAgent = e.UserAgent,
                Referrer = e.Referrer,
                CatchupDays = e.CatchupDays,
                CatchupMode = e.CatchupDays > 0 ? e.CatchupMode ?? "default" : null,
                CatchupSourceSealed = e.CatchupDays > 0 && !string.IsNullOrWhiteSpace(e.CatchupSource) ? _cipher.Seal(e.CatchupSource) : null,
            });
        }
        return (channels, parsed.EpgUrl, null);
    }

    private async Task<(List<Channel> Channels, string? EpgUrl, string? ServerTimezone)> FetchXtreamAsync(Source source, IProgress<LoadProgress>? progress, CancellationToken ct)
    {
        var credentials = new XtreamCredentials(source.Location, source.Username!, source.Password!);

        progress?.Report(new LoadProgress("A autenticar no servidor Xtream…"));
        var account = await _xtream.AuthenticateAsync(credentials, ct).ConfigureAwait(false);

        progress?.Report(new LoadProgress("A obter categorias…"));
        var categories = await _xtream.GetLiveCategoriesAsync(credentials, ct).ConfigureAwait(false);

        progress?.Report(new LoadProgress("A obter canais em direto…"));
        var streams = await _xtream.GetLiveStreamsAsync(credentials, ct).ConfigureAwait(false);

        var categoryNames = new Dictionary<string, string>(StringComparer.Ordinal);
        var categoryOrder = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var c in categories)
        {
            if (c.CategoryId is null || categoryNames.ContainsKey(c.CategoryId)) continue;
            categoryNames[c.CategoryId] = string.IsNullOrWhiteSpace(c.CategoryName) ? NoGroup : c.CategoryName.Trim();
            categoryOrder[c.CategoryId] = categoryOrder.Count;
        }

        progress?.Report(new LoadProgress($"A processar {streams.Count:N0} canais…"));
        var ordered = streams
            .Where(s => s.StreamId > 0)
            .Select((s, index) => (Stream: s, Index: index))
            .OrderBy(x => x.Stream.CategoryId is not null && categoryOrder.TryGetValue(x.Stream.CategoryId, out var o) ? o : int.MaxValue)
            .ThenBy(x => x.Index)
            .Select(x => x.Stream)
            .ToList();

        var channels = new List<Channel>(ordered.Count);
        for (var i = 0; i < ordered.Count; i++)
        {
            if ((i & 1023) == 0) ct.ThrowIfCancellationRequested();
            var s = ordered[i];
            channels.Add(new Channel
            {
                Key = "x:" + s.StreamId,
                Name = string.IsNullOrWhiteSpace(s.Name) ? $"Canal {s.StreamId}" : s.Name.Trim(),
                TvgId = string.IsNullOrWhiteSpace(s.EpgChannelId) ? null : s.EpgChannelId,
                LogoUrl = string.IsNullOrWhiteSpace(s.StreamIcon) ? null : s.StreamIcon,
                GroupTitle = s.CategoryId is not null && categoryNames.TryGetValue(s.CategoryId, out var name) ? name : "Sem categoria",
                UrlSealed = _cipher.Seal($"{s.StreamId}.{account.StreamExtension}"),
                SortOrder = i,
                CatchupDays = s.TvArchive == 1 ? (int)Math.Clamp(s.TvArchiveDuration, 1, 30) : 0,
                CatchupMode = s.TvArchive == 1 ? "xc" : null,
            });
        }

        // XMLTV do próprio servidor (sem credenciais aqui: o URL completo é montado na fase 2).
        return (channels, null, account.Timezone);
    }

    private static string HashKey(string url) =>
        "m:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url)), 0, 16);
}
