using System.IO.Compression;
using Anitools.Core.Logging;
using Anitools.Core.Media;
using Anitools.Core.Operations.AudioTools;
using Anitools.Core.Operations.Common;
using Anitools.Core.Operations.Fonts;
using Anitools.Core.Operations.Hardsub;
using Anitools.Core.Operations.MkaMux;
using Anitools.Core.Operations.Remux;
using Anitools.Core.Processes;
using Anitools.Core.Tests.Fixtures;

namespace Anitools.Core.Tests.Integration;

/// <summary>Доп. инструменты на настоящих ffmpeg и MKVToolNix.</summary>
[Trait("Category", "Integration")]
public sealed class ToolsIntegrationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Audio_shift_adds_or_cuts_a_second_and_keeps_5_1()
    {
        var (tools, probe) = Tools();
        using var dir = new TempDir();
        await MediaFactory.CreateAsync(dir.Combine("Voice.ac3"), ["-f", "lavfi", "-t", "3", "-i", "anullsrc=channel_layout=5.1:sample_rate=44100", "-c:a", "ac3"]);

        var plus = await Execute(tools, probe, dir, AudioShiftOperation.Plan(dir.Path, new AudioShiftOptions { Seconds = 1 }), 6);
        Assert.Equal(ItemOutcome.Done, Assert.Single(plus.Items).Outcome);
        var longer = await probe.ProbeAsync(dir.Combine("audio_fixed", "Voice.mka"), Ct);
        Assert.InRange(longer.Duration!.Value, 3.9, 4.15);
        Assert.Equal((6, "aac"), (longer.AudioStreams[0].Channels, longer.AudioStreams[0].CodecName)); // раскладка исходника цела

        File.Delete(dir.Combine("audio_fixed", "Voice.mka"));
        var minus = await Execute(tools, probe, dir, AudioShiftOperation.Plan(dir.Path, new AudioShiftOptions { Seconds = -1 }), 6);
        Assert.Equal(ItemOutcome.Done, Assert.Single(minus.Items).Outcome);
        Assert.InRange((await probe.ProbeAsync(dir.Combine("audio_fixed", "Voice.mka"), Ct)).Duration!.Value, 1.9, 2.15);
    }

    [Fact]
    public async Task Audio_convert_downmixes_to_stereo_aac_and_flac()
    {
        var (tools, probe) = Tools();
        using var dir = new TempDir();
        await MediaFactory.CreateAsync(dir.Combine("A.ac3"), ["-f", "lavfi", "-t", "2", "-i", "anullsrc=channel_layout=5.1:sample_rate=48000", "-c:a", "ac3"]);
        await MediaFactory.CreateAsync(dir.Combine("B.wav"), [.. MediaFactory.Sine(440)]);

        var mka = await Execute(tools, probe, dir, AudioConvertOperation.Plan(dir.Path, new AudioConvertOptions()), 8);
        Assert.All(mka.Items, i => Assert.Equal(ItemOutcome.Done, i.Outcome));
        var a = (await probe.ProbeAsync(dir.Combine("converted", "A.mka"), Ct)).AudioStreams[0];
        Assert.Equal(("aac", 2), (a.CodecName, a.Channels));

        var flac = await Execute(tools, probe, dir, AudioConvertOperation.Plan(dir.Path, new AudioConvertOptions { Format = AudioFormat.Flac }), 8);
        Assert.All(flac.Items, i => Assert.Equal(ItemOutcome.Done, i.Outcome));
        Assert.Equal("flac", (await probe.ProbeAsync(dir.Combine("converted", "B.flac"), Ct)).AudioStreams[0].CodecName);
    }

    [Fact]
    public async Task Hardsub_burns_subtitles_into_part_file_then_renames()
    {
        var (tools, probe) = Tools();
        using var dir = new TempDir();
        await MediaFactory.CreateAsync(dir.Combine("Show [01], it's.mkv"), [.. MediaFactory.Video, .. MediaFactory.Sine(440), "-c:v", "libx264", "-preset", "ultrafast", "-c:a", "aac"]);
        await File.WriteAllTextAsync(dir.Combine("Show [01], it's.ass"), MediaFactory.Ass, Ct);
        var plan = HardsubOperation.Plan(dir.Path, new HardsubOptions { EncodeArgs = ["-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p"] });

        var result = await Execute(tools, probe, dir, plan, 1);

        var item = Assert.Single(result.Items);
        Assert.True(item.Outcome == ItemOutcome.Done, $"{item.Message} {item.LogPath}");
        Assert.Equal(["Show [01], it's.mkv"], Directory.EnumerateFiles(dir.Combine("Hardsub")).Select(Path.GetFileName));
        var info = await probe.ProbeAsync(dir.Combine("Hardsub", "Show [01], it's.mkv"), Ct);
        Assert.Equal("h264", Assert.Single(info.VideoStreams).CodecName);
        Assert.Equal("aac", Assert.Single(info.AudioStreams).CodecName);
        Assert.Empty(info.SubtitleStreams);
    }

    [Fact]
    public async Task Remux_presets_avi_and_bluray_m2ts()
    {
        var (tools, probe) = Tools();
        using var dir = new TempDir();
        await MediaFactory.CreateAsync(dir.Combine("Old.avi"), [.. MediaFactory.Video, .. MediaFactory.Sine(440), "-c:v", "mpeg4", "-c:a", "mp3"]);
        await MediaFactory.CreateAsync(dir.Combine("Disc.m2ts"), [
            .. MediaFactory.Video, .. MediaFactory.Sine(440), "-c:v", "libx264", "-preset", "ultrafast", "-c:a", "pcm_bluray",
            "-f", "mpegts", "-mpegts_m2ts_mode", "1"]);

        var avi = await Execute(tools, probe, dir, RemuxPresets.AviToMkv(dir.Path), 1);
        var m2ts = await Execute(tools, probe, dir, RemuxPresets.M2tsToMkv(dir.Path), 1);

        Assert.Equal(ItemOutcome.Done, Assert.Single(avi.Items).Outcome);
        Assert.Equal(ItemOutcome.Done, Assert.Single(m2ts.Items).Outcome);
        Assert.Equal("mpeg4", (await probe.ProbeAsync(dir.Combine("Old.mkv"), Ct)).VideoStreams[0].CodecName);
        var disc = await probe.ProbeAsync(dir.Combine("Disc.mkv"), Ct);
        Assert.Equal(("h264", "flac"), (disc.VideoStreams[0].CodecName, disc.AudioStreams[0].CodecName));
    }

    [Fact]
    public async Task Fonts_from_videos_go_to_zip_once()
    {
        var tools = TestTools.RequireMkvToolNix();
        var probe = new MediaProbe(new ProcessRunner(), tools);
        using var dir = new TempDir();
        var font = dir.File("_src/Шрифт.ttf", "TTF-данные");
        var other = dir.File("_src/Other.otf", "OTF");
        var cover = dir.File("_src/cover.jpg", "JPG");
        await MediaFactory.CreateAsync(dir.Combine("_src", "plain.mkv"), [.. MediaFactory.Video, "-c:v", "libx264", "-preset", "ultrafast"]);
        foreach (var (name, attachments) in new[] { ("A - 01.mkv", new[] { font, cover }), ("A - 02.mkv", new[] { font, other }) })
        {
            List<string> args = ["-o", dir.Combine(name), dir.Combine("_src", "plain.mkv")];
            foreach (var file in attachments)
            {
                args.AddRange(["--attachment-mime-type", file.EndsWith(".jpg", StringComparison.Ordinal) ? "image/jpeg" : "application/x-truetype-font", "--attach-file", file]);
            }

            var made = await new ProcessRunner().RunAsync(new ProcessSpec(tools.Mkvmerge!, args), Ct);
            Assert.True(made.ExitCode <= 1, made.StandardOutput);
        }

        var result = await new VideoFontsOperation(new ProcessRunner(), tools, probe, new ErrorLogWriter(dir.Combine("_logs"))).ExecuteAsync(dir.Path, cancellationToken: Ct);

        Assert.Empty(result.Errors);
        Assert.Equal(["Шрифт.ttf", "Other.otf"], result.Packed);
        Assert.Equal([(dir.Combine("A - 02.mkv"), "Шрифт.ttf")], result.Duplicates);
        using var zip = ZipFile.OpenRead(result.ZipPath!);
        using var reader = new StreamReader(zip.GetEntry("Шрифт.ttf")!.Open());
        Assert.Equal("TTF-данные", await reader.ReadToEndAsync(Ct));
    }

    [Fact]
    public async Task Voices_are_muxed_into_one_mka_per_episode()
    {
        var (tools, probe) = Tools();
        using var dir = new TempDir();
        foreach (var (folder, title, freq) in new[] { ("1. AniLibria.TV", "AniLibria.TV", 440), ("2. Оригинальная", "Japanese", 550) })
        {
            foreach (var ep in new[] { "01", "02" })
            {
                await MediaFactory.CreateAsync(dir.Combine(folder, $"{folder[..2]} Show - {ep}.{title}.mka"), [.. MediaFactory.Sine(freq), "-c:a", "aac", "-metadata:s:a:0", $"title={title}"]);
            }
        }

        var sources = await MkaMuxOperation.InspectAsync(dir.Path, probe, Ct);
        var groups = MkaMuxOperation.Groups(sources, MkaMuxMode.ByEpisode);
        Assert.Equal(["AniLibria.TV", "Оригинальная"], MkaMuxOperation.Labels(groups).Select(l => l.Label));
        var plan = MkaMuxOperation.Plan(dir.Path, groups, new MkaLabelOptions
        {
            Order = ["Оригинальная", "AniLibria.TV"],
            Titles = new Dictionary<string, string> { ["Оригинальная"] = "Оригинал" },
        });

        var result = await Execute(tools, probe, dir, plan, 1);

        Assert.All(result.Items, i => Assert.Equal(ItemOutcome.Done, i.Outcome));
        var mka = await probe.ProbeAsync(dir.Combine("MKA", "Show - 02.mka"), Ct);
        Assert.Equal(["Оригинал", "AniLibria.TV"], mka.AudioStreams.Select(a => a.Title));
        Assert.Equal(["jpn", "rus"], mka.AudioStreams.Select(a => a.Language));
        Assert.Equal([true, false], mka.AudioStreams.Select(a => a.IsDefault));
    }

    private static (ToolPaths Tools, MediaProbe Probe) Tools()
    {
        var tools = TestTools.RequireFfmpeg();
        return (tools, new MediaProbe(new ProcessRunner(), tools));
    }

    private static Task<OperationResult> Execute(ToolPaths tools, MediaProbe probe, TempDir dir, OperationPlan plan, int parallel) =>
        new PlanExecutor(new ProcessRunner(), tools, new ErrorLogWriter(dir.Combine("_logs")), probe).ExecuteAsync(plan, cancellationToken: Ct, maxParallel: parallel);
}
