using System.Numerics;
using System.Text.RegularExpressions;

namespace Anitools.Core.Parsing;

/// <summary>
/// Натуральная сортировка (_natural_key, py:1723): сначала по числу из префикса «N. »
/// (10 после 9, а не после 1; без префикса — в конец), затем по имени без учёта регистра.
/// </summary>
public static partial class NaturalSort
{
    /// <summary>Ключ сортировки: число из префикса «N. » (null — префикса нет) и имя в нижнем регистре.</summary>
    public static (BigInteger? Number, string Lower) Key(string name)
    {
        var m = TrackNumberPrefixRegex().Match(name);
        return (m.Success ? PyText.ParseInt(m.Value.Split('.')[0]) : null, PyText.Lower(name));
    }

    public static int Compare(string? a, string? b)
    {
        if (a is null || b is null)
        {
            return a is null ? (b is null ? 0 : -1) : 1;
        }

        var (na, la) = Key(a);
        var (nb, lb) = Key(b);
        if (na != nb)
        {
            // без префикса — как +∞
            return na is null ? 1 : nb is null ? -1 : na.Value.CompareTo(nb.Value);
        }

        return PyText.CompareCodePoints(la, lb);
    }

    public static IComparer<string> Comparer { get; } = Comparer<string>.Create(Compare);

    /// <summary>Стабильная сортировка, как sorted(…, key=_natural_key).</summary>
    public static IEnumerable<string> Order(IEnumerable<string> names) => names.OrderBy(n => n, Comparer);

    /// <summary>Имя без префикса «N. » (_TRACK_NUM_PREFIX_RE.sub(…, count=1)).</summary>
    public static string StripTrackNumber(string name) => TrackNumberPrefixRegex().Replace(name, "", 1);

    // ^\d+\.\s* — префикс номера дорожки, который пишет «Только аудио»: «2. Show - 01.Title.mka»
    [GeneratedRegex(@"^\d+\.[" + PyText.SpaceChars + "]*", RegexOptions.CultureInvariant)]
    private static partial Regex TrackNumberPrefixRegex();
}
