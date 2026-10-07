using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Anitools.Core.Operations.Common;
using Anitools.Core.Parsing;

namespace Anitools.Core.Operations.SubShift;

/// <summary>Итог по файлу: что получилось или почему нет.</summary>
public sealed record SubShiftFileResult(string Source, string? Output, string? Error);

/// <summary>
/// Сдвиг субтитров (subtitle_delay+1s.py): .srt .ass .ssa из папки → subs_fixed\&lt;то же имя&gt; (UTF-8 без BOM).
/// SRT — все метки «a --> b», ASS/SSA — время в строках Dialogue; раньше нуля не уходит.
/// Переводы строк сохраняются как в файле.
/// </summary>
public static partial class SubtitleShift
{
    public const string OutputFolderName = "subs_fixed";

    public static IReadOnlyList<string> Extensions { get; } = [".srt", ".ass", ".ssa"];

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>shift_time_srt: «HH:MM:SS,mmm» + сдвиг (миллисекунды — с отбрасыванием дробной части).</summary>
    public static string ShiftSrtTime(string time, double seconds)
    {
        var (h, m, rest) = Split3(time, ':');
        var parts = rest.Split(',');
        if (parts.Length != 2)
        {
            throw new FormatException($"Не время SRT: {time}");
        }

        var total = ((((Int(h) * 3600) + (Int(m) * 60) + Int(parts[0])) * 1000) + Int(parts[1])) + (long)(seconds * 1000);
        if (total < 0)
        {
            total = 0;
        }

        return string.Create(CultureInfo.InvariantCulture, $"{total / 3600000:00}:{total % 3600000 / 60000:00}:{total % 60000 / 1000:00},{total % 1000:000}");
    }

    /// <summary>shift_time_ass: «H:MM:SS.cc» + сдвиг (сотые — с отбрасыванием дробной части).</summary>
    public static string ShiftAssTime(string time, double seconds)
    {
        var (h, m, rest) = Split3(time, ':');
        var parts = rest.Split('.');
        if (parts.Length != 2)
        {
            throw new FormatException($"Не время ASS: {time}");
        }

        var total = ((((Int(h) * 3600) + (Int(m) * 60) + Int(parts[0])) * 100) + Int(parts[1])) + (long)(seconds * 100);
        if (total < 0)
        {
            total = 0;
        }

        return string.Create(CultureInfo.InvariantCulture, $"{total / 360000}:{total % 360000 / 6000:00}:{total % 6000 / 100:00}.{total % 100:00}");
    }

    /// <summary>Текст SRT со сдвинутыми метками; пробелы вокруг «-->» приводятся к одному.</summary>
    public static string ShiftSrt(string text, double seconds) =>
        SrtTimesRegex().Replace(text, m => $"{ShiftSrtTime(m.Groups[1].Value, seconds)} --> {ShiftSrtTime(m.Groups[2].Value, seconds)}");

    /// <summary>Текст ASS/SSA со сдвинутым началом и концом в строках Dialogue.</summary>
    public static string ShiftAss(string text, double seconds) =>
        AssDialogueRegex().Replace(text, m =>
            $"Dialogue: {m.Groups[1].Value}{ShiftAssTime(m.Groups[2].Value, seconds)},{ShiftAssTime(m.Groups[3].Value, seconds)},{m.Groups[4].Value}");

    /// <summary>Файлы для сдвига (верхний уровень папки).</summary>
    public static IReadOnlyList<string> ListFiles(string folder) => MediaFiles.List(folder, Extensions);

    /// <summary>Сдвигает все файлы; готовые в subs_fixed перезаписываются (сдвиг могли поменять).</summary>
    public static IReadOnlyList<SubShiftFileResult> Execute(string folder, double seconds)
    {
        var files = ListFiles(folder);
        if (files.Count == 0)
        {
            throw new PlanException("Файлы субтитров не найдены.");
        }

        var outputFolder = Path.Combine(folder, OutputFolderName);
        Directory.CreateDirectory(outputFolder);
        return [.. files.AsParallel().AsOrdered().Select(file => ShiftFile(file, outputFolder, seconds))];
    }

    private static SubShiftFileResult ShiftFile(string file, string outputFolder, double seconds)
    {
        var output = Path.Combine(outputFolder, Path.GetFileName(file));
        try
        {
            // utf-8-sig с заменой битых байтов, как в оригинале
            var text = File.ReadAllText(file, Utf8NoBom);
            var shifted = MediaFiles.Suffix(Path.GetFileName(file)).Equals(".srt", StringComparison.OrdinalIgnoreCase)
                ? ShiftSrt(text, seconds)
                : ShiftAss(text, seconds);
            File.WriteAllText(output, shifted, Utf8NoBom);
            return new SubShiftFileResult(file, output, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
        {
            return new SubShiftFileResult(file, null, ex.Message);
        }
    }

    private static long Int(string s) => (long)PyText.ParseInt(s);

    private static (string, string, string) Split3(string time, char separator)
    {
        var parts = time.Split(separator);
        return parts.Length == 3 ? (parts[0], parts[1], parts[2]) : throw new FormatException($"Не время: {time}");
    }

    [GeneratedRegex(@"(\d{2}:\d{2}:\d{2},\d{3})" + PyText.Space + "*-->" + PyText.Space + @"*(\d{2}:\d{2}:\d{2},\d{3})")]
    private static partial Regex SrtTimesRegex();

    [GeneratedRegex(@"Dialogue: (\d+,)(\d:\d{2}:\d{2}\.\d{2}),(\d:\d{2}:\d{2}\.\d{2}),(.*)")]
    private static partial Regex AssDialogueRegex();
}
