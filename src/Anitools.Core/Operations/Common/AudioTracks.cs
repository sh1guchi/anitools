using Anitools.Core.Media;

namespace Anitools.Core.Operations.Common;

/// <summary>Аудиодорожка файла для выбора в «Только аудио» и «Сборке аудио».</summary>
/// <param name="Index">N в «0:a:N».</param>
/// <param name="Title">Настоящий тайтл (ffprobe, а для MOV/MP4 — ещё и из боксов файла); null — нет.</param>
/// <param name="Description">Кодек как в «Audio: …» у ffmpeg -i: «aac (LC)».</param>
public sealed record AudioTrackInfo(int Index, string? Title, string Description, string? Language, int? Channels);

public static class AudioTracks
{
    private static readonly string[] MovLike = [".mov", ".qt", ".mp4", ".m4a", ".m4v"];

    /// <summary>
    /// Аудиодорожки файла по порядку. Если ffprobe не дал тайтл хоть одной дорожке, а файл QuickTime/MP4 —
    /// недостающие берутся по порядку из боксов файла (экспорты DaVinci).
    /// </summary>
    public static async Task<IReadOnlyList<AudioTrackInfo>> ReadAsync(IMediaProbe probe, string path, CancellationToken ct = default)
    {
        var streams = (await probe.ProbeAsync(path, ct).ConfigureAwait(false)).AudioStreams;
        var titles = streams.Select(s => string.IsNullOrEmpty(s.Title) ? null : s.Title).ToList();
        if ((titles.Count == 0 || titles.Any(t => t is null))
            && MovLike.Contains(MediaFiles.Suffix(Path.GetFileName(path)), StringComparer.OrdinalIgnoreCase))
        {
            var mov = MovAtomReader.ReadAudioTitles(path);
            if (mov.Count > 0)
            {
                titles = titles.Select((t, pos) => t ?? (pos < mov.Count ? mov[pos] : null)).ToList();
            }
        }

        return streams.Select((s, i) => new AudioTrackInfo(i, titles[i], s.CodecDescription, s.Language, s.Channels)).ToList();
    }
}
