using System.Diagnostics;
using System.Text;

namespace Anitools.Core.Processes;

/// <summary>Настоящий запуск через <see cref="Process"/>: вывод в UTF-8 (битые байты → �), stdin закрыт.</summary>
public sealed class ProcessRunner : IProcessRunner
{
    private static readonly Encoding Utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    public async Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var psi = new ProcessStartInfo(spec.FileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Utf8,
            StandardErrorEncoding = Utf8,
        };
        foreach (var arg in spec.Arguments)
        {
            psi.ArgumentList.Add(arg);
        }

        if (spec.WorkingDirectory is { } wd)
        {
            psi.WorkingDirectory = wd;
        }

        // Linux/macOS: без UTF-8-локали mkvmerge портит кириллицу в JSON, а mkvextract — в именах файлов
        if (!OperatingSystem.IsWindows() && !IsUtf8Locale(psi.Environment))
        {
            psi.Environment["LC_ALL"] = "C.UTF-8";
        }

        using var process = new Process { StartInfo = psi };
        var stopwatch = Stopwatch.StartNew();
        process.Start();
        // Никаких вопросов в консоли: ffmpeg без -nostdin иначе ждёт ввода
        process.StandardInput.Close();

        var stdout = new StringBuilder();
        var stderrTail = new Queue<string>();
        var stdoutTask = ReadLinesAsync(process.StandardOutput, line =>
        {
            if (spec.OnStdoutLine is { } onLine)
            {
                onLine(line);
            }
            else
            {
                stdout.Append(line).Append('\n');
            }
        });
        var stderrTask = ReadLinesAsync(process.StandardError, line =>
        {
            spec.OnStderrLine?.Invoke(line);
            lock (stderrTail)
            {
                stderrTail.Enqueue(line);
                while (stderrTail.Count > spec.StderrTailLines)
                {
                    stderrTail.Dequeue();
                }
            }
        });

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
            throw;
        }

        await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
        string tail;
        lock (stderrTail)
        {
            tail = string.Join('\n', stderrTail);
        }

        return new ProcessResult(process.ExitCode, stdout.ToString(), tail, stopwatch.Elapsed);
    }

    private static bool IsUtf8Locale(IDictionary<string, string?> environment)
    {
        // Действует первая заданная из LC_ALL, LC_CTYPE, LANG
        foreach (var name in new[] { "LC_ALL", "LC_CTYPE", "LANG" })
        {
            if (environment.TryGetValue(name, out var value) && !string.IsNullOrEmpty(value))
            {
                return value.Contains("UTF-8", StringComparison.OrdinalIgnoreCase) || value.Contains("utf8", StringComparison.OrdinalIgnoreCase);
            }
        }

        return false;
    }

    private static void Kill(Process process)
    {
        try
        {
            // Всё дерево: на Windows шим Chocolatey запускает настоящий ffmpeg дочерним процессом
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // уже завершился
        }
    }

    private static async Task ReadLinesAsync(StreamReader reader, Action<string> onLine)
    {
        while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            onLine(line);
        }
    }
}
