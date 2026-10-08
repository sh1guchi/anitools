using System.Text;
using System.Text.RegularExpressions;
using Anitools.Core.Parsing;

namespace Anitools.Core.Operations.Fonts;

/// <summary>Имена шрифтов и разбор .ass (ass_fonts.py).</summary>
public static partial class FontText
{
    /// <summary>Байты 0x80–0xFF в MacRoman — таблица кодека mac_roman Python.</summary>
    private const string MacRomanHigh =
        "ÄÅÇÉÑÖÜáàâäãåçéèêëíìîïñóòôöõúùûü"
        + "†°¢£§•¶ß®©™´¨≠ÆØ∞±≤≥¥µ∂∑∏π∫ªºΩæø"
        + "¿¡¬√ƒ≈∆«»… ÀÃÕŒœ–—“”‘’÷◊ÿŸ⁄€‹›ﬁﬂ"
        + "‡·‚„‰ÂÊÁËÈÍÎÏÌÓÔÒÚÛÙıˆ˜¯˘˙˚¸˝˛ˇ";

    /// <summary>Байты 0x80–0xFF в cp1251 — таблица кодека Python (0x98 не определён → U+FFFD, как errors="replace").</summary>
    private const string Cp1251High =
        "ЂЃ‚ѓ„…†‡€‰Љ‹ЊЌЋЏђ‘’“”•–—�™љ›њќћџ"
        + " ЎўЈ¤Ґ¦§Ё©Є«¬­®Ї°±Ііґµ¶·ё№є»јЅѕї"
        + "АБВГДЕЖЗИЙКЛМНОПРСТУФХЦЧШЩЪЫЬЭЮЯ"
        + "абвгдежзийклмнопрстуфхцчшщъыьэюя";

    private static readonly UTF8Encoding StrictUtf8 = new(false, throwOnInvalidBytes: true);

    /// <summary>Ключ сравнения имён (font_key): пробелы схлопнуты, casefold — libass сравнивает без учёта регистра.</summary>
    public static string FontKey(string name) => TextUtils.CaseFold(string.Join(' ', SplitWhitespace(name)));

    /// <summary>Имя файла шрифта для запасного поиска (normalize): без пробелов, «-» и «_», в нижнем регистре.</summary>
    public static string Normalize(string s) => TextUtils.Lower(NormalizeRegex().Replace(s, ""));

    /// <summary>Слаг для dafont/1001fonts (slugify): всё, кроме a–z и 0–9, → разделитель.</summary>
    public static string Slugify(string name, char separator) =>
        SlugRegex().Replace(TextUtils.Lower(name), separator.ToString()).Trim(separator);

    /// <summary>Имя файла для скачанного шрифта (safe_filename): без пробелов и недопустимых символов.</summary>
    public static string SafeFileName(string s) => SafeFileNameRegex().Replace(s, "");

    /// <summary>«@» в начале — вертикальный вариант того же шрифта (clean_font_name).</summary>
    public static string CleanFontName(string name) => TextUtils.Strip(TextUtils.Strip(name).TrimStart('@'));

    /// <summary>Текст .ass (read_ass): BOM UTF-16 → UTF-16, иначе UTF-8 (BOM снимается), не UTF-8 — cp1251.</summary>
    public static string ReadAss(byte[] raw)
    {
        if (raw.Length >= 2 && ((raw[0] == 0xFF && raw[1] == 0xFE) || (raw[0] == 0xFE && raw[1] == 0xFF)))
        {
            var encoding = raw[0] == 0xFF ? new UnicodeEncoding(false, false) : new UnicodeEncoding(true, false);
            return encoding.GetString(raw, 2, raw.Length - 2);
        }

        var start = raw.Length >= 3 && raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF ? 3 : 0;
        try
        {
            return StrictUtf8.GetString(raw, start, raw.Length - start);
        }
        catch (DecoderFallbackException)
        {
            return DecodeSingleByte(raw, Cp1251High);
        }
    }

