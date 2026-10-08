using System.Text.RegularExpressions;

namespace Anitools.Core.Parsing;

/// <summary>
/// Названия тайтлов и имена папок/файлов. Три санитизации разные и
/// не взаимозаменяемы (docs/PLAN.md §2.4.4): <see cref="SanitizeFolder"/> (HLS),
/// <see cref="SanitizeTrackFolder"/> («Только аудио»), <see cref="FileNameSafe"/> («Переименовать»).
/// </summary>
public static partial class TitleText
{
    private const string S = TextUtils.SpaceChars;

    /// <summary>Тип релиза из anitomy → метка группы: OVA/ONA/спешлы/фильмы идут отдельным тайтлом.</summary>
    public static IReadOnlyDictionary<string, string> SpecialLabels { get; } = new Dictionary<string, string>
    {
        ["ova"] = "OVA",
        ["oav"] = "OVA",
        ["oad"] = "OVA",
        ["ona"] = "ONA",
        ["sp"] = "Special",
        ["special"] = "Special",
        ["specials"] = "Special",
        ["movie"] = "Movie",
        ["movies"] = "Movie",
    };

    /// <summary>Римский номер сезона в конце названия.</summary>
    public static IReadOnlyDictionary<string, int> RomanSeason { get; } = new Dictionary<string, int>
    {
        ["II"] = 2,
        ["III"] = 3,
        ["IV"] = 4,
        ["V"] = 5,
        ["VI"] = 6,
    };

    /// <summary>
    /// Название тайтла из имени файла: anitomy, иначе всё до « - NN»
    /// или до числа в конце, иначе имя без расширения.
    /// </summary>
    public static string AnimeTitle(string filename)
    {
        var title = Anitomy.Parse(filename).AnimeTitle;
        if (!string.IsNullOrEmpty(title))
        {
            return TextUtils.Strip(title);
        }

        var stem = TextUtils.Strip(BracketedRegex().Replace(TextUtils.Stem(filename), ""));
        var m = TitleBeforeDashNumberRegex().Match(stem);
        if (m.Success)
        {
            return TextUtils.Strip(m.Groups[1].Value);
        }

        m = TitleBeforeTrailingNumberRegex().Match(stem);
        return m.Success ? TextUtils.Strip(m.Groups[1].Value) : TextUtils.Strip(stem);
    }

    /// <summary>Название группы для HLS: тайтл + « OVA» / « Movie» / …, если файл — спешл.</summary>
    public static string AnimeGroup(string filename)
    {
        var title = AnimeTitle(filename);
        var type = Anitomy.Parse(filename).AnimeType ?? "";
        return SpecialLabels.TryGetValue(TextUtils.Lower(type), out var kind) && title.Length > 0 ? $"{title} {kind}" : title;
    }

    /// <summary>Номер сезона по названию: «X 2», «X Season 3», «X 2nd Season», «X III»; иначе 1.</summary>
    public static int Season(string? name)
    {
        var n = (name ?? "").Split(": ")[0];
        n = SeasonPartSuffixRegex().Replace(n, "");
        n = TextUtils.Strip(SeasonKindSuffixRegex().Replace(n, ""));
        var m = SeasonWordRegex().Match(n);
        if (!m.Success)
        {
            m = OrdinalSeasonRegex().Match(n);
        }

        if (!m.Success)
        {
            m = TrailingSeasonNumberRegex().Match(n);
        }

        if (m.Success)
        {
            return (int)TextUtils.ParseInt(m.Groups[1].Value);
        }

        m = RomanSeasonSuffixRegex().Match(n);
        return m.Success ? RomanSeason[m.Groups[1].Value] : 1;
    }

    /// <summary>
    /// Название без сезона/части для поиска на Shikimori:
    /// «Enen no Shouboutai 3 pt 1» → «Enen no Shouboutai», «Overlord III» → «Overlord».
    /// </summary>
    public static string CleanForSearch(string title)
    {
        title = TextUtils.Strip(CleanPtRegex().Replace(title, ""));
        title = TextUtils.Strip(CleanPartRegex().Replace(title, ""));
        title = TextUtils.Strip(CleanSeasonRegex().Replace(title, ""));
        title = TextUtils.Strip(CleanOrdinalSeasonRegex().Replace(title, ""));
        title = TextUtils.Strip(CleanRomanRegex().Replace(title, ""));
        return TextUtils.Strip(CleanArabicRegex().Replace(title, ""));
    }

    /// <summary>Нормализация для сравнения названий: нижний регистр, всё кроме букв и цифр → пробел.</summary>
    public static string Normalize(string? s) => TextUtils.Strip(NotAlnumRunRegex().Replace(TextUtils.Lower(s ?? ""), " "));

    /// <summary>Название для имени файла Windows: «X 2: Sub» → «X 2 - Sub», «Re:Zero» → «Re Zero».</summary>
    public static string FileNameSafe(string name)
    {
        name = ColonRegex().Replace(name, " - ");
        name = FileNameForbiddenRegex().Replace(name, " ");
        return SpaceRunRegex().Replace(name, " ").Trim(' ', '.');
    }

