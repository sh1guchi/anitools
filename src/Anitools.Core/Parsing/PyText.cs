using System.Globalization;
using System.Numerics;
using System.Text;

namespace Anitools.Core.Parsing;

/// <summary>
/// Строки и регулярки «как в Python» — чтобы порт вёл себя как оригинал
/// (reference/python/anitools.py) на любых именах: пробелы, регистр, int(),
/// splitext/stem, порядок сортировки. Проверено на всём диапазоне Unicode (Python 3.13):
/// \w = [\p{L}\p{N}_], \d = \p{Nd}, isalnum() = буква или цифра любого вида.
/// </summary>
internal static partial class PyText
{
    // ── Фрагменты для регулярок .NET ──
    // \w в Python: буква или цифра любого вида (L*, N*) или '_'.
    // В .NET \w другой: в нём есть диакритика (Mn) и все Pc, но нет Nl/No («²», «Ⅻ»).
    public const string Word = @"[\p{L}\p{N}_]";

    // \W в Python
    public const string NotWord = @"[^\p{L}\p{N}_]";

    // Содержимое \s для вставки внутрь [...]: .NET \s плюс разделители \x1c–\x1f (str.isspace())
    public const string SpaceChars = @"\s\x1c-\x1f";

    // \s в Python
    public const string Space = "[" + SpaceChars + "]";

    // \b перед словом и после слова
    public const string WordStart = @"(?<![\p{L}\p{N}_])";

    public const string WordEnd = @"(?![\p{L}\p{N}_])";

    /// <summary>str.isspace() для одного символа.</summary>
    public static bool IsSpace(char c) => char.IsWhiteSpace(c) || c is >= '\x1c' and <= '\x1f';

    /// <summary>str.strip() без аргументов.</summary>
    public static string Strip(string s)
    {
        var start = 0;
        var end = s.Length;
        while (start < end && IsSpace(s[start]))
        {
            start++;
        }

        while (end > start && IsSpace(s[end - 1]))
        {
            end--;
        }

        return s[start..end];
    }

    /// <summary>str.lower(): ToLowerInvariant, но İ → i̇ (полное отображение Unicode, как в Python).</summary>
    /// <remarks>Греческую Σ в конце слова Python превращает в ς, здесь будет σ — в именах файлов не встречается.</remarks>
    public static string Lower(string s) =>
        (s.Contains('İ', StringComparison.Ordinal) ? s.Replace("İ", "i̇", StringComparison.Ordinal) : s)
        .ToLowerInvariant();

    /// <summary>str.casefold(): регистронезависимое сравнение (ß → ss, ſ → s, …).</summary>
    public static string CaseFold(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var rune in s.EnumerateRunes())
        {
            if (CaseFoldExceptions.TryGetValue(rune.Value, out var folded))
            {
                sb.Append(folded);
            }
            else if (rune.Value == 0x130)
            {
                sb.Append("i̇");
            }
            else
            {
                AppendRune(sb, Rune.ToLowerInvariant(rune));
            }
        }

