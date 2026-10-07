using System.IO.Compression;
using Anitools.Core.Logging;
using Anitools.Core.Media;
using Anitools.Core.Operations.Common;
using Anitools.Core.Parsing;
using Anitools.Core.Processes;

namespace Anitools.Core.Operations.Fonts;

/// <summary>Итог: архив (null — шрифтов нет), что упаковано, что пропущено как дубль, ошибки по видео.</summary>
public sealed record VideoFontsResult(
    string? ZipPath,
    IReadOnlyList<string> Packed,
    IReadOnlyList<(string Video, string Font)> Duplicates,
    IReadOnlyList<(string Video, string Error)> Errors);

/// <summary>
/// Шрифты из видео → fonts.zip (extract_fonts.py): вложения-шрифты всех видео папки (mkvmerge -J →
/// mkvextract attachments), одинаковые имена — один раз (берётся из первого видео), архив Deflate в папке.
/// </summary>
public sealed class VideoFontsOperation(IProcessRunner runner, ToolPaths tools, IMediaProbe probe, ErrorLogWriter logs)
{
    public const string ZipName = "fonts.zip";

    public static IReadOnlyList<string> VideoExtensions { get; } = [".mkv", ".mp4", ".webm", ".avi", ".mov", ".matroska"];

    public static IReadOnlyList<string> FontExtensions { get; } = [".ttf", ".otf", ".woff", ".woff2", ".eot"];

    /// <summary>Шрифт ли вложение: тип содержит «font» или расширение шрифта.</summary>
    public static bool IsFont(MkvAttachment attachment) =>
        PyText.Lower(attachment.ContentType).Contains("font", StringComparison.Ordinal)
        || FontExtensions.Contains(MediaFiles.Suffix(PyText.Lower(attachment.FileName)), StringComparer.Ordinal);

    /// <summary>Видео папки — по порядку sorted(Path) оригинала (имена в нижнем регистре по кодам символов).</summary>
    public static IReadOnlyList<string> ListVideos(string folder) =>
        [.. MediaFiles.List(folder, VideoExtensions).OrderBy(p => PyText.Lower(Path.GetFileName(p)), PyText.CodePointComparer)];

    public async Task<VideoFontsResult> ExecuteAsync(string folder, IProgress<(int Index, int Count, string Video)>? progress = null, CancellationToken cancellationToken = default)
    {
        var videos = ListVideos(folder);
        if (videos.Count == 0)
        {
            throw new PlanException("Видеофайлы не найдены в папке.");
        }

        var mkvextract = tools.Mkvextract ?? throw new ToolNotFoundException(Tool.Mkvextract);
        var fonts = new List<(string Name, byte[] Data)>();
        var duplicates = new List<(string, string)>();
        var errors = new List<(string, string)>();
        var temp = Directory.CreateTempSubdirectory("anitools-fonts-");
        try
        {
            for (var i = 0; i < videos.Count; i++)
            {
                var video = videos[i];
                progress?.Report((i + 1, videos.Count, video));
                MkvIdentification identification;
                try
                {
                    identification = await probe.IdentifyAsync(video, cancellationToken).ConfigureAwait(false);
                }
                catch (MediaProbeException ex)
                {
                    errors.Add((video, ex.Message));
                    continue;
                }

                foreach (var attachment in identification.Attachments.Where(IsFont))
                {
                    if (fonts.Any(f => f.Name == attachment.FileName))
                    {
                        duplicates.Add((video, attachment.FileName));
                        continue;
                    }

                    var tmp = Path.Combine(temp.FullName, $"font_{i}_{attachment.Id}");
                    string[] args = ["--output-charset", "UTF-8", video, "attachments", $"{attachment.Id}:{tmp}"];
                    var result = await runner.RunAsync(new ProcessSpec(mkvextract, args), cancellationToken).ConfigureAwait(false);
                    if (result.ExitCode > 1 || !File.Exists(tmp))
                    {
                        logs.WriteProcessError(PyText.Stem(Path.GetFileName(video)), result.StandardOutput + "\n" + result.StandardErrorTail, [mkvextract, .. args], result.ExitCode);
                        errors.Add((video, $"не удалось извлечь {attachment.FileName} (id={attachment.Id})"));
                        continue;
                    }

                    fonts.Add((attachment.FileName, await File.ReadAllBytesAsync(tmp, cancellationToken).ConfigureAwait(false)));
                }
            }
        }
        finally
        {
            temp.Delete(recursive: true);
        }

        if (fonts.Count == 0)
        {
            return new VideoFontsResult(null, [], duplicates, errors);
        }

        var zipPath = Path.Combine(folder, ZipName);
        using (var stream = new FileStream(zipPath, FileMode.Create, FileAccess.ReadWrite))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            foreach (var (name, data) in fonts)
            {
                using var entryStream = zip.CreateEntry(name, CompressionLevel.Optimal).Open();
                entryStream.Write(data);
            }
        }

        return new VideoFontsResult(zipPath, [.. fonts.Select(f => f.Name)], duplicates, errors);
    }
}
