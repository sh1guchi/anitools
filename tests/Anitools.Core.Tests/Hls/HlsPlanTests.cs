using Anitools.Core.Operations.Common;
using Anitools.Core.Operations.Hls;
using Anitools.Core.Tests.Fixtures;

namespace Anitools.Core.Tests.Hls;

/// <summary>HLS: группы по тайтлам, раскладки дорожек, озвучки, имена папок серий и «уже готово».</summary>
public sealed class HlsPlanTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Groups_layouts_and_episode_folders()
    {
        using var dir = new TempDir();
        var media = new Dictionary<string, string>
        {
            ["[Sub] Frieren - 01 [1080p].mkv"] = "Test Show - 01.mkv",
            ["[Sub] Frieren - 02 [1080p].mkv"] = "Test Show - 02.mkv",
            ["[Sub] Frieren - 03 [1080p].mkv"] = "Test Show - 01.mkv",
            ["[Sub] Frieren - 03 [1080p].mp4"] = "Test Show - 01.mkv",
            ["[Sub] Frieren - 04 [1080p].mkv"] = "Silent Show - 01.mkv",
            ["Hellsing Ultimate OVA 01.mkv"] = "Test_Show_-_03.mp4",
            ["[Group].mkv"] = "Test_Show_-_03.mp4",
        };
        foreach (var name in media.Keys.Append("[Sub] Frieren - 05 [1080p].mkv").Append("notes.txt"))
        {
            dir.File(name);
        }

        var inspection = await HlsOperation.InspectAsync(dir.Path, new NameFixtureProbe(media), Ct);

        // Порядок — как у путей Windows: по именам в нижнем регистре, «[» раньше букв
        Assert.Equal([HlsOperation.UntitledGroup, "Frieren", "Hellsing Ultimate OVA"], inspection.Groups.Select(g => g.Title));
        var frieren = inspection.Groups[1];
        Assert.Equal(6, frieren.Files.Count);
        Assert.Contains("Invalid data", frieren.Files.Single(f => f.Name.Contains("05", StringComparison.Ordinal)).Error);

        // Раскладки: у 01, 03 (оба) — три дорожки, у 02 — две; без звука и непрочитанный файл в раскладки не входят
        Assert.Equal(2, frieren.Layouts.Count);
        Assert.Equal(3, frieren.Layouts[0].Files.Count);
        Assert.Equal(["AniLibria.TV", "Оригинальная", "DEEP"], frieren.Layouts[0].Tracks.Select(t => t.Title));
        Assert.Equal(["AniLibria.TV", "jpn"], frieren.Layouts[1].Tracks.Select(t => t.Title));

        var plan = HlsOperation.Plan(inspection,
        [
            new HlsGroupOptions
            {
                Group = frieren,
                TitleFolder = HlsOperation.TitleFolder(52991, "Frieren: Beyond"),
                Voices = new Dictionary<string, IReadOnlyList<VoiceChoice>>
                {
                    [frieren.Layouts[0].Layout.Key] = [new(0, "AniLibria.TV"), new(1, null), new(2, "DEEP")],
                    // вторая раскладка без выбора — её файл пропускается
                },
            },
        ]);

        var titleOut = Path.Combine(dir.Path, "hls_multi", "52991 - Frieren_ Beyond");
        Assert.Equal(titleOut, plan.Episodes[0].TitleOut);
        Assert.Equal(
            [
                (PlanItemStatus.Run, "[Sub] Frieren - 01 [1080p]"),
                (PlanItemStatus.Skip, ""),
                (PlanItemStatus.Run, "[Sub] Frieren - 03 [1080p]"),
                (PlanItemStatus.Run, "[Sub] Frieren - 03 [1080p].mp4"),
                (PlanItemStatus.Error, ""),
                (PlanItemStatus.Error, ""),
            ],
            plan.Episodes.Select(e => (e.Status, e.EpisodeName)));
        Assert.Equal(["AniLibria.TV", "DEEP"], plan.Episodes[0].Voices.Select(v => v.Folder));
        Assert.Equal([0, 2], plan.Episodes[0].Voices.Select(v => v.TrackIndex));
        Assert.Contains("озвучки", plan.Episodes[1].Reason);
        Assert.Contains("нет аудиодорожек", plan.Episodes[4].Reason);
        Assert.Contains("не удалось прочитать", plan.Episodes[5].Reason);
        Assert.Equal(Path.Combine(titleOut, "audio", "DEEP", "[Sub] Frieren - 01 [1080p].DEEP.mka"), plan.Episodes[0].AudioPaths[1]);
    }

    [Fact]
    public async Task Done_episode_is_skipped_only_with_zip_and_every_audio()
    {
        using var dir = new TempDir();
        dir.File("Show - 01.mkv");
        dir.File("Show - 02.mkv");
        dir.File("hls_multi/Show/Show - 01.zip", "zip");
        dir.File("hls_multi/Show/audio/AniLibria.TV/Show - 01.AniLibria.TV.mka", "mka");
        dir.File("hls_multi/Show/Show - 02.zip", "zip");
        dir.File("hls_multi/Show/audio/AniLibria.TV/Show - 02.AniLibria.TV.mka", ""); // пустой — не готово
        var probe = new NameFixtureProbe(new Dictionary<string, string> { ["Show - 01.mkv"] = "Test Show - 02.mkv", ["Show - 02.mkv"] = "Test Show - 02.mkv" });
        var inspection = await HlsOperation.InspectAsync(dir.Path, probe, Ct);
        var group = Assert.Single(inspection.Groups);

        var plan = HlsOperation.Plan(inspection, [Options(group, "Show", [new(0, "AniLibria.TV")])]);

        Assert.Equal([PlanItemStatus.Skip, PlanItemStatus.Run], plan.Episodes.Select(e => e.Status));
        Assert.Equal("уже готово", plan.Episodes[0].Reason);
    }

    [Fact]
    public async Task Same_title_folder_from_two_groups_keeps_episode_folders_apart()
    {
        using var dir = new TempDir();
        dir.File("A - 01.mkv");
        dir.File("A - 01.mp4");
        var probe = new NameFixtureProbe(new Dictionary<string, string> { ["A - 01.mkv"] = "Test_Show_-_03.mp4", ["A - 01.mp4"] = "Test_Show_-_03.mp4" });
        var inspection = await HlsOperation.InspectAsync(dir.Path, probe, Ct);
        var files = inspection.Files;

        var plan = HlsOperation.Plan(inspection,
        [
            Options(new HlsGroup("A", [files[0]]), "A", [new(0, "Рус")]),
            Options(new HlsGroup("Другое A", [files[1]]), "A", [new(0, "Рус")]),
        ]);

        Assert.Equal(["A - 01", "A - 01.mp4"], plan.Episodes.Select(e => e.EpisodeName));
    }

    [Fact]
    public void Title_folder_from_shikimori_id_or_group_title()
    {
        Assert.Equal("52991 - Re_Zero", HlsOperation.TitleFolder(52991, "Re:Zero"));
        Assert.Equal("Re_Zero", HlsOperation.TitleFolder(null, " Re:Zero "));
    }

    [Fact]
    public async Task Empty_folder_is_a_plan_error()
    {
        using var dir = new TempDir();
        dir.File("notes.txt");
        await Assert.ThrowsAsync<PlanException>(() => HlsOperation.InspectAsync(dir.Path, new NameFixtureProbe(new Dictionary<string, string>()), Ct));
    }

    internal static HlsGroupOptions Options(HlsGroup group, string titleFolder, IReadOnlyList<VoiceChoice> voices) => new()
    {
        Group = group,
        TitleFolder = titleFolder,
        Voices = group.Layouts.ToDictionary(l => l.Layout.Key, _ => voices),
    };
}
