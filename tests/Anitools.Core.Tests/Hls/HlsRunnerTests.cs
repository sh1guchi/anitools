using System.IO.Compression;
using Anitools.Core.Logging;
using Anitools.Core.Media;
using Anitools.Core.Operations.Hls;
using Anitools.Core.Processes;
using Anitools.Core.Tests.Fixtures;

namespace Anitools.Core.Tests.Hls;

/// <summary>Обработка серий HLS на фейковом ffmpeg: архивы, перенос озвучек, повтор на CPU, очистка, отмена.</summary>
public sealed class HlsRunnerTests
{
    private const string Episode = "Show - 01";
    private static readonly ToolPaths Tools = new("/bin/ffmpeg", "/bin/ffprobe", null, null);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Episode_becomes_zip_of_all_qualities_and_audio_files()
    {
        using var dir = new TempDir();
        var (plan, probe) = await PlanAsync(dir, 1);
        var ffmpeg = new FakeFfmpeg();
        var events = new List<HlsProgress>();

        var result = await Runner(ffmpeg, probe, dir).ExecuteAsync(plan, workRoot: null, new Inline<HlsProgress>(events.Add), Ct);

        var episode = Assert.Single(result.Episodes);
        Assert.Equal(HlsEpisodeOutcome.Done, episode.Outcome);
        var titleOut = dir.Combine("hls_multi", "Show");
        using (var zip = ZipFile.OpenRead(Path.Combine(titleOut, Episode + ".zip")))
        {
            Assert.Equal(
                HlsSettings.DefaultLadder.SelectMany(r => new[] { $"{r.Name}/master.m3u8", $"{r.Name}/seg000.ts", $"{r.Name}/seg001.ts" }).Order(StringComparer.Ordinal),
                zip.Entries.Select(e => e.FullName).Order(StringComparer.Ordinal));
            Assert.All(zip.Entries, e => Assert.Equal(e.Length, e.CompressedLength)); // без сжатия
        }

        Assert.Equal("mka", await File.ReadAllTextAsync(Path.Combine(titleOut, "audio", "AniLibria.TV", $"{Episode}.AniLibria.TV.mka"), Ct));
        Assert.True(File.Exists(Path.Combine(titleOut, "audio", "DEEP", $"{Episode}.DEEP.mka")));
        Assert.Equal([Episode + ".zip", "audio"], Directory.EnumerateFileSystemEntries(titleOut).Select(Path.GetFileName).Order(StringComparer.Ordinal));

        // Видео, потом аудио; к командам добавлен только машинный прогресс; DEEP (5.1) сводится в стерео
        var calls = ffmpeg.FfmpegArguments.ToList();
        Assert.Equal(2, calls.Count);
        Assert.Equal(["-progress", "pipe:1", "-nostats", "-nostdin", "-y", "-hwaccel", "cuda"], calls[0].Take(7));
        Assert.Contains("[v5out]", calls[0]);
        Assert.Equal(["-map", "0:a:2", "-c:a", "aac", "-b:a", "192k", "-ac", "2"], calls[1].Skip(calls[1].ToList().IndexOf("0:a:2") - 1).Take(8));
        Assert.Contains(events, e => e.Stage == HlsStage.Video && e.Fraction is > 0);
        Assert.Contains(events, e => e.Stage == HlsStage.Packing);
        Assert.Equal(6, episode.Bitrates.Count);
    }

    [Fact]
    public async Task Temporary_files_go_to_work_root_and_are_removed()
    {
        using var dir = new TempDir();
        var (plan, probe) = await PlanAsync(dir, 1);
        dir.File($"hls_multi/Show/{Episode}/360p/seg000.ts", "остаток прошлого запуска");
        var work = dir.Combine("work");
        var ffmpeg = new FakeFfmpeg();

        var result = await Runner(ffmpeg, probe, dir).ExecuteAsync(plan, work, cancellationToken: Ct);

        Assert.Equal(HlsEpisodeOutcome.Done, Assert.Single(result.Episodes).Outcome);
        Assert.Contains(Path.Combine(work, "Show", Episode, "360p", "master.m3u8"), ffmpeg.FfmpegArguments.First());
        Assert.Empty(Directory.EnumerateFileSystemEntries(work)); // и пустая папка тайтла убрана
        Assert.False(Directory.Exists(dir.Combine("hls_multi", "Show", Episode)));
        Assert.True(File.Exists(dir.Combine("hls_multi", "Show", Episode + ".zip")));
    }

