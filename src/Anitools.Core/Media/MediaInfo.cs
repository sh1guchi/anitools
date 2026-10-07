namespace Anitools.Core.Media;

/// <summary>Поток из ffprobe -show_streams.</summary>
public sealed record MediaStream
{
    /// <summary>Номер потока в файле (0:N).</summary>
    public int Index { get; init; }

    /// <summary>video, audio, subtitle, attachment, data.</summary>
    public string CodecType { get; init; } = "";

    public string CodecName { get; init; } = "";

    public string? Profile { get; init; }

    public string? CodecTagString { get; init; }

    /// <summary>«0x6134»; «0x0000» — тега нет.</summary>
    public string? CodecTag { get; init; }

    public int? Width { get; init; }

    public int? Height { get; init; }

    public int? Channels { get; init; }

    public string? ChannelLayout { get; init; }

    /// <summary>Тег title (тайтл дорожки), null — нет.</summary>
    public string? Title { get; init; }

    /// <summary>Тег language («rus»), null — нет.</summary>
    public string? Language { get; init; }

    public bool IsDefault { get; init; }

    /// <summary>Обложка (MP4 хранит её как видеопоток).</summary>
    public bool IsAttachedPicture { get; init; }

    /// <summary>
    /// Кодек так, как его пишет ffmpeg -i в строке «Audio: …»: «aac (LC)», «pcm_s16le (sowt / 0x74776F73)».
    /// Нужен там, где оригинал брал имя дорожки из этой строки (дорожка без тайтла).
    /// </summary>
    public string CodecDescription
    {
        get
        {
            var text = CodecName;
            if (!string.IsNullOrEmpty(Profile) && Profile != "unknown")
            {
                text += $" ({Profile})";
            }

            if (!string.IsNullOrEmpty(CodecTag) && CodecTag != "0x0000" && !string.IsNullOrEmpty(CodecTagString))
            {
                text += $" ({CodecTagString} / 0x{CodecTag[2..].ToUpperInvariant()})";
            }

            return text;
        }
    }
}

/// <summary>Что лежит в медиафайле (ffprobe -show_streams -show_format).</summary>
public sealed record MediaInfo(IReadOnlyList<MediaStream> Streams, double? Duration, string? FormatName)
{
    public static MediaInfo Empty { get; } = new([], null, null);

    /// <summary>Аудиопотоки по порядку — индекс в списке = N в «0:a:N».</summary>
    public IReadOnlyList<MediaStream> AudioStreams => [.. Streams.Where(s => s.CodecType == "audio")];

    public IReadOnlyList<MediaStream> VideoStreams => [.. Streams.Where(s => s.CodecType == "video")];

    /// <summary>Потоки субтитров по порядку — индекс в списке = N в «0:s:N».</summary>
    public IReadOnlyList<MediaStream> SubtitleStreams => [.. Streams.Where(s => s.CodecType == "subtitle")];
}
