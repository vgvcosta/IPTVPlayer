using System.Net;
using System.Text;
using IPTVPlayer.Models;
using IPTVPlayer.Services;
using IPTVPlayer.Services.Data;
using IPTVPlayer.Services.Parsing;
using IPTVPlayer.Services.Playback;
using IPTVPlayer.Services.Xtream;

namespace IPTVPlayer.Tests;

public class CatchupTests
{
    private static readonly DateTime Start = new(2026, 10, 11, 18, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Stop = Start.AddMinutes(45);
    private static readonly DateTime Now = Start.AddHours(3);
    private static readonly long StartUnix = new DateTimeOffset(Start).ToUnixTimeSeconds();
    private static readonly long NowUnix = new DateTimeOffset(Now).ToUnixTimeSeconds();

    [Fact]
    public void Xtream_url_uses_server_timezone_and_minutes()
    {
        var lisbon = CatchupUrlBuilder.ResolveZone("Europe/Lisbon"); // outubro: UTC+1
        var url = CatchupUrlBuilder.Xtream("http://srv:8080", "u", "p@ss", 42, Start, Stop, lisbon);
        Assert.Equal("http://srv:8080/timeshift/u/p%40ss/45/2026-10-11:19-00/42.ts", url);
    }

    [Fact]
    public void Unknown_timezone_falls_back_to_local() =>
        Assert.Equal(TimeZoneInfo.Local, CatchupUrlBuilder.ResolveZone("Planeta/Marte"));

    [Fact]
    public void Default_mode_fills_placeholders()
    {
        var url = CatchupUrlBuilder.M3u("default", "http://x/live.m3u8",
            "http://x/archive/{utc}/{utcend}?d={duration}&m={duration:60}&o={offset:1}&now={lutc}&t=${start}-{Y}{m}{d}{H}{M}",
            Start, Stop, Now);
        Assert.Equal($"http://x/archive/{StartUnix}/{StartUnix + 2700}?d=2700&m=45&o={NowUnix - StartUnix}&now={NowUnix}&t={StartUnix}-202610111800", url);
    }

    [Fact]
    public void Append_and_relative_default_are_added_to_stream_url()
    {
        Assert.Equal($"http://x/live.m3u8?utc={StartUnix}",
            CatchupUrlBuilder.M3u("append", "http://x/live.m3u8", "?utc={utc}", Start, Stop, Now));
        Assert.Equal($"http://x/live.m3u8?utc={StartUnix}",
            CatchupUrlBuilder.M3u("default", "http://x/live.m3u8", "?utc={utc}", Start, Stop, Now));
    }

    [Fact]
    public void Shift_mode_adds_utc_and_lutc()
    {
        Assert.Equal($"http://x/live.ts?token=1&utc={StartUnix}&lutc={NowUnix}",
            CatchupUrlBuilder.M3u("shift", "http://x/live.ts?token=1", null, Start, Stop, Now));
    }

    [Fact]
    public void Flussonic_hls_and_ts()
    {
        Assert.Equal($"http://f/ch1/index-{StartUnix}-2700.m3u8?token=a",
            CatchupUrlBuilder.M3u("flussonic", "http://f/ch1/index.m3u8?token=a", null, Start, Stop, Now));
        Assert.Equal($"http://f/ch1/timeshift_abs-{StartUnix}.ts",
            CatchupUrlBuilder.M3u("fs", "http://f/ch1/mpegts", null, Start, Stop, Now));
    }

    [Fact]
    public void Xc_mode_derives_timeshift_url_from_stream_url()
    {
        var url = CatchupUrlBuilder.M3u("xc", "http://srv:80/live/user/pass/77.ts", null, Start, Stop, Now);
        Assert.Equal("http://srv:80/timeshift/user/pass/45/2026-10-11:18-00/77.ts", url);
        Assert.Throws<UserFacingException>(() => CatchupUrlBuilder.M3u("xc", "http://srv/stream.m3u8", null, Start, Stop, Now));
    }

    [Fact]
    public async Task M3u_parser_reads_catchup_attributes_and_header_defaults()
    {
        var result = await new M3uParser().ParseAsync(new StringReader("""
            #EXTM3U catchup="append" catchup-source="?utc={utc}" catchup-days="3"
            #EXTINF:-1 tvg-id="a",Herda do cabeçalho
            http://x/a
            #EXTINF:-1 catchup="default" catchup-source="http://x/b/{utc}" tvg-rec="7",Próprio
            http://x/b
            #EXTINF:-1 catchup="shift",Sem dias
            http://x/c
            """));

        Assert.Equal(("append", 3, "?utc={utc}"), (result.Entries[0].CatchupMode, result.Entries[0].CatchupDays, result.Entries[0].CatchupSource));
        Assert.Equal(("default", 7, "http://x/b/{utc}"), (result.Entries[1].CatchupMode, result.Entries[1].CatchupDays, result.Entries[1].CatchupSource));
        Assert.Equal("shift", result.Entries[2].CatchupMode);
        Assert.Equal(3, result.Entries[2].CatchupDays); // dias do cabeçalho
    }

    [Fact]
    public async Task Xtream_catchup_channels_are_imported_and_resolved()
    {
        using var env = new TempEnvironment();
        var handler = new XtreamClientTests.StubHandler(request =>
        {
            var q = request.RequestUri!.Query;
            var body = q.Contains("get_live_categories") ? "[]"
                : q.Contains("get_live_streams") ? """[{"name":"Com arquivo","stream_id":5,"tv_archive":1,"tv_archive_duration":"2"},{"name":"Sem","stream_id":6,"tv_archive":0}]"""
                : """{"user_info":{"auth":1,"status":"Active"},"server_info":{"timezone":"UTC"}}""";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
        });
        var service = env.CreateService(new XtreamClient(new HttpClient(handler)));
        var data = await service.AddAsync(new NewPlaylistRequest(PlaylistKind.Xtream, "X", "srv:8080", "u", "p"), null, CancellationToken.None);

        var cached = await service.LoadCachedChannelsAsync(data.Playlist.Id);
        Assert.Equal(2, cached[0].CatchupDays);
        Assert.Equal("xc", cached[0].CatchupMode);
        Assert.False(cached[1].HasCatchup);
        Assert.Equal("UTC", env.Playlists.Get(data.Playlist.Id)!.ServerTimezone);

        var start = DateTime.UtcNow.AddHours(-5);
        start = new DateTime(start.Year, start.Month, start.Day, start.Hour, 0, 0, DateTimeKind.Utc);
        var url = service.ResolveCatchupUrl(data.Playlist, cached[0], start, start.AddMinutes(30));
        Assert.Equal($"http://srv:8080/timeshift/u/p/30/{start:yyyy-MM-dd:HH-mm}/5.ts", url);

        Assert.Throws<UserFacingException>(() => service.ResolveCatchupUrl(data.Playlist, cached[1], start, start.AddMinutes(30)));
        Assert.Throws<UserFacingException>(() => service.ResolveCatchupUrl(data.Playlist, cached[0], start.AddDays(-3), start.AddDays(-3).AddMinutes(30)));
    }
}

public class ParentalAndProgressTests
{
    [Fact]
    public void Pin_is_hashed_verified_and_unlocks_the_session()
    {
        using var env = new TempEnvironment();
        var parental = new ParentalControlService(env.Settings, env.Database);
        Assert.False(parental.HasPin);
        Assert.False(parental.IsActive);

        Assert.Throws<UserFacingException>(() => parental.SetPin("12a4"));
        parental.SetPin("2468");
        Assert.True(parental.HasPin);
        Assert.True(parental.IsUnlocked); // quem acabou de o definir fica desbloqueado

        var stored = env.Settings.Get("parental_pin")!;
        Assert.StartsWith("pbkdf2$", stored);
        Assert.DoesNotContain("2468", stored);

        // Novo arranque: bloqueado até introduzir o PIN.
        var fresh = new ParentalControlService(env.Settings, env.Database);
        Assert.True(fresh.IsActive);
        Assert.False(fresh.Unlock("0000"));
        Assert.True(fresh.IsActive);
        Assert.True(fresh.Unlock("2468"));
        Assert.False(fresh.IsActive);
        fresh.Lock();
        Assert.True(fresh.IsActive);

        fresh.RemovePin();
        Assert.False(new ParentalControlService(env.Settings, env.Database).HasPin);
    }

