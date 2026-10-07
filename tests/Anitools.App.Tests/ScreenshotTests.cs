using System.Net;
using System.Text;
using Anitools.App.ViewModels;
using Anitools.App.Views;
using Anitools.App.Views.Dialogs;
using Anitools.Core.Jobs;
using Anitools.Core.Shikimori;
using Avalonia.Headless.XUnit;

namespace Anitools.App.Tests;

/// <summary>Скриншоты экранов для docs/screenshots (ANITOOLS_UPDATE_SCREENSHOTS=1) и проверка, что они не пустые.</summary>
public sealed class ScreenshotTests
{
    private static readonly string[] Episodes = [.. Enumerable.Range(1, 12).Select(n => $"Sousou no Frieren - {n:00}.mkv")];

    [AvaloniaFact]
    public async Task Video_only_page()
    {
        using var app = new AppFixture().WithFiles([.. Episodes, "Video only/Sousou no Frieren - 01.mkv", "Video only/Sousou no Frieren - 02.mkv"]);
        var vm = app.CreateViewModel();
        var window = new MainWindow { DataContext = vm };
        window.Show();
        await vm.CheckToolsAsync();
        await AppFixture.WaitUntilAsync(() => vm.VideoOnlyPage.Preview.Rows.Count == 12, "план");
        vm.VideoOnlyPage.Preview.Rows[5].IsChecked = false;

        Capture(window, "main-window");
    }

    [AvaloniaFact]
    public async Task Remux_page()
    {
        using var app = new AppFixture().WithFiles([.. Episodes.Take(6), "Sousou no Frieren - OVA.avi"]);
        var vm = app.CreateViewModel();
        var window = new MainWindow { DataContext = vm };
        window.Show();
        vm.SelectedNav = vm.RemuxPage;
        vm.RemuxPage.IsMkv = true;
        await vm.CheckToolsAsync();
        await AppFixture.WaitUntilAsync(() => vm.RemuxPage.Preview.Rows.Count == 7 && !vm.RemuxPage.IsLoading, "план");

        Capture(window, "remux");
    }

    [AvaloniaFact]
    public async Task Jobs_page()
    {
        using var app = new AppFixture();
        var vm = app.CreateViewModel();
        var window = new MainWindow { DataContext = vm };
        window.Show();
        await vm.CheckToolsAsync();
        var jobs = app.Services.Jobs;
        jobs.Enqueue("Субтитры → надписи · Sousou no Frieren", app.Folder, context =>
        {
            context.Log("✓ Sousou no Frieren - 01.mkv");
            return Task.FromResult(new JobOutcome(true, "12 готово"));
        });
        jobs.Enqueue("Ремукс в MP4 · Sousou no Frieren", app.Folder, context =>
        {
            context.Log("✗ Sousou no Frieren - 03.mkv — ffmpeg вернул код 1");
            return Task.FromResult(new JobOutcome(false, "2 готово · 1 ошибка"));
        });
        var release = new TaskCompletionSource();
        var hls = jobs.Enqueue("HLS · Sousou no Frieren", app.Folder, async context =>
        {
            context.Report(0.21, "серия 3/12 · видео 41% · x2.3");
            context.SetDetail("01 ✓ 23:41 · 02 ✓ 24:02 · 03 …");
            await release.Task;
            return new JobOutcome(true, "12 готово");
        });
        jobs.Enqueue("Только видео · Sousou no Frieren", app.Folder, _ => Task.FromResult(new JobOutcome(true, "")));
        vm.ShowJobs();
        await AppFixture.WaitUntilAsync(() => vm.JobsPage.Jobs.Count == 4 && vm.JobsPage.Jobs[2].Progress > 20, "задачи");
        vm.JobsPage.Jobs[1].IsLogOpen = true;
        AppFixture.Flush();

        Capture(window, "jobs");
        release.SetResult();
        await hls.Completion.WaitAsync(TestContext.Current.CancellationToken);
    }

    [AvaloniaFact]
    public async Task Settings_page()
    {
        using var app = new AppFixture();
        var vm = app.CreateViewModel();
        var window = new MainWindow { DataContext = vm };
        window.Show();
        await vm.CheckToolsAsync();
        vm.SelectedNav = vm.SettingsPage;
        AppFixture.Flush();

        Capture(window, "settings");
    }

