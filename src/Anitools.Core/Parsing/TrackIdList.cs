namespace Anitools.Core.Parsing;

/// <summary>Ввод номеров дорожек «1,3-5».</summary>
public static class TrackIdList
{
    /// <summary>Самый длинный диапазон: «1-99999999» по ошибке не должен подвесить программу.</summary>
    public const int MaxRange = 10_000;

    /// <summary>
    /// «1,3-5,8» → 0-based [0, 2, 3, 4, 7]: диапазоны раскрываются, обратный
    /// переворачивается, повторы убираются, порядок ввода сохраняется. Ошибка ввода — <see cref="FormatException"/>.
    /// </summary>
    public static IReadOnlyList<int> Parse(string text)
    {
        var ids = new List<int>();
        foreach (var raw in text.Split(','))
        {
            var part = TextUtils.Strip(raw);
            if (part.Length == 0)
            {
                continue;
            }

            if (part.Contains('-', StringComparison.Ordinal))
            {
                var pieces = part.Split('-', 2);
                var lo = ToInt(TextUtils.Strip(pieces[0]));
                var hi = ToInt(TextUtils.Strip(pieces[1]));
                if (lo > hi)
                {
                    (lo, hi) = (hi, lo);
                }

                if ((long)hi - lo > MaxRange)
                {
                    throw new FormatException($"Слишком большой диапазон дорожек: {part}");
                }

                for (var i = lo - 1; i < hi; i++)
                {
                    ids.Add(i);
                }
            }
            else
            {
                ids.Add(ToInt(part) - 1);
            }
        }

        var seen = new HashSet<int>();
        return ids.Where(seen.Add).ToList();
    }

    private static int ToInt(string s) => (int)TextUtils.ParseInt(s);
}
