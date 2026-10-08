namespace Anitools.Core.Shikimori;

/// <summary>Тайтл из поиска Shikimori.</summary>
/// <param name="Name">Оригинальное (ромадзи) название.</param>
/// <param name="Russian">Русское, а если его нет — оригинальное.</param>
/// <param name="Year">Год выхода или «????».</param>
/// <param name="Kind">tv, ova, ona, special, tv_special, movie, music, pv, cm.</param>
public sealed record ShikimoriAnime(long Id, string Name, string Russian, string Year, string Kind, int Episodes)
{
    /// <summary>Тип по-русски: TV, OVA, ONA, Спешл, Фильм, Клип, PV, CM.</summary>
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

    /// <summary>Английское название (из подробного поиска).</summary>
    public string? English { get; init; }

    /// <summary>anons, ongoing, released.</summary>
    public string? Status { get; init; }

    /// <summary>Оценка на Shikimori; нет — null.</summary>
    public double? Score { get; init; }

    /// <summary>Сколько серий уже вышло (у онгоингов).</summary>
    public int EpisodesAired { get; init; }

    /// <summary>Длительность серии, мин.</summary>
    public int? Duration { get; init; }

    /// <summary>Постер 225×318; нет — null.</summary>
    public string? PosterUrl { get; init; }

    public IReadOnlyList<string> Genres { get; init; } = [];

    public IReadOnlyList<string> Studios { get; init; } = [];

    /// <summary>Описание без разметки Shikimori ([character=…] и т.п.).</summary>
    public string? Description { get; init; }

    /// <summary>Статус по-русски: «анонс», «выходит», «вышло».</summary>
    public string StatusName => Status switch
    {
        "anons" => "анонс",
        "ongoing" => "выходит",
        "released" => "вышло",
        _ => "",
    };

    public string Url(Uri site) => new Uri(site, $"/animes/{Id}").ToString();
}
