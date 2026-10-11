using System.Globalization;
using System.Text;

namespace IPTVPlayer.Services;

public static class TextNormalizer
{
    /// <summary>Minúsculas e sem acentos: "Notícias" → "noticias".</summary>
    public static string ForSearch(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";

        var decomposed = text.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }

    /// <summary>Até duas iniciais para o marcador quando o canal não tem logótipo.</summary>
    public static string Initials(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "?";

        var sb = new StringBuilder(2);
        foreach (var word in name.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var c = word.FirstOrDefault(char.IsLetterOrDigit);
            if (c == default) continue;
            sb.Append(char.ToUpperInvariant(c));
            if (sb.Length == 2) break;
        }
        return sb.Length == 0 ? "?" : sb.ToString();
    }
}
