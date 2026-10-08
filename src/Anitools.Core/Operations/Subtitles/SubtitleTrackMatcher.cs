using Anitools.Core.Media;
using Anitools.Core.Parsing;

namespace Anitools.Core.Operations.Subtitles;

/// <summary>
/// Выбор дорожки субтитров в каждой серии по эталонной дорожке первого файла («Субтитры»):
/// по тайтлу или по языку. Результат — ID дорожки и расширение файла субтитров.
/// </summary>
public static class SubtitleTrackMatcher
{
    private static readonly string[] SignsWords = ["надпис", "sign"];
    private static readonly string[] SubsWords = ["полн", "full", "диалог", "dialog", "субтитр", "сабы", "subs"];

    /// <summary>Чем похожа дорожка по тайтлу: «Надписи / Signs» → надписи, «Полные / Full / Субтитры» → сабы, иначе null.</summary>
    public static SubtitleKind? GuessKind(string trackName)
    {
        var name = TextUtils.Lower(trackName);
        return SignsWords.Any(w => name.Contains(w, StringComparison.Ordinal)) ? SubtitleKind.Signs
            : SubsWords.Any(w => name.Contains(w, StringComparison.Ordinal)) ? SubtitleKind.Subs
            : null;
    }

    /// <summary>
    /// Дорожки по умолчанию для надписей и сабов — по тайтлам; ничего не похоже — первая дорожка в надписи
    /// (как было, когда выбиралась одна дорожка).
    /// </summary>
    public static (SubtitleTrack? Signs, SubtitleTrack? Subs) GuessRoles(IReadOnlyList<SubtitleTrack> tracks)
    {
        var signs = tracks.FirstOrDefault(t => GuessKind(t.Name) == SubtitleKind.Signs);
        var subs = tracks.FirstOrDefault(t => GuessKind(t.Name) == SubtitleKind.Subs);
        return signs is null && subs is null ? (tracks.FirstOrDefault(), null) : (signs, subs);
    }

    /// <summary>Расширение по codec_id mkvmerge: ASS/SSA → .ass, UTF8/ASCII → .srt, PGS → .sup, VobSub → .sub.</summary>
    public static string CodecIdToExtension(string codecId)
    {
        var c = codecId.ToUpperInvariant();
        if (c.Contains("ASS", StringComparison.Ordinal) || c.Contains("SSA", StringComparison.Ordinal))
        {
            return ".ass";
        }

        if (c.Contains("UTF8", StringComparison.Ordinal) || c.Contains("ASCII", StringComparison.Ordinal)
            || c.Contains("UTF-8", StringComparison.Ordinal))
        {
            return ".srt";
        }

        if (c.Contains("PGS", StringComparison.Ordinal) || c.Contains("HDMV", StringComparison.Ordinal))
        {
            return ".sup";
        }

        return c.Contains("VOBSUB", StringComparison.Ordinal) ? ".sub" : ".ass";
    }

    /// <summary>Все обозначения языка дорожки без «und» ({«rus», «ru»}); пусто — язык не указан.</summary>
    public static IReadOnlySet<string> Languages(SubtitleTrack track)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var l in new[] { track.Language, track.LanguageIetf })
        {
            var s = TextUtils.Strip(l ?? "");
            if (s.Length > 0 && TextUtils.Lower(s) != "und")
            {
                set.Add(TextUtils.CaseFold(s));
            }
        }

        return set;
    }

    /// <summary>
    /// Дорожка с тем же тайтлом: точное совпадение без учёта регистра
    /// и пробелов по краям, если оно одно; иначе единственное вхождение подстроки; иначе null.
    /// </summary>
    public static (int Id, string Extension)? FindByTitle(IReadOnlyList<SubtitleTrack> tracks, string refTitle)
    {
        var r = TextUtils.CaseFold(TextUtils.Strip(refTitle));
        var exact = tracks.Where(t => TextUtils.CaseFold(TextUtils.Strip(t.Name)) == r).ToList();
        if (exact.Count != 1)
        {
            exact = tracks.Where(t => TextUtils.CaseFold(t.Name).Contains(r, StringComparison.Ordinal)).ToList();
            if (exact.Count != 1)
            {
                return null;
            }
        }

        return (exact[0].Id, CodecIdToExtension(exact[0].CodecId));
    }

    /// <summary>
    /// Дорожка на том же языке, что эталон. Если таких несколько
    /// (русские надписи и полные) — та, у которой совпал и тайтл, иначе та, что стоит на месте
    /// <paramref name="refPosition"/> среди дорожек этого языка.
    /// </summary>
    public static (int Id, string Extension)? FindByLanguage(IReadOnlyList<SubtitleTrack> tracks, SubtitleTrack reference, int refPosition)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(refPosition);
        var langs = Languages(reference);
        var same = tracks.Where(t => Languages(t).Overlaps(langs)).ToList();
        if (same.Count == 0)
        {
            return null;
        }

        if (same.Count > 1)
        {
            var refName = TextUtils.CaseFold(TextUtils.Strip(reference.Name));
            var byName = same.Where(t => TextUtils.CaseFold(TextUtils.Strip(t.Name)) == refName).ToList();
            if (byName.Count == 1)
            {
                same = byName;
            }
            else if (refPosition < same.Count)
            {
                same = [same[refPosition]];
            }
        }

        return (same[0].Id, CodecIdToExtension(same[0].CodecId));
    }
}
