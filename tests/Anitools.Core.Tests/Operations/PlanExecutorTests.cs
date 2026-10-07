using Anitools.Core.Logging;
using Anitools.Core.Operations.Common;
using Anitools.Core.Processes;
using Anitools.Core.Tests.Fixtures;

namespace Anitools.Core.Tests.Operations;

/// <summary>Исполнитель плана на фейковом запуске процессов.</summary>
public sealed class PlanExecutorTests
{
    private static readonly ToolPaths Tools = new("/bin/ffmpeg", "/bin/ffprobe", "/bin/mkvmerge", "/bin/mkvextract");

    [Fact]
    public async Task Runs_steps_writes_outputs_and_skips_done()
    {
        using var dir = new TempDir();
        var runner = new FakeRunner((spec, _) => Write(spec, "out", 0));
        var plan = Plan(dir, [Step(dir, "a"), Step(dir, "b") with { Status = PlanItemStatus.Skip, Reason = "уже готово" }, Step(dir, "c")]);
        var events = new List<OperationProgress>();

        var result = await Executor(runner, dir).ExecuteAsync(plan, new SyncProgress<OperationProgress>(events.Add), TestContext.Current.CancellationToken);

        Assert.Equal([ItemOutcome.Done, ItemOutcome.Skipped, ItemOutcome.Done], result.Items.Select(i => i.Outcome));
        Assert.Equal(2, runner.Specs.Count);
        // К команде оригинала добавлен только машинный прогресс
        Assert.Equal(["-progress", "pipe:1", "-nostats", "-nostdin", "-i", dir.Combine("a.mkv"), dir.Combine("out", "a.mka")], runner.Specs[0].Arguments);
        Assert.Equal("/bin/ffmpeg", runner.Specs[0].FileName);
        Assert.Contains(events, e => e.ItemIndex == 2 && e.ItemCount == 2 && e.Fraction == 1);
    }

    [Fact]
    public async Task Failure_writes_log_deletes_partial_output_and_goes_on()
    {
        using var dir = new TempDir();
        var runner = new FakeRunner((spec, n) => n == 0 ? Write(spec, "half", 1, "Invalid data found") : Write(spec, "out", 0));
        var plan = Plan(dir, [Step(dir, "a"), Step(dir, "b")]);

        var result = await Executor(runner, dir).ExecuteAsync(plan, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal([ItemOutcome.Failed, ItemOutcome.Done], result.Items.Select(i => i.Outcome));
        Assert.False(File.Exists(dir.Combine("out", "a.mka")), "Недописанный выход должен быть удалён");
        Assert.True(File.Exists(dir.Combine("out", "b.mka")));
        var log = File.ReadAllText(result.Items[0].LogPath!);
        Assert.Contains("Return code: 1", log);
        Assert.Contains("Invalid data found", log);
        Assert.StartsWith("a_ffmpeg_error_", Path.GetFileName(result.Items[0].LogPath));
    }

    [Fact]
    public async Task Mkvextract_warning_with_output_is_not_an_error()
    {
        using var dir = new TempDir();
        var runner = new FakeRunner((spec, n) => Write(spec, n == 0 ? "subs" : "", 1));
        var step = Step(dir, "a") with
        {
            Command = new PlannedCommand(Tool.Mkvextract, ["tracks", dir.Combine("a.mkv"), "3:" + dir.Combine("out", "a.mka")]) { WarningExitCodes = [1] },
        };
        var plan = Plan(dir, [step, step with { Source = dir.Combine("b.mkv"), Outputs = [dir.Combine("out", "b.mka")] }]);

        var result = await Executor(runner, dir).ExecuteAsync(plan, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal([ItemOutcome.Warning, ItemOutcome.Failed], result.Items.Select(i => i.Outcome));
        Assert.Equal(["--output-charset", "UTF-8", "tracks"], runner.Specs[0].Arguments.Take(3));
    }

    [Fact]
    public async Task Cancel_deletes_current_output_and_stops()
    {
        using var dir = new TempDir();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var runner = new FakeRunner((spec, n) =>
        {
            if (n == 1)
            {
                File.WriteAllText(spec.Arguments[^1], "half");
                cts.Cancel();
                throw new OperationCanceledException(cts.Token);
            }

            return Write(spec, "out", 0);
        });
        var plan = Plan(dir, [Step(dir, "a"), Step(dir, "b"), Step(dir, "c")]);

        var result = await Executor(runner, dir).ExecuteAsync(plan, cancellationToken: cts.Token);

        Assert.Equal([ItemOutcome.Done, ItemOutcome.Cancelled, ItemOutcome.NotRun], result.Items.Select(i => i.Outcome));
        Assert.True(result.WasCancelled);
        Assert.False(File.Exists(dir.Combine("out", "b.mka")));
        Assert.True(File.Exists(dir.Combine("out", "a.mka")));
    }

    [Fact]
    public async Task Missing_tool_fails_the_step_with_clear_message()
    {
        using var dir = new TempDir();
        var executor = new PlanExecutor(new FakeRunner((s, _) => Write(s, "x", 0)), new ToolPaths(null, null, null, null), new ErrorLogWriter(dir.Combine("logs")));
        var result = await executor.ExecuteAsync(Plan(dir, [Step(dir, "a")]), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("Не найдена программа ffmpeg", Assert.Single(result.Items).Message);
    }

    private static PlanExecutor Executor(IProcessRunner runner, TempDir dir) => new(runner, Tools, new ErrorLogWriter(dir.Combine("logs")));

    private static OperationPlan Plan(TempDir dir, IReadOnlyList<PlanItem> items) => new("тест", dir.Path, items);

    private static PlanItem Step(TempDir dir, string name)
    {
        var output = dir.Combine("out", name + ".mka");
        return new PlanItem
        {
            Source = dir.Combine(name + ".mkv"),
            Label = name,
            Status = PlanItemStatus.Run,
            Outputs = [output],
            Command = new PlannedCommand(Tool.Ffmpeg, ["-nostdin", "-i", dir.Combine(name + ".mkv"), output]),
        };
    }

    /// <summary>«Программа» пишет содержимое в выходной файл (последний аргумент; у mkvextract — «ID:путь») и возвращает код.</summary>
    private static ProcessResult Write(ProcessSpec spec, string content, int exitCode, string stderr = "")
    {
        var last = spec.Arguments[^1];
        var mkvextractSpec = System.Text.RegularExpressions.Regex.Match(last, @"^\d+:(.+)$");
        File.WriteAllText(mkvextractSpec.Success ? mkvextractSpec.Groups[1].Value : last, content);
        return new ProcessResult(exitCode, "", stderr, TimeSpan.Zero);
    }

    private sealed class FakeRunner(Func<ProcessSpec, int, ProcessResult> behaviour) : IProcessRunner
    {
        public List<ProcessSpec> Specs { get; } = [];

        public Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken cancellationToken = default)
        {
            Specs.Add(spec);
            return Task.FromResult(behaviour(spec, Specs.Count - 1));
        }
    }

    /// <summary>Progress&lt;T&gt; без SynchronizationContext — события сразу, по порядку.</summary>
    private sealed class SyncProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }
}
