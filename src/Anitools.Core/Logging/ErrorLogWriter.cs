using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Anitools.Core.Parsing;
using Anitools.Core.Processes;

namespace Anitools.Core.Logging;

/// <summary>
/// Логи ошибок внешних программ (write_process_error_log, py:846): время, команда, код возврата и хвост вывода.
/// Пишутся в %LOCALAPPDATA%\anitools\logs (в оригинале — в текущую папку процесса, docs/PLAN.md §2.8 #8).
/// </summary>
public sealed partial class ErrorLogWriter(string directory, Func<DateTime>? clock = null)
{
    private readonly Func<DateTime> _clock = clock ?? (() => DateTime.Now);

    public string Directory { get; } = directory;

    public static string DefaultDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "anitools", "logs");

    /// <summary>Пишет лог «{имя}_ffmpeg_error_{дата_время}.log» и возвращает путь; null — записать не удалось.</summary>
    public string? WriteProcessError(string prefix, string output, IReadOnlyList<string>? command, int? exitCode)
    {
        var now = _clock();
        var safe = UnsafeCharsRegex().Replace(prefix, "_");
        safe = new string(safe.EnumerateRunes().Take(80).SelectMany(r => r.ToString()).ToArray());
        if (safe.Length == 0)
        {
            safe = "log";
        }

        var text = new StringBuilder();
        text.Append("Time: ").Append(now.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff", CultureInfo.InvariantCulture)).Append('\n');
        if (command is not null)
        {
            text.Append("Command: ").Append(CommandLine.Format(command)).Append('\n');
        }

        if (exitCode is { } code)
        {
            text.Append("Return code: ").Append(code.ToString(CultureInfo.InvariantCulture)).Append('\n');
        }

        text.Append("\n--- output (stdout+stderr, tail) ---\n");
        var trimmed = PyText.Strip(output ?? "");
        text.Append(trimmed.Length > 0 ? trimmed + "\n" : "(процесс не вывел ничего — возможно, был прерван снаружи)\n");

        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            var stamp = now.ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture);
            var path = Path.Combine(Directory, $"{safe}_ffmpeg_error_{stamp}.log");
            for (var n = 2; File.Exists(path); n++)
            {
                path = Path.Combine(Directory, $"{safe}_ffmpeg_error_{stamp}_{n}.log");
            }

            File.WriteAllText(path, text.ToString(), new UTF8Encoding(false));
            return path;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    // [^\w.-] с \w как в Python
    [GeneratedRegex(@"[^\p{L}\p{N}_.-]", RegexOptions.CultureInvariant)]
    private static partial Regex UnsafeCharsRegex();
}
