using System.Globalization;
using Anitools.Core.Operations.Common;
using Anitools.Core.Operations.Hls;
using Anitools.Core.Parsing;
using Anitools.Core.Text;
using Anitools.Core.WorkDir;

namespace Anitools.Core.Jobs;

/// <summary>
/// HLS как задача очереди: временная папка (RAM-диск / папка / рядом с выходом) берётся на время работы и снимается
/// после, серии идут по одной, ход — «серия 3/12 · Frieren - 03 · видео 41% · x2.3», итоги серий — второй строкой.
/// </summary>
public static class HlsJobs
{
    /// <param name="shutdown">Выключить компьютер, если всё прошло без отмены; null — не выключать.</param>
    /// <param name="cleanupOrphans">
    /// Снять RAM-диски, оставшиеся от аварийно завершённого запуска (как _setup_work_dir оригинала — перед каждым HLS,
    /// при любом выборе временной папки); возвращает снятые буквы. null — не нужно (не Windows).
    /// </param>
    public static Func<JobContext, Task<JobOutcome>> Run(
        HlsPlan plan,
        HlsRunner runner,
        IWorkDirProvider workDir,
        bool calibrates,
        Func<CancellationToken, Task<bool>>? shutdown = null,
        Func<CancellationToken, Task<IReadOnlyList<char>>>? cleanupOrphans = null) => async context =>
    {
        var ct = context.CancellationToken;
        context.Log($"HLS: {plan.Folder} → {plan.OutputRoot}");
        context.Log($"к выполнению {plan.RunCount}, пропуск {plan.SkipCount}, ошибок в плане {plan.ErrorCount}");
        if (cleanupOrphans is not null)
        {
            foreach (var letter in await cleanupOrphans(ct).ConfigureAwait(false))
            {
                context.Log($"Снят незакрытый RAM-диск {letter}: от прошлого запуска.");
            }
        }

        context.Report(0, "временная папка…");
        WorkDirLease lease;
        try
        {
            lease = await workDir.AcquireAsync(ct).ConfigureAwait(false);
        }
        catch (WorkDirException ex)
        {
            context.Log("✗ " + ex.Message);
            return new JobOutcome(false, ex.Message);
        }

        HlsResult result;
        var tracker = new Tracker(context, calibrates);
        await using (lease.ConfigureAwait(false))
        {
            context.Log($"Временные файлы: {lease.Description}");
            result = await runner.ExecuteAsync(plan, lease.Path, tracker, ct, tracker.Done).ConfigureAwait(false);
        }

        // Пропущенные планом и не начатые из-за отмены серии исполнитель не сообщает по ходу — в журнал в конце
        foreach (var episode in result.Episodes.Where(e => !tracker.Reported(e.Episode)))
        {
            foreach (var line in Lines(episode, null))
            {
                context.Log(line);
            }
        }

        var outcome = Outcome(result);
        context.Log($"{outcome.Summary} · {RuText.Duration(result.Elapsed)}");
        if (shutdown is not null && outcome.Success && !ct.IsCancellationRequested)
        {
            context.Log(await shutdown(CancellationToken.None).ConfigureAwait(false)
                ? "Компьютер выключится через минуту (отменить: shutdown /a в консоли)."
                : "Не удалось запланировать выключение компьютера.");
        }

        return outcome;
    };

    /// <summary>«12 готово · 1 пропуск · 1 ошибка».</summary>
    public static JobOutcome Outcome(HlsResult result)
    {
        var parts = new List<string> { $"{result.Count(HlsEpisodeOutcome.Done)} готово" };
        if (result.Count(HlsEpisodeOutcome.Skipped) is > 0 and var skipped)
        {
            parts.Add(RuText.Plural(skipped, "пропуск", "пропуска", "пропусков"));
        }

        var failed = result.Count(HlsEpisodeOutcome.Failed);
        if (failed > 0)
        {
            parts.Add(RuText.Plural(failed, "ошибка", "ошибки", "ошибок"));
        }

        if (result.Count(HlsEpisodeOutcome.Cancelled) + result.Count(HlsEpisodeOutcome.NotRun) is > 0 and var notDone)
        {
            parts.Add($"не выполнено {notDone}");
        }

        return new JobOutcome(failed == 0 && !result.WasCancelled, string.Join(" · ", parts));
    }

