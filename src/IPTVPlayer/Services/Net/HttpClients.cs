using System.Net;
using System.Net.Http.Headers;

namespace IPTVPlayer.Services.Net;

/// <summary>Clientes HTTP partilhados (um para API/playlists, outro para logótipos).</summary>
public sealed class HttpClients : IDisposable
{
    /// <summary>Muitos fornecedores bloqueiam user agents desconhecidos; usamos o do VLC.</summary>
    public const string DefaultUserAgent = "VLC/3.0.20 LibVLC/3.0.20";

    public HttpClients()
    {
        Api = Create(TimeSpan.FromSeconds(30));
        Logos = Create(TimeSpan.FromSeconds(15));
    }

    /// <summary>
    /// Playlists e Xtream. O timeout cobre só até aos cabeçalhos (usamos ResponseHeadersRead);
    /// downloads longos podem ser cancelados pelo utilizador.
    /// </summary>
    public HttpClient Api { get; }
    public HttpClient Logos { get; }

    private static HttpClient Create(TimeSpan timeout)
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            MaxConnectionsPerServer = 8,
        };
        var client = new HttpClient(handler) { Timeout = timeout };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(DefaultUserAgent);
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));
        return client;
    }

    public void Dispose()
    {
        Api.Dispose();
        Logos.Dispose();
    }
}