    /// <summary>
    /// Шрифты .ass (parse_font_names): поле Fontname стилей [V4+ Styles]/[V4 Styles] (номер — по строке Format, по
    /// умолчанию 1) и теги \fn в строках Dialogue. Пустой \fn — сброс, не шрифт. Порядок — первого появления.
    /// </summary>
    public static IReadOnlyList<string> ParseFontNames(string text)
    {
        var names = new List<string>();
        var section = "";
        var fontnameIndex = 1;
        foreach (var rawLine in TextUtils.SplitLines(text))
        {
            var line = TextUtils.Strip(rawLine);
            if (line.StartsWith('['))
            {
                section = TextUtils.Lower(line);
                continue;
            }

            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon < 0)
            {
                continue;
            }

            var key = TextUtils.Lower(TextUtils.Strip(line[..colon]));
            var value = line[(colon + 1)..];
            if (section is "[v4+ styles]" or "[v4 styles]")
            {
                if (key == "format")
                {
                    var fields = value.Split(',').Select(f => TextUtils.Lower(TextUtils.Strip(f))).ToList();
                    if (fields.IndexOf("fontname") is >= 0 and var index)
                    {
                        fontnameIndex = index;
                    }
                }
                else if (key == "style")
                {
                    var parts = value.Split(',');
                    if (parts.Length > fontnameIndex)
                    {
                        Add(CleanFontName(parts[fontnameIndex]));
                    }
                }
            }
            else if (section == "[events]" && key == "dialogue")
            {
                foreach (Match m in FnTagRegex().Matches(value))
                {
                    Add(CleanFontName(m.Groups[1].Value));
                }
            }
        }

        return names;

        void Add(string name)
        {
            if (name.Length > 0 && !names.Contains(name))
            {
                names.Add(name);
            }
        }
    }

    /// <summary>UTF-16BE с errors="ignore": непарные суррогаты и нечётный последний байт пропускаются.</summary>
    internal static string DecodeUtf16BeIgnore(ReadOnlySpan<byte> raw)
    {
        var text = new StringBuilder(raw.Length / 2);
        var units = raw.Length / 2;
        for (var i = 0; i < units; i++)
        {
            var c = (char)((raw[2 * i] << 8) | raw[(2 * i) + 1]);
            if (char.IsHighSurrogate(c))
            {
                if (i + 1 < units && (char)((raw[(2 * i) + 2] << 8) | raw[(2 * i) + 3]) is var low && char.IsLowSurrogate(low))
                {
                    text.Append(c).Append(low);
                    i++;
                }

                continue;
            }

            if (!char.IsLowSurrogate(c))
            {
                text.Append(c);
            }
        }

        return text.ToString();
    }

    internal static string DecodeMacRoman(ReadOnlySpan<byte> raw) => DecodeSingleByte(raw, MacRomanHigh);

    private static string DecodeSingleByte(ReadOnlySpan<byte> raw, string high)
    {
        var text = new StringBuilder(raw.Length);
        foreach (var b in raw)
        {
            text.Append(b < 0x80 ? (char)b : high[b - 0x80]);
        }

        return text.ToString();
    }

    private static IEnumerable<string> SplitWhitespace(string s)
    {
        var start = -1;
        for (var i = 0; i <= s.Length; i++)
        {
            var space = i == s.Length || TextUtils.IsSpace(s[i]);
            if (space && start >= 0)
            {
                yield return s[start..i];
                start = -1;
            }
            else if (!space && start < 0)
            {
                start = i;
            }
        }
    }

    [GeneratedRegex(@"\\fn([^\\}]*)")]
    private static partial Regex FnTagRegex();

    [GeneratedRegex("[" + TextUtils.SpaceChars + @"\-_]")]
    private static partial Regex NormalizeRegex();

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex SlugRegex();

    [GeneratedRegex(@"[<>:""/\\|?*" + TextUtils.SpaceChars + "]+")]
    private static partial Regex SafeFileNameRegex();
}
