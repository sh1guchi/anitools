using Anitools.Core.Operations.Common;
using Anitools.Core.Parsing;
using Anitools.Core.Processes;

namespace Anitools.Core.Operations.Remux;

public enum RemuxFormat
{
    Mp4,
    Mkv,
}

/// <param name="CopySubtitles">Только для MKV: копировать субтитры (по умолчанию — да).</param>
public sealed record RemuxOptions(RemuxFormat Format = RemuxFormat.Mp4, bool CopySubtitles = true);

/// <summary>«Ремукс»: смена контейнера без перекодирования — в MP4 или MKV.</summary>
public static class RemuxOperation
{
    /// <summary>Папка называется так и для MKV.</summary>
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
            var output = Path.Combine(outputFolder, TextUtils.Stem(name) + ext);
            if (MediaFiles.IsDone(output))
            {
                return new PlanItem { Source = file, Label = name, Status = PlanItemStatus.Skip, Reason = "уже конвертирован", Outputs = [output] };
            }

            // AVI: меток времени часто не хватает — генерируются, отрицательные сдвигаются к нулю (§2.9.10)
            var avi = string.Equals(Path.GetExtension(file), ".avi", StringComparison.OrdinalIgnoreCase);
            List<string> args = ["-nostdin"];
            if (avi)
            {
                args.AddRange(["-fflags", "+genpts"]);
            }

            // «-map 0:a?», а не «0:a»: иначе файлы без звука падают (docs/PLAN.md §2.8 #7)
            args.AddRange(["-i", file, "-map", "0:v:0", "-map", "0:a?"]);
            if (options.Format == RemuxFormat.Mkv && options.CopySubtitles)
            {
                args.AddRange(["-map", "0:s?"]);
            }

            args.AddRange(["-c", "copy"]);
            if (avi)
            {
                args.AddRange(["-avoid_negative_ts", "make_zero"]);
            }

            args.AddRange(["-y", output]);
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
