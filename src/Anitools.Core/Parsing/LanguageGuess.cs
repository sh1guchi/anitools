using System.Text.RegularExpressions;

namespace Anitools.Core.Parsing;

/// <summary>Язык озвучки по метке/тайтлу дорожки.</summary>
public static partial class LanguageGuess
{
    private static readonly string[] Japanese = ["ориг", "orig", "japan", "jpn", "яп"];
    private static readonly string[] English = ["english", "англ"];

    /// <summary>
    /// Язык по метке: «Оригинальная / Original / Japan / JP» → jpn, «ENG / English / Англ» → eng, иначе null.
    /// </summary>
    public static string? Detect(string? text)
    {
        var low = TextUtils.Lower(text ?? "");
        if (Japanese.Any(k => low.Contains(k, StringComparison.Ordinal)) || JpWordRegex().IsMatch(low))
        {
            return "jpn";
        }

        if (English.Any(k => low.Contains(k, StringComparison.Ordinal)) || EngWordRegex().IsMatch(low))
        {
            return "eng";
        }

        return null;
    }

    [GeneratedRegex(TextUtils.WordStart + "jp" + TextUtils.WordEnd, RegexOptions.CultureInvariant)]
    private static partial Regex JpWordRegex();

    [GeneratedRegex(TextUtils.WordStart + "eng?" + TextUtils.WordEnd, RegexOptions.CultureInvariant)]
    private static partial Regex EngWordRegex();
}
