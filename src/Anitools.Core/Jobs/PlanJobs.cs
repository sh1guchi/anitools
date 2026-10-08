using Anitools.Core.Operations.Common;
using Anitools.Core.Text;

namespace Anitools.Core.Jobs;

/// <summary>План операции как задача очереди: ход работы по шагам, журнал итогов, сводка.</summary>
public static class PlanJobs
{
    /// <param name="maxParallel">Сколько шагов сразу (сдвиг и перекодирование аудио — по 6–8).</param>
    public static Func<JobContext, Task<JobOutcome>> Run(OperationPlan plan, PlanExecutor executor, int maxParallel = 1) => async context =>
    {
        context.Log($"{plan.Title}: {plan.Folder}");
        context.Log($"к выполнению {plan.RunCount}, пропуск {plan.SkipCount}, ошибок в плане {plan.ErrorCount}");
        var tracker = new ProgressTracker(plan.RunCount, context);
        var result = await executor.ExecuteAsync(plan, tracker, context.CancellationToken, maxParallel).ConfigureAwait(false);
        foreach (var item in result.Items)
        {
            context.Log(Line(item));
        }

        var outcome = Outcome(result);
        context.Log(outcome.Summary);
        return outcome;
    };

    /// <summary>«11 готово · 1 пропуск · 1 ошибка».</summary>
    public static JobOutcome Outcome(OperationResult result)
    {
        var done = result.Count(ItemOutcome.Done) + result.Count(ItemOutcome.Warning);
        var failed = result.Count(ItemOutcome.Failed);
        var parts = new List<string> { $"{done} готово" };
        if (result.Count(ItemOutcome.Skipped) is > 0 and var skipped)
        {
            parts.Add(RuText.Plural(skipped, "пропуск", "пропуска", "пропусков"));
        }

        if (failed > 0)
        {
            parts.Add(RuText.Plural(failed, "ошибка", "ошибки", "ошибок"));
        }

        if (result.Count(ItemOutcome.Cancelled) + result.Count(ItemOutcome.NotRun) is > 0 and var notDone)
        {
            parts.Add($"не выполнено {notDone}");
        }

        return new JobOutcome(failed == 0 && !result.WasCancelled, string.Join(" · ", parts));
    }

    /// <summary>Строка журнала об итоге шага.</summary>
    public static string Line(ItemResult item) => item.Outcome switch
    {
        ItemOutcome.Done => $"✓ {item.Item.Label}",
        ItemOutcome.Warning => $"✓ {item.Item.Label} — {item.Message}",
        ItemOutcome.Skipped => $"· {item.Item.Label} — пропуск: {item.Message}",
        ItemOutcome.Cancelled => $"⊘ {item.Item.Label} — отменено",
        ItemOutcome.NotRun => $"· {item.Item.Label} — не выполнено (отмена)",
        _ => $"✗ {item.Item.Label} — {item.Message}" + (item.LogPath is { } log ? $" (лог: {log})" : ""),
    };

    /// <summary>
    /// Общая готовность = сумма долей шагов / число шагов (шаги могут идти параллельно), строка хода — по последнему
    /// событию: «файл 3/12 · Frieren - 03.mkv · 41% · x2.3».
    /// </summary>
    private sealed class ProgressTracker(int count, JobContext context) : IProgress<OperationProgress>
    {
        private readonly object _lock = new();
        private readonly Dictionary<int, double> _fractions = [];

        public void Report(OperationProgress value)
        {
            double overall;
            lock (_lock)
            {
                if (value.Fraction is { } f)
                {
                    _fractions[value.ItemIndex] = Math.Clamp(f, 0, 1);
                }
                else
                {
                    _fractions.TryAdd(value.ItemIndex, 0);
                }

                overall = count > 0 ? _fractions.Values.Sum() / count : 0;
            }

            var parts = new List<string> { $"файл {value.ItemIndex}/{value.ItemCount}", value.Item.Label };
            if (value.Fraction is > 0 and < 1 and var fraction)
            {
                parts.Add(RuText.Percent(fraction));
            }

            if (value.Speed is { } speed)
            {
                parts.Add(RuText.Speed(speed));
            }

            context.Report(overall, string.Join(" · ", parts));
        }
    }
}
