using Anitools.App.ViewModels;
using Anitools.Core.Jobs;
using Anitools.Core.Operations.Common;
using Anitools.Core.Shikimori;
using Avalonia.Headless.XUnit;

namespace Anitools.App.Tests;

public sealed class HlsPageTests
{
    private const string TwoTracks = """
        {"streams":[{"index":0,"codec_type":"video","codec_name":"hevc","width":3840,"height":2160},
          {"index":1,"codec_type":"audio","codec_name":"aac","channels":2,"tags":{"title":"AniLibria.TV","language":"rus"}},
          {"index":2,"codec_type":"audio","codec_name":"flac","channels":2,"tags":{"title":"Japanese","language":"jpn"}}],
         "format":{"duration":"1420.0"}}
        """;

    private const string ThreeTracks = """
        {"streams":[{"index":0,"codec_type":"video","codec_name":"hevc","width":3840,"height":2160},
          {"index":1,"codec_type":"audio","codec_name":"aac","channels":2,"tags":{"title":"AniLibria.TV","language":"rus"}},
          {"index":2,"codec_type":"audio","codec_name":"flac","channels":2,"tags":{"title":"Japanese","language":"jpn"}},
          {"index":3,"codec_type":"audio","codec_name":"aac","channels":2,"tags":{"title":"Commentary","language":"jpn"}}],
         "format":{"duration":"1420.0"}}
        """;

    [AvaloniaFact]
    public async Task Groups_layouts_shikimori_regroup_and_run()
    {
        using var app = new AppFixture().WithFiles("Sousou no Frieren - 01.mkv", "Sousou no Frieren - 02.mkv", "Other Show - 01.mkv");
        app.Runner.FfprobeJson = path => path.EndsWith("Frieren - 02.mkv", StringComparison.Ordinal) ? ThreeTracks : TwoTracks;
        var vm = app.CreateViewModel();
        var page = vm.HlsPage;
        vm.SelectedNav = page;
        await AppFixture.WaitUntilAsync(() => page.Groups.Count == 2 && page.Preview.Rows.Count == 3, "группы");

        var (other, frieren) = (page.Groups[0], page.Groups[1]);
        Assert.Equal(("Other Show", "Sousou no Frieren"), (other.Title, frieren.Title));
        Assert.Equal(2, frieren.Layouts.Count);
        Assert.StartsWith("Набор дорожек 2: 1 файл из 2", frieren.Layouts[1].Header);
        Assert.Equal(["AniLibria.TV", "Japanese", "Commentary"], frieren.Layouts[1].Rows.Select(r => r.Voice));

        other.IsIncluded = false;
        frieren.Layouts[1].Rows[2].IsTaken = false;
        frieren.Layouts[0].Rows[1].Voice = "Оригинальная";
        frieren.Layouts[1].Rows[1].Voice = "Оригинальная";
        app.Dialogs.NextShikimori = new ShikimoriChoice(new ShikimoriAnime(52991, "Sousou no Frieren", "Фрирен", "2023", "tv", 28), null);
        await frieren.PickShikimoriCommand.ExecuteAsync(null);

        Assert.Equal("52991 - Sousou no Frieren", frieren.TitleFolder);
        Assert.Equal(2, page.Preview.Rows.Count);
        Assert.Equal("Sousou no Frieren - 02.mkv · AniLibria.TV, Оригинальная", page.Preview.Rows[1].Label);
        Assert.Equal("→ " + Path.Combine("hls_multi", "52991 - Sousou no Frieren", "Sousou no Frieren - 02.zip"), page.Preview.Rows[1].Target);
        Assert.StartsWith("2 серии × 6 качеств → " + Path.Combine("hls_multi", "52991 - Sousou no Frieren"), page.Summary);

        // перегруппировка: Other Show — к Frieren, остальное как было
        app.Dialogs.Regroup = regroup =>
        {
            regroup.Rows.Single(r => r.Name == "Other Show - 01.mkv").Group = "Sousou no Frieren";
            Assert.Equal(["Sousou no Frieren"], regroup.GroupNames);
            return true;
        };
        await page.RegroupCommand.ExecuteAsync(null);
        Assert.Equal("Sousou no Frieren", Assert.Single(page.Groups).Title);
        Assert.Equal(3, page.Preview.Rows.Count);

        // запуск: сводка → задача; ffmpeg держим, чтобы увидеть ход, потом отменяем
        page.IsNearOutput = true;
        page.Groups[0].Layouts[0].Rows[1].Voice = "Оригинальная";
        app.Runner.Gate = new TaskCompletionSource();
        await page.RunCommand.ExecuteAsync(null);

        Assert.Contains("3 серии × 6 качеств", app.Dialogs.Messages[^1]);
        Assert.Contains("Временные файлы: рядом с выходом.", app.Dialogs.Messages[^1]);
        var job = Assert.Single(app.Services.Jobs.Jobs);
        Assert.Equal("HLS · Sousou no Frieren", job.Title);
        await AppFixture.WaitUntilAsync(() => job.Snapshot.Status.StartsWith("серия 1/3 · Other Show - 01 · видео", StringComparison.Ordinal), "ход HLS");
        job.Cancel();
        await job.Completion.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(JobState.Cancelled, job.State);
        Assert.Contains(job.Log, l => l.Contains("Временные файлы: рядом с выходом", StringComparison.Ordinal));
    }

    [AvaloniaFact]
    public async Task Ram_disk_without_imdisk_warns()
    {
        using var app = new AppFixture().WithFiles("Sousou no Frieren - 01.mkv");
        app.Runner.FfprobeJson = _ => TwoTracks;
        var vm = app.CreateViewModel();
        var page = vm.HlsPage;
        page.IsRamDisk = true;
        vm.SelectedNav = page;
        await AppFixture.WaitUntilAsync(() => page.Preview.Rows.Count == 1, "план");

        Assert.Contains("ImDisk не найден", page.Warning);
        page.IsNearOutput = true;
        Assert.Null(page.Warning);
        Assert.Equal(PlanItemStatus.Run, page.Preview.Rows[0].Item.Status);
    }
}
