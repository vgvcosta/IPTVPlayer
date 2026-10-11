namespace IPTVPlayer.Services.Playback;

/// <summary>Reconhece o idioma de uma faixa pelo nome que o VLC lhe dá ("Faixa 1 - [Português]", "eng"…).</summary>
public static class TrackLanguages
{
    public static readonly IReadOnlyList<(string Code, string Label)> Options =
    [
        ("pt", "Português"),
        ("en", "Inglês"),
        ("es", "Espanhol"),
        ("fr", "Francês"),
    ];

    private static readonly Dictionary<string, string[]> Aliases = new()
    {
        ["pt"] = ["portugues", "portuguese", "por", "pt", "ptpt", "ptbr", "brazilian", "brasileiro"],
        ["en"] = ["english", "ingles", "eng", "en"],
        ["es"] = ["espanol", "spanish", "castellano", "spa", "es"],
        ["fr"] = ["francais", "french", "fra", "fre", "fr"],
    };

    /// <summary>Primeira faixa (Id ≥ 0) cujo nome indica o idioma pedido.</summary>
    public static TrackInfo? Find(IEnumerable<TrackInfo> tracks, string? code)
    {
        if (string.IsNullOrEmpty(code) || !Aliases.TryGetValue(code, out var aliases)) return null;
        return tracks.FirstOrDefault(t => t.Id >= 0 && Tokens(t.Name).Any(aliases.Contains));
    }

    private static IEnumerable<string> Tokens(string name) =>
        TextNormalizer.ForSearch(name.Replace("-", "")).Split([' ', '[', ']', '(', ')', ',', '_', '.', '/', ':'],
            StringSplitOptions.RemoveEmptyEntries);
}
