using System.Numerics;
using System.Text.RegularExpressions;

namespace Anitools.Core.Parsing;

/// <summary>
/// Номер серии из имени файла (п.5 «Переименовать», mka_muxer) — порт extract_episode_number_smart
/// и двух её запасных функций (py:3034–3196). Результат — минимум 2 цифры («05», «128»).
/// </summary>
public static partial class EpisodeNumber
{
    private const string S = TextUtils.SpaceChars;

    /// <summary>extract_episode_number_smart: Title.NN_MMM → anitomy → <see cref="ExtractAdvanced"/> → <see cref="ExtractBasic"/>.</summary>
    public static string? Extract(string filename)
    {
        var basename = TextUtils.SplitExt(filename).Root;

        // Приоритет 0: паттерн Title.NN_MMM (сезон/арк + серия) — например HunterHunter.11_001.
        // Без этого anitomy ошибочно берёт NN как номер серии
        var m = SeasonUnderscoreEpisodeRegex().Match(basename);
        if (m.Success)
        {
            return TextUtils.Pad2(TextUtils.ParseInt(m.Groups[1].Value));
        }

        return FromAnitomy(filename) ?? ExtractAdvanced(filename) ?? ExtractBasic(filename);
    }

    /// <summary>extract_episode_number_advanced: anitomy, затем наборы регулярок; номер должен быть больше 0.</summary>
    public static string? ExtractAdvanced(string filename)
    {
        var basename = TextUtils.SplitExt(filename).Root.Replace(".надписи", "", StringComparison.Ordinal);

        if (FromAnitomy(filename) is { } fromAnitomy)
        {
            return fromAnitomy;
        }

        foreach (var (regex, handler) in AdvancedPatterns)
        {
            var m = regex.Match(basename);
            if (m.Success && handler(m) is { } n && n > 0)
            {
                return TextUtils.Pad2(n);
            }
        }

        return null;
    }

    /// <summary>extract_episode_number: старый список регулярок, затем первое число в имени.</summary>
    public static string? ExtractBasic(string filename)
    {
        var basename = TextUtils.SplitExt(filename).Root.Replace(".надписи", "", StringComparison.Ordinal);

        foreach (var (regex, handler) in BasicPatterns)
        {
            var m = regex.Match(basename);
            if (m.Success && handler(m) is { } n)
            {
                return TextUtils.Pad2(n);
            }
        }

        // Fallback: первое число в имени файла
        var first = FirstNumberRegex().Match(basename);
        return first.Success ? TextUtils.Pad2(TextUtils.ParseInt(first.Groups[1].Value)) : null;
    }

    private static string? FromAnitomy(string filename) =>
        Anitomy.Parse(filename).EpisodeNumber is { Length: > 0 } ep ? TextUtils.Pad2(TextUtils.ParseInt(ep)) : null;

    private static BigInteger Group(Match m, int group) => TextUtils.ParseInt(m.Groups[group].Value);

    private static bool IsDecimal(string s) => s.Length > 0 && s.EnumerateRunes().All(r => TextUtils.DecimalDigitValue(r) >= 0);

    private static readonly string[] Roman20 =
        ["I", "II", "III", "IV", "V", "VI", "VII", "VIII", "IX", "X", "XI", "XII", "XIII", "XIV", "XV", "XVI", "XVII", "XVIII", "XIX", "XX"];

    private static BigInteger? RomanToInt(string roman)
    {
        var i = Array.IndexOf(Roman20, roman.ToUpperInvariant());
        return i >= 0 ? i + 1 : null;
    }

    // Обработчик возвращает null, если в оригинале он вернул None или бросил исключение — тогда берётся следующий шаблон
    private static readonly (Regex Regex, Func<Match, BigInteger?> Handler)[] BasicPatterns =
    [
        // Число после дефиса и перед [ (например, - 0530 [1080p])
        (BasicDashBracketRegex(), m => Group(m, 1)),
        // S01E02, s1e2
        (BasicSeasonEpisodeRegex(), m => Group(m, 2)),
        // 01x02, 1x2
        (BasicCrossRegex(), m => Group(m, 2)),
        // [01], (01)
        (BasicBracketRegex(), m => Group(m, 1)),
        // ep01, episode01, эпизод01, ep-01, ep_01, ep 01
        (BasicEpRegex(), m => Group(m, 1)),
        // E01, e01
        (BasicERegex(), m => Group(m, 1)),
        // OVA 01, SP01. Римские (OVA II) в оригинале не срабатывают никогда: roman_map.get(…, int("II"))
        // вычисляет int("II") заранее, тот бросает ValueError, и шаблон пропускается — повторяем
        (BasicSpecialRegex(), m => IsDecimal(m.Groups[1].Value) ? Group(m, 1) : null),
        // Part 1, part1
        (BasicPartRegex(), m => Group(m, 1)),
        // 01, 001, 1 в конце после пробела/дефиса/подчёркивания
        (BasicTailSeparatedRegex(), m => Group(m, 1)),
        // Просто число в конце
        (BasicTailRegex(), m => Group(m, 1)),
        // Японский стиль: 第01話
        (BasicJapaneseRegex(), m => Group(m, 1)),
    ];