    [Fact]
    public void Locked_groups_are_stored_per_playlist_and_scope()
    {
        using var env = new TempEnvironment();
        var id = env.Playlists.Insert(new Playlist { Name = "P", SourceProtected = [1], CreatedUtc = DateTime.UtcNow });
        var parental = new ParentalControlService(env.Settings, env.Database);
        var changes = 0;
        parental.Changed += (_, _) => changes++;

        parental.SetLockedGroups(id, LockScope.Live, ["Adultos", "Adultos", "XXX"]);
        parental.SetLockedGroups(id, LockScope.Movies, ["Terror"]);

        Assert.Equal(["Adultos", "XXX"], parental.GetLockedGroups(id, LockScope.Live).Order());
        Assert.Equal(["Terror"], parental.GetLockedGroups(id, LockScope.Movies));
        Assert.Empty(parental.GetLockedGroups(id, LockScope.Series));
        Assert.Equal(2, changes);
    }

    [Theory]
    [InlineData("Adultos", true)]
    [InlineData("XXX Premium", true)]
    [InlineData("Canais +18", true)]
    [InlineData("FOR ADULTS", true)]
    [InlineData("Desporto", false)]
    [InlineData("Sexta-feira Filmes", false)]
    [InlineData("Essex News", false)]
    public void Suggests_adult_groups_by_name(string name, bool expected) =>
        Assert.Equal(expected, ParentalControlService.LooksAdult(name));

