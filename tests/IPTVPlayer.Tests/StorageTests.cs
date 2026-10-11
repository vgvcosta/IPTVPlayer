using System.Diagnostics;
using System.Net;
using System.Text;
using IPTVPlayer.Models;
using IPTVPlayer.Services;
using IPTVPlayer.Services.Data;
using IPTVPlayer.Services.Net;
using IPTVPlayer.Services.Parsing;
using IPTVPlayer.Services.Security;
using IPTVPlayer.Services.Xtream;
using Microsoft.Data.Sqlite;

namespace IPTVPlayer.Tests;

/// <summary>Base de dados e chave num diretório temporário, apagado no fim.</summary>
public sealed class TempEnvironment : IDisposable
{
    public TempEnvironment()
    {
        Directory = Path.Combine(Path.GetTempPath(), "IPTVPlayerTests", Guid.NewGuid().ToString("N"));
        Paths = new AppPaths(Directory);
        Protector = new CredentialProtector();
        Database = new AppDatabase(Paths, Protector);
        Database.Initialize();
        Cipher = new StreamUrlCipher(Paths, Protector);
        Playlists = new PlaylistRepository(Database);
        Channels = new ChannelRepository(Database);
        Favorites = new FavoritesRepository(Database);
        Settings = new SettingsRepository(Database);
    }

    public string Directory { get; }
    public AppPaths Paths { get; }
    public AppDatabase Database { get; }
    public CredentialProtector Protector { get; }
    public StreamUrlCipher Cipher { get; }
    public PlaylistRepository Playlists { get; }
    public ChannelRepository Channels { get; }
    public FavoritesRepository Favorites { get; }
    public SettingsRepository Settings { get; }

    public PlaylistService CreateService(XtreamClient? xtream = null)
    {
        var clients = new HttpClients();
        return new PlaylistService(clients, new M3uParser(), xtream ?? new XtreamClient(clients), Protector, Cipher, Playlists, Channels);
    }