    [AvaloniaFact]
    public async Task Audio_extract_page()
    {
        using var app = new AppFixture().WithFiles([.. Episodes]);
        app.Runner.FfprobeJson = _ => PagesTests.ThreeVoices;
        var (vm, window) = await OpenAsync(app);
        var page = vm.AudioExtractPage;
        vm.SelectedNav = page;
        await AppFixture.WaitUntilAsync(() => page.Tracks.Count == 3, "дорожки");
        page.Tracks[2].IsSelected = true;
        page.IsSingleMka = true;
        page.OutputTracks[0].Title = "AniLiberty (AniLibria)";

        Capture(window, "audio-extract");
    }

    [AvaloniaFact]
    public async Task Audio_mux_page()
    {
        using var app = new AppFixture().WithFiles(
        [
            .. Episodes.Take(4),
            .. Episodes.Take(3).Select(e => $"Audio only/2. DEEP/2. {Path.GetFileNameWithoutExtension(e)}.DEEP.mka"),
        ]);
        app.Runner.FfprobeJson = path => path.EndsWith(".mka", StringComparison.Ordinal)
            ? """{"streams":[{"index":0,"codec_type":"audio","codec_name":"ac3","channels":6,"tags":{"title":"DEEP"}}],"format":{}}"""
            : PagesTests.ThreeVoices;
        var (vm, window) = await OpenAsync(app);
        var page = vm.AudioMuxPage;
        vm.SelectedNav = page;
        await AppFixture.WaitUntilAsync(() => page.Sets.Count == 2 && !page.IsAnalyzing, "наборы");
        page.SourceTracks[1].IsSelected = false;
        await AppFixture.WaitUntilAsync(() => page.Sets.Count == 2 && page.Sets[0].Slots.Count == 3 && !page.IsAnalyzing, "без оригинала");

        Capture(window, "audio-mux");
    }

    [AvaloniaFact]
    public async Task Subtitles_page()
    {
        using var app = new AppFixture().WithFiles([.. Episodes]);
        app.Runner.MkvmergeJson = path => path.EndsWith("05.mkv", StringComparison.Ordinal)
            ? """{"container":{"type":"Matroska"},"tracks":[{"id":2,"type":"subtitles","codec":"SubStationAlpha","properties":{"codec_id":"S_TEXT/ASS","track_name":"Signs","language":"eng"}}],"attachments":[]}"""
            : """
              {"container":{"type":"Matroska"},"tracks":[
                {"id":3,"type":"subtitles","codec":"SubStationAlpha","properties":{"codec_id":"S_TEXT/ASS","track_name":"Надписи","language":"rus"}},
                {"id":4,"type":"subtitles","codec":"SubStationAlpha","properties":{"codec_id":"S_TEXT/ASS","track_name":"Полные","language":"rus"}},
                {"id":5,"type":"subtitles","codec":"SubRip/SRT","properties":{"codec_id":"S_TEXT/UTF8","track_name":"English","language":"eng"}}
              ],"attachments":[]}
              """;
        var (vm, window) = await OpenAsync(app);
        var page = vm.SubtitlesPage;
        vm.SelectedNav = page;
        await AppFixture.WaitUntilAsync(() => page.Tracks.Count == 3 && page.Preview.Rows.Count == 12, "план");
        page.IsByTitle = true;
        await AppFixture.WaitUntilAsync(() => page.Preview.Rows.Any(r => r.IsSkip) && !page.IsPlanning, "по тайтлу");

        Capture(window, "subtitles");
    }

    [AvaloniaFact]
    public async Task Rename_page()
    {
        using var app = new AppFixture().WithFiles(
        [
            .. Enumerable.Range(1, 8).Select(n => $"[SubsPlease] Sousou no Frieren - {n:00} (1080p) [ABCD1234].mkv"),
            "Sousou no Frieren - 09.mkv",
            "NCOP.mkv",
        ]);
        var (vm, window) = await OpenAsync(app);
        var page = vm.RenamePage;
        vm.SelectedNav = page;
        await AppFixture.WaitUntilAsync(() => page.Rows.Count == 10, "строки");
        page.Rows.Single(r => r.File.StartsWith("Sousou", StringComparison.Ordinal)).Episode = "10";

        Capture(window, "rename");
    }