    [Fact]
    public void Watch_progress_roundtrip_and_finish_rules()
    {
        using var env = new TempEnvironment();
        var id = env.Playlists.Insert(new Playlist { Name = "P", SourceProtected = [1], CreatedUtc = DateTime.UtcNow });
        var repo = new WatchProgressRepository(env.Database);

        repo.Save(id, new WatchProgress(WatchProgress.MovieKey(10), null, 754_000, 6_000_000, false, DateTime.UtcNow));
        repo.Save(id, new WatchProgress(WatchProgress.EpisodeKey(99), 7, 100_000, 2_700_000, false, DateTime.UtcNow));
        repo.Save(id, new WatchProgress(WatchProgress.MovieKey(10), null, 800_000, 6_000_000, false, DateTime.UtcNow)); // atualiza

        var all = repo.GetAll(id);
        Assert.Equal(2, all.Count);
        Assert.Equal(800_000, all["m:10"].PositionMs);
        Assert.True(all["m:10"].CanResume);
        Assert.Equal(7, all["e:99"].SeriesId);

        Assert.False(new WatchProgress("m:1", null, 20_000, 6_000_000, false, DateTime.UtcNow).CanResume); // menos de 30 s
        Assert.True(WatchProgress.IsFinished(5_750_000, 6_000_000));   // últimos 5%
        Assert.True(WatchProgress.IsFinished(5_850_000, 6_000_000));
        Assert.True(WatchProgress.IsFinished(5_830_000, 6_000_000));   // últimos 3 minutos de um filme longo
        Assert.False(WatchProgress.IsFinished(3_000_000, 6_000_000));
        Assert.False(WatchProgress.IsFinished(400_000, 500_000));      // curto: só conta os 5%
    }
}