    public void Dispose()
    {
        Cipher.Dispose();
        SqliteConnection.ClearAllPools();
        try
        {
            System.IO.Directory.Delete(Directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

public class SecurityTests
{
    [Fact]
    public void Dpapi_roundtrip_and_ciphertext_differs_from_plaintext()
    {
        var protector = new CredentialProtector();
        var data = protector.Protect("segredo-123");

        Assert.DoesNotContain("segredo", Encoding.UTF8.GetString(data));
        Assert.Equal("segredo-123", protector.Unprotect(data));
    }

    [Fact]
    public void Stream_url_cipher_roundtrip_and_key_is_reused()
    {
        using var env = new TempEnvironment();
        var sealedUrl = env.Cipher.Seal("http://host/user/pass/1.ts");

        Assert.DoesNotContain("pass", Encoding.UTF8.GetString(sealedUrl));
        Assert.Equal("http://host/user/pass/1.ts", env.Cipher.Open(sealedUrl));

        // Uma segunda instância (novo arranque) lê a mesma chave.
        using var second = new StreamUrlCipher(env.Paths, env.Protector);
        Assert.False(second.KeyWasReset);
        Assert.Equal("http://host/user/pass/1.ts", second.Open(sealedUrl));
    }

    [Fact]
    public void Tampered_data_gives_user_facing_error()
    {
        using var env = new TempEnvironment();
        var data = env.Cipher.Seal("http://x");
        data[^1] ^= 0xFF;

        Assert.Throws<UserFacingException>(() => env.Cipher.Open(data));
    }
}

public class RepositoryTests
{
    [Fact]
    public void Replace_and_read_20000_channels_with_favorites_and_settings()
    {
        using var env = new TempEnvironment();
        var id = env.Playlists.Insert(new Playlist
        {
            Name = "Teste",
            Kind = PlaylistKind.M3uUrl,
            SourceProtected = env.Protector.Protect("http://x/get.php?username=a&password=b"),
            CreatedUtc = DateTime.UtcNow,
            LastLoadedUtc = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc),
        });

        var channels = Enumerable.Range(0, 20_000).Select(i => new Channel
        {
            Key = "k" + i,
            Name = "Canal " + i,
            GroupTitle = "G" + (i % 50),
            LogoUrl = i % 2 == 0 ? null : "http://l/" + i,
            UrlSealed = env.Cipher.Seal("http://s/" + i),
            SortOrder = i,
        }).ToList();

        var sw = Stopwatch.StartNew();
        env.Channels.ReplaceAll(id, channels);
        var loaded = env.Channels.GetByPlaylist(id);
        sw.Stop();

        Assert.Equal(20_000, loaded.Count);
        Assert.Equal("k19999", loaded[^1].Key);
        Assert.Equal("http://s/123", env.Cipher.Open(loaded[123].UrlSealed));
        Assert.True(sw.ElapsedMilliseconds < 5000, $"Gravar+ler demorou {sw.ElapsedMilliseconds} ms");

        var playlist = env.Playlists.Get(id)!;
        Assert.Equal(20_000, playlist.ChannelCount);
        Assert.Equal(new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc), playlist.LastLoadedUtc);
        Assert.Equal("http://x/get.php?username=a&password=b", env.Protector.Unprotect(playlist.SourceProtected));

        env.Favorites.Add(id, "k5");
        env.Favorites.Add(id, "k5");
        Assert.Equal(["k5"], env.Favorites.GetKeys(id));

        env.Settings.Set(SettingsRepository.LastChannelKey(id), "k5");
        Assert.Equal("k5", env.Settings.Get(SettingsRepository.LastChannelKey(id)));

        // Apagar a playlist apaga canais e favoritos (ON DELETE CASCADE).
        env.Playlists.Delete(id);
        Assert.Empty(env.Channels.GetByPlaylist(id));
        Assert.Empty(env.Favorites.GetKeys(id));
    }
}

public class PlaylistServiceTests
{
    [Fact]
    public async Task Adds_m3u_file_and_resolves_stream_url()
    {
        using var env = new TempEnvironment();
        var file = Path.Combine(env.Directory, "lista.m3u");
        await File.WriteAllTextAsync(file, """
            #EXTM3U
            #EXTINF:-1 tvg-logo="http://l/a.png" group-title="Notícias",Canal A
            http://s/a.ts
            #EXTINF:-1,Canal B
            http://s/b.ts
            """);

        var service = env.CreateService();
        var data = await service.AddAsync(new NewPlaylistRequest(PlaylistKind.M3uFile, null, file), null, CancellationToken.None);

        Assert.Equal("lista", data.Playlist.Name);
        Assert.Equal(2, data.Channels.Count);
        Assert.Equal("Sem grupo", data.Channels[1].GroupTitle);
        Assert.Equal("http://s/b.ts", service.ResolveStreamUrl(data.Playlist, data.Channels[1]));

        var cached = await service.LoadCachedChannelsAsync(data.Playlist.Id);
        Assert.Equal(data.Channels.Select(c => c.Key), cached.Select(c => c.Key));
    }

    [Fact]
    public async Task Invalid_inputs_have_clear_messages()
    {
        using var env = new TempEnvironment();
        var service = env.CreateService();

        var ex1 = await Assert.ThrowsAsync<UserFacingException>(() =>
            service.AddAsync(new NewPlaylistRequest(PlaylistKind.M3uUrl, null, "isto não é um url"), null, CancellationToken.None));
        Assert.Contains("não é válido", ex1.Message);

        var ex2 = await Assert.ThrowsAsync<UserFacingException>(() =>
            service.AddAsync(new NewPlaylistRequest(PlaylistKind.M3uFile, null, @"C:\nao\existe.m3u"), null, CancellationToken.None));
        Assert.Contains("não existe", ex2.Message);

        var empty = Path.Combine(env.Directory, "vazia.m3u");
        await File.WriteAllTextAsync(empty, "#EXTM3U\n");
        var ex3 = await Assert.ThrowsAsync<UserFacingException>(() =>
            service.AddAsync(new NewPlaylistRequest(PlaylistKind.M3uFile, null, empty), null, CancellationToken.None));
        Assert.Contains("não contém canais", ex3.Message);

        Assert.Empty(env.Playlists.GetAll());
    }

    [Fact]
    public async Task Adds_xtream_account_without_storing_credentials_in_channels()
    {
        using var env = new TempEnvironment();
        var handler = new XtreamClientTests.StubHandler(request =>
        {
            var query = request.RequestUri!.Query;
            var body = query.Contains("get_live_categories")
                ? """[{"category_id":"2","category_name":"Desporto"},{"category_id":"1","category_name":"Notícias"}]"""
                : query.Contains("get_live_streams")
                    ? """[{"num":1,"name":"News","stream_id":11,"category_id":"1"},{"num":2,"name":"Sport","stream_id":22,"category_id":"2"},{"num":3,"name":"Outro","stream_id":33,"category_id":"99"}]"""
                    : """{"user_info":{"auth":1,"status":"Active"}}""";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
        });
        var service = env.CreateService(new XtreamClient(new HttpClient(handler)));

        var data = await service.AddAsync(
            new NewPlaylistRequest(PlaylistKind.Xtream, "Casa", "srv.example:8080", "joao", "segredo"), null, CancellationToken.None);

        // Ordem das categorias do servidor; canal sem categoria conhecida no fim.
        Assert.Equal(["Sport", "News", "Outro"], data.Channels.Select(c => c.Name));
        Assert.Equal(["Desporto", "Notícias", "Sem categoria"], data.Channels.Select(c => c.GroupTitle));
        Assert.Equal("x:22", data.Channels[0].Key);

        Assert.Equal("22.ts", env.Cipher.Open(data.Channels[0].UrlSealed));
        Assert.Equal("http://srv.example:8080/live/joao/segredo/22.ts", service.ResolveStreamUrl(data.Playlist, data.Channels[0]));

        // Nada em texto simples na base de dados.
        SqliteConnection.ClearAllPools();
        var dbBytes = await File.ReadAllBytesAsync(env.Paths.DatabasePath);
        var walPath = env.Paths.DatabasePath + "-wal";
        if (File.Exists(walPath)) dbBytes = [.. dbBytes, .. await File.ReadAllBytesAsync(walPath)];
        var dbText = Encoding.UTF8.GetString(dbBytes);
        Assert.DoesNotContain("segredo", dbText);
        Assert.DoesNotContain("joao", dbText);
        Assert.DoesNotContain("srv.example", dbText);
    }
}
