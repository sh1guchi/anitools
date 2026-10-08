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
    public async Task Audio_shift_without_reencoding_moves_start_and_keeps_codec()
    {
        var tools = TestTools.RequireMkvToolNix();
        var probe = new MediaProbe(new ProcessRunner(), tools);
        using var dir = new TempDir();
        await MediaFactory.CreateAsync(dir.Combine("Voice.ac3"), ["-f", "lavfi", "-t", "3", "-i", "anullsrc=channel_layout=5.1:sample_rate=48000", "-c:a", "ac3"]);

        var plus = await Execute(tools, probe, dir, AudioShiftOperation.Plan(dir.Path, new AudioShiftOptions { Seconds = 1 }), 6);
        Assert.Equal(ItemOutcome.Done, Assert.Single(plus.Items).Outcome);
        var shifted = dir.Combine("audio_fixed", "Voice.mka");
        Assert.InRange(await StartTimeAsync(tools, shifted), 0.95, 1.05); // звук начинается на секунду позже
        var info = await probe.ProbeAsync(shifted, Ct);
        Assert.Equal((6, "ac3"), (info.AudioStreams[0].Channels, info.AudioStreams[0].CodecName)); // тот же кодек, без перекодирования

        File.Delete(shifted);
        var minus = await Execute(tools, probe, dir, AudioShiftOperation.Plan(dir.Path, new AudioShiftOptions { Seconds = -1 }), 6);
        Assert.Equal(ItemOutcome.Done, Assert.Single(minus.Items).Outcome);
        Assert.InRange((await probe.ProbeAsync(shifted, Ct)).Duration!.Value, 1.9, 2.1); // первая секунда отброшена
    }

    [Fact]
    public async Task Audio_shift_with_aac_adds_or_cuts_a_second_and_keeps_5_1()
    {
        var (tools, probe) = Tools();
        using var dir = new TempDir();
        await MediaFactory.CreateAsync(dir.Combine("Voice.ac3"), ["-f", "lavfi", "-t", "3", "-i", "anullsrc=channel_layout=5.1:sample_rate=44100", "-c:a", "ac3"]);

        var plus = await Execute(tools, probe, dir, AudioShiftOperation.Plan(dir.Path, new AudioShiftOptions { Seconds = 1, Reencode = true }), 6);
        Assert.Equal(ItemOutcome.Done, Assert.Single(plus.Items).Outcome);
        var longer = await probe.ProbeAsync(dir.Combine("audio_fixed", "Voice.mka"), Ct);
        Assert.InRange(longer.Duration!.Value, 3.9, 4.15);
        Assert.Equal((6, "aac"), (longer.AudioStreams[0].Channels, longer.AudioStreams[0].CodecName)); // раскладка исходника цела

        File.Delete(dir.Combine("audio_fixed", "Voice.mka"));
        var minus = await Execute(tools, probe, dir, AudioShiftOperation.Plan(dir.Path, new AudioShiftOptions { Seconds = -1, Reencode = true }), 6);
        Assert.Equal(ItemOutcome.Done, Assert.Single(minus.Items).Outcome);
        Assert.InRange((await probe.ProbeAsync(dir.Combine("audio_fixed", "Voice.mka"), Ct)).Duration!.Value, 1.9, 2.15);
    }

    /// <summary>Начало первой дорожки по ffprobe (start_time), с.</summary>
    private static async Task<double> StartTimeAsync(ToolPaths tools, string file)
    {
        var result = await new ProcessRunner().RunAsync(
            new ProcessSpec(tools.Ffprobe!, ["-v", "error", "-select_streams", "a:0", "-show_entries", "stream=start_time", "-of", "csv=p=0", file]), Ct);
        return double.Parse(result.StandardOutput.Trim(), System.Globalization.CultureInfo.InvariantCulture);
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
    public async Task Audio_convert_to_mov_keeps_only_audio()
    {
        var (tools, probe) = Tools();
        using var dir = new TempDir();
        // mp3 с обложкой: в m4a обложка переносится, в .mov — нет, там только звук
        await MediaFactory.CreateAsync(dir.Combine("C.mp3"),
        [
            .. MediaFactory.Sine(440), "-f", "lavfi", "-i", "color=c=red:s=64x64:d=1",
            "-map", "0:a", "-map", "1:v", "-c:a", "libmp3lame", "-c:v", "png", "-frames:v", "1", "-disposition:v:0", "attached_pic",
        ]);
        Assert.Single((await probe.ProbeAsync(dir.Combine("C.mp3"), Ct)).VideoStreams);

        var mov = await Execute(tools, probe, dir, AudioConvertOperation.Plan(dir.Path, new AudioConvertOptions { Format = AudioFormat.Mov }), 8);

        var item = Assert.Single(mov.Items);
        Assert.True(item.Outcome == ItemOutcome.Done, $"{item.Message} {item.LogPath}");
        var info = await probe.ProbeAsync(dir.Combine("converted", "C.mov"), Ct);
        Assert.Empty(info.VideoStreams);
        Assert.Equal(("aac", 2), (Assert.Single(info.AudioStreams).CodecName, info.AudioStreams[0].Channels));
        // именно QuickTime: в ftyp основной бренд «qt  », а не mp4/m4a
        var head = new byte[12];
        await using (var file = File.OpenRead(dir.Combine("converted", "C.mov")))
        {
            await file.ReadExactlyAsync(head, Ct);
        }

        Assert.Equal("ftypqt  ", System.Text.Encoding.ASCII.GetString(head, 4, 8));
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
    public async Task Remux_avi_to_mp4_and_mkv()
    {
        var (tools, probe) = Tools();
        using var dir = new TempDir();
        await MediaFactory.CreateAsync(dir.Combine("Old.avi"), [.. MediaFactory.Video, .. MediaFactory.Sine(440), "-c:v", "mpeg4", "-c:a", "mp3"]);

        var mp4 = await Execute(tools, probe, dir, RemuxOperation.Plan(dir.Path, new RemuxOptions(RemuxFormat.Mp4)), 1);
        var mkv = await Execute(tools, probe, dir, RemuxOperation.Plan(dir.Path, new RemuxOptions(RemuxFormat.Mkv)), 1);

        Assert.Equal(ItemOutcome.Done, Assert.Single(mp4.Items).Outcome);
        Assert.Equal(ItemOutcome.Done, Assert.Single(mkv.Items).Outcome);
        foreach (var output in new[] { "Old.mp4", "Old.mkv" })
        {
            var info = await probe.ProbeAsync(dir.Combine("converted_mp4", output), Ct);
            Assert.Equal(("mpeg4", "mp3"), (info.VideoStreams[0].CodecName, info.AudioStreams[0].CodecName));
        }
    }

    [Fact]
    public async Task Remux_preset_bluray_m2ts()
    {
        var (tools, probe) = Tools();
        using var dir = new TempDir();
        await MediaFactory.CreateAsync(dir.Combine("Disc.m2ts"), [
            .. MediaFactory.Video, .. MediaFactory.Sine(440), "-c:v", "libx264", "-preset", "ultrafast", "-c:a", "pcm_bluray",
            "-f", "mpegts", "-mpegts_m2ts_mode", "1"]);

        var m2ts = await Execute(tools, probe, dir, RemuxPresets.M2tsToMkv(dir.Path), 1);

        Assert.Equal(ItemOutcome.Done, Assert.Single(m2ts.Items).Outcome);
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
    public async Task Fonts_from_a_single_video_are_still_fonts_zip()
    {
        var tools = TestTools.RequireMkvToolNix();
        var probe = new MediaProbe(new ProcessRunner(), tools);
        using var dir = new TempDir();
        var font = dir.File("_src/Шрифт.ttf", "TTF");
        await MediaFactory.CreateAsync(dir.Combine("_src", "plain.mkv"), [.. MediaFactory.Video, "-c:v", "libx264", "-preset", "ultrafast"]);
        var made = await new ProcessRunner().RunAsync(new ProcessSpec(tools.Mkvmerge!, [
            "-o", dir.Combine("Movie.mkv"), dir.Combine("_src", "plain.mkv"),
            "--attachment-mime-type", "application/x-truetype-font", "--attach-file", font]), Ct);
        Assert.True(made.ExitCode <= 1, made.StandardOutput);

        var result = await new VideoFontsOperation(new ProcessRunner(), tools, probe, new ErrorLogWriter(dir.Combine("_logs"))).ExecuteAsync(dir.Path, cancellationToken: Ct);

        // одна серия — архив всё равно fonts.zip, а не по имени видео
        Assert.Equal(dir.Combine("fonts.zip"), result.ZipPath);
        Assert.Equal(["Шрифт.ttf"], result.Packed);
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
