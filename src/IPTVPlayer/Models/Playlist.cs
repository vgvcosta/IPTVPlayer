namespace IPTVPlayer.Models;

/// <summary>
/// Playlist guardada. Origem e credenciais ficam sempre encriptadas (DPAPI):
/// um URL M3U de fornecedor normalmente contém utilizador e password.
/// </summary>
public sealed class Playlist
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public PlaylistKind Kind { get; set; }

    /// <summary>URL M3U, caminho do ficheiro ou servidor Xtream (encriptado).</summary>
    public byte[] SourceProtected { get; set; } = [];
    public byte[]? UsernameProtected { get; set; }
    public byte[]? PasswordProtected { get; set; }

    /// <summary>URL XMLTV indicado pela própria playlist (url-tvg), encriptado.</summary>
    public byte[]? EpgUrlProtected { get; set; }

    /// <summary>URL XMLTV definido manualmente pelo utilizador (tem prioridade), encriptado.</summary>
    public byte[]? CustomEpgUrlProtected { get; set; }

    public DateTime? EpgLoadedUtc { get; set; }

    /// <summary>Fuso horário do servidor Xtream (ex.: "Europe/Lisbon"), usado nos URLs de catch-up.</summary>
    public string? ServerTimezone { get; set; }
    public DateTime? LastLoadedUtc { get; set; }
    public DateTime CreatedUtc { get; set; }

    /// <summary>Calculado na leitura, não é persistido.</summary>
    public int ChannelCount { get; set; }

    public string KindLabel => Kind switch
    {
        PlaylistKind.M3uUrl => "M3U (URL)",
        PlaylistKind.M3uFile => "M3U (ficheiro)",
        PlaylistKind.Xtream => "Xtream Codes",
        _ => "",
    };
}
