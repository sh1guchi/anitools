using System.Globalization;
using Anitools.Core.Media;

namespace Anitools.Core.Operations.Rename;

/// <summary>
/// Значения переменных из самого видео (ffprobe) — как их пишут в именах релизов:
/// «1080p», «AVC», «FLAC», «2.0», «MULTi». Нечего взять — пусто.
/// </summary>
public static class RenameMedia
{
    /// <summary>Значение переменной <see cref="RenameTemplate.MediaVariables"/>; не она — null.</summary>
    public static string? Value(MediaInfo info, string variable) => variable switch
    {
        RenameTemplate.Resolution => Resolution(info),
        RenameTemplate.VideoCodec => VideoCodec(info),
        RenameTemplate.AudioCodec => AudioCodec(info),
        RenameTemplate.Channels => Channels(info),
        RenameTemplate.Multi => Multi(info),
        _ => null,
    };

    /// <summary>По ширине или высоте: 1920×800 (широкий экран) — тоже 1080p.</summary>
    public static string Resolution(MediaInfo info) =>
        Video(info) is { Width: > 0 and var w, Height: > 0 and var h }
            ? w >= 3800 || h >= 2000 ? "2160p"
            : w >= 1900 || h >= 1000 ? "1080p"
            : w >= 1260 || h >= 700 ? "720p"
            : h >= 560 ? "576p"
            : "480p"
            : "";

    public static string VideoCodec(MediaInfo info) => Video(info)?.CodecName switch
    {
        null or "" => "",
        "h264" => "AVC",
        "hevc" => "HEVC",
        "av1" => "AV1",
        "vp9" => "VP9",
        "mpeg2video" => "MPEG2",
        "mpeg4" => "MPEG4",
        "vc1" => "VC-1",
        var codec => codec.ToUpperInvariant(),
    };

    public static string AudioCodec(MediaInfo info) => MainAudio(info) switch
    {
        null => "",
        { CodecName: "dts", Profile: { } profile } when profile.Contains("MA", StringComparison.Ordinal) => "DTS-HD.MA",
        { CodecName: var codec } => codec switch
        {
            "flac" => "FLAC",
            "aac" => "AAC",
            "ac3" => "AC3",
            "eac3" => "EAC3",
            "dts" => "DTS",
            "truehd" => "TrueHD",
            "opus" => "OPUS",
            "mp3" => "MP3",
            "vorbis" => "Vorbis",
            _ when codec.StartsWith("pcm_", StringComparison.Ordinal) => "LPCM",
            _ => codec.ToUpperInvariant(),
        },
    };

    /// <summary>Каналы основной дорожки: 2 → «2.0», 6 → «5.1», 8 → «7.1».</summary>
    public static string Channels(MediaInfo info) => MainAudio(info)?.Channels switch
    {
        1 => "1.0",
        2 => "2.0",
        3 => "2.1",
        6 => "5.1",
        7 => "6.1",
        8 => "7.1",
        > 0 and var n => n.ToString(CultureInfo.InvariantCulture) + "ch",
        _ => "",
    };

    /// <summary>«MULTi» — звук на нескольких языках (дорожка без языка считается своим языком).</summary>
    public static string Multi(MediaInfo info) =>
        info.AudioStreams
            .Select((a, i) => a.Language is { Length: > 0 } lang && lang != "und" ? lang.ToLowerInvariant() : $"#{i}")
            .Distinct(StringComparer.Ordinal)
            .Count() > 1
            ? "MULTi"
            : "";

    /// <summary>Основная дорожка: по умолчанию, иначе первая.</summary>
    private static MediaStream? MainAudio(MediaInfo info) =>
        info.AudioStreams.FirstOrDefault(a => a.IsDefault) ?? info.AudioStreams.FirstOrDefault();

    private static MediaStream? Video(MediaInfo info) => info.VideoStreams.FirstOrDefault(v => !v.IsAttachedPicture);
}