    [AvaloniaFact]
    public async Task Shikimori_dialog()
    {
        const string results = """
            [{"id":52991,"name":"Sousou no Frieren","russian":"Провожающая в последний путь Фрирен","kind":"tv","episodes":28,"aired_on":"2023-09-29"},
             {"id":59978,"name":"Sousou no Frieren 2nd Season","russian":"Провожающая в последний путь Фрирен 2","kind":"tv","episodes":0,"aired_on":"2026-01-16"},
             {"id":56805,"name":"Sousou no Frieren: ●● no Mahou","russian":"Провожающая в последний путь Фрирен: Магия ●●","kind":"special","episodes":0,"aired_on":"2023-10-06"},
             {"id":57000,"name":"Sousou no Frieren Recap","russian":"","kind":"tv_special","episodes":1,"aired_on":null}]
            """;
        using var http = new HttpClient(new Answer(results));
        var picker = new ShikimoriPickerViewModel(new ShikimoriClient(http, null, (_, _) => Task.CompletedTask), "Sousou no Frieren");
        var dialog = new ShikimoriDialog(picker);
        dialog.Show();
        await AppFixture.WaitUntilAsync(() => picker.Results.Count == 4, "результаты");

        AppFixture.Flush();
        var frame = Screenshots.Capture(dialog, "shikimori");
        Assert.True(Screenshots.CountColors(frame) > 50);
        dialog.Close();
    }

    [AvaloniaFact]
    public async Task Hls_page()
    {
        using var app = new AppFixture().WithFiles([.. Episodes, "Sousou no Frieren OVA - 01.mkv", "hls_multi/52991 - Sousou no Frieren/Sousou no Frieren - 01.zip"]);
        app.Runner.FfprobeJson = path => path.EndsWith("07.mkv", StringComparison.Ordinal)
            ? PagesTests.ThreeVoices.Replace("\"DEEP\"", "\"Commentary\"", StringComparison.Ordinal)
            : PagesTests.ThreeVoices;
        var (vm, window) = await OpenAsync(app);
        var page = vm.HlsPage;
        page.IsNearOutput = true;
        vm.SelectedNav = page;
        await AppFixture.WaitUntilAsync(() => page.Groups.Count == 2 && page.Preview.Rows.Count == 13, "план HLS");
        var main = page.Groups[0];
        app.Dialogs.NextShikimori = new ShikimoriChoice(new ShikimoriAnime(52991, "Sousou no Frieren", "Фрирен", "2023", "tv", 28), null);
        await main.PickShikimoriCommand.ExecuteAsync(null);
        main.Layouts[1].Rows[2].IsTaken = false;
        page.Groups[1].IsIncluded = false;

        Capture(window, "hls");
    }

    [AvaloniaFact]
    public async Task Regroup_dialog()
    {
        using var app = new AppFixture().WithFiles([.. Episodes.Take(4), "Sousou no Frieren OVA - 01.mkv", "NCOP.mkv"]);
        app.Runner.FfprobeJson = _ => PagesTests.ThreeVoices;
        var inspection = await Anitools.Core.Operations.Hls.HlsOperation.InspectAsync(app.Folder, app.Services.Probe, TestContext.Current.CancellationToken);
        var regroup = new RegroupViewModel(inspection.Files, inspection.Groups);
        regroup.Rows.Single(r => r.Name == "NCOP.mkv").Group = "";
        var dialog = new RegroupDialog(regroup);
        dialog.Show();
        AppFixture.Flush();

        var frame = Screenshots.Capture(dialog, "regroup");
        Assert.True(Screenshots.CountColors(frame) > 50);
        dialog.Close();
    }

    private static async Task<(MainWindowViewModel Vm, MainWindow Window)> OpenAsync(AppFixture app)
    {
        var vm = app.CreateViewModel();
        var window = new MainWindow { DataContext = vm };
        window.Show();
        await vm.CheckToolsAsync();
        return (vm, window);
    }

    private sealed class Answer(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
    }

    private static void Capture(MainWindow window, string name)
    {
        AppFixture.Flush();
        var frame = Screenshots.Capture(window, name);
        Assert.True(Screenshots.CountColors(frame) > 50, $"Экран «{name}» отрисовался пустым");
        window.Close();
    }
}
