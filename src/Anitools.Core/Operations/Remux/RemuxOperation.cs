using Anitools.Core.Operations.Common;
using Anitools.Core.Parsing;
using Anitools.Core.Processes;

namespace Anitools.Core.Operations.Remux;

public enum RemuxFormat
{
    Mp4,
    Mkv,
}

/// <param name="CopySubtitles">Только для MKV: копировать субтитры (в оригинале по умолчанию — да).</param>
public sealed record RemuxOptions(RemuxFormat Format = RemuxFormat.Mp4, bool CopySubtitles = true);

/// <summary>П.6 «Конвертировать видео» (convert_mkv_to_mp4, py:3520): ремукс без перекодирования в MP4 или MKV.</summary>
public static class RemuxOperation
{
    /// <summary>Папка называется так и для MKV — как в оригинале.</summary>
    public const string OutputFolderName = "converted_mp4";

    public static IReadOnlyList<string> Extensions { get; } = [".mkv", ".mp4", ".avi", ".mov", ".ts", ".m2ts", ".webm", ".flv", ".wmv", ".vob", ".m4v"];

    public static OperationPlan Plan(string folder, RemuxOptions options)
    {
        var files = MediaFiles.List(folder, Extensions);
        if (files.Count == 0)
        {
            throw new PlanException($"Не найдено видеофайлов. Поддерживаются: {string.Join("  ", Extensions)}");
        }

        var ext = options.Format == RemuxFormat.Mp4 ? ".mp4" : ".mkv";
        var outputFolder = Path.Combine(folder, OutputFolderName);
        var items = files.Select(file =>
        {
            var name = Path.GetFileName(file);
            var output = Path.Combine(outputFolder, PyText.Stem(name) + ext);
            if (MediaFiles.IsDone(output))
            {
                return new PlanItem { Source = file, Label = name, Status = PlanItemStatus.Skip, Reason = "уже конвертирован", Outputs = [output] };
            }

            // «-map 0:a?» вместо «0:a» оригинала: файлы без звука иначе падают (docs/PLAN.md §2.8 #7)
            List<string> args = ["-nostdin", "-i", file, "-map", "0:v:0", "-map", "0:a?"];
            if (options.Format == RemuxFormat.Mkv && options.CopySubtitles)
            {
                args.AddRange(["-map", "0:s?"]);
            }

            args.AddRange(["-c", "copy", "-y", output]);
            return new PlanItem
            {
                Source = file,
                Label = name,
                Status = PlanItemStatus.Run,
                Outputs = [output],
                Command = new PlannedCommand(Tool.Ffmpeg, args),
            };
        }).ToList();
        return new OperationPlan(options.Format == RemuxFormat.Mp4 ? "Ремукс в MP4" : "Ремукс в MKV", folder, items);
    }
}