    [Fact]
    public async Task Gpu_failure_is_retried_with_cpu_decoding()
    {
        using var dir = new TempDir();
        var (plan, probe) = await PlanAsync(dir, 1);
        var ffmpeg = new FakeFfmpeg { ExitCodes = { [0] = 1 } };

        var result = await Runner(ffmpeg, probe, dir).ExecuteAsync(plan, null, cancellationToken: Ct);

        var episode = Assert.Single(result.Episodes);
        Assert.Equal(HlsEpisodeOutcome.Done, episode.Outcome);
        var calls = ffmpeg.FfmpegArguments.ToList();
        Assert.Equal(3, calls.Count);
        Assert.Contains("-hwaccel", calls[0]);
        Assert.Contains("-init_hw_device", calls[1]);
        Assert.Contains(episode.Notes, n => n.Contains("CPU", StringComparison.Ordinal));
        using var zip = ZipFile.OpenRead(dir.Combine("hls_multi", "Show", Episode + ".zip"));
        Assert.Equal(18, zip.Entries.Count); // недописанный первый прогон не попал в архив
    }

    [Fact]
    public async Task Video_failure_logs_cleans_up_and_goes_on()
    {
        using var dir = new TempDir();
        var (plan, probe) = await PlanAsync(dir, 2);
        var ffmpeg = new FakeFfmpeg { ExitCodes = { [0] = 1, [1] = 1 } };

        var result = await Runner(ffmpeg, probe, dir).ExecuteAsync(plan, null, cancellationToken: Ct);

        Assert.Equal([HlsEpisodeOutcome.Failed, HlsEpisodeOutcome.Done], result.Episodes.Select(e => e.Outcome));
        var failed = result.Episodes[0];
        Assert.Equal("Ошибка видео", failed.Message);
        Assert.StartsWith("Show_-_01_ffmpeg_error_", Path.GetFileName(failed.LogPath));
        Assert.Contains("Error while decoding", await File.ReadAllTextAsync(failed.LogPath!, Ct));
        Assert.False(Directory.Exists(dir.Combine("hls_multi", "Show", Episode)));
        Assert.False(File.Exists(dir.Combine("hls_multi", "Show", Episode + ".zip")));
        Assert.True(File.Exists(dir.Combine("hls_multi", "Show", "Show - 02.zip")));
    }

