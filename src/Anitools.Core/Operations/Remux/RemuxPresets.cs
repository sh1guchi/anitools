using Anitools.Core.Operations.Common;
using Anitools.Core.Parsing;
using Anitools.Core.Processes;

namespace Anitools.Core.Operations.Remux;

/// <summary>
/// Готовые профили ремукса (§2.9.10): результат — рядом с исходником, готовый не переделывается.
/// AVI отдельного профиля не требует — обычный ремукс сам добавляет к нему флаги меток времени.
/// </summary>
public static class RemuxPresets
{
    /// <summary>
    /// Blu-ray M2TS → MKV: видео и субтитры копией, звук (обычно PCM Blu-ray) — во FLAC без потерь.
    /// </summary>
    public static OperationPlan M2tsToMkv(string folder) => Plan(folder, ".m2ts", "M2TS → MKV", (input, output) =>
        ["-i", input, "-map", "0:v", "-map", "0:a", "-map", "0:s?", "-c:v", "copy", "-c:a", "flac", "-c:s", "copy", output, "-y"]);

    private static OperationPlan Plan(string folder, string extension, string title, Func<string, string, IReadOnlyList<string>> command)
    {
        var files = MediaFiles.List(folder, [extension]);
        if (files.Count == 0)
        {
            throw new PlanException($"Файлов {extension} в папке нет.");
        }

        var items = files.Select(file =>
        {
            var name = Path.GetFileName(file);
            var output = Path.Combine(folder, TextUtils.SplitExt(name).Root + ".mkv");
            return MediaFiles.IsDone(output)
                ? new PlanItem { Source = file, Label = name, Status = PlanItemStatus.Skip, Reason = "уже есть .mkv", Outputs = [output] }
                : new PlanItem
                {
                    Source = file,
                    Label = name,
                    Status = PlanItemStatus.Run,
                    Outputs = [output],
                    Command = new PlannedCommand(Tool.Ffmpeg, ["-nostdin", .. command(file, output)]),
                };
        }).ToList();
        return new OperationPlan(title, folder, items);
    }
}
