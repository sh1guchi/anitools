using Anitools.Core.Jobs;
using Anitools.Core.Logging;
using Anitools.Core.Operations.Common;
using Anitools.Core.Processes;
using Anitools.Core.Tests.Fixtures;

namespace Anitools.Core.Tests.Jobs;

public sealed class JobQueueTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Jobs_run_strictly_one_at_a_time_in_order()
    {
        var queue = new JobQueue();
        var running = 0;
        var maxRunning = 0;
        var order = new List<int>();
        var jobs = Enumerable.Range(1, 4).Select(n => queue.Enqueue($"задача {n}", "/anime", async context =>
        {
            var now = Interlocked.Increment(ref running);
            lock (order)
            {
                maxRunning = Math.Max(maxRunning, now);
                order.Add(n);
            }

            await Task.Delay(20, context.CancellationToken);
            Interlocked.Decrement(ref running);
            return new JobOutcome(true, "ok");
        })).ToList();

        await queue.WhenIdleAsync().WaitAsync(Ct);

        Assert.Equal(1, maxRunning);
        Assert.Equal([1, 2, 3, 4], order);
        Assert.All(jobs, j => Assert.Equal(JobState.Done, j.State));
        Assert.False(queue.IsBusy);
        Assert.Null(queue.Current);
    }

    [Fact]
    public async Task Cancel_stops_running_job_and_queued_ones_never_start()
    {
        var queue = new JobQueue();
        var started = new TaskCompletionSource();
        var secondRan = false;
        var first = queue.Enqueue("долгая", "/anime", async context =>
        {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, context.CancellationToken);
            return new JobOutcome(true, "не дойдёт");
        });
        var second = queue.Enqueue("вторая", "/anime", _ =>
        {
            secondRan = true;
            return Task.FromResult(new JobOutcome(true, "ok"));
        });

        await started.Task.WaitAsync(Ct);
        second.Cancel();
        first.Cancel();
        await queue.WhenIdleAsync().WaitAsync(Ct);

        Assert.Equal(JobState.Cancelled, first.State);
        Assert.Equal(JobState.Cancelled, second.State);
        Assert.False(secondRan);
        Assert.Equal("отменена до запуска", second.Snapshot.Summary);
    }

    [Fact]
    public async Task Failure_or_exception_does_not_stop_the_queue()
    {
        var queue = new JobQueue();
        var stillCurrent = new List<bool>();
        var allFinished = new TaskCompletionSource();
        queue.JobFinished += job =>
        {
            lock (stillCurrent)
            {
                stillCurrent.Add(queue.Current == job);
                if (stillCurrent.Count == 3)
                {
                    allFinished.SetResult();
                }
            }
        };

        var a = queue.Enqueue("ошибки", "/a", _ => Task.FromResult(new JobOutcome(false, "1 ошибка")));
        var b = queue.Enqueue("исключение", "/b", _ => throw new InvalidOperationException("сломалось"));
        var c = queue.Enqueue("нормальная", "/c", context =>
        {
            context.Log("привет");
            return Task.FromResult(new JobOutcome(true, "1 готово"));
        });

        await queue.WhenIdleAsync().WaitAsync(Ct);

        Assert.Equal(JobState.Failed, a.State);
        Assert.Equal(JobState.Failed, b.State);
        Assert.Equal("сломалось", b.Snapshot.Summary);
        Assert.Contains(b.Log, line => line.Contains("InvalidOperationException", StringComparison.Ordinal));
        Assert.Equal(JobState.Done, c.State);
        Assert.Equal(1.0, c.Snapshot.Fraction);
        Assert.EndsWith("привет", Assert.Single(c.Log));

        // к событию «закончилась» задача уже не текущая (строка состояния её не покажет)
        await allFinished.Task.WaitAsync(Ct);
        Assert.Equal([false, false, false], stillCurrent);
        queue.ClearFinished();
        Assert.Empty(queue.Jobs);
    }

    [Fact]
    public async Task Progress_and_eta_are_reported()
    {
        var now = new DateTime(2026, 10, 7, 20, 0, 0);
        var queue = new JobQueue(() => now);
        var halfway = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var job = queue.Enqueue("HLS · Frieren", "/anime", async context =>
        {
            now = now.AddMinutes(10);
            context.Report(0.25, "серия 1/4 · видео 100%");
            halfway.SetResult();
            await release.Task;
            return new JobOutcome(true, "4 готово");
        });

        await halfway.Task.WaitAsync(Ct);
        var snapshot = job.Snapshot;
        release.SetResult();
        await job.Completion.WaitAsync(Ct);

        Assert.Equal(JobState.Running, snapshot.State);
        Assert.Equal(0.25, snapshot.Fraction);
        Assert.Equal("серия 1/4 · видео 100%", snapshot.Status);
        Assert.Equal(TimeSpan.FromMinutes(30), snapshot.Eta);
        Assert.Equal("готово", job.Snapshot.Status);
        Assert.Null(job.Snapshot.Eta);
    }

    [Fact]
    public async Task Plan_job_reports_files_and_summarizes()
    {
        using var dir = new TempDir();
        var plan = new OperationPlan("Только видео", dir.Path,
        [
            Item(dir, "a", PlanItemStatus.Run),
            Item(dir, "b", PlanItemStatus.Skip) with { Reason = "уже готово" },
            Item(dir, "c", PlanItemStatus.Run),
        ]);
        var runner = new ScriptedRunner(n => n == 0 ? 0 : 1);
        var executor = new PlanExecutor(runner, new ToolPaths("/bin/ffmpeg", null, null, null), new ErrorLogWriter(dir.Combine("logs")));
        var queue = new JobQueue();
        var statuses = new List<string>();

        queue.JobAdded += added => added.Changed += j =>
        {
            lock (statuses)
            {
                statuses.Add(j.Snapshot.Status);
            }
        };

        var job = queue.Enqueue("Только видео · test", dir.Path, PlanJobs.Run(plan, executor));
        await job.Completion.WaitAsync(Ct);

        Assert.Equal(JobState.Failed, job.State);
        Assert.Equal("1 готово · 1 пропуск · 1 ошибка", job.Snapshot.Summary);
        Assert.Contains(job.Log, l => l.EndsWith("✓ a.mkv", StringComparison.Ordinal));
        Assert.Contains(job.Log, l => l.Contains("· b.mkv — пропуск: уже готово", StringComparison.Ordinal));
        Assert.Contains(job.Log, l => l.Contains("✗ c.mkv — ffmpeg вернул код 1 (лог: ", StringComparison.Ordinal));
        Assert.Contains("файл 2/2 · c.mkv", statuses);
    }

    private static PlanItem Item(TempDir dir, string name, PlanItemStatus status) => new()
    {
        Source = dir.Combine(name + ".mkv"),
        Label = name + ".mkv",
        Status = status,
        Outputs = [dir.Combine("out", name + ".mkv")],
        Command = new PlannedCommand(Tool.Ffmpeg, ["-i", dir.Combine(name + ".mkv"), dir.Combine("out", name + ".mkv")]),
    };

    /// <summary>Код возврата по номеру вызова; при успехе пишет выходной файл (последний аргумент).</summary>
    private sealed class ScriptedRunner(Func<int, int> exitCode) : IProcessRunner
    {
        private int _calls;

        public Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken cancellationToken = default)
        {
            var code = exitCode(_calls++);
            if (code == 0)
            {
                File.WriteAllText(spec.Arguments[^1], "video");
            }

            return Task.FromResult(new ProcessResult(code, "", code == 0 ? "" : "Invalid data", TimeSpan.Zero));
        }
    }
}
