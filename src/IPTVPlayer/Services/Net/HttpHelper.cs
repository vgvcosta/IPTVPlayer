namespace IPTVPlayer.Services.Net;

/// <summary>Pedidos HTTP com erros traduzidos para mensagens claras.</summary>
internal static class HttpHelper
{
    public static Uri ValidateHttpUrl(string? input, string what)
    {
        var text = input?.Trim() ?? "";
        if (text.Length == 0)
            throw new UserFacingException($"Indica o {what}.");

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            string.IsNullOrEmpty(uri.Host))
        {
            throw new UserFacingException($"O {what} não é válido. Tem de começar por http:// ou https://.");
        }
        return uri;
    }

    public static async Task<HttpResponseMessage> GetAsync(HttpClient http, Uri url, CancellationToken ct)
    {
        HttpResponseMessage response;
        try
        {
            response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new UserFacingException("O servidor não respondeu a tempo. Verifica o endereço ou tenta mais tarde.", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new UserFacingException($"Não foi possível ligar ao servidor: {Describe(ex)}.", ex);
        }

        if (!response.IsSuccessStatusCode)
        {
            var code = (int)response.StatusCode;
            response.Dispose();
            throw new UserFacingException(DescribeStatus(code));
        }
        return response;
    }

    public static string Describe(HttpRequestException ex) => ex.HttpRequestError switch
    {
        HttpRequestError.NameResolutionError => "o endereço não foi encontrado (DNS)",
        HttpRequestError.ConnectionError => "a ligação foi recusada ou o servidor está offline",
        HttpRequestError.SecureConnectionError => "erro na ligação segura (SSL/TLS)",
        HttpRequestError.ProxyTunnelError => "erro no proxy",
        HttpRequestError.ResponseEnded or HttpRequestError.InvalidResponse => "o servidor fechou a ligação ou enviou uma resposta inválida",
        _ => "erro de rede",
    };

    public static string DescribeStatus(int code) => code switch
    {
        401 or 403 => $"Acesso negado pelo servidor (HTTP {code}). Verifica o endereço e as credenciais.",
        404 => "O endereço não foi encontrado no servidor (HTTP 404).",
        429 => "O servidor recusou por excesso de pedidos (HTTP 429). Tenta daqui a alguns minutos.",
        >= 500 => $"Erro no servidor (HTTP {code}). Tenta mais tarde.",
        _ => $"O servidor respondeu com erro (HTTP {code}).",
    };
}
