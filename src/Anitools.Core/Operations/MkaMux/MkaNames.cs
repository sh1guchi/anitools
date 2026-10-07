using System.Text.RegularExpressions;
using Anitools.Core.Parsing;

namespace Anitools.Core.Operations.MkaMux;

/// <summary>
/// Имена в сборке озвучек (mka_muxer.py): префикс «N. » от п.2 anitools, номер серии (наш Anitomy, затем регулярки),
/// имя выходного файла, метка озвучки.
/// </summary>
public static partial class MkaNames
{
    /// <summary>«2. Show - 01.Rus.mka» → «Show - 01.Rus.mka».</summary>
    public static string StripTrackNumber(string name) => TrackNumberRegex().Replace(name, "", 1);

    /// <summary>Номер из префикса «N. » или null.</summary>
    public static int? TrackNumber(string name)
    {
        var m = TrackNumberRegex().Match(name);
        return m.Success ? (int)PyText.ParseInt(m.Groups[1].Value) : null;
    }

    /// <summary>Сортировка _natural_key: по номеру «N. » (без номера — в конец), затем по имени в нижнем регистре.</summary>
    public static IComparer<string> NaturalComparer { get; } = Comparer<string>.Create((a, b) =>
    {
        var na = TrackNumber(a);
        var nb = TrackNumber(b);
        var byNumber = (na, nb) switch
        {
            (null, null) => 0,
            (null, _) => 1,
            (_, null) => -1,
            _ => na.Value.CompareTo(nb.Value),
        };
        return byNumber != 0 ? byNumber : PyText.CompareCodePoints(PyText.Lower(a), PyText.Lower(b));
    });

    /// <summary>
    /// Папка озвучки (voice_folder): первая папка пути относительно корня — метка без «N. » и сам номер;
    /// файл прямо в корне — (null, null).
    /// </summary>
    public static (string? Label, int? Number) VoiceFolder(string relativePath)
    {
        var parts = relativePath.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            return (null, null);
        }

