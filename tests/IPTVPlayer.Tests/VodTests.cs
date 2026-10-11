using System.Net;
using System.Text.Json;
using IPTVPlayer.Models;
using IPTVPlayer.Services;
using IPTVPlayer.Services.Data;
using IPTVPlayer.Services.Xtream;

namespace IPTVPlayer.Tests;

public class VodTests
{
    [Fact]
    public void Series_info_with_episodes_as_object_keyed_by_season()
    {
        using var doc = JsonDocument.Parse("""
            {
              "seasons": [ { "season_number": 1, "name": "Season 1" }, { "season_number": 2, "name": "Parte Dois" }, { "season_number": 3, "name": "Vazia" } ],
              "info": { "name": "Série", "plot": "Enredo.", "genre": "Drama", "releaseDate": "2021-05-01" },
              "episodes": {
                "2": [ { "id": "202", "episode_num": "1", "title": "Regresso", "container_extension": "mkv", "info": { "duration": "00:45:00" } } ],
                "1": [
                  { "id": "102", "episode_num": 2, "title": "Segundo", "container_extension": "mp4", "season": 1 },
                  { "id": 101, "episode_num": 1, "title": "Piloto", "container_extension": "mp4", "season": "1",
                    "info": { "plot": "Começa.", "movie_image": "http://img/1.jpg" } }
                ]
              }
            }
            """);

        var d = XtreamClient.ParseSeriesInfo(doc.RootElement);

        Assert.Equal("Enredo.", d.Plot);
        Assert.Equal("Drama", d.Genre);
        // "Season 1" → "Temporada 1"; nomes próprios mantêm-se; temporadas vazias não aparecem.
        Assert.Equal(["Temporada 1", "Parte Dois"], d.Seasons.Select(s => s.Name));
        Assert.Equal([101L, 102L, 202L], d.Episodes.Select(e => e.Id));
        Assert.Equal(2, d.Episodes[2].Season);
        Assert.Equal("Começa.", d.Episodes[0].Plot);
        Assert.Equal("mkv", d.Episodes[2].Extension);
    }

    [Fact]
    public void Series_info_with_episodes_as_array_of_arrays()
    {
        using var doc = JsonDocument.Parse("""
            { "info": {}, "episodes": [ [ { "id": "5", "episode_num": 1, "season": 1, "title": "" } ], [ { "id": "6", "episode_num": 1, "season": 2 } ] ] }
            """);

        var d = XtreamClient.ParseSeriesInfo(doc.RootElement);

        Assert.Equal(2, d.Episodes.Count);
        Assert.Equal("Episódio 1", d.Episodes[0].Title);
        Assert.Equal(["Temporada 1", "Temporada 2"], d.Seasons.Select(s => s.Name));
    }

    [Fact]
    public void Series_info_that_is_not_an_object_gives_clear_error()
    {
        using var doc = JsonDocument.Parse("[]");
        Assert.Throws<UserFacingException>(() => XtreamClient.ParseSeriesInfo(doc.RootElement));
    }

    [Theory]
    [InlineData("2019", null, null, "2019")]
    [InlineData(null, "2021-05-01", null, "2021")]
    [InlineData(null, null, "O Filme (2018) [PT]", "2018")]
    [InlineData(null, null, "Blade Runner 2049", "2049")]
    [InlineData(null, "", "Sem ano", null)]
    public void Year_is_taken_from_year_release_date_or_name(string? year, string? release, string? name, string? expected) =>
        Assert.Equal(expected, VodService.YearOf(year, release, name));

    [Fact]
    public void Vod_urls_use_container_extension_and_escape_credentials()
    {
        var c = new XtreamCredentials("http://srv:80", "user", "p@ss");
        Assert.Equal("http://srv:80/movie/user/p%40ss/77.mkv", XtreamClient.BuildMovieUrl(c, 77, "mkv"));
        Assert.Equal("http://srv:80/series/user/p%40ss/9.mp4", XtreamClient.BuildEpisodeUrl(c, 9, null));
        Assert.Equal("http://srv:80/movie/user/p%40ss/1.mp4", XtreamClient.BuildMovieUrl(c, 1, "../evil"));
        Assert.Equal("http://srv:80/xmltv.php?username=user&password=p%40ss", XtreamClient.BuildXmltvUrl(c));
    }

    [Fact]
    public async Task Fetches_movies_caches_them_and_builds_play_requests()
    {
        using var env = new TempEnvironment();
        var calls = 0;
        var handler = new XtreamClientTests.StubHandler(request =>
        {
            var q = request.RequestUri!.Query;
            string body;
            if (q.Contains("get_vod_categories")) body = """[{"category_id":"1","category_name":"Ação"},{"category_id":"2","category_name":"Comédia"}]""";
            else if (q.Contains("get_vod_streams"))
            {
                calls++;
                body = """
                    [{"name":"Riso (2020)","stream_id":"11","category_id":"2","rating":"6,5","container_extension":"mkv","stream_icon":"http://p/11.jpg"},
                     {"name":"Explosão","stream_id":12,"category_id":1,"rating":"0","releasedate":"2019-03-03"}]
                    """;
            }
            else if (q.Contains("get_live_categories")) body = "[]";
            else if (q.Contains("get_live_streams")) body = """[{"name":"Canal","stream_id":1}]""";
            else body = """{"user_info":{"auth":1,"status":"Active"}}""";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
        });
        var xtream = new XtreamClient(new HttpClient(handler));
        var playlists = env.CreateService(xtream);
        var data = await playlists.AddAsync(new NewPlaylistRequest(PlaylistKind.Xtream, "X", "srv:8080", "u", "p"), null, CancellationToken.None);
        var vod = new VodService(xtream, playlists, new VodRepository(env.Database));

        var movies = await vod.GetAsync(data.Playlist, VodKind.Movie, false, null, null, CancellationToken.None);

        // Ordem das categorias do servidor (Ação antes de Comédia).
        Assert.Equal(["Explosão", "Riso (2020)"], movies.Select(m => m.Name));
        Assert.Equal("2019", movies[0].Year);
        Assert.Null(movies[0].Rating);
        Assert.Equal("6.5", movies[1].Rating);
        Assert.Equal("2020", movies[1].Year);
        Assert.Equal("Comédia", movies[1].Category);

        // Segunda chamada vem da cache.
        var cached = await vod.GetAsync(data.Playlist, VodKind.Movie, false, null, null, CancellationToken.None);
        Assert.Equal(2, cached.Count);
        Assert.Equal(1, calls);

        var request = vod.BuildMovieRequest(data.Playlist, cached[1]);
        Assert.Equal("http://srv:8080/movie/u/p/11.mkv", request.Url);
        Assert.False(request.IsLive);

        // EPG do Xtream: xmltv.php do servidor (montado em memória).
        Assert.Equal(["http://srv:8080/xmltv.php?username=u&password=p"], playlists.GetEpgSources(data.Playlist));
    }
}
