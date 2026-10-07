using Anitools.Core.Parsing;

namespace Anitools.Core.Media;

/// <summary>Дорожка из mkvmerge -J.</summary>
public sealed record MkvTrack
{
    public int Id { get; init; }

    /// <summary>video, audio, subtitles.</summary>
    public string Type { get; init; } = "";

    /// <summary>Название кодека от mkvmerge: «SubStationAlpha», «SubRip/SRT», «Timed Text».</summary>
    public string Codec { get; init; } = "";

    /// <summary>codec_id на верхнем уровне дорожки (у настоящего mkvmerge его там нет — см. <see cref="PropertiesCodecId"/>).</summary>
    public string CodecId { get; init; } = "";

    public string PropertiesCodecId { get; init; } = "";

    public string PropertiesCodec { get; init; } = "";

    public string TrackName { get; init; } = "";

    public string Language { get; init; } = "";

    public string LanguageIetf { get; init; } = "";

    /// <summary>tags.simple дорожки: (name, value).</summary>
    public IReadOnlyList<(string Name, string Value)> SimpleTags { get; init; } = [];

    public bool HasTags { get; init; }
}

/// <summary>Вложение (шрифт и т.п.) из mkvmerge -J.</summary>
public sealed record MkvAttachment(int Id, string FileName, string ContentType, long Size);

/// <summary>Результат mkvmerge -J.</summary>
public sealed record MkvIdentification(IReadOnlyList<MkvTrack> Tracks, IReadOnlyList<MkvAttachment> Attachments, string? ContainerType)
{
    public static MkvIdentification Empty { get; } = new([], [], null);

    /// <summary>Контейнер — Matroska/WebM: тогда субтитры достаются mkvextract, иначе — ffmpeg (docs/PLAN.md §2.8 #4).</summary>
    public bool IsMatroska => ContainerType is { } t && t.StartsWith("Matroska", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Дорожки субтитров как в оригинале (list_subtitle_tracks, py:2560): имя — track_name → тег name/title →
    /// «Язык: xxx» → «Субтитры N»; codec_id при отсутствии угадывается по названию кодека.
    /// </summary>
    public IReadOnlyList<SubtitleTrack> SubtitleTracks()
    {
        var list = new List<SubtitleTrack>();
        foreach (var t in Tracks)
        {
            if (t.Type != "subtitles" && !t.CodecId.Contains("S_TEXT", StringComparison.Ordinal))
            {
                continue;
            }

            var name = t.TrackName;
            if (name.Length == 0 && t.HasTags)
            {
                foreach (var (tagName, value) in t.SimpleTags)
                {
                    if (PyText.Lower(tagName) is "name" or "title")
                    {
                        name = value;
                        break;
                    }
                }
            }

            var codecId = t.CodecId.Length > 0 ? t.CodecId : t.PropertiesCodecId;
            if (codecId.Length == 0)
            {
                var codecName = PyText.Lower(t.Codec.Length > 0 ? t.Codec : t.PropertiesCodec);
                codecId = codecName.Contains("ass", StringComparison.Ordinal) || codecName.Contains("ssa", StringComparison.Ordinal) ? "S_TEXT/ASS"
                    : codecName.Contains("subrip", StringComparison.Ordinal) || codecName.Contains("srt", StringComparison.Ordinal) ? "S_TEXT/UTF8"
                    : codecName.Contains("pgs", StringComparison.Ordinal) || codecName.Contains("hdmv", StringComparison.Ordinal) ? "S_HDMV/PGS"
                    : codecName.Contains("vobsub", StringComparison.Ordinal) ? "S_VOBSUB"
                    : "";
            }

            if (name.Length == 0)
            {
                name = t.Language.Length > 0 ? $"Язык: {t.Language}" : $"Субтитры {t.Id + 1}";
            }

            list.Add(new SubtitleTrack(t.Id, name, codecId, t.Language, t.LanguageIetf));
        }

        return list;
    }
}