        var top = parts[0];
        var label = PyText.Strip(StripTrackNumber(top));
        return (label.Length > 0 ? label : top, TrackNumber(top));
    }

    /// <summary>Номер серии (parse_episode): «01», «1001», «ep 3» → «03»; не нашёлся — null.</summary>
    public static string? ParseEpisode(string fileName)
    {
        var name = StripTrackNumber(FileName(fileName));
        var episode = Anitomy.Parse(name).EpisodeNumber;
        if (episode is not null && PyText.Strip(episode).Length > 0)
        {
            var s = PyText.Strip(episode);
            return IsDigits(s) ? s.PadLeft(2, '0') : s;
        }

        var stem = PyText.Stem(name);
        foreach (var pattern in new[] { EpisodeMarkerRegex(), EpisodeAfterDashRegex(), EpisodeInBracketsRegex() })
        {
            var m = pattern.Match(stem);
            if (m.Success)
            {
                return m.Groups[1].Value.PadLeft(2, '0');
            }
        }

        return null;
    }

    /// <summary>Имя выходного файла (episode_base): «Тайтл - 01», иначе тайтл, иначе имя без последнего [тега].</summary>
    public static string EpisodeBase(string fileName)
    {
        var name = StripTrackNumber(FileName(fileName));
        var parsed = Anitomy.Parse(name);
        if (parsed.AnimeTitle is { Length: > 0 } title)
        {
            return parsed.EpisodeNumber is { } ep
                ? SanitizeName($"{title} - {(IsDigits(ep) ? ep.PadLeft(2, '0') : ep)}")
                : SanitizeName(title);
        }

        var stem = PyText.Stem(name);
        var s = TrailingTagRegex().Replace(stem, "", 1).Trim(' ', '-', '_', '.');
        return SanitizeName(s.Length > 0 ? s : stem);
    }

    /// <summary>
    /// Срезает хвостовой номер серии из метки, только если он равен номеру серии файла
    /// (_strip_trailing_episode): «AniFilm 01» (серия 01) → «AniFilm», «Studio 2x2» не трогается.
    /// </summary>
    public static string StripTrailingEpisode(string? label, string? episode)
    {
        var s = PyText.Strip(label ?? "");
        if (episode is null)
        {
            return s;
        }

        long number;
        try
        {
            var trimmed = episode.TrimStart('0');
            number = (long)PyText.ParseInt(trimmed.Length > 0 ? trimmed : "0");
        }
        catch (Exception ex) when (ex is FormatException or OverflowException)
        {
            return s;
        }

        var m = TrailingNumberRegex().Match(s);
        if (m.Success && PyText.ParseInt(m.Groups[1].Value) == number)
        {
            var cut = s[..m.Index].Trim(' ', '-', '_', '.');
            return cut.Length > 0 ? cut : s;
        }

        return s;
    }

    /// <summary>Метка озвучки по тайтлу дорожки или имени (voice_label без папки): тайтл → последний [тег] → остаток имени.</summary>
    public static string LabelFromFile(string fileName, string? title, string episodeBase)
    {
        var stem = StripTrackNumber(PyText.Stem(FileName(fileName)));
        string candidate;
        if (title is { Length: > 0 })
        {
            candidate = PyText.Strip(title);
        }
        else if (BracketTagRegex().Matches(stem) is { Count: > 0 } tags)
        {
            candidate = PyText.Strip(tags[^1].Groups[1].Value);
        }
        else
        {
            var s = episodeBase.Length > 0 && stem.StartsWith(episodeBase, StringComparison.Ordinal) ? stem[episodeBase.Length..] : stem;
            candidate = s.Trim(' ', '-', '_', '.', '[', ']', '(', ')');
            if (candidate.Length == 0)
            {
                candidate = stem;
            }
        }

        candidate = StripTrailingEpisode(candidate, ParseEpisode(fileName));
        return candidate.Length > 0 ? candidate : stem;
    }

    /// <summary>Имя файла без недопустимых символов (_sanitize_name); пусто → «output».</summary>
    public static string SanitizeName(string name)
    {
        var s = PyText.Strip(UnsafeNameRegex().Replace(name, "_")).TrimEnd('.');
        return s.Length > 0 ? s : "output";
    }

    private static string FileName(string path) => path[(Math.Max(path.LastIndexOf('/'), path.LastIndexOf('\\')) + 1)..];

    /// <summary>str.isdigit() для номеров серий: только десятичные цифры (у Anitomy других не бывает).</summary>
    private static bool IsDigits(string s) => s.Length > 0 && s.All(char.IsDigit);

    [GeneratedRegex(@"^(\d+)\." + PyText.Space + "*")]
    private static partial Regex TrackNumberRegex();

    [GeneratedRegex(@"(?:^|[" + PyText.SpaceChars + @"_\-\.])(?:e|ep|episode|серия|с)" + PyText.Space + @"*(\d{1,4})(?=[" + PyText.SpaceChars + @"_\-\.\[\]\(\)]|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EpisodeMarkerRegex();

    [GeneratedRegex(@"[" + PyText.SpaceChars + @"_\-]" + PyText.Space + @"*(\d{1,4})(?=" + PyText.Space + @"*(?:\[|\(|$|[" + PyText.SpaceChars + @"_\-\.]))", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EpisodeAfterDashRegex();

    [GeneratedRegex(@"\[(\d{1,4})\]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EpisodeInBracketsRegex();

    [GeneratedRegex(PyText.Space + @"*[\[\(][^\]\)]*[\]\)]" + PyText.Space + "*$")]
    private static partial Regex TrailingTagRegex();

    [GeneratedRegex(@"[" + PyText.SpaceChars + @"\-_.]+0*(\d{1,4})$")]
    private static partial Regex TrailingNumberRegex();

    [GeneratedRegex(@"[\[\(]([^\]\)]+)[\]\)]")]
    private static partial Regex BracketTagRegex();

    [GeneratedRegex(@"[<>:""/\\|?*\x00-\x1f]")]
    private static partial Regex UnsafeNameRegex();
}