    /// <summary>Строки журнала об итоге серии: результат, качество, битрейты, пояснения.</summary>
    public static IEnumerable<string> Lines(HlsEpisodeResult result, TimeSpan? elapsed)
    {
        var name = result.Episode.Source.Name;
        var time = elapsed is { } e ? $" · {RuText.Duration(e)}" : "";
        yield return result.Outcome switch
        {
            HlsEpisodeOutcome.Done => $"✓ {name}{time}",
            HlsEpisodeOutcome.Skipped => $"· {name} — пропуск: {result.Message}",
            HlsEpisodeOutcome.Cancelled => $"⊘ {name} — отменено",
            HlsEpisodeOutcome.NotRun => $"· {name} — не выполнено (отмена)",
            _ => $"✗ {name} — {result.Message}" + (result.LogPath is { } log ? $" (лог: {log})" : "") + time,
        };
        if (result.RateControl is { Count: > 0 } rc)
        {
            var cqs = rc.Select(r => r.Cq).Distinct().ToList();
            yield return "   CQ " + string.Join("/", cqs.Select(c => c.ToString("0.#", CultureInfo.InvariantCulture)));
        }

        if (result.Bitrates.Count > 0)
        {
            yield return "   " + string.Join(", ", result.Bitrates.Select(b => $"{b.Rung} {b.Mbps.ToString("0.0", CultureInfo.InvariantCulture)}")) + " Мбит/с";
        }

        foreach (var note in result.Notes)
        {
            yield return "   " + note;
        }
    }

    /// <summary>«видео», «аудио»… для строки хода.</summary>
    public static string StageName(HlsStage stage) => stage switch
    {
        HlsStage.Preparing => "подготовка",
        HlsStage.Analysis => "анализ битрейта",
        HlsStage.Calibration => "подбор CQ",
        HlsStage.Video => "видео",
        HlsStage.Audio => "аудио",
        HlsStage.Packing => "архив",
        _ => "перенос",
    };

    /// <summary>
    /// Доля серии по стадии: видео — основная часть; анализ и подбор CQ есть только при подборе качества.
    /// </summary>
    public static double EpisodeFraction(HlsStage stage, double? fraction, bool calibrates)
    {
        var (start, end) = stage switch
        {
            HlsStage.Preparing => (0.0, 0.01),
            HlsStage.Analysis => (0.01, 0.05),
            HlsStage.Calibration => (0.05, 0.15),
            HlsStage.Video => (calibrates ? 0.15 : 0.01, 0.9),
            HlsStage.Audio => (0.9, 0.95),
            HlsStage.Packing => (0.95, 0.99),
            _ => (0.99, 1.0),
        };
        return start + ((end - start) * Math.Clamp(fraction ?? 0, 0, 1));
    }

    /// <summary>Ход по сериям и итоги готовых серий второй строкой.</summary>
    private sealed class Tracker(JobContext context, bool calibrates) : IProgress<HlsProgress>
    {
        private const int DetailEpisodes = 12;
        private readonly object _lock = new();
        private readonly List<string> _done = [];
        private readonly Dictionary<string, DateTime> _started = new(StringComparer.Ordinal);
        private readonly HashSet<HlsEpisode> _reported = [];

        public void Report(HlsProgress value)
        {
            lock (_lock)
            {
                _started.TryAdd(value.Episode.EpisodeName, DateTime.Now);
            }

            var overall = (value.EpisodeIndex - 1 + EpisodeFraction(value.Stage, value.Fraction, calibrates)) / Math.Max(1, value.EpisodeCount);
            var parts = new List<string> { $"серия {value.EpisodeIndex}/{value.EpisodeCount}", value.Episode.EpisodeName, StageName(value.Stage) };
            if (value.Fraction is > 0 and < 1 and var f)
            {
                parts[^1] += " " + RuText.Percent(f);
            }

            if (value.Speed is { } speed)
            {
                parts.Add(RuText.Speed(speed));
            }

            context.Report(overall, string.Join(" · ", parts));
        }

        public bool Reported(HlsEpisode episode)
        {
            lock (_lock)
            {
                return _reported.Contains(episode);
            }
        }

        public void Done(HlsEpisodeResult result)
        {
            TimeSpan? elapsed = null;
            string detail;
            lock (_lock)
            {
                _reported.Add(result.Episode);
                if (_started.TryGetValue(result.Episode.EpisodeName, out var started))
                {
                    elapsed = DateTime.Now - started;
                }

                if (result.Outcome is HlsEpisodeOutcome.Done or HlsEpisodeOutcome.Failed)
                {
                    var number = EpisodeNumber.Extract(result.Episode.Source.Name) ?? result.Episode.EpisodeName;
                    var mark = result.Outcome == HlsEpisodeOutcome.Done ? "✓" : "✗";
                    _done.Add($"{number} {mark}" + (elapsed is { } e ? " " + RuText.Duration(e) : ""));
                }

                var shown = _done.Count > DetailEpisodes ? ["…", .. _done.TakeLast(DetailEpisodes)] : _done;
                detail = string.Join(" · ", shown);
            }

            foreach (var line in Lines(result, elapsed))
            {
                context.Log(line);
            }

            if (detail.Length > 0)
            {
                context.SetDetail(detail);
            }
        }
    }
}
