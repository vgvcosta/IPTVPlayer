using System.Net;
using System.Text;
using System.Text.Json;
using IPTVPlayer.Services;
using IPTVPlayer.Services.Xtream;

namespace IPTVPlayer.Tests;

public class XtreamClientTests
{
    private static readonly XtreamCredentials Credentials = new("http://srv.example:8080", "user", "p@ss word");

    [Theory]
    [InlineData("srv.example:8080", "http://srv.example:8080")]
    [InlineData("http://srv.example:8080/", "http://srv.example:8080")]
    [InlineData("  https://srv.example/player_api.php ", "https://srv.example")]
    [InlineData("http://srv.example/sub/get.php", "http://srv.example/sub")]
    public void Normalizes_server(string input, string expected) =>
        Assert.Equal(expected, XtreamClient.NormalizeServer(input));

    [Theory]
    [InlineData("")]
    [InlineData("ftp://srv.example")]
    [InlineData("http://")]
    public void Rejects_invalid_server(string input) =>
        Assert.Throws<UserFacingException>(() => XtreamClient.NormalizeServer(input));

    [Fact]
    public async Task Authenticate_success_picks_ts_and_escapes_credentials()
    {
        var handler = new StubHandler(_ => Json("""
            {"user_info":{"auth":1,"status":"Active","exp_date":"1893456000","allowed_output_formats":["m3u8","ts"]},
             "server_info":{"url":"srv.example","port":"8080"}}
            """));
        var client = new XtreamClient(new HttpClient(handler));

        var account = await client.AuthenticateAsync(Credentials, CancellationToken.None);

        Assert.Equal("ts", account.StreamExtension);
        Assert.Equal(2030, account.ExpiresUtc!.Value.Year);
        Assert.Contains("password=p%40ss%20word", handler.LastUri!.Query);
    }

    [Theory]
    [InlineData("""{"user_info":{"auth":0}}""", "utilizador ou password")]
    [InlineData("[]", "Login falhado")]
    [InlineData("""{"user_info":{"auth":"1","status":"Expired"}}""", "expirada")]
    [InlineData("<html>not json</html>", "dados inválidos")]
    public async Task Authenticate_failures_have_clear_messages(string body, string expectedFragment)
    {
        var client = new XtreamClient(new HttpClient(new StubHandler(_ => Json(body))));

        var ex = await Assert.ThrowsAsync<UserFacingException>(() => client.AuthenticateAsync(Credentials, CancellationToken.None));
        Assert.Contains(expectedFragment, ex.Message);
    }

    [Fact]
    public async Task Http_errors_become_user_messages()
    {
        var client = new XtreamClient(new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden))));

        var ex = await Assert.ThrowsAsync<UserFacingException>(() => client.AuthenticateAsync(Credentials, CancellationToken.None));
        Assert.Contains("403", ex.Message);
    }

    [Fact]
    public async Task Live_streams_accept_numbers_or_strings()
    {
        var client = new XtreamClient(new HttpClient(new StubHandler(_ => Json("""
            [
              {"num":1,"name":"RTP 1","stream_id":101,"stream_icon":"http://l/1.png","category_id":"5","epg_channel_id":"rtp1.pt"},
              {"num":"2","name":"SIC","stream_id":"102","stream_icon":null,"category_id":5,"epg_channel_id":null,"extra":{"x":[1,2]}}
            ]
            """))));

        var streams = await client.GetLiveStreamsAsync(Credentials, CancellationToken.None);

        Assert.Equal(2, streams.Count);
        Assert.Equal(102, streams[1].StreamId);
        Assert.Equal("5", streams[1].CategoryId);
        Assert.Null(streams[1].StreamIcon);
    }

    [Fact]
    public void Builds_live_url_with_escaped_credentials() =>
        Assert.Equal("http://srv.example:8080/live/user/p%40ss%20word/101.ts",
            XtreamClient.BuildLiveUrl(Credentials, "101.ts"));

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    internal sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

        public Uri? LastUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastUri = request.RequestUri;
            return Task.FromResult(_respond(request));
        }
    }
}
