using IPTVPlayer.Models;
using IPTVPlayer.Services.Data;
using IPTVPlayer.Services.Playback;

namespace IPTVPlayer.Tests;

public class Phase4Tests
{
    [Theory]
    [InlineData("pt", "Faixa 2 - [Português]")]
    [InlineData("en", "Track 1 - [English]")]
    [InlineData("pt", "por")]
    [InlineData("es", "Pista 3 - [Español]")]
    [InlineData("fr", "fre")]
    public void Finds_tracks_by_language(string code, string expectedName)
    {
        TrackInfo[] tracks =
        [
            new(-1, "Desativar"),
            new(1, "Track 1 - [English]"),
            new(2, "Faixa 2 - [Português]"),
            new(3, "Pista 3 - [Español]"),
            new(4, "por"),
            new(5, "fre"),
        ];
        var found = TrackLanguages.Find(tracks, code);
        Assert.NotNull(found);
        Assert.True(found.Name == expectedName || (code == "pt" && found.Name is "Faixa 2 - [Português]" or "por"));
    }

    [Fact]
    public void Track_language_does_not_match_inside_words()
    {
        TrackInfo[] tracks = [new(1, "Sport commentary"), new(2, "Report")];
        Assert.Null(TrackLanguages.Find(tracks, "pt"));   // "Sport"/"Report" não são português
        Assert.Null(TrackLanguages.Find(tracks, null));
    }

    [Fact]
    public void Recording_file_names_are_safe_and_readable()
    {
        var r = new Recording
        {
            ChannelName = "RTP 1 HD",
            Title = "Telejornal: edição <especial>?",
            StartUtc = new DateTime(2026, 10, 11, 19, 0, 0, DateTimeKind.Utc),
        };
        var name = RecordingService.FileName(r);
        Assert.EndsWith(".ts", name);
        Assert.StartsWith("RTP 1 HD - Telejornal_ edição _especial__ - 2026-10-11 ", name);
        Assert.Equal(-1, name.IndexOfAny(Path.GetInvalidFileNameChars()));

        // Sem programa (título = canal): não repetir o nome.
        r.Title = r.ChannelName;
        Assert.StartsWith("RTP 1 HD - 2026-10-11 ", RecordingService.FileName(r));
        Assert.True(RecordingService.Sanitize(new string('x', 400)).Length <= 150);
    }

    [Fact]
    public void Recording_parts_are_found_and_measured()
    {
        var dir = Path.Combine(Path.GetTempPath(), "IPTVPlayerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var main = Path.Combine(dir, "Canal - 2026.ts");
            File.WriteAllBytes(main, new byte[100]);
            File.WriteAllBytes(Path.Combine(dir, "Canal - 2026 (parte 2).ts"), new byte[50]);

            Assert.Equal(2, RecordingService.PartFiles(main).Count());
            Assert.Equal(150, RecordingService.TotalSize(main));
            Assert.Empty(RecordingService.PartFiles(Path.Combine(dir, "nao-existe.ts")));
            Assert.Equal(0, RecordingService.TotalSize(null));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Recordings_roundtrip_and_cascade_with_playlist()
    {
        using var env = new TempEnvironment();
        var playlistId = env.Playlists.Insert(new Playlist { Name = "P", SourceProtected = [1], CreatedUtc = DateTime.UtcNow });
        var repo = new RecordingRepository(env.Database);
        var start = new DateTime(2026, 10, 11, 20, 0, 0, DateTimeKind.Utc);

        var id = repo.Insert(new Recording
        {
            PlaylistId = playlistId,
            ChannelKey = "x:1",
            ChannelName = "Canal",
            Title = "Filme",
            StartUtc = start,
            StopUtc = start.AddHours(2),
            Status = RecordingStatus.Scheduled,
            CreatedUtc = DateTime.UtcNow,
        });

        var r = repo.Get(id)!;
        Assert.Equal(start, r.StartUtc);
        Assert.Equal(RecordingStatus.Scheduled, r.Status);

        r.Status = RecordingStatus.Completed;
        r.FilePath = @"C:\x\filme.ts";
        r.Error = "A ligação caiu; gravado em 2 partes.";
        repo.Update(r);
        Assert.Equal(RecordingStatus.Completed, repo.Get(id)!.Status);
        Assert.Equal(@"C:\x\filme.ts", repo.Get(id)!.FilePath);

        env.Playlists.Delete(playlistId);
        Assert.Empty(repo.GetAll());
    }
}
