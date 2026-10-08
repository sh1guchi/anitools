using System.Text.RegularExpressions;
using Anitools.Core.Parsing;

namespace Anitools.Core.Shikimori;

/// <summary>
/// Что искать на Shikimori для названия группы: тип (OVA/ONA/Special/Movie → kind), сезон и запрос
/// без сезона/части. Сезон и тип не ищутся, а учитываются при ранжировании.
/// </summary>
public sealed partial record ShikimoriQuery(IReadOnlyList<string> Kinds, string BaseTitle, int Season, string SearchText)
{
    /// <summary>Метка группы → kind на Shikimori.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> KindsByLabel { get; } = new Dictionary<string, IReadOnlyList<string>>
    {
        ["OVA"] = ["ova"],
        ["ONA"] = ["ona"],
        ["Special"] = ["special", "tv_special"],
        ["Movie"] = ["movie"],
    };

    public static ShikimoriQuery FromTitle(string title)
    {
        var m = SpecialSuffixRegex().Match(title);
        var kinds = m.Success ? KindsByLabel[m.Groups[1].Value] : ["tv"];
        var baseTitle = SpecialSuffixRegex().Replace(title, "");
        var clean = TitleText.CleanForSearch(baseTitle);
        return new ShikimoriQuery(kinds, baseTitle, TitleText.Season(baseTitle), clean.Length > 0 ? clean : baseTitle);
    }

    /// <summary>Запасные запросы: как есть → без знаков препинания → первые три слова.</summary>
    public static IReadOnlyList<string> SearchVariants(string query)
    {
        var variants = new List<string> { query };
        var plain = TextUtils.Strip(SpaceRunRegex().Replace(PunctuationRegex().Replace(query, " "), " "));
        if (plain != query)
        {
            variants.Add(plain);
        }

        var words = plain.Split(' ');
        if (words.Length > 3)
        {
            variants.Add(string.Join(" ", words.Take(3)));
        }

        return variants;
    }

    /// <summary>
    /// Ранжирование: +4 — нормализованный запрос входит в название, +2 — совпал сезон,
    /// +1 — подходит тип; при равенстве — исходный порядок (популярность).
    /// </summary>
    public IReadOnlyList<ShikimoriAnime> Rank(IReadOnlyList<ShikimoriAnime> results) => Rank(results, SearchText, Season, Kinds);

    public static IReadOnlyList<ShikimoriAnime> Rank(IReadOnlyList<ShikimoriAnime> results, string query, int season, IReadOnlyList<string> kinds)
    {
        var q = TitleText.Normalize(query);
        return results
            .Select((r, index) => (r, index, score:
                (q.Length > 0 && (TitleText.Normalize(r.Name).Contains(q, StringComparison.Ordinal)
                                  || TitleText.Normalize(r.Russian).Contains(q, StringComparison.Ordinal)) ? 4 : 0)
                + (TitleText.Season(r.Name) == season ? 2 : 0)
                + (kinds.Contains(r.Kind) ? 1 : 0)))
            .OrderByDescending(x => x.score)
            .ThenBy(x => x.index)
            .Select(x => x.r)
            .ToList();
    }

    // \s+(OVA|ONA|Special|Movie)$
    [GeneratedRegex(@"[" + TextUtils.SpaceChars + @"]+(OVA|ONA|Special|Movie)$", RegexOptions.CultureInvariant)]
    private static partial Regex SpecialSuffixRegex();

    [GeneratedRegex(@"[!?:;,.'""~()\[\]]", RegexOptions.CultureInvariant)]
    private static partial Regex PunctuationRegex();

    [GeneratedRegex(@"[" + TextUtils.SpaceChars + @"]+", RegexOptions.CultureInvariant)]
    private static partial Regex SpaceRunRegex();
}