    [Fact]
    public async Task Audio_failure_cleans_up()
    {
        using var dir = new TempDir();
        var (plan, probe) = await PlanAsync(dir, 1);
        var ffmpeg = new FakeFfmpeg { ExitCodes = { [1] = 1 } };

        var result = await Runner(ffmpeg, probe, dir).ExecuteAsync(plan, dir.Combine("work"), cancellationToken: Ct);

        Assert.Equal("Ошибка аудио", Assert.Single(result.Episodes).Message);
        Assert.Empty(Directory.EnumerateFileSystemEntries(dir.Combine("work")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(dir.Combine("hls_multi", "Show")));
    }

    [Fact]
    public async Task Program_that_cannot_start_fails_the_episode_and_cleans_up()
    {
        using var dir = new TempDir();
        var (plan, probe) = await PlanAsync(dir, 2);
        var ffmpeg = new FakeFfmpeg { BeforeFfmpeg = (n, _) => { if (n == 0) { throw new System.ComponentModel.Win32Exception(2, "Не удаётся найти указанный файл"); } } };

        var result = await Runner(ffmpeg, probe, dir).ExecuteAsync(plan, dir.Combine("work"), cancellationToken: Ct);

        Assert.Equal([HlsEpisodeOutcome.Failed, HlsEpisodeOutcome.Done], result.Episodes.Select(e => e.Outcome));
        Assert.Contains("найти", result.Episodes[0].Message);
        Assert.Empty(Directory.EnumerateFileSystemEntries(dir.Combine("work")));
    }

    [Fact]
    public async Task Heavy_top_quality_goes_to_separate_zip()
    {
        using var dir = new TempDir();
        var (plan, probe) = await PlanAsync(dir, 1);
        dir.File($"hls_multi/Show/{Episode}.4K.zip", "старый"); // не помешает
        var ffmpeg = new FakeFfmpeg { SegmentBytes = { ["4K"] = 600 } };
        var settings = HlsSettings.Default with { SeparateTopZipBytes = 1000 };

        var result = await Runner(ffmpeg, probe, dir, settings).ExecuteAsync(plan, null, cancellationToken: Ct);

        Assert.Contains(Assert.Single(result.Episodes).Notes, n => n.Contains("отдельный архив", StringComparison.Ordinal));
        using var main = ZipFile.OpenRead(dir.Combine("hls_multi", "Show", Episode + ".zip"));
        using var top = ZipFile.OpenRead(dir.Combine("hls_multi", "Show", Episode + ".4K.zip"));
        Assert.DoesNotContain(main.Entries, e => e.FullName.StartsWith("4K/", StringComparison.Ordinal));
        Assert.Equal(15, main.Entries.Count);
        Assert.Equal(["4K/master.m3u8", "4K/seg000.ts", "4K/seg001.ts"], top.Entries.Select(e => e.FullName));
    }

    [Fact]
    public async Task Light_top_quality_stays_in_main_zip_and_stale_top_zip_is_removed()
    {
        using var dir = new TempDir();
        var (plan, probe) = await PlanAsync(dir, 1);
        dir.File($"hls_multi/Show/{Episode}.4K.zip", "от прошлого запуска");
        var settings = HlsSettings.Default with { SeparateTopZipBytes = 208 }; // 4K: сегменты 2 × 100 + плейлист 8 — ровно порог, не больше

        await Runner(new FakeFfmpeg(), probe, dir, settings).ExecuteAsync(plan, null, cancellationToken: Ct);

        Assert.False(File.Exists(dir.Combine("hls_multi", "Show", Episode + ".4K.zip")));
        using var main = ZipFile.OpenRead(dir.Combine("hls_multi", "Show", Episode + ".zip"));
        Assert.Equal(18, main.Entries.Count);
    }

    [Fact]
    public async Task Cancel_removes_current_episode_and_stops()
    {
        using var dir = new TempDir();
        var (plan, probe) = await PlanAsync(dir, 2);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var ffmpeg = new FakeFfmpeg { BeforeFfmpeg = (n, _) => { if (n == 1) { cts.Cancel(); } } };

        var result = await Runner(ffmpeg, probe, dir).ExecuteAsync(plan, dir.Combine("work"), cancellationToken: cts.Token);

        Assert.Equal([HlsEpisodeOutcome.Cancelled, HlsEpisodeOutcome.NotRun], result.Episodes.Select(e => e.Outcome));
        Assert.True(result.WasCancelled);
        Assert.Empty(Directory.EnumerateFileSystemEntries(dir.Combine("work")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(dir.Combine("hls_multi", "Show")));
    }

    [Fact]
    public async Task Episode_done_meanwhile_is_skipped_at_run_time()
    {
        using var dir = new TempDir();
        var (plan, probe) = await PlanAsync(dir, 1);
        dir.File($"hls_multi/Show/{Episode}.zip", "zip");
        dir.File($"hls_multi/Show/audio/AniLibria.TV/{Episode}.AniLibria.TV.mka", "mka");
        dir.File($"hls_multi/Show/audio/DEEP/{Episode}.DEEP.mka", "mka");
        var ffmpeg = new FakeFfmpeg();

        var result = await Runner(ffmpeg, probe, dir).ExecuteAsync(plan, null, cancellationToken: Ct);

        Assert.Equal(HlsEpisodeOutcome.Skipped, Assert.Single(result.Episodes).Outcome);
        Assert.Equal(0, ffmpeg.FfmpegCalls);
    }

    [Fact]
    public async Task Wide_h264_is_decoded_on_cpu_from_the_start()
    {
        using var dir = new TempDir();
        var fixture = MediaProbe.ParseFfprobe(MediaFixtures.Ffprobe("Test Show - 01.mkv"));
        var wide = fixture with { Streams = [.. fixture.Streams.Select(s => s.CodecType == "video" ? s with { Width = 5120, Height = 2880 } : s)] };
        var (plan, _) = await PlanAsync(dir, 1);
        var probe = new NameFixtureProbe(new Dictionary<string, string>(), new Dictionary<string, MediaInfo> { [Episode + ".mkv"] = wide });
        var ffmpeg = new FakeFfmpeg();

        var result = await Runner(ffmpeg, probe, dir).ExecuteAsync(plan, null, cancellationToken: Ct);

        var episode = Assert.Single(result.Episodes);
        Assert.Contains("-init_hw_device", ffmpeg.FfmpegArguments.First());
        Assert.Contains(episode.Notes, n => n.StartsWith("Источник 5120px", StringComparison.Ordinal));
        Assert.Contains(episode.Notes, n => n.StartsWith("H264 5120×2880", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Calibrated_quality_is_used_for_the_video()
    {
        using var dir = new TempDir();
        var (plan, probe) = await PlanAsync(dir, 1);
        // Серия 60 с, «видеокарта» даёт вдвое больше бит, чем в лестнице, при CQ 26 → CQ растёт
        var settings = HlsSettings.Default with { FixedCq = null, Ladder = [new("360p", 640, 360, 800_000), new("720p", 1280, 720, 3_000_000)] };
        var ffmpeg = new FakeFfmpeg
        {
            PacketLines = [.. Enumerable.Range(0, 60 * 24).Select(i => FormattableString.Invariant($"{i / 24.0:F6},{5000 + (i % 7 * 1000)}"))],
            WindowBytes = (rung, cq) => (long)((rung == "360p" ? 800_000 : 3_000_000) * 2 * Math.Pow(2, (26 - cq) / 6) * 6 / 8),
        };

        var result = await Runner(ffmpeg, probe, dir, settings).ExecuteAsync(plan, null, cancellationToken: Ct);

        var episode = Assert.Single(result.Episodes);
        Assert.Equal(HlsEpisodeOutcome.Done, episode.Outcome);
        Assert.All(episode.RateControl!, rc => Assert.InRange(rc.Cq, 31.5, 32.5)); // +6 к CQ ≈ битрейт / 2
        var video = ffmpeg.FfmpegArguments.Single(a => a.Contains("hls"));
        Assert.Equal(episode.RateControl!.Select(r => r.Cq.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)), video.Select((a, i) => (a, i)).Where(p => p.a == "-cq").Select(p => video[p.i + 1]));
        Assert.False(Directory.Exists(dir.Combine("hls_multi", "Show", Episode, "_calibration")));
        Assert.Contains(episode.Notes, n => n.StartsWith("CQ под серию", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Job_logs_each_episode_shows_results_and_shuts_down_only_after_success()
    {
        using var dir = new TempDir();
        var (plan, probe) = await PlanAsync(dir, 2);
        var ffmpeg = new FakeFfmpeg();
        ffmpeg.ExitCodes[2] = 1; // видео второй серии…
        ffmpeg.ExitCodes[3] = 1; // …и повтор на CPU
        var shutdowns = 0;
        var queue = new Anitools.Core.Jobs.JobQueue();

        var job = queue.Enqueue("HLS · Show", dir.Path, Anitools.Core.Jobs.HlsJobs.Run(
            plan, Runner(ffmpeg, probe, dir), new Anitools.Core.WorkDir.NearOutputWorkDir(), calibrates: false, _ =>
            {
                shutdowns++;
                return Task.FromResult(true);
            }));
        await job.Completion.WaitAsync(Ct);

        Assert.Equal(Anitools.Core.Jobs.JobState.Failed, job.State);
        Assert.Equal("1 готово · 1 ошибка", job.Snapshot.Summary);
        Assert.Matches(@"^01 ✓ \d+:\d\d · 02 ✗ \d+:\d\d$", job.Snapshot.Detail);
        Assert.Contains(job.Log, l => l.Contains("Временные файлы: рядом с выходом", StringComparison.Ordinal));
        Assert.Contains(job.Log, l => l.Contains("✓ Show - 01.mkv", StringComparison.Ordinal));
        Assert.Contains(job.Log, l => l.Contains("✗ Show - 02.mkv — Ошибка видео", StringComparison.Ordinal));
        Assert.Contains(job.Log, l => l.Contains("Мбит/с", StringComparison.Ordinal));
        Assert.Equal(0, shutdowns);

        // Всё готово — повторный запуск пропускает первую серию, вторая проходит, компьютер выключается
        ffmpeg.ExitCodes.Clear();
        var (again, _) = await PlanAsync(dir, 2);
        var second = queue.Enqueue("HLS · Show", dir.Path, Anitools.Core.Jobs.HlsJobs.Run(
            again, Runner(ffmpeg, probe, dir), new Anitools.Core.WorkDir.NearOutputWorkDir(), calibrates: false, _ =>
            {
                shutdowns++;
                return Task.FromResult(true);
            }));
        await second.Completion.WaitAsync(Ct);

        Assert.Equal(Anitools.Core.Jobs.JobState.Done, second.State);
        Assert.Equal("1 готово · 1 пропуск", second.Snapshot.Summary);
        Assert.Contains(second.Log, l => l.Contains("· Show - 01.mkv — пропуск: уже готово", StringComparison.Ordinal));
        Assert.Equal(1, shutdowns);
    }

    [Theory]
    [InlineData(HlsStage.Video, 0.5, false, 0.455)]
    [InlineData(HlsStage.Video, 0.5, true, 0.525)]
    [InlineData(HlsStage.Calibration, 1.0, true, 0.15)]
    [InlineData(HlsStage.Moving, 1.0, false, 1.0)]
    public void Episode_fraction_by_stage(HlsStage stage, double fraction, bool calibrates, double expected) =>
        Assert.Equal(expected, Anitools.Core.Jobs.HlsJobs.EpisodeFraction(stage, fraction, calibrates), 3);

    private static HlsRunner Runner(FakeFfmpeg ffmpeg, IMediaProbe probe, TempDir dir, HlsSettings? settings = null) =>
        new(ffmpeg, Tools, probe, new ErrorLogWriter(dir.Combine("_logs")), settings ?? HlsSettings.Default);

    /// <summary>Папка с сериями «Show - 0N.mkv» (три дорожки: AniLibria.TV, Оригинальная, DEEP 5.1); берутся 1-я и 3-я.</summary>
    private static async Task<(HlsPlan Plan, IMediaProbe Probe)> PlanAsync(TempDir dir, int episodes)
    {
        var media = new Dictionary<string, string>();
        for (var i = 1; i <= episodes; i++)
        {
            dir.File($"Show - 0{i}.mkv");
            media[$"Show - 0{i}.mkv"] = "Test Show - 01.mkv";
        }

        var probe = new NameFixtureProbe(media);
        var inspection = await HlsOperation.InspectAsync(dir.Path, probe, Ct);
        var group = Assert.Single(inspection.Groups);
        return (HlsOperation.Plan(inspection, [HlsPlanTests.Options(group, "Show", [new(0, "AniLibria.TV"), new(2, "DEEP")])]), probe);
    }

    private sealed class Inline<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }
}