    private static readonly (Regex Regex, Func<Match, BigInteger?> Handler)[] AdvancedPatterns =
    [
        // season_episode
        (BasicSeasonEpisodeRegex(), m => Group(m, 2)),
        (BasicCrossRegex(), m => Group(m, 2)),
        // japanese
        (BasicJapaneseRegex(), m => Group(m, 1)),
        (JapaneseWaRegex(), m => Group(m, 1)),
        (JapaneseDaiKaiRegex(), m => Group(m, 1)),
        (JapaneseKaiRegex(), m => Group(m, 1)),
        // english
        (BasicEpRegex(), m => Group(m, 1)),
        (BasicERegex(), m => Group(m, 1)),
        (AdvancedPartRegex(), m => Group(m, 1)),
        // special
        (AdvancedSpecialRegex(), m => IsDecimal(m.Groups[1].Value) ? Group(m, 1) : RomanToInt(m.Groups[1].Value)),
        // general
        (BasicBracketRegex(), m => Group(m, 1)),
        (BasicDashBracketRegex(), m => Group(m, 1)),
        (BasicTailSeparatedRegex(), m => Group(m, 1)),
        (BasicTailRegex(), m => Group(m, 1)),
        // fallback — любое число в имени
        (FirstNumberRegex(), m => Group(m, 1)),
    ];

    private const RegexOptions I = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    [GeneratedRegex(@"[._\- ]\d{1,2}_(\d{3,})", RegexOptions.CultureInvariant)]
    private static partial Regex SeasonUnderscoreEpisodeRegex();

    [GeneratedRegex(@"-[" + S + @"]*(\d{2,4})[" + S + @"]*\[", I)]
    private static partial Regex BasicDashBracketRegex();

    [GeneratedRegex(@"[Ss](\d{1,2})[Ee](\d{1,2})", I)]
    private static partial Regex BasicSeasonEpisodeRegex();

    [GeneratedRegex(@"(\d{1,2})[xX](\d{1,2})", I)]
    private static partial Regex BasicCrossRegex();

    [GeneratedRegex(@"[\[\(](\d{1,3})[\]\)]", I)]
    private static partial Regex BasicBracketRegex();

    [GeneratedRegex(@"(?:ep|episode|эпизод)[" + S + @"\-_]*(\d{1,3})", I)]
    private static partial Regex BasicEpRegex();

    [GeneratedRegex(@"[Ee](\d{1,3})", I)]
    private static partial Regex BasicERegex();

    [GeneratedRegex(@"(?:OVA|SP|SPECIAL)[" + S + @"\-_]*(\d{1,3}|[IVX]+)", I)]
    private static partial Regex BasicSpecialRegex();

    [GeneratedRegex(@"part[" + S + @"\-_]*(\d{1,3})", I)]
    private static partial Regex BasicPartRegex();

    [GeneratedRegex(@"[-_" + S + @"](\d{1,3})$", I)]
    private static partial Regex BasicTailSeparatedRegex();

    [GeneratedRegex(@"(\d{1,3})$", I)]
    private static partial Regex BasicTailRegex();

    [GeneratedRegex(@"第(\d{1,3})話", I)]
    private static partial Regex BasicJapaneseRegex();

    [GeneratedRegex(@"(\d{1,3})話", I)]
    private static partial Regex JapaneseWaRegex();

    [GeneratedRegex(@"第(\d{1,3})回", I)]
    private static partial Regex JapaneseDaiKaiRegex();

    [GeneratedRegex(@"(\d{1,3})回", I)]
    private static partial Regex JapaneseKaiRegex();

    [GeneratedRegex(@"[Pp]art[" + S + @"\-_]*(\d{1,3})", I)]
    private static partial Regex AdvancedPartRegex();

    [GeneratedRegex(@"(?:OVA|SP|SPECIAL|ONA|MOVIE)[" + S + @"\-_]*(\d{1,3}|[IVX]+)", I)]
    private static partial Regex AdvancedSpecialRegex();

    [GeneratedRegex(@"(\d{1,3})", I)]
    private static partial Regex FirstNumberRegex();
}
