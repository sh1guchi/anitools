using System.Globalization;
using System.Numerics;
using System.Text;

namespace Anitools.Core.Parsing;

/// <summary>
/// Строки и регулярки по одним правилам для любых имён: пробелы, регистр, разбор целых чисел,
/// расширение и имя без него, порядок сортировки. Правила — для всего диапазона Unicode:
/// \w = [\p{L}\p{N}_], \d = \p{Nd}, <see cref="IsAlnum"/> — буква или цифра любого вида.
/// </summary>
internal static partial class TextUtils
{
    // ── Фрагменты для регулярок .NET ──
    // \w: буква или цифра любого вида (L*, N*) или '_'.
    // Встроенный \w в .NET другой: в нём есть диакритика (Mn) и все Pc, но нет Nl/No («²», «Ⅻ»).
    public const string Word = @"[\p{L}\p{N}_]";

    // \W — всё, кроме \w
    public const string NotWord = @"[^\p{L}\p{N}_]";

    // Содержимое \s для вставки внутрь [...]: .NET \s плюс разделители \x1c–\x1f (как IsSpace)
    public const string SpaceChars = @"\s\x1c-\x1f";

    // \s — пробельный символ (IsSpace)
    public const string Space = "[" + SpaceChars + "]";

    // \b перед словом и после слова
    public const string WordStart = @"(?<![\p{L}\p{N}_])";

    public const string WordEnd = @"(?![\p{L}\p{N}_])";

    /// <summary>Пробельный символ: char.IsWhiteSpace или разделители \x1c–\x1f.</summary>
    public static bool IsSpace(char c) => char.IsWhiteSpace(c) || c is >= '\x1c' and <= '\x1f';

    /// <summary>Деление на строки: границы — \n, \r, \r\n, \v, \f, \x1c–\x1e, \x85, U+2028, U+2029; последний перевод строки не даёт пустой строки.</summary>
    public static IReadOnlyList<string> SplitLines(string s)
    {
        var lines = new List<string>();
        var start = 0;
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c is '\n' or '\r' or '\v' or '\f' or '\x1c' or '\x1d' or '\x1e' or '\x85' or '\u2028' or '\u2029')
            {
                lines.Add(s[start..i]);
                if (c == '\r' && i + 1 < s.Length && s[i + 1] == '\n')
                {
                    i++;
                }

                start = i + 1;
            }
        }

        if (start < s.Length)
        {
            lines.Add(s[start..]);
        }

        return lines;
    }

    /// <summary>Без пробельных символов (<see cref="IsSpace"/>) по краям.</summary>
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

    /// <summary>Нижний регистр: ToLowerInvariant, но İ → i̇ (полное отображение Unicode).</summary>
    /// <remarks>Конечная сигма не учитывается: Σ в конце слова → σ, а не ς — в именах файлов не встречается.</remarks>
    public static string Lower(string s) =>
        (s.Contains('İ', StringComparison.Ordinal) ? s.Replace("İ", "i̇", StringComparison.Ordinal) : s)
        .ToLowerInvariant();

    /// <summary>Свёртка регистра Unicode (C+F) для сравнения без учёта регистра: ß → ss, ſ → s, …</summary>
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

    /// <summary>Буква или цифра любого вида.</summary>
    public static bool IsAlnum(Rune rune) => Rune.IsLetter(rune) || Rune.IsNumber(rune);

    /// <summary>Длина строки в символах Unicode (кодовых точках), а не в единицах UTF-16.</summary>
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
    /// Целое из строки: пробелы по краям, знак, десятичные цифры любого письма («０３», «٢»),
    /// одиночные '_' между цифрами. Иначе — <see cref="FormatException"/>.
    /// </summary>
    /// <remarks>Пробелы здесь — как char.IsWhiteSpace: в отличие от <see cref="Strip"/>, \x1c–\x1f не срезаются.</remarks>
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
                throw new FormatException($"Не целое число: {s}");
            }

            value = (value * 10) + digit;
            digits++;
            afterDigit = true;
            i += rune.Utf16SequenceLength;
        }

        if (digits == 0 || !afterDigit)
        {
            throw new FormatException($"Не целое число: {s}");
        }

        return negative ? -value : value;
    }

    /// <summary>Значение десятичной цифры любого письма или -1.</summary>
    public static int DecimalDigitValue(Rune rune) =>
        Rune.GetUnicodeCategory(rune) == UnicodeCategory.DecimalDigitNumber ? (int)Rune.GetNumericValue(rune) : -1;

    /// <summary>Строка цифр как число без ведущих нулей: «007» → «7».</summary>
    public static string IntString(string s) => ParseInt(s).ToString(CultureInfo.InvariantCulture);

    /// <summary>Число не меньше чем из двух цифр: 5 → «05», 128 → «128».</summary>
    public static string Pad2(BigInteger n) => n.ToString(CultureInfo.InvariantCulture).PadLeft(2, '0');

    /// <summary>Путь → (без расширения, расширение), разделители Windows (/ и \): «a.b.» → («a.b», «.»), «.mkv» → («.mkv», «»).</summary>
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

    /// <summary>Имя файла из пути без последнего суффикса («a..b» → «a.», «.mkv» → «.mkv», «x.» → «x.»).</summary>
    public static string Stem(string p)
    {
        var path = p.TrimEnd('/', '\\');
        var name = path[(Math.Max(path.LastIndexOf('/'), path.LastIndexOf('\\')) + 1)..];
        var i = name.LastIndexOf('.');
        return i > 0 && i < name.Length - 1 ? name[..i] : name;
    }

    /// <summary>Сравнение строк по кодовым точкам, а не по единицам UTF-16.</summary>
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

    /// <summary>Сравнение строк по кодовым точкам — для сортировок по коду символа.</summary>
    public static IComparer<string?> CodePointComparer { get; } = Comparer<string?>.Create(CompareCodePoints);

    private static void AppendRune(StringBuilder sb, Rune rune)
    {
        Span<char> buffer = stackalloc char[2];
        var written = rune.EncodeToUtf16(buffer);
        sb.Append(buffer[..written]);
    }
}
