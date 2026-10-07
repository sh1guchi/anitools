using Anitools.Core.Media;
using Anitools.Core.Operations.AudioMux;
using Anitools.Core.Operations.Common;
using Anitools.Core.Tests.Fixtures;

namespace Anitools.Core.Tests.Operations;

/// <summary>
/// П.3: у части серий другой набор внешних файлов (docs/PLAN.md §2.8 #6). В оригинале дорожки такой серии
/// молча съезжали: слоты брались по номеру файла в списке первой серии.
/// </summary>
public sealed class AudioMuxSetsTests
{
    private const string Ext = "Audio only/1. AniLibria.TV/1. Test Show - {0}.AniLibria.TV.mka";

    [Fact]
    public async Task Episode_without_external_file_gets_its_own_set_and_nothing_shifts()
    {
        using var dir = new TempDir();
        var media = new Dictionary<string, string>();
        foreach (var ep in new[] { "01", "02", "03" })
        {
            dir.File($"Test Show - {ep}.mkv");
            media[$"Test Show - {ep}.mkv"] = "Test Show - 01.mkv";
            dir.File($"Test Show - {ep}.mka");
            media[$"Test Show - {ep}.mka"] = "Test Show - 01.mka";
            if (ep != "02")
            {
                var ext = string.Format(System.Globalization.CultureInfo.InvariantCulture, Ext, ep);
                dir.File(ext);
                media[ext] = "1. Test Show - 01.AniLibria.TV.mka";
            }
        }

        var probe = new Probe(dir.Path, media);
        var ct = TestContext.Current.CancellationToken;
        var analysis = await AudioMuxOperation.AnalyzeAsync(await AudioMuxOperation.InspectAsync(dir.Path, probe, ct), [0], true, probe, ct);

        Assert.Equal(2, analysis.Sets.Count);
        var main = analysis.Sets[0];
        var other = analysis.Sets[1];
        Assert.True(main.IsMain);
        Assert.Equal(["Test Show - 01.mkv", "Test Show - 03.mkv"], main.Episodes.Select(e => Path.GetFileName(e.Video)));
        Assert.Equal(["Test Show - 02.mkv"], other.Episodes.Select(e => Path.GetFileName(e.Video)));
        Assert.Equal(4, main.Slots.Count); // внутр. + 2 дорожки .mka + внешний AniLibria
        Assert.Equal(3, other.Slots.Count);

        // Основной набор: внешний AniLibria — первым; второй набор берёт порядок, тайтлы и язык из основного
        var mainConfig = AudioSlotSetConfig.Default(main, "rus", order: [main.Slots[3].Key, main.Slots[0].Key, main.Slots[2].Key, main.Slots[1].Key]);
        var otherConfig = AudioSlotSetConfig.FromMain(mainConfig, other);
        Assert.Equal([main.Slots[0].Key, main.Slots[2].Key, main.Slots[1].Key], otherConfig.Order);

        var plan = AudioMuxOperation.Plan(analysis, [mainConfig, otherConfig]);
        var commands = plan.Items.Where(i => i.Status == PlanItemStatus.Run).ToDictionary(i => Path.GetFileName(i.Source), i => i.Command!.Arguments);

        // 01 и 03: три входа, AniLibria из 2-го внешнего файла первой; у 02 его просто нет — и ничего не съехало
        Assert.Equal(["2:a:0", "0:a:0", "1:a:1", "1:a:0"], Maps(commands["Test Show - 01.mkv"]));
        Assert.Equal(["2:a:0", "0:a:0", "1:a:1", "1:a:0"], Maps(commands["Test Show - 03.mkv"]));
        Assert.Equal(["0:a:0", "1:a:1", "1:a:0"], Maps(commands["Test Show - 02.mkv"]));
        Assert.Equal(2, commands["Test Show - 02.mkv"].Count(a => a == "-i"));
        Assert.Contains("language=jpn", commands["Test Show - 02.mkv"]); // «Оригинальная» — по-прежнему jpn
    }

    [Fact]
    public async Task Episode_with_fewer_internal_tracks_is_not_mapped_to_missing_track()
    {
        using var dir = new TempDir();
        dir.File("Show - 01.mkv");
        dir.File("Show - 02.mkv");
        var probe = new Probe(dir.Path, new Dictionary<string, string>
        {
            ["Show - 01.mkv"] = "Test Show - 01.mkv", // 3 аудио
            ["Show - 02.mkv"] = "Test Show - 02.mkv", // 2 аудио
        });
        var ct = TestContext.Current.CancellationToken;
        var analysis = await AudioMuxOperation.AnalyzeAsync(await AudioMuxOperation.InspectAsync(dir.Path, probe, ct), [2, 0], false, probe, ct);

        Assert.Equal(2, analysis.Sets.Count);
        var main = AudioSlotSetConfig.Default(analysis.Sets[0]);
        var plan = AudioMuxOperation.Plan(analysis, [main, AudioSlotSetConfig.FromMain(main, analysis.Sets[1])]);
        var second = plan.Items.Single(i => i.Source.EndsWith("Show - 02.mkv", StringComparison.Ordinal));
        Assert.Equal(["0:a:0"], Maps(second.Command!.Arguments));
    }

    [Fact]
    public async Task Nothing_selected_and_no_external_is_a_plan_error()
    {
        using var dir = new TempDir();
        dir.File("Show - 01.mkv");
        var probe = new Probe(dir.Path, new Dictionary<string, string> { ["Show - 01.mkv"] = "Test Show - 01.mkv" });
        var ct = TestContext.Current.CancellationToken;
        var source = await AudioMuxOperation.InspectAsync(dir.Path, probe, ct);
        await Assert.ThrowsAsync<PlanException>(() => AudioMuxOperation.AnalyzeAsync(source, [], false, probe, ct));
        await Assert.ThrowsAsync<PlanException>(() => AudioMuxOperation.AnalyzeAsync(source, [7], false, probe, ct));
    }

    private static IReadOnlyList<string> Maps(IReadOnlyList<string> args) =>
        args.Select((a, i) => (a, i)).Where(p => p.a == "-map" && args[p.i + 1] != "0:v:0?").Select(p => args[p.i + 1]).ToList();

    private sealed class Probe(string root, IReadOnlyDictionary<string, string> media) : IMediaProbe
    {
        public Task<MediaInfo> ProbeAsync(string path, CancellationToken cancellationToken = default) =>
            Task.FromResult(MediaProbe.ParseFfprobe(MediaFixtures.Ffprobe(media[Path.GetRelativePath(root, path).Replace('\\', '/')])));

        public Task<MkvIdentification> IdentifyAsync(string path, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
