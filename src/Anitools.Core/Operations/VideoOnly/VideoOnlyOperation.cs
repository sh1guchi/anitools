using Anitools.Core.Operations.Common;
using Anitools.Core.Parsing;
using Anitools.Core.Processes;

namespace Anitools.Core.Operations.VideoOnly;

/// <summary>«Только видео»: первая видеодорожка без звука, субтитров и вложений.</summary>
public static class VideoOnlyOperation
{
    public const string OutputFolderName = "Video only";

    public static OperationPlan Plan(string folder)
    {
        var files = MediaFiles.List(folder, MediaFiles.VideoExtensions);
        if (files.Count == 0)
        {
            throw new PlanException("Нет подходящих файлов в папке.");
        }

        var outputFolder = Path.Combine(folder, OutputFolderName);
        var items = files.Select(file =>
        {
            var name = Path.GetFileName(file);
            var output = Path.Combine(outputFolder, TitleText.OutputFileName(name));
            return MediaFiles.IsDone(output)
                ? new PlanItem { Source = file, Label = name, Status = PlanItemStatus.Skip, Reason = "уже готово", Outputs = [output] }
                : new PlanItem
                {
                    Source = file,
                    Label = name,
                    Status = PlanItemStatus.Run,
                    Outputs = [output],
                    Command = new PlannedCommand(Tool.Ffmpeg, ["-nostdin", "-y", "-i", file, "-map", "0:v:0", "-c:v", "copy", "-an", output]),
                };
        }).ToList();
        return new OperationPlan("Только видео", folder, items);
    }
}
