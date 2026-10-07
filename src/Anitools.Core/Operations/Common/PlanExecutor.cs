using System.Globalization;
using System.Text.RegularExpressions;
using Anitools.Core.Logging;
using Anitools.Core.Media;
using Anitools.Core.Parsing;
using Anitools.Core.Platform;
using Anitools.Core.Processes;

namespace Anitools.Core.Operations.Common;

public enum ItemOutcome
{
    Done,

    /// <summary>Программа вернула предупреждение, но выход на месте (mkvextract: код 1).</summary>
    Warning,
    Failed,
    Skipped,
    Cancelled,

    /// <summary>Не дошли: операцию отменили раньше.</summary>
    NotRun,
}

/// <summary>Итог одного шага; <see cref="LogPath"/> — лог ошибки, если он записан.</summary>
public sealed record ItemResult(PlanItem Item, ItemOutcome Outcome, string? Message = null, string? LogPath = null);

public sealed record OperationResult(IReadOnlyList<ItemResult> Items)
{
    public int Count(ItemOutcome outcome) => Items.Count(i => i.Outcome == outcome);

    public bool WasCancelled => Items.Any(i => i.Outcome == ItemOutcome.Cancelled);
}

/// <summary>Ход выполнения: шаг i из n, доля готовности текущего шага, скорость и битрейт ffmpeg.</summary>
public sealed record OperationProgress(int ItemIndex, int ItemCount, PlanItem Item, double? Fraction, double? Speed = null, string? Bitrate = null);

/// <summary>
/// Выполняет план: шаги по одному, перед запуском создаёт папки и снимает «только чтение»,
/// после успеха — снимает его с выходных файлов. Ошибка → лог и удаление недописанного выхода, дальше
/// следующий шаг. Отмена → процесс убит, недописанный выход удалён, остальные шаги не выполняются.
/// </summary>
public sealed partial class PlanExecutor(IProcessRunner runner, ToolPaths tools, ErrorLogWriter logs, IMediaProbe? probe = null)
{
    public async Task<OperationResult> ExecuteAsync(
        OperationPlan plan,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var results = new List<ItemResult>();
        var toRun = plan.Items.Where(i => i.Status == PlanItemStatus.Run).ToList();
        var index = 0;
        foreach (var item in plan.Items)
        {
            if (item.Status != PlanItemStatus.Run)
            {
                results.Add(new ItemResult(item, item.Status == PlanItemStatus.Skip ? ItemOutcome.Skipped : ItemOutcome.Failed, item.Reason));
                continue;
            }

            if (cancellationToken.IsCancellationRequested)
            {
                results.Add(new ItemResult(item, ItemOutcome.NotRun));
                continue;
            }

            index++;
            results.Add(await RunItemAsync(item, index, toRun.Count, progress, cancellationToken).ConfigureAwait(false));
        }

        return new OperationResult(results);
    }

    private async Task<ItemResult> RunItemAsync(PlanItem item, int index, int count, IProgress<OperationProgress>? progress, CancellationToken ct)
    {
        var command = item.Command ?? throw new InvalidOperationException($"У шага «{item.Label}» нет команды");
        var exe = command.Tool switch
        {
            Tool.Ffmpeg => tools.Ffmpeg,
            Tool.Ffprobe => tools.Ffprobe,
            Tool.Mkvmerge => tools.Mkvmerge,
            Tool.Mkvextract => tools.Mkvextract,
            _ => null,
        };
        if (exe is null)
        {
            return new ItemResult(item, ItemOutcome.Failed, $"Не найдена программа {command.Tool.ToString().ToLowerInvariant()}");
        }

        foreach (var output in item.Outputs)
        {
            var dir = Path.GetDirectoryName(output);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
                ReadOnlyAttr.Clear(dir);
            }

            if (File.Exists(output))
            {
                // пустой остаток прошлого запуска перезапишется — пусть не мешает «только чтение»
                ReadOnlyAttr.Clear(output);
            }
        }

        progress?.Report(new OperationProgress(index, count, item, 0));
        var captured = new List<string>();
        var spec = await BuildSpecAsync(item, command, exe, index, count, progress, captured, ct).ConfigureAwait(false);

