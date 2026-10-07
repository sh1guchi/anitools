using System.ComponentModel;
using System.Diagnostics;
using Anitools.Core.Processes;

namespace Anitools.Core.Tests.Processes;

/// <summary>Запуск процессов на системной оболочке (sh / cmd) — без ffmpeg.</summary>
public sealed class ProcessRunnerTests
{
    private static ProcessSpec Shell(string unix, string windows) =>
        OperatingSystem.IsWindows()
            ? new ProcessSpec("cmd.exe", ["/d", "/c", windows])
            : new ProcessSpec("/bin/sh", ["-c", unix]);

    [Fact]
    public async Task Collects_stdout_stderr_and_exit_code()
    {
        var result = await new ProcessRunner().RunAsync(
            Shell("echo out; echo err 1>&2; exit 3", "(echo out)&(echo err)1>&2&exit /b 3"),
            TestContext.Current.CancellationToken);

        Assert.Equal(3, result.ExitCode);
        Assert.False(result.Succeeded);
        Assert.Equal("out", result.StandardOutput.Trim());
        Assert.Equal("err", result.StandardErrorTail.Trim());
    }

    [Fact]
    public async Task Keeps_only_the_tail_of_stderr()
    {
        var spec = Shell(
            "i=1; while [ $i -le 20 ]; do echo line$i 1>&2; i=$((i+1)); done",
            "for /l %i in (1,1,20) do @(echo line%i)1>&2") with { StderrTailLines = 5 };
        var result = await new ProcessRunner().RunAsync(spec, TestContext.Current.CancellationToken);

        Assert.Equal(["line16", "line17", "line18", "line19", "line20"], result.StandardErrorTail.Split('\n').Select(l => l.Trim()));
    }

    [Fact]
    public async Task Stdout_lines_go_to_callback_instead_of_result()
    {
        var lines = new List<string>();
        var spec = Shell("echo a; echo b", "(echo a)&(echo b)") with { OnStdoutLine = lines.Add };
        var result = await new ProcessRunner().RunAsync(spec, TestContext.Current.CancellationToken);

        Assert.Equal(["a", "b"], lines.Select(l => l.Trim()));
        Assert.Equal("", result.StandardOutput);
    }

    [Fact]
    public async Task Cancel_kills_the_process_quickly()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromMilliseconds(300));
        var stopwatch = Stopwatch.StartNew();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ProcessRunner().RunAsync(
            Shell("sleep 30", "ping -n 30 127.0.0.1 >NUL"), cts.Token));
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10), $"Процесс не убит вовремя: {stopwatch.Elapsed}");
    }

    [Fact]
    public async Task Cancel_kills_child_processes_too()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Проверка через /proc — только на Linux");
        var pids = new List<string>();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var spec = new ProcessSpec("/bin/sh", ["-c", "sleep 30 & echo $!; wait"]) { OnStdoutLine = line => { pids.Add(line); cts.Cancel(); } };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ProcessRunner().RunAsync(spec, cts.Token));

        var child = int.Parse(Assert.Single(pids), System.Globalization.CultureInfo.InvariantCulture);
        await Task.Delay(200, TestContext.Current.CancellationToken);
        Assert.False(IsAlive(child), "Дочерний процесс пережил отмену");
    }

    [Fact]
    public async Task Missing_program_throws_win32_exception() =>
        await Assert.ThrowsAsync<Win32Exception>(() => new ProcessRunner().RunAsync(
            new ProcessSpec("anitools-no-such-program-42", []), TestContext.Current.CancellationToken));

    /// <summary>Жив ли процесс: зомби (убит, но ещё не убран родителем — в контейнере его некому убрать) — не жив.</summary>
    private static bool IsAlive(int pid)
    {
        var stat = $"/proc/{pid}/stat";
        if (!File.Exists(stat))
        {
            return false;
        }

        var text = File.ReadAllText(stat);
        var state = text[(text.LastIndexOf(')') + 2)..].Split(' ')[0];
        return state is not ("Z" or "X");
    }

    [Fact]
    public async Task Child_gets_utf8_locale_on_unix()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "На Windows локаль не нужна");
        var result = await new ProcessRunner().RunAsync(
            new ProcessSpec("/bin/sh", ["-c", "echo ${LC_ALL:-${LC_CTYPE:-$LANG}}"]), TestContext.Current.CancellationToken);
        Assert.Matches("(?i)utf-?8", result.StandardOutput);
    }

    [Fact]
    public void Closing_job_kills_its_processes_and_their_children()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Job Object — только на Windows");
        var job = Anitools.Core.Platform.ChildProcessJob.TryCreate();
        Assert.NotNull(job);
        // cmd запускает ping дочерним процессом — он тоже в задании
        using var process = Process.Start(new ProcessStartInfo("cmd.exe", "/d /c ping -n 60 127.0.0.1 > nul")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;

        Assert.True(job.Assign(process));
        Assert.False(process.WaitForExit(500));
        job.Dispose();

        Assert.True(process.WaitForExit(10_000), "После закрытия задания процесс должен завершиться");
    }
}
