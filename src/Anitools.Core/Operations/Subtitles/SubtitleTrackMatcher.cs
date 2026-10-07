using Anitools.Core.Media;
using Anitools.Core.Parsing;

namespace Anitools.Core.Operations.Subtitles;

/// <summary>
/// Выбор дорожки субтитров в каждой серии по эталонной дорожке первого файла (п.4):
/// по тайтлу или по языку. Результат — ID дорожки и расширение файла субтитров.
/// </summary>
public static class SubtitleTrackMatcher
{
    /// <summary>Расширение по codec_id mkvmerge (codec_id_to_ext, py:2783): ASS/SSA → .ass, UTF8/ASCII → .srt, PGS → .sup, VobSub → .sub.</summary>
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

    /// <summary>Все обозначения языка дорожки без «und» ({«rus», «ru»}); пусто — язык не указан (_sub_langs, py:2814).</summary>
    public static IReadOnlySet<string> Languages(SubtitleTrack track)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var l in new[] { track.Language, track.LanguageIetf })
        {
            var s = PyText.Strip(l ?? "");
            if (s.Length > 0 && PyText.Lower(s) != "und")
            {
                set.Add(PyText.CaseFold(s));
            }
        }

        return set;
    }

    /// <summary>
    /// Дорожка с тем же тайтлом (_find_subtitle_track_by_title, py:2798): точное совпадение без учёта регистра
    /// и пробелов по краям, если оно одно; иначе единственное вхождение подстроки; иначе null.
    /// </summary>
    public static (int Id, string Extension)? FindByTitle(IReadOnlyList<SubtitleTrack> tracks, string refTitle)
    {
        var r = PyText.CaseFold(PyText.Strip(refTitle));
        var exact = tracks.Where(t => PyText.CaseFold(PyText.Strip(t.Name)) == r).ToList();
        if (exact.Count != 1)
        {
            exact = tracks.Where(t => PyText.CaseFold(t.Name).Contains(r, StringComparison.Ordinal)).ToList();
            if (exact.Count != 1)
            {
                return null;
            }
        }

        return (exact[0].Id, CodecIdToExtension(exact[0].CodecId));
    }

    /// <summary>
    /// Дорожка на том же языке, что эталон (_find_subtitle_track_by_lang, py:2819). Если таких несколько
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
            var refName = PyText.CaseFold(PyText.Strip(reference.Name));
            var byName = same.Where(t => PyText.CaseFold(PyText.Strip(t.Name)) == refName).ToList();
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
