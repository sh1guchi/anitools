using System.IO.Compression;
using Anitools.Core.Logging;
using Anitools.Core.Media;
using Anitools.Core.Operations.Hls;
using Anitools.Core.Processes;
using Anitools.Core.Tests.Fixtures;

namespace Anitools.Core.Tests.Integration;

/// <summary>
/// П.7 на настоящем ffmpeg с профилем Software (libx264 + scale вместо видеокарты) и урезанной лестницей:
/// архивы, сегменты по 6 с с одинаковыми границами во всех качествах, озвучки, подбор CQ.
/// </summary>
[Trait("Category", "Integration")]
public sealed class HlsIntegrationTests
{
    private static readonly HlsSettings Software = HlsSettings.Default with
    {
        Encoder = EncoderProfile.Software,
        SoftwarePreset = "ultrafast",
        Ladder = [new("180p", 320, 180, 300_000), new("90p", 160, 90, 150_000)],
    };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Two_episodes_become_archives_and_audio()
    {
        var tools = TestTools.RequireFfmpeg();
        using var dir = new TempDir();
        await EpisodeAsync(dir.Combine("Kaiju - 01.mkv"), 13);
        await EpisodeAsync(dir.Combine("Kaiju - 02.mkv"), 13);
        var probe = new MediaProbe(new ProcessRunner(), tools);
        var inspection = await HlsOperation.InspectAsync(dir.Path, probe, Ct);
        var group = Assert.Single(inspection.Groups);
        var plan = HlsOperation.Plan(inspection, [Hls.HlsPlanTests.Options(group, HlsOperation.TitleFolder(12345, group.Title), [new(0, "AniLibria.TV"), new(1, "DEEP")])]);

        var result = await new HlsRunner(new ProcessRunner(), tools, probe, new ErrorLogWriter(dir.Combine("_logs")), Software)
            .ExecuteAsync(plan, dir.Combine("work"), cancellationToken: Ct);

        Assert.All(result.Episodes, e => Assert.True(e.Outcome == HlsEpisodeOutcome.Done, $"{e.Episode.Source.Name}: {e.Message} {e.LogPath}"));
        Assert.Empty(Directory.EnumerateFileSystemEntries(dir.Combine("work")));
        var titleOut = dir.Combine("hls_multi", "12345 - Kaiju");
        foreach (var ep in new[] { "Kaiju - 01", "Kaiju - 02" })
        {
            using var zip = ZipFile.OpenRead(Path.Combine(titleOut, ep + ".zip"));
            Assert.All(zip.Entries, e => Assert.Equal(e.Length, e.CompressedLength));
            // 13 с при сегментах по 6 с: 6 + 6 + 1, границы одинаковые в обоих качествах
            Assert.Equal(
                ["180p/master.m3u8", "180p/seg000.ts", "180p/seg001.ts", "180p/seg002.ts", "90p/master.m3u8", "90p/seg000.ts", "90p/seg001.ts", "90p/seg002.ts"],
                zip.Entries.Select(e => e.FullName).Order(StringComparer.Ordinal));
            Assert.Equal(Durations(zip, "180p"), Durations(zip, "90p"));
            Assert.Equal(["6.000000", "6.000000"], Durations(zip, "180p").Take(2));

            var segment = Path.Combine(dir.Path, "_segment.ts");
            zip.GetEntry("90p/seg001.ts")!.ExtractToFile(segment, overwrite: true);
            var video = Assert.Single((await probe.ProbeAsync(segment, Ct)).VideoStreams);
            Assert.Equal(("h264", 160, 90), (video.CodecName, video.Width, video.Height));

            var deep = await probe.ProbeAsync(Path.Combine(titleOut, "audio", "DEEP", $"{ep}.DEEP.mka"), Ct);
            Assert.Equal(("aac", 2), (deep.AudioStreams[0].CodecName, deep.AudioStreams[0].Channels)); // 5.1 → стерео AAC
            var main = await probe.ProbeAsync(Path.Combine(titleOut, "audio", "AniLibria.TV", $"{ep}.AniLibria.TV.mka"), Ct);
            Assert.Equal("flac", main.AudioStreams[0].CodecName); // остальное — копией
        }
    }

    [Fact]
    public async Task Quality_is_calibrated_on_real_encoder()
    {
        var tools = TestTools.RequireFfmpeg();
        using var dir = new TempDir();
        await EpisodeAsync(dir.Combine("Kaiju - 01.mkv"), 25);
        var probe = new MediaProbe(new ProcessRunner(), tools);
        var inspection = await HlsOperation.InspectAsync(dir.Path, probe, Ct);
        var plan = HlsOperation.Plan(inspection, [Hls.HlsPlanTests.Options(inspection.Groups[0], "Kaiju", [new(0, "AniLibria.TV")])]);
        var settings = Software with { FixedCq = null, CalibrationWindows = 2, CalibrationMaxPasses = 2 };

        var result = await new HlsRunner(new ProcessRunner(), tools, probe, new ErrorLogWriter(dir.Combine("_logs")), settings)
            .ExecuteAsync(plan, null, cancellationToken: Ct);

        var episode = Assert.Single(result.Episodes);
        Assert.True(episode.Outcome == HlsEpisodeOutcome.Done, $"{episode.Message} {episode.LogPath}");
        Assert.NotNull(episode.RateControl);
        Assert.All(episode.RateControl, rc => Assert.InRange(rc.Cq, settings.CqMin, settings.CqMax));
        Assert.Contains(episode.Notes, n => n.StartsWith("CQ под серию", StringComparison.Ordinal));
        Assert.False(Directory.Exists(dir.Combine("hls_multi", "Kaiju", "Kaiju - 01")));
        Assert.True(File.Exists(dir.Combine("hls_multi", "Kaiju", "Kaiju - 01.zip")));
    }

    /// <summary>Серия нужной длины: видео 320×180 с меняющейся картинкой, стерео FLAC «AniLibria.TV» и 5.1 AC3 «DEEP».</summary>
    private static Task<string> EpisodeAsync(string output, int seconds) => MediaFactory.CreateAsync(output, [
        "-f", "lavfi", "-i", $"testsrc2=size=320x180:rate=24:duration={seconds}",
        "-f", "lavfi", "-i", $"sine=f=440:d={seconds}",
        "-f", "lavfi", "-t", $"{seconds}", "-i", "anullsrc=channel_layout=5.1:sample_rate=48000",
        "-map", "0", "-map", "1", "-map", "2",
        "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", "-g", "48",
        "-c:a:0", "flac", "-c:a:1", "ac3",
        "-metadata:s:a:0", "title=AniLibria.TV", "-metadata:s:a:0", "language=rus",
        "-metadata:s:a:1", "title=DEEP", "-metadata:s:a:1", "language=rus"]);

    private static List<string> Durations(ZipArchive zip, string rung)
    {
        using var reader = new StreamReader(zip.GetEntry($"{rung}/master.m3u8")!.Open());
        return [.. reader.ReadToEnd().Split('\n').Where(l => l.StartsWith("#EXTINF:", StringComparison.Ordinal)).Select(l => l["#EXTINF:".Length..].TrimEnd(','))];
    }
}
