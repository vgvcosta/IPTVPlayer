using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using IPTVPlayer.Models;
using IPTVPlayer.Services;
using IPTVPlayer.Services.Data;
using IPTVPlayer.Services.Epg;
using Microsoft.Data.Sqlite;

namespace IPTVPlayer.Tests;

public class XmltvParserTests
{
    private static readonly DateTime Now = new(2026, 10, 11, 18, 30, 0, DateTimeKind.Utc);

    private const string Sample = """
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE tv SYSTEM "xmltv.dtd">
        <tv generator-info-name="teste">
          <channel id="RTP1.pt">
            <display-name>RTP 1</display-name>
            <display-name lang="en">RTP One</display-name>
          </channel>
          <channel id="sicnot.pt"><display-name>SIC Notícias</display-name></channel>
          <channel id="outro.pt"><display-name>Canal Que Não Está Na Lista</display-name></channel>
          <programme start="20261011190000 +0100" stop="20261011200000 +0100" channel="RTP1.pt">
            <title lang="pt">Telejornal</title><desc>Notícias do dia.</desc>
          </programme>
          <programme start="20261011200000 +0100" stop="20261011213000 +0100" channel="RTP1.pt">
            <title>Filme da Noite</title>
          </programme>
          <programme start="20261011180000 +0000" channel="sicnot.pt"><title>Jornal das 7</title></programme>
          <programme start="20261011190000 +0000" stop="20261011200000 +0000" channel="sicnot.pt"><title>Opinião</title></programme>
          <programme start="20261011180000 +0000" stop="20261011190000 +0000" channel="outro.pt"><title>Ignorado</title></programme>
          <programme start="20260101180000 +0000" stop="20260101190000 +0000" channel="RTP1.pt"><title>Muito antigo</title></programme>
          <programme start="20261011210000 +0000" stop="20261011220000 +0000" channel="soporid"><title>Sem channel</title></programme>
        </tv>
        """;

    private static readonly EpgChannelRef[] Channels =
    [
        new("k1", "rtp1.pt", null, "RTP 1 HD"),            // tvg-id (sem distinguir maiúsculas)
        new("k2", null, null, "PT: SIC Notícias FHD"),      // só pelo nome normalizado
        new("k3", "SOPORID", null, "Canal sem <channel>"),  // id só nos <programme>
        new("k4", null, null, "Sem EPG"),
    ];

    private static XmltvResult Parse(byte[] data)
    {
        var parser = new XmltvParser(Channels, Now);
        parser.Parse(new MemoryStream(data));
        return parser.Complete();
    }

    [Fact]
    public void Matches_by_tvg_id_by_name_and_filters_programmes()
    {
        var result = Parse(Encoding.UTF8.GetBytes(Sample));

        Assert.Equal("RTP1.pt", result.KeyToEpgId["k1"]);
        Assert.Equal("sicnot.pt", result.KeyToEpgId["k2"]);
        Assert.Equal("soporid", result.KeyToEpgId["k3"]);
        Assert.False(result.KeyToEpgId.ContainsKey("k4"));

        var titles = result.Programmes.Select(p => p.Title).ToList();
        Assert.DoesNotContain("Ignorado", titles);
        Assert.DoesNotContain("Muito antigo", titles);
        Assert.Contains("Sem channel", titles);

        var telejornal = result.Programmes.Single(p => p.Title == "Telejornal");
        Assert.Equal("Notícias do dia.", telejornal.Description);
        Assert.Equal(XmltvParser.ToUnix(new DateTime(2026, 10, 11, 18, 0, 0, DateTimeKind.Utc)), telejornal.Start);

        // Sem "stop": termina quando começa o seguinte.
        var jornal = result.Programmes.Single(p => p.Title == "Jornal das 7");
        Assert.Equal(XmltvParser.ToUnix(new DateTime(2026, 10, 11, 19, 0, 0, DateTimeKind.Utc)), jornal.Stop);
    }

    [Fact]
    public void Reads_gzip_compressed_files()
    {
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Fastest, leaveOpen: true))
            gz.Write(Encoding.UTF8.GetBytes(Sample));

        var result = Parse(ms.ToArray());
        Assert.Contains(result.Programmes, p => p.Title == "Filme da Noite");
    }

    [Fact]
    public void Rejects_non_xmltv_content() =>
        Assert.Throws<UserFacingException>(() => Parse(Encoding.UTF8.GetBytes("<html><body>404</body></html>")));

    [Theory]
    [InlineData("20261011190000 +0100", "2026-10-11T18:00:00Z")]
    [InlineData("20261011190000 -0230", "2026-10-11T21:30:00Z")]
    [InlineData("202610111900", "2026-10-11T19:00:00Z")]
    [InlineData("20261011190000", "2026-10-11T19:00:00Z")]
    public void Parses_xmltv_times(string input, string expectedUtc) =>
        Assert.Equal(DateTimeOffset.Parse(expectedUtc).ToUnixTimeSeconds(), XmltvParser.ParseTime(input));

    [Theory]
    [InlineData("PT: RTP 1 HD", "rtp1")]
    [InlineData("RTP 1", "rtp1")]
    [InlineData("SIC Notícias (FHD)", "sicnoticias")]
    [InlineData("PT | TVI 4K", "tvi")]
    [InlineData("Eurosport 1 [backup]", "eurosport1")]
    public void Normalizes_channel_names(string input, string expected) =>
        Assert.Equal(expected, XmltvParser.NormalizeName(input));

    [Fact]
    public void Handles_large_guides_quickly()
    {
        var sb = new StringBuilder("<tv>");
        for (var c = 0; c < 2000; c++) sb.Append($"<channel id=\"c{c}\"><display-name>Canal {c}</display-name></channel>");
        var start = Now.AddHours(-2); // dentro da janela guardada (3 h para trás, 4 dias para a frente)
        for (var c = 0; c < 2000; c++)
        {
            for (var h = 0; h < 48; h++)
            {
                var s = start.AddMinutes(h * 30);
                sb.Append($"<programme start=\"{s:yyyyMMddHHmmss} +0000\" stop=\"{s.AddMinutes(30):yyyyMMddHHmmss} +0000\" channel=\"c{c}\"><title>P{h}</title><desc>Descrição {h}</desc></programme>");
            }
        }
        sb.Append("</tv>");
        var refs = Enumerable.Range(0, 2000).Select(i => new EpgChannelRef("k" + i, "c" + i, null, "Canal " + i));

        var sw = Stopwatch.StartNew();
        var parser = new XmltvParser(refs, Now);
        parser.Parse(new MemoryStream(Encoding.UTF8.GetBytes(sb.ToString())));
        var result = parser.Complete();
        sw.Stop();

        Assert.Equal(2000, result.KeyToEpgId.Count);
        Assert.Equal(2000 * 48, result.Programmes.Count);
        Assert.True(sw.ElapsedMilliseconds < 5000, $"EPG demorou {sw.ElapsedMilliseconds} ms");
    }
}