        ProcessResult result;
        try
        {
            result = await runner.RunAsync(spec, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            DeleteOutputs(item);
            return new ItemResult(item, ItemOutcome.Cancelled, "Отменено");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            return new ItemResult(item, ItemOutcome.Failed, $"Не удалось запустить {Path.GetFileName(exe)}: {ex.Message}");
        }

        var outputsReady = item.Outputs.All(MediaFiles.IsDone);
        if (result.ExitCode == 0 || (command.WarningExitCodes.Contains(result.ExitCode) && outputsReady && item.Outputs.Count > 0))
        {
            foreach (var output in item.Outputs)
            {
                ReadOnlyAttr.Clear(output);
            }

            progress?.Report(new OperationProgress(index, count, item, 1));
            return result.ExitCode == 0
                ? new ItemResult(item, ItemOutcome.Done)
                : new ItemResult(item, ItemOutcome.Warning, $"{command.Tool.ToString().ToLowerInvariant()} вернул предупреждения (код {result.ExitCode}), файл извлечён");
        }

        string capturedText;
        lock (captured)
        {
            capturedText = string.Join('\n', captured);
        }

        var processOutput = string.Join('\n', new[] { capturedText, result.StandardOutput, result.StandardErrorTail }.Where(s => !string.IsNullOrWhiteSpace(s)));
        var log = logs.WriteProcessError(PyText.Stem(Path.GetFileName(item.Source)), processOutput, [exe, .. spec.Arguments], result.ExitCode);
        DeleteOutputs(item);
        return new ItemResult(item, ItemOutcome.Failed, $"{command.Tool.ToString().ToLowerInvariant()} вернул код {result.ExitCode}", log);
    }

    /// <param name="captured">Сюда — строки stdout, которые не прогресс (у MKVToolNix ошибки идут в stdout), для лога ошибки.</param>
    private async Task<ProcessSpec> BuildSpecAsync(
        PlanItem item,
        PlannedCommand command,
        string exe,
        int index,
        int count,
        IProgress<OperationProgress>? progress,
        List<string> captured,
        CancellationToken ct)
    {
        if (command.Tool == Tool.Ffmpeg)
        {
            // Команда та же, что в оригинале, плюс машинный прогресс: время, скорость, битрейт
            var duration = probe is null ? null : await DurationAsync(item.Source, ct).ConfigureAwait(false);
            var parser = new FfmpegProgressParser(p => progress?.Report(new OperationProgress(
                index, count, item, duration is > 0 ? p.Fraction(duration.Value) : null, p.Speed, p.Bitrate)));
            return new ProcessSpec(exe, ["-progress", "pipe:1", "-nostats", .. command.Arguments]) { OnStdoutLine = parser.Feed };
        }

        if (command.Tool == Tool.Mkvextract)
        {
            return new ProcessSpec(exe, ["--output-charset", "UTF-8", .. command.Arguments])
            {
                // «Progress: 45%» идут через \r — ReadLine режет и по нему; остальные строки — в лог ошибки
                OnStdoutLine = line =>
                {
                    var m = MkvProgressRegex().Match(line);
                    if (m.Success)
                    {
                        progress?.Report(new OperationProgress(index, count, item, int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) / 100.0));
                    }
                    else if (line.Length > 0)
                    {
                        lock (captured)
                        {
                            captured.Add(line);
                        }
                    }
                },
            };
        }

        return new ProcessSpec(exe, command.Arguments);
    }

    private async Task<double?> DurationAsync(string path, CancellationToken ct)
    {
        try
        {
            return (await probe!.ProbeAsync(path, ct).ConfigureAwait(false)).Duration;
        }
        catch (MediaProbeException)
        {
            return null;
        }
        catch (ToolNotFoundException)
        {
            return null;
        }
    }

    private static void DeleteOutputs(PlanItem item)
    {
        foreach (var output in item.Outputs)
        {
            try
            {
                if (File.Exists(output))
                {
                    ReadOnlyAttr.Clear(output);
                    File.Delete(output);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    [GeneratedRegex(@"^Progress: (\d{1,3})%")]
    private static partial Regex MkvProgressRegex();
}