    /// <summary>Имя папки тайтла/серии/озвучки в HLS: \/:*?"&lt;&gt;| → _, пробелы по краям — прочь.</summary>
    public static string SanitizeFolder(string name) => TextUtils.Strip(HlsForbiddenRegex().Replace(name, "_"));

    /// <summary>
    /// Имя папки дорожки в «Только аудио»: запрещённые и управляющие → _,
    /// пробелы и точки по краям — прочь, пусто → «audio_track».
    /// </summary>
    public static string SanitizeTrackFolder(string name)
    {
        var clean = TextUtils.Strip(TrackForbiddenRegex().Replace(name, "_")).Trim('.');
        return clean.Length > 0 ? clean : "audio_track";
    }

    /// <summary>Имя выходного файла в «Только видео», «Только аудио», «Сборке аудио» и «Субтитрах»: «_» → пробел.</summary>
    public static string OutputFileName(string file) => file.Replace('_', ' ');

    private const RegexOptions I = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
    private const RegexOptions C = RegexOptions.CultureInvariant;

    [GeneratedRegex(@"[\[\(][^\]\)]*[\]\)]", C)]
    private static partial Regex BracketedRegex();

    [GeneratedRegex(@"^(.*?)[" + S + @"]*[-–][" + S + @"]*\d{1,4}", C)]
    private static partial Regex TitleBeforeDashNumberRegex();

    [GeneratedRegex(@"^(.*?)[" + S + @"]+\d{1,4}[" + S + @"]*$", C)]
    private static partial Regex TitleBeforeTrailingNumberRegex();

    [GeneratedRegex(@"[" + S + @"]+(?:pt|part)[" + S + @"]*\d+[" + S + @"]*$", I)]
    private static partial Regex SeasonPartSuffixRegex();

    [GeneratedRegex(@"[" + S + @"]+(?:OVA|ONA|Movie|Specials?)[" + S + @"]*$", I)]
    private static partial Regex SeasonKindSuffixRegex();

    [GeneratedRegex(TextUtils.WordStart + @"Seasons?[" + S + @"]*(\d{1,2})" + TextUtils.WordEnd, I)]
    private static partial Regex SeasonWordRegex();

    [GeneratedRegex(TextUtils.WordStart + @"(\d{1,2})(?:st|nd|rd|th)[" + S + @"]+Season" + TextUtils.WordEnd, I)]
    private static partial Regex OrdinalSeasonRegex();

    [GeneratedRegex(@"[" + S + @"](\d{1,2})$", C)]
    private static partial Regex TrailingSeasonNumberRegex();

    [GeneratedRegex(@"[" + S + @"](II|III|IV|V|VI)$", C)]
    private static partial Regex RomanSeasonSuffixRegex();

    [GeneratedRegex(@"[" + S + @"]+pt[" + S + @"]*\d+[" + S + @"]*$", I)]
    private static partial Regex CleanPtRegex();

    [GeneratedRegex(@"[" + S + @"]+part[" + S + @"]*\d+[" + S + @"]*$", I)]
    private static partial Regex CleanPartRegex();

    [GeneratedRegex(@"[" + S + @"]+seasons?[" + S + @"]*\d+[" + S + @"]*$", I)]
    private static partial Regex CleanSeasonRegex();

    [GeneratedRegex(@"[" + S + @"]+\d{1,2}(?:st|nd|rd|th)[" + S + @"]+season[" + S + @"]*$", I)]
    private static partial Regex CleanOrdinalSeasonRegex();

    [GeneratedRegex(@"[" + S + @"]+(?:I{1,3}|IV|VI{0,3}|IX|XI{0,3})[" + S + @"]*$", C)]
    private static partial Regex CleanRomanRegex();

    [GeneratedRegex(@"[" + S + @"]+\d{1,2}[" + S + @"]*$", C)]
    private static partial Regex CleanArabicRegex();

    // [\W_]+ — всё, кроме букв и цифр
    [GeneratedRegex(@"[^\p{L}\p{N}]+", C)]
    private static partial Regex NotAlnumRunRegex();

    [GeneratedRegex(@"[" + S + @"]*:[" + S + @"]+", C)]
    private static partial Regex ColonRegex();

    [GeneratedRegex(@"[<>:""/\\|?*\x00-\x1f]", C)]
    private static partial Regex FileNameForbiddenRegex();

    [GeneratedRegex(@"[" + S + @"]+", C)]
    private static partial Regex SpaceRunRegex();

    [GeneratedRegex(@"[\\/:*?""<>|]", C)]
    private static partial Regex HlsForbiddenRegex();

    [GeneratedRegex(@"[<>:""/\\|?*\x00-\x1F]", C)]
    private static partial Regex TrackForbiddenRegex();
}