public class EpgGuideTests
{
    [Fact]
    public void Now_next_and_range_lookups()
    {
        var list = new[]
        {
            new EpgProgramme("e", 100, 200, "A"),
            new EpgProgramme("e", 200, 300, "B"),
            new EpgProgramme("e", 400, 500, "C"), // intervalo sem programa entre 300 e 400
        };
        var guide = new EpgGuide(new Dictionary<string, string> { ["k"] = "e" },
            new Dictionary<string, EpgProgramme[]> { ["e"] = list });

        Assert.Equal(("A", "B"), Titles(guide.GetNowNext("e", 150)));
        Assert.Equal(("B", "C"), Titles(guide.GetNowNext("e", 200)));
        Assert.Equal((null, "C"), Titles(guide.GetNowNext("e", 350)));
        Assert.Equal((null, null), Titles(guide.GetNowNext("e", 600)));
        Assert.Equal((null, null), Titles(guide.GetNowNext("outro", 150)));

        Assert.Equal(["B", "C"], guide.GetRange("e", 250, 450).Select(p => p.Title));
    }

    private static (string?, string?) Titles((EpgProgramme? Now, EpgProgramme? Next) x) => (x.Now?.Title, x.Next?.Title);
}

public class EpgStorageAndMigrationTests
{
    [Fact]
    public void Stores_and_loads_epg_with_lazy_descriptions()
    {
        using var env = new TempEnvironment();
        var id = env.Playlists.Insert(new Playlist { Name = "P", SourceProtected = [1], CreatedUtc = DateTime.UtcNow });
        var repo = new EpgRepository(env.Database);

        var data = new XmltvResult();
        data.KeyToEpgId["k1"] = "e1";
        data.Programmes.Add(new XmltvProgramme("e1", 100, 200, "Título", "Descrição longa"));
        data.Programmes.Add(new XmltvProgramme("e1", 200, 300, "Outro", null));
        repo.ReplaceAll(id, data);

        var guide = repo.Load(id);
        Assert.Equal("e1", guide.KeyToEpgId["k1"]);
        Assert.Equal(2, guide.ProgrammeCount);
        Assert.Equal("Descrição longa", repo.GetDescription(id, "e1", 100));

        env.Playlists.Delete(id); // ON DELETE CASCADE
        Assert.Equal(0, repo.Load(id).ProgrammeCount);
    }

    [Fact]
    public void Migrates_v1_database_and_encrypts_plaintext_epg_url()
    {
        var dir = Path.Combine(Path.GetTempPath(), "IPTVPlayerTests", Guid.NewGuid().ToString("N"));
        var paths = new AppPaths(dir);
        try
        {
            // Base de dados como a fase 1 a deixava (v1, epg_url em texto simples).
            using (var c = new SqliteConnection($"Data Source={paths.DatabasePath}"))
            {
                c.Open();
                using var cmd = c.CreateCommand();
                cmd.CommandText = AppDatabase.SchemaV1 + """
                    PRAGMA user_version = 1;
                    INSERT INTO playlists (name, kind, source_protected, epg_url, created_utc)
                    VALUES ('Antiga', 0, x'01', 'http://epg.example/xmltv.php?username=joao&password=segredo', '2026-01-01');
                    """;
                cmd.ExecuteNonQuery();
            }

            var protector = new IPTVPlayer.Services.Security.CredentialProtector();
            var db = new AppDatabase(paths, protector);
            db.Initialize();

            var playlist = Assert.Single(new PlaylistRepository(db).GetAll());
            Assert.Equal("http://epg.example/xmltv.php?username=joao&password=segredo", protector.Unprotect(playlist.EpgUrlProtected!));

            using var check = db.Open();
            using var q = check.CreateCommand();
            q.CommandText = "SELECT epg_url FROM playlists";
            Assert.True(q.ExecuteScalar() is null or DBNull);
            q.CommandText = "PRAGMA user_version";
            Assert.Equal((long)AppDatabase.CurrentVersion, q.ExecuteScalar());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }
}
