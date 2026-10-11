using System.Text.Json;
using IPTVPlayer.Services.Net;
using Xunit;

namespace IPTVPlayer.Tests;

public class UpdateTests
{
    private static JsonElement Release(string tag, params (string Name, string Url)[] assets)
    {
        var json = JsonSerializer.Serialize(new
        {
            tag_name = tag,
            html_url = "https://github.com/vgvcosta/IPTVPlayer/releases/tag/" + tag,
            assets = assets.Select(a => new { name = a.Name, browser_download_url = a.Url, size = 1234 }),
        });
        return JsonDocument.Parse(json).RootElement;
    }

    private const string MsiUrl = "https://github.com/vgvcosta/IPTVPlayer/releases/download/v4.3.0/PepinoTv-4.3.0-win-x64.msi";

    [Fact]
    public void NewerRelease_ReturnsMsi()
    {
        var release = Release("v4.3.0", ("PepinoTv-4.3.0-win-x64.zip", "https://x/a.zip"), ("PepinoTv-4.3.0-win-x64.msi", MsiUrl));
        var update = UpdateService.ParseRelease(release, new Version(4, 2, 1, 0));

        Assert.NotNull(update);
        Assert.Equal(new Version(4, 3, 0), update.Version);
        Assert.Equal(MsiUrl, update.MsiUrl);
        Assert.Equal("PepinoTv-4.3.0-win-x64.msi", update.MsiName);
        Assert.Equal(1234, update.MsiSize);
    }

    [Theory]
    [InlineData("v4.2.1")]
    [InlineData("v4.2.0")]
    [InlineData("4.1")]
    [InlineData("nightly")]
    public void SameOlderOrInvalidTag_ReturnsNull(string tag)
    {
        var release = Release(tag, ("PepinoTv.msi", MsiUrl));
        Assert.Null(UpdateService.ParseRelease(release, new Version(4, 2, 1, 0)));
    }

    [Fact]
    public void WithoutHttpsMsi_ReturnsNull()
    {
        Assert.Null(UpdateService.ParseRelease(Release("v5.0.0", ("PepinoTv.zip", "https://x/a.zip")), new Version(4, 2, 1)));
        Assert.Null(UpdateService.ParseRelease(Release("v5.0.0", ("PepinoTv.msi", "http://x/a.msi")), new Version(4, 2, 1)));
    }
}
