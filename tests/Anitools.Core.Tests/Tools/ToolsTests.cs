using System.Text;
using Anitools.Core.Logging;
using Anitools.Core.Operations.AssEdit;
using Anitools.Core.Operations.AudioTools;
using Anitools.Core.Operations.Common;
using Anitools.Core.Operations.Hardsub;
using Anitools.Core.Operations.Remux;
using Anitools.Core.Operations.SubShift;
using Anitools.Core.Operations.TrackList;
using Anitools.Core.Processes;
using Anitools.Core.Tests.Fixtures;

namespace Anitools.Core.Tests.Tools;

/// <summary>Доп. инструменты §2.9 и общий исполнитель: параллельность, «.part», бэкап, переводы строк.</summary>
public sealed class ToolsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Subtitle_shift_keeps_line_endings_and_drops_bom()
    {
        using var dir = new TempDir();
        File.WriteAllBytes(dir.Combine("a.srt"), [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("1\r\n00:00:01,000 --> 00:00:02,000\r\nПривет\r\n")]);
        File.WriteAllBytes(dir.Combine("b.ASS"), [.. Encoding.UTF8.GetBytes("Dialogue: 0,0:00:01.00,0:00:02.00,Default,,0,0,0,,Текст "), 0xFF, (byte)'\n']);
        dir.File("c.txt", "00:00:01,000 --> 00:00:02,000");

        var results = SubtitleShift.Execute(dir.Path, -0.5);

        Assert.All(results, r => Assert.Null(r.Error));
        Assert.Equal("1\r\n00:00:00,500 --> 00:00:01,500\r\nПривет\r\n", File.ReadAllText(dir.Combine("subs_fixed", "a.srt")));
        Assert.Equal("Dialogue: 0,0:00:00.50,0:00:01.50,Default,,0,0,0,,Текст �\n", File.ReadAllText(dir.Combine("subs_fixed", "b.ASS")));
        Assert.NotEqual(0xEF, File.ReadAllBytes(dir.Combine("subs_fixed", "a.srt"))[0]); // UTF-8 без BOM
        Assert.False(File.Exists(dir.Combine("subs_fixed", "c.txt")));
    }

    [Fact]
    public void Ass_cleanup_backs_up_originals_and_keeps_line_endings()
    {
        using var dir = new TempDir();
        const string original = "[Events]\r\nDialogue: 0,0:00:01.00,0:00:02.00,Signs,,0,0,0,,Надпись\r\nDialogue: 0,0:00:01.00,0:00:02.00,Default,,0,0,0,,Текст\r\n";
        dir.File("Ep 01.ass", original);
        File.WriteAllBytes(dir.Combine("bad.ass"), [0xFF, 0xFE, 0x00]); // не UTF-8 — не трогаем

        var inspection = AssEditOperation.Inspect(dir.Path, AssField.Style);
        Assert.Equal(["Default", "Signs"], inspection.Values.Select(v => v.Value));
        Assert.Equal("файл не в UTF-8", Assert.Single(inspection.Unreadable).Error);

        var remove = AssEditOperation.ValuesToRemove(inspection, ["Default"], keepSelected: true);
        var result = AssEditOperation.Execute(inspection, remove, new DateTime(2026, 10, 7, 21, 30, 0));

        Assert.Equal(1, result.TotalRemoved);
        Assert.EndsWith("ass_backup_2026-10-07_21-30-00", result.BackupFolder);
        Assert.Equal(original, File.ReadAllText(Path.Combine(result.BackupFolder, "Ep 01.ass")));
        var edited = File.ReadAllBytes(dir.Combine("Ep 01.ass"));
        Assert.Equal([0xEF, 0xBB, 0xBF], edited.Take(3)); // UTF-8 с BOM
        Assert.Equal("[Events]\r\nDialogue: 0,0:00:01.00,0:00:02.00,Default,,0,0,0,,Текст\r\n", Encoding.UTF8.GetString(edited[3..]));
        Assert.Equal([0xFF, 0xFE, 0x00], File.ReadAllBytes(dir.Combine("bad.ass")));
        Assert.Equal(AssEditOperation.ValuesToRemove(inspection, ["Signs"], keepSelected: false), remove);
    }

    [Fact]
    public void Hardsub_plan_uses_relative_paths_part_file_and_fonts_dir()
    {
        using var dir = new TempDir();
        dir.File("Show - 01.mkv");
        dir.File("Show - 01.ass");
        dir.File("Show - 02.mp4"); // без .ass — не берётся
        dir.File("Hardsub/Show - 01.mkv", "старый");
        Directory.CreateDirectory(dir.Combine("Fonts"));

        var item = Assert.Single(HardsubOperation.Plan(dir.Path, new HardsubOptions { EncodeArgs = ["-c:v", "libx264"] }).Items);

        Assert.Equal(dir.Path, item.Command!.WorkingDirectory);
        Assert.Equal(
            ["-hide_banner", "-y", "-v", "error", "-i", "Show - 01.mkv", "-map", "0:v:0", "-map", "0:a?", "-vf", "subtitles=Show - 01.ass:fontsdir=Fonts",
                "-c:v", "libx264", "-c:a", "copy", Path.Combine("Hardsub", "Show - 01.part.mkv")],
            item.Command.Arguments);
        Assert.Equal((dir.Combine("Hardsub", "Show - 01.part.mkv"), dir.Combine("Hardsub", "Show - 01.mkv")), item.RenameOnSuccess);
        Assert.Contains("перезаписан", item.Reason);
    }

    [Fact]
    public async Task Part_file_is_renamed_only_after_success()
    {
        using var dir = new TempDir();
        var part = dir.Combine("out", "x.part.mkv");
        var final = dir.File("out/x.mkv", "старый");
        string? workingDirectory = null;
        var runner = new LambdaRunner((spec, n) =>
        {
            workingDirectory = spec.WorkingDirectory;
            File.WriteAllText(part, n == 0 ? "новый" : "недописан");
            return Task.FromResult(n == 0 ? 0 : 1);
        });
        PlanItem Item() => new()
        {
            Source = dir.Combine("x.mkv"),
            Label = "x",
            Status = PlanItemStatus.Run,
            Outputs = [part],
            RenameOnSuccess = (part, final),
            Command = new PlannedCommand(Tool.Ffmpeg, ["-i", "x.mkv", part]) { WorkingDirectory = dir.Path },
        };
        var executor = new PlanExecutor(runner, new ToolPaths("ffmpeg", null, null, null), new ErrorLogWriter(dir.Combine("logs")));

        var ok = await executor.ExecuteAsync(new OperationPlan("т", dir.Path, [Item()]), cancellationToken: Ct);
        Assert.Equal(ItemOutcome.Done, Assert.Single(ok.Items).Outcome);
        Assert.Equal("новый", File.ReadAllText(final));
        Assert.False(File.Exists(part));
        Assert.Equal(dir.Path, workingDirectory);

        var failed = await executor.ExecuteAsync(new OperationPlan("т", dir.Path, [Item()]), cancellationToken: Ct);
        Assert.Equal(ItemOutcome.Failed, Assert.Single(failed.Items).Outcome);
        Assert.Equal("новый", File.ReadAllText(final)); // готовый не тронут
        Assert.False(File.Exists(part));
    }

    [Fact]
    public async Task Steps_run_in_parallel_up_to_the_limit_and_results_keep_plan_order()
    {
        using var dir = new TempDir();
        var running = 0;
        var peak = 0;
        var runner = new LambdaRunner(async (spec, _) =>
        {
            var now = Interlocked.Increment(ref running);
            InterlockedMax(ref peak, now);
            await Task.Delay(100, Ct);
            File.WriteAllText(spec.Arguments[^1], "x");
            Interlocked.Decrement(ref running);
            return spec.Arguments[^1].EndsWith("3.mka", StringComparison.Ordinal) ? 1 : 0;
        });
        var items = Enumerable.Range(0, 8).Select(i => new PlanItem
        {
            Source = dir.Combine($"{i}.wav"),
            Label = i.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Status = PlanItemStatus.Run,
            Outputs = [dir.Combine($"{i}.mka")],
            Command = new PlannedCommand(Tool.Mkvmerge, ["-o", dir.Combine($"{i}.mka")]),
        }).ToList();
        var executor = new PlanExecutor(runner, new ToolPaths(null, null, "mkvmerge", null), new ErrorLogWriter(dir.Combine("logs")));

        var result = await executor.ExecuteAsync(new OperationPlan("т", dir.Path, items), cancellationToken: Ct, maxParallel: 3);

        Assert.Equal(3, peak);
        Assert.Equal(items, result.Items.Select(r => r.Item));
        Assert.Equal(ItemOutcome.Failed, result.Items[3].Outcome);
        Assert.Equal(7, result.Count(ItemOutcome.Done));
    }

    [Fact]
    public void Audio_shift_plans_delay_or_cut()
    {
        using var dir = new TempDir();
        dir.File("Voice.flac");
        dir.File("audio_fixed/Done.mka", "готово");
        dir.File("Done.mp3");

        var plus = AudioShiftOperation.Plan(dir.Path, new AudioShiftOptions { Seconds = 1.5, Reencode = true });
        var minus = AudioShiftOperation.Plan(dir.Path, new AudioShiftOptions { Seconds = -2, Reencode = true });
        var lossless = AudioShiftOperation.Plan(dir.Path, new AudioShiftOptions { Seconds = -2 });

        Assert.Equal([PlanItemStatus.Skip, PlanItemStatus.Run], plus.Items.Select(i => i.Status));
        Assert.Contains("adelay=delays=1500:all=1", plus.Items[1].Command!.Arguments);
        Assert.Equal(["-ss", "2.0"], minus.Items[1].Command!.Arguments.SkipWhile(a => a != "-ss").Take(2));
        // по умолчанию — без перекодирования: mkvmerge сдвигает метки времени всех дорожек
        var copy = lossless.Items[1].Command!;
        Assert.Equal(Tool.Mkvmerge, copy.Tool);
        Assert.Equal(["-o", dir.Combine("audio_fixed", "Voice.mka"), "--no-video", "--sync", "-1:-2000", dir.Combine("Voice.flac")], copy.Arguments);
        Assert.Equal([1], copy.WarningExitCodes);
        Assert.Throws<PlanException>(() => AudioShiftOperation.Plan(dir.Path, new AudioShiftOptions { Seconds = 0 }));
    }

    [Fact]
    public void Remux_preset_puts_mkv_next_to_source_and_skips_done()
    {
        using var dir = new TempDir();
        dir.File("a.AVI");
        dir.File("b.m2ts");
        dir.File("c.M2TS");
        dir.File("c.mkv", "готово");

        var m2ts = RemuxPresets.M2tsToMkv(dir.Path).Items;
        Assert.Equal([PlanItemStatus.Run, PlanItemStatus.Skip], m2ts.Select(i => i.Status));
        Assert.Contains("flac", m2ts[0].Command!.Arguments);
    }

    [Fact]
    public void Remux_fixes_avi_timestamps_like_the_bat()
    {
        using var dir = new TempDir();
        dir.File("a.AVI");
        dir.File("b.mkv");

        var items = RemuxOperation.Plan(dir.Path, new RemuxOptions(RemuxFormat.Mkv)).Items;

        Assert.Equal(
            ["-nostdin", "-fflags", "+genpts", "-i", dir.Combine("a.AVI"), "-map", "0:v:0", "-map", "0:a?", "-map", "0:s?",
                "-c", "copy", "-avoid_negative_ts", "make_zero", "-y", dir.Combine("converted_mp4", "a.mkv")],
            items[0].Command!.Arguments);
        Assert.DoesNotContain("+genpts", items[1].Command!.Arguments);
        Assert.DoesNotContain("-avoid_negative_ts", items[1].Command!.Arguments);
    }

    [Fact]
    public void Comma_list_puts_dubs_first_then_english_then_original()
    {
        TrackRow Row(int n, string? title, string? language) => new(n, title, language, "aac", "2.0", n == 1);
        IReadOnlyList<TrackRow> rows =
        [
            Row(1, "Оригинальная", "jpn"),
            Row(2, "AniLibria.TV", "rus"),
            Row(3, "Crunchyroll", "eng"),
            Row(4, "DEEP", "rus"),
            Row(5, null, null),
            Row(6, "Japanese", "und"),
            Row(7, "ENG", null),
        ];

        Assert.Equal("AniLibria.TV, DEEP, Дорожка 5, English, Original", TrackListOperation.CopyList(rows, CopyListStyle.Comma));
        Assert.Equal(VoiceGroup.Original, TrackListOperation.Group(rows[5]));
        Assert.Equal(VoiceGroup.English, TrackListOperation.Group(rows[6]));
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while ((current = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, current) != current)
        {
        }
    }

    private sealed class LambdaRunner(Func<ProcessSpec, int, Task<int>> run) : IProcessRunner
    {
        private int _calls;

        public async Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken cancellationToken = default)
        {
            var n = Interlocked.Increment(ref _calls) - 1;
            return new ProcessResult(await run(spec, n), "", "", TimeSpan.Zero);
        }
    }
}
