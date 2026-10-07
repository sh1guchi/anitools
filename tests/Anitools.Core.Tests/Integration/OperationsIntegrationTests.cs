using Anitools.Core.Logging;
using Anitools.Core.Media;
using Anitools.Core.Operations.AudioExtract;
using Anitools.Core.Operations.AudioMux;
using Anitools.Core.Operations.Common;
using Anitools.Core.Operations.Remux;
using Anitools.Core.Operations.Subtitles;
using Anitools.Core.Operations.VideoOnly;
using Anitools.Core.Processes;
using Anitools.Core.Tests.Fixtures;

namespace Anitools.Core.Tests.Integration;

/// <summary>Операции 1–4, 6 целиком на настоящих ffmpeg / MKVToolNix; выход проверяется через ffprobe.</summary>
[Trait("Category", "Integration")]
public sealed class OperationsIntegrationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Video_only_keeps_just_the_video()
    {
        var (tools, probe) = Tools();
        using var dir = new TempDir();
        await MediaFactory.EpisodeAsync(dir.Combine("Test_Show_-_01.mkv"));

        var result = await Execute(tools, probe, dir, VideoOnlyOperation.Plan(dir.Path));

        Assert.Equal(ItemOutcome.Done, Assert.Single(result.Items).Outcome);
        var info = await probe.ProbeAsync(dir.Combine("Video only", "Test Show - 01.mkv"), Ct);
        Assert.Equal(["video"], info.Streams.Select(s => s.CodecType));
    }

    [Fact]
    public async Task Audio_extract_separate_and_single_mka()
    {
        var (tools, probe) = Tools();
        using var dir = new TempDir();
        await MediaFactory.EpisodeAsync(dir.Combine("Show - 01.mkv"));
        var source = await AudioExtractOperation.InspectAsync(dir.Path, probe, Ct);
        Assert.Equal(["AniLibria.TV", "Оригинальная", "DEEP"], source.Tracks.Select(t => t.Title));

        var separate = await Execute(tools, probe, dir, AudioExtractOperation.Plan(source, new AudioExtractOptions { TrackIds = [0, 2] }));
        Assert.All(separate.Items, i => Assert.Equal(ItemOutcome.Done, i.Outcome));
        var deep = await probe.ProbeAsync(dir.Combine("Audio only", "3. DEEP", "3. Show - 01.DEEP.mka"), Ct);
        Assert.Equal("ac3", Assert.Single(deep.AudioStreams).CodecName);

        var single = await Execute(tools, probe, dir, AudioExtractOperation.Plan(source, new AudioExtractOptions
        {
            TrackIds = [1, 0],
            Mode = AudioExtractMode.SingleMka,
        }));
        Assert.Equal(ItemOutcome.Done, Assert.Single(single.Items).Outcome);
        var mka = await probe.ProbeAsync(dir.Combine("Audio only", "Show - 01.mka"), Ct);
        Assert.Equal(["Оригинальная", "AniLibria.TV"], mka.AudioStreams.Select(a => a.Title));
        Assert.Equal(["jpn", "rus"], mka.AudioStreams.Select(a => a.Language));
        Assert.Equal([true, false], mka.AudioStreams.Select(a => a.IsDefault));
        Assert.Empty(mka.VideoStreams);
    }

    [Fact]
    public async Task Audio_mux_with_external_track_first()
    {
        var (tools, probe) = Tools();
        using var dir = new TempDir();
        await MediaFactory.EpisodeAsync(dir.Combine("Show - 01.mkv"));
        await MediaFactory.CreateAsync(dir.Combine("Audio only", "2. Studio Band", "2. Show - 01.Studio Band.mka"),
            [.. MediaFactory.Sine(550), "-c:a", "aac", "-metadata:s:a:0", "title=Studio Band", "-metadata:s:a:0", "language=rus"]);

        var analysis = await AudioMuxOperation.AnalyzeAsync(await AudioMuxOperation.InspectAsync(dir.Path, probe, Ct), [1, 0], true, probe, Ct);
        var set = Assert.Single(analysis.Sets);
        Assert.Equal(3, set.Slots.Count);
        var config = AudioSlotSetConfig.Default(set, "rus", order: [set.Slots[2].Key, set.Slots[1].Key, set.Slots[0].Key]);
        var result = await Execute(tools, probe, dir, AudioMuxOperation.Plan(analysis, [config]));

        Assert.Equal(ItemOutcome.Done, Assert.Single(result.Items).Outcome);
        var info = await probe.ProbeAsync(dir.Combine("Processed Audio", "Show - 01.mkv"), Ct);
        Assert.Single(info.VideoStreams);
        Assert.Empty(info.SubtitleStreams);
        Assert.Equal(["Studio Band", "AniLibria.TV", "Оригинальная"], info.AudioStreams.Select(a => a.Title));
        Assert.Equal(["rus", "rus", "jpn"], info.AudioStreams.Select(a => a.Language));
        Assert.Equal([true, false, false], info.AudioStreams.Select(a => a.IsDefault));
    }

    [Fact]
    public async Task Subtitles_from_mkv_by_title_and_from_mp4_via_ffmpeg()
    {
        var (tools, probe) = Tools(mkvToolNix: true);
        using var dir = new TempDir();
        await MediaFactory.EpisodeAsync(dir.Combine("Show - 01.mkv"));
        await File.WriteAllTextAsync(dir.Combine("full.srt"), MediaFactory.Srt, Ct);
        await MediaFactory.CreateAsync(dir.Combine("Show - 02.mp4"), [
            .. MediaFactory.Video, "-i", dir.Combine("full.srt"), "-map", "0", "-map", "1",
            "-c:v", "libx264", "-preset", "ultrafast", "-c:s", "mov_text", "-metadata:s:s:0", "language=rus"]);
        File.Delete(dir.Combine("full.srt"));

        var source = await SubtitleExtractOperation.InspectAsync(dir.Path, probe, Ct);
        var signs = source.Tracks.Single(t => t.Name == "Надписи");
        var byTitle = await Execute(tools, probe, dir, await SubtitleExtractOperation.PlanAsync(source, new SubtitleExtractOptions(signs.Id, SubtitleMatchMode.ByTitle), probe, Ct));
        Assert.Equal(ItemOutcome.Done, byTitle.Items[0].Outcome);
        Assert.Contains("Надпись", await File.ReadAllTextAsync(dir.Combine("надписи", "Show - 01.надписи.ass"), Ct));
        Assert.Equal(ItemOutcome.Skipped, byTitle.Items[1].Outcome); // в mp4 нет дорожки «Надписи»

        var byLanguage = await Execute(tools, probe, dir, await SubtitleExtractOperation.PlanAsync(
            source, new SubtitleExtractOptions(signs.Id, SubtitleMatchMode.ByLanguage, SubtitleKind.Subs), probe, Ct));
        Assert.All(byLanguage.Items, i => Assert.Equal(ItemOutcome.Done, i.Outcome));
        Assert.Contains("Hello", await File.ReadAllTextAsync(dir.Combine("сабы", "Show - 02.сабы.srt"), Ct));
    }

    [Fact]
    public async Task Remux_to_mp4_including_silent_file()
    {
        var (tools, probe) = Tools();
        using var dir = new TempDir();
        await MediaFactory.CreateAsync(dir.Combine("Clip.mkv"), [.. MediaFactory.Video, .. MediaFactory.Sine(440), "-c:v", "libx264", "-preset", "ultrafast", "-c:a", "aac"]);
        await MediaFactory.CreateAsync(dir.Combine("Silent.mkv"), [.. MediaFactory.Video, "-c:v", "libx264", "-preset", "ultrafast"]);

        var result = await Execute(tools, probe, dir, RemuxOperation.Plan(dir.Path, new RemuxOptions()));

        Assert.All(result.Items, i => Assert.Equal(ItemOutcome.Done, i.Outcome));
        Assert.Single((await probe.ProbeAsync(dir.Combine("converted_mp4", "Clip.mp4"), Ct)).AudioStreams);
        Assert.Empty((await probe.ProbeAsync(dir.Combine("converted_mp4", "Silent.mp4"), Ct)).AudioStreams);
    }

    [Fact]
    public async Task Broken_input_fails_with_log_and_leaves_no_output()
    {
        var (tools, probe) = Tools();
        using var dir = new TempDir();
        await File.WriteAllBytesAsync(dir.Combine("Broken - 01.mkv"), [.. Enumerable.Range(0, 4096).Select(i => (byte)(i * 7))], Ct);

        var result = await Execute(tools, probe, dir, VideoOnlyOperation.Plan(dir.Path));

        var item = Assert.Single(result.Items);
        Assert.Equal(ItemOutcome.Failed, item.Outcome);
        Assert.True(File.Exists(item.LogPath));
        Assert.Contains("Command:", await File.ReadAllTextAsync(item.LogPath!, Ct));
        Assert.False(File.Exists(dir.Combine("Video only", "Broken - 01.mkv")));
    }

    [Fact]
    public async Task Empty_read_only_leftover_is_overwritten()
    {
        var (tools, probe) = Tools();
        using var dir = new TempDir();
        await MediaFactory.EpisodeAsync(dir.Combine("Show - 01.mkv"));
        var leftover = dir.File("Video only/Show - 01.mkv");
        File.SetAttributes(leftover, FileAttributes.ReadOnly);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(leftover, UnixFileMode.UserRead);
        }

        var result = await Execute(tools, probe, dir, VideoOnlyOperation.Plan(dir.Path));

        Assert.Equal(ItemOutcome.Done, Assert.Single(result.Items).Outcome);
        Assert.True(new FileInfo(leftover).Length > 0);
    }

    private static (ToolPaths Tools, MediaProbe Probe) Tools(bool mkvToolNix = false)
    {
        var tools = mkvToolNix ? TestTools.RequireMkvToolNix() : TestTools.RequireFfmpeg();
        return (tools, new MediaProbe(new ProcessRunner(), tools));
    }

    private static Task<OperationResult> Execute(ToolPaths tools, MediaProbe probe, TempDir dir, OperationPlan plan) =>
        new PlanExecutor(new ProcessRunner(), tools, new ErrorLogWriter(dir.Combine("_logs")), probe).ExecuteAsync(plan, cancellationToken: Ct);
}
