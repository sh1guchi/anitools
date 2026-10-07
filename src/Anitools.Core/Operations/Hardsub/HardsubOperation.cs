using Anitools.Core.Operations.Common;
using Anitools.Core.Parsing;
using Anitools.Core.Processes;

namespace Anitools.Core.Operations.Hardsub;

public sealed record HardsubOptions
{
    /// <summary>Кодирование (hardsub.py: hevc_nvenc, CQ 17, 10 бит); в тестах — libx264.</summary>
    public IReadOnlyList<string> EncodeArgs { get; init; } = DefaultEncodeArgs;

    /// <summary>Папка со шрифтами для рендера .ass в рабочей папке; есть — подхватывается (fontsdir).</summary>
    public string FontsDirName { get; init; } = "Fonts";

    public static IReadOnlyList<string> DefaultEncodeArgs { get; } =
    [
        "-c:v", "hevc_nvenc", "-tune", "hq", "-multipass", "fullres", "-rc", "vbr", "-cq", "17", "-qmin", "1", "-qmax", "51",
        "-bufsize", "80M", "-tier", "high", "-pix_fmt", "yuv420p10le",
    ];
}

/// <summary>
/// Хардсаб (hardsub.py): пары «видео + .ass с тем же именем» → Hardsub\&lt;имя видео&gt;. Пишется в
/// Hardsub\&lt;имя&gt;.part&lt;расширение&gt; и переименовывается только после успеха. ffmpeg запускается из рабочей папки
/// с относительными путями — как в оригинале, без проблем с «C:» в фильтре subtitles.
/// </summary>
public static class HardsubOperation
{
    public const string OutputFolderName = "Hardsub";

    public static IReadOnlyList<string> VideoExtensions { get; } = [".mkv", ".mp4", ".avi", ".m2ts", ".ts"];

    /// <summary>
    /// Путь для параметра фильтра subtitles: обратные слеши → прямые, затем экранирование в два уровня —
    /// для разбора параметров фильтра (\ ' :) и для графа фильтров (\ ' [ ] , ;). У оригинала (escape_filter)
    /// уровень один, и файл с апострофом в имени («it's») ffmpeg не открывал.
    /// </summary>
    public static string EscapeFilter(string path) => Escape(Escape(path.Replace('\\', '/'), "\\':"), "\\'[],;");

    private static string Escape(string text, string special)
    {
        var result = new System.Text.StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (special.Contains(c, StringComparison.Ordinal))
            {
                result.Append('\\');
            }

            result.Append(c);
        }

        return result.ToString();
    }

    public static OperationPlan Plan(string folder, HardsubOptions options)
    {
        // sorted(Path(".").iterdir()) оригинала: имена в нижнем регистре по кодам символов
        var pairs = MediaFiles.List(folder, VideoExtensions)
            .OrderBy(p => PyText.Lower(Path.GetFileName(p)), PyText.CodePointComparer)
            .Select(video => (Video: video, Ass: Path.Combine(folder, PyText.SplitExt(Path.GetFileName(video)).Root + ".ass")))
            .Where(p => File.Exists(p.Ass))
            .ToList();
        if (pairs.Count == 0)
        {
            throw new PlanException("Пар «видео + .ass с тем же именем» в папке нет.");
        }

        var fonts = Directory.Exists(Path.Combine(folder, options.FontsDirName)) ? $":fontsdir={EscapeFilter(options.FontsDirName)}" : "";
        var items = pairs.Select(p =>
        {
            var name = Path.GetFileName(p.Video);
            var (stem, ext) = PyText.SplitExt(name);
            var part = Path.Combine(OutputFolderName, $"{stem}.part{ext}"); // .part перед расширением — иначе ffmpeg не поймёт контейнер
            var output = Path.Combine(folder, OutputFolderName, name);
            List<string> args =
            [
                "-hide_banner", "-y", "-v", "error", "-i", name, "-map", "0:v:0", "-map", "0:a?",
                "-vf", $"subtitles={EscapeFilter(Path.GetFileName(p.Ass))}{fonts}",
                .. options.EncodeArgs, "-c:a", "copy", part,
            ];
            return new PlanItem
            {
                Source = p.Video,
                Label = name,
                Status = PlanItemStatus.Run,
                Reason = File.Exists(output) ? "уже есть в Hardsub — будет перезаписан" : null,
                Outputs = [Path.Combine(folder, part)],
                RenameOnSuccess = (Path.Combine(folder, part), output),
                Command = new PlannedCommand(Tool.Ffmpeg, args) { WorkingDirectory = folder },
            };
        }).ToList();
        return new OperationPlan("Хардсаб", folder, items);
    }
}
