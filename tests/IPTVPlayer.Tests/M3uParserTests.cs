using System.Diagnostics;
using System.Text;
using IPTVPlayer.Services.Parsing;

namespace IPTVPlayer.Tests;

public class M3uParserTests
{
    private static Task<M3uPlaylist> Parse(string text) => new M3uParser().ParseAsync(new StringReader(text));

    [Fact]
    public async Task Parses_extinf_attributes_and_name()
    {
        var result = await Parse("""
            #EXTM3U url-tvg="http://epg.example/guide.xml"
            #EXTINF:-1 tvg-id="rtp1.pt" tvg-name="RTP 1" tvg-logo="http://logo/rtp1.png" group-title="Portugal",RTP 1 HD
            http://stream.example/rtp1.m3u8
            """);

        Assert.Equal("http://epg.example/guide.xml", result.EpgUrl);
        var e = Assert.Single(result.Entries);
        Assert.Equal("RTP 1 HD", e.Name);
        Assert.Equal("rtp1.pt", e.TvgId);
        Assert.Equal("RTP 1", e.TvgName);
        Assert.Equal("http://logo/rtp1.png", e.TvgLogo);
        Assert.Equal("Portugal", e.GroupTitle);
        Assert.Equal("http://stream.example/rtp1.m3u8", e.Url);
    }

    [Fact]
    public async Task Handles_commas_inside_quotes_and_in_name()
    {
        var result = await Parse("""
            #EXTM3U
            #EXTINF:-1 group-title="Notícias, Info",CNN, International
            http://s/cnn
            """);

        var e = Assert.Single(result.Entries);
        Assert.Equal("Notícias, Info", e.GroupTitle);
        Assert.Equal("CNN, International", e.Name);
    }

    [Fact]
    public async Task Supports_extgrp_and_vlcopt()
    {
        var result = await Parse("""
            #EXTM3U
            #EXTINF:-1,Canal A
            #EXTGRP:Desporto
            #EXTVLCOPT:http-user-agent=MyAgent/1.0
            #EXTVLCOPT:http-referrer=http://ref.example/
            http://s/a
            #EXTINF:-1,Canal B
            http://s/b
            """);

        Assert.Equal(2, result.Entries.Count);
        Assert.Equal("Desporto", result.Entries[0].GroupTitle);
        Assert.Equal("MyAgent/1.0", result.Entries[0].UserAgent);
        Assert.Equal("http://ref.example/", result.Entries[0].Referrer);
        // As opções não passam para o canal seguinte.
        Assert.Null(result.Entries[1].GroupTitle);
        Assert.Null(result.Entries[1].UserAgent);
    }

    [Fact]
    public async Task Url_without_extinf_uses_tvg_name_or_url_as_name()
    {
        var result = await Parse("""
            #EXTM3U
            http://s/no-info
            #EXTINF:-1 tvg-name="Só tvg-name",
            http://s/tvg
            """);

        Assert.Equal("http://s/no-info", result.Entries[0].Name);
        Assert.Equal("Só tvg-name", result.Entries[1].Name);
    }

    [Fact]
    public async Task Ignores_blank_lines_whitespace_and_unknown_tags()
    {
        var result = await Parse("#EXTM3U\r\n\r\n  #EXTINF:-1 , Canal  \r\n#KODIPROP:inputstream=x\r\n   http://s/x   \r\n");

        var e = Assert.Single(result.Entries);
        Assert.Equal("Canal", e.Name);
        Assert.Equal("http://s/x", e.Url);
    }

    [Fact]
    public async Task Html_page_is_rejected()
    {
        await Assert.ThrowsAsync<M3uFormatException>(() =>
            Parse("<html><body>401 Unauthorized</body></html>"));
    }

    [Fact]
    public async Task Parses_25000_channels_quickly()
    {
        var sb = new StringBuilder("#EXTM3U\n");
        for (var i = 0; i < 25_000; i++)
        {
            sb.Append($"#EXTINF:-1 tvg-id=\"c{i}\" tvg-name=\"Canal {i}\" tvg-logo=\"http://logo/{i}.png\" group-title=\"Grupo {i % 120}\",Canal {i}\n");
            sb.Append($"http://stream.example:8080/user/pass/{i}.ts\n");
        }

        var sw = Stopwatch.StartNew();
        var result = await Parse(sb.ToString());
        sw.Stop();

        Assert.Equal(25_000, result.Entries.Count);
        Assert.Equal("Grupo 7", result.Entries[24_967].GroupTitle);
        Assert.True(sw.ElapsedMilliseconds < 2000, $"Parsing demorou {sw.ElapsedMilliseconds} ms");
    }
}