        return sb.ToString();
    }

    /// <summary>str.isalnum() для одного символа: буква или цифра любого вида.</summary>
    public static bool IsAlnum(Rune rune) => Rune.IsLetter(rune) || Rune.IsNumber(rune);

    /// <summary>Длина строки в символах Unicode, как len() в Python (а не в UTF-16).</summary>
    public static int Len(string s)
    {
        var n = 0;
        foreach (var _ in s.EnumerateRunes())
        {
            n++;
        }

        return n;
    }

    /// <summary>
    /// int(str) из Python: пробелы по краям, знак, десятичные цифры любого письма («０３», «٢»),
    /// одиночные '_' между цифрами. Иначе — <see cref="FormatException"/> (в Python — ValueError).
    /// </summary>
    /// <remarks>Пробелы здесь — как char.IsWhiteSpace: int() в отличие от strip() не срезает \x1c–\x1f.</remarks>
    public static BigInteger ParseInt(string s)
    {
        var t = s.Trim();
        var i = 0;
        var negative = false;
        if (i < t.Length && t[i] is '+' or '-')
        {
            negative = t[i] == '-';
            i++;
        }

        var value = BigInteger.Zero;
        var digits = 0;
        var afterDigit = false;
        while (i < t.Length)
        {
            var rune = Rune.GetRuneAt(t, i);
            if (rune.Value == '_' && afterDigit && i + 1 < t.Length)
            {
                afterDigit = false;
                i++;
                continue;
            }

            var digit = DecimalDigitValue(rune);
            if (digit < 0)
            {
                throw new FormatException($"invalid literal for int(): '{s}'");
            }

            value = (value * 10) + digit;
            digits++;
            afterDigit = true;
            i += rune.Utf16SequenceLength;
        }

        if (digits == 0 || !afterDigit)
        {
            throw new FormatException($"invalid literal for int(): '{s}'");
        }

        return negative ? -value : value;
    }

    /// <summary>Значение десятичной цифры любого письма или -1.</summary>
    public static int DecimalDigitValue(Rune rune) =>
        Rune.GetUnicodeCategory(rune) == UnicodeCategory.DecimalDigitNumber ? (int)Rune.GetNumericValue(rune) : -1;

    /// <summary>str(int(s)) для строки цифр: «007» → «7».</summary>
    public static string IntString(string s) => ParseInt(s).ToString(CultureInfo.InvariantCulture);

    /// <summary>f"{n:02d}".</summary>
    public static string Pad2(BigInteger n) => n.ToString(CultureInfo.InvariantCulture).PadLeft(2, '0');

    /// <summary>os.path.splitext с разделителями Windows (/ и \): «a.b.» → («a.b», «.»), «.mkv» → («.mkv», «»).</summary>
    public static (string Root, string Ext) SplitExt(string p)
    {
        var sepIndex = Math.Max(p.LastIndexOf('/'), p.LastIndexOf('\\'));
        var dotIndex = p.LastIndexOf('.');
        if (dotIndex > sepIndex)
        {
            // точки в начале имени — не расширение
            for (var i = sepIndex + 1; i < dotIndex; i++)
            {
                if (p[i] != '.')
                {
                    return (p[..dotIndex], p[dotIndex..]);
                }
            }
        }

        return (p, string.Empty);
    }

    /// <summary>PurePath(p).stem: имя без последнего суффикса («a..b» → «a.», «.mkv» → «.mkv», «x.» → «x.»).</summary>
    public static string Stem(string p)
    {
        var path = p.TrimEnd('/', '\\');
        var name = path[(Math.Max(path.LastIndexOf('/'), path.LastIndexOf('\\')) + 1)..];
        var i = name.LastIndexOf('.');
        return i > 0 && i < name.Length - 1 ? name[..i] : name;
    }

    /// <summary>Сравнение строк как в Python — по кодовым точкам, а не по единицам UTF-16.</summary>
    public static int CompareCodePoints(string? a, string? b)
    {
        if (a is null || b is null)
        {
            return a is null ? (b is null ? 0 : -1) : 1;
        }

        var n = Math.Min(a.Length, b.Length);
        for (var i = 0; i < n; i++)
        {
            if (a[i] != b[i])
            {
                return Fixup(a[i]).CompareTo(Fixup(b[i]));
            }
        }

        return a.Length.CompareTo(b.Length);

        // Суррогатные пары кодируют символы ≥ U+10000: поднимаем их выше U+E000–U+FFFF
        static int Fixup(char c) => c < 0xD800 ? c : c < 0xE000 ? c + 0x2000 : c - 0x800;
    }

    /// <summary>Сравнение строк по кодовым точкам — для сортировок «как sorted() в Python».</summary>
    public static IComparer<string> CodePointComparer { get; } = Comparer<string>.Create(CompareCodePoints);

    private static void AppendRune(StringBuilder sb, Rune rune)
    {
        Span<char> buffer = stackalloc char[2];
        var written = rune.EncodeToUtf16(buffer);
        sb.Append(buffer[..written]);
    }
}
