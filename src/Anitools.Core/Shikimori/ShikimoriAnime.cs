namespace Anitools.Core.Shikimori;

/// <summary>Тайтл из поиска Shikimori (_search_shikimori, py:470).</summary>
/// <param name="Name">Оригинальное (ромадзи) название.</param>
/// <param name="Russian">Русское, а если его нет — оригинальное.</param>
/// <param name="Year">Год выхода или «????».</param>
/// <param name="Kind">tv, ova, ona, special, tv_special, movie, music, pv, cm.</param>
public sealed record ShikimoriAnime(long Id, string Name, string Russian, string Year, string Kind, int Episodes)
{
    /// <summary>Тип по-русски (_SHIKI_KIND_RU): TV, OVA, ONA, Спешл, Фильм, Клип, PV, CM.</summary>
    public static IReadOnlyDictionary<string, string> KindNames { get; } = new Dictionary<string, string>
    {
        ["tv"] = "TV",
        ["ova"] = "OVA",
        ["ona"] = "ONA",
        ["special"] = "Спешл",
        ["tv_special"] = "Спешл",
        ["movie"] = "Фильм",
        ["music"] = "Клип",
        ["pv"] = "PV",
        ["cm"] = "CM",
    };

    public string KindName => KindNames.TryGetValue(Kind, out var name) ? name : (Kind.Length > 0 ? Kind : "?");

    public string Url(Uri site) => new Uri(site, $"/animes/{Id}").ToString();
}
