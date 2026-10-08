using Anitools.App.ViewModels;
using Anitools.App.Views;
using Anitools.App.Views.Dialogs;
using Anitools.Core.Jobs;
using Anitools.Core.Shikimori;
using Avalonia.Controls;
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
    public async Task Narrow_window_collapses_tools_into_one_chip()
    {
        using var app = new AppFixture().WithFiles([.. Episodes.Take(4)]);
        var (vm, window) = await OpenAsync(app);
        var chips = window.FindControl<ItemsControl>("ToolChipsList")!;
        var summary = window.FindControl<Border>("ToolsSummaryChip")!;
        AppFixture.Flush();
        Assert.True(chips.IsVisible);
        Assert.False(summary.IsVisible);

        window.Width = 1000;
        var release = new TaskCompletionSource();
        app.Services.Jobs.Enqueue("HLS · Sousou no Frieren", app.Folder, async context =>
        {
            context.Report(0.41, "серия 5/12 · видео 41%");
            await release.Task;
            return new JobOutcome(true, "");
        });
        await AppFixture.WaitUntilAsync(() => vm.CurrentJob is { Progress: > 40 }, "задача в шапке");

        Assert.False(chips.IsVisible);
        Assert.True(summary.IsVisible);
        // чего нет — по именам (на Windows без ImDisk — «ImDisk»), всё есть — «программы»
        var missing = vm.ToolChips.Where(c => !c.Ok).Select(c => c.Name).ToList();
        Assert.Equal(missing.Count == 0 ? "программы" : string.Join(", ", missing), vm.ToolsSummary);
        Assert.Equal(missing.Count == 0, vm.ToolsOk);
        Capture(window, "main-window-narrow");
        release.SetResult();
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
        app.Services.Installer = FakeToolSite.WithFfmpeg("9.0.2", mkvToolNixHangs: true).Installer(app.Root);
        var vm = app.CreateViewModel();
        var window = new MainWindow { DataContext = vm };
        window.Show();
        await vm.CheckToolsAsync();
        vm.SelectedNav = vm.SettingsPage;
        var packages = vm.SettingsPage.Packages;
        await AppFixture.WaitUntilAsync(() => packages[0].Latest is not null, "последняя версия");
        var installing = packages[1].InstallCommand.ExecuteAsync(null); // MKVToolNix «качается» — виден ход и «Отмена»
        await AppFixture.WaitUntilAsync(() => packages[1].IsInstalling, "установка");
        AppFixture.Flush();

        Capture(window, "settings");
        packages[1].CancelInstallCommand.Execute(null);
        await installing;
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
        await AppFixture.WaitUntilAsync(() => page.Tracks.Count == 3 && page.Preview.Rows.Count == 24, "план");
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
            {"data":{"animes":[
              {"id":"52991","name":"Sousou no Frieren","russian":"Провожающая в последний путь Фрирен","english":"Frieren: Beyond Journey's End",
               "kind":"tv","status":"released","episodes":28,"episodesAired":28,"airedOn":{"year":2023},"score":9.29,"duration":24,
               "poster":{"mainUrl":"https://shikimori.io/p/52991.webp"},
               "genres":[{"russian":"Приключения"},{"russian":"Драма"},{"russian":"Фэнтези"},{"russian":"Сёнэн"}],"studios":[{"name":"Madhouse"}],
               "description":"Десять лет [character=1]Фрирен[/character] странствовала с отрядом героя и победила Короля демонов.[br][br]Для эльфийки это лишь миг. Когда спутники начинают уходить один за другим, она понимает, как мало знала о них, и отправляется в новое путешествие — чтобы понять людей."},
              {"id":"59978","name":"Sousou no Frieren 2nd Season","russian":"Провожающая в последний путь Фрирен 2","english":"Frieren: Beyond Journey's End Season 2",
               "kind":"tv","status":"ongoing","episodes":10,"episodesAired":4,"airedOn":{"year":2026},"score":9.1,"duration":24,
               "poster":{"mainUrl":"https://shikimori.io/p/59978.webp"},"genres":[{"russian":"Приключения"}],"studios":[{"name":"Madhouse"}],"description":"Продолжение пути на север."},
              {"id":56805,"name":"Sousou no Frieren: ●● no Mahou","russian":"Провожающая в последний путь Фрирен: Магия ●●","kind":"special",
               "status":"released","episodes":10,"episodesAired":10,"airedOn":{"year":2023},"score":7.6,"duration":2,
               "poster":{"mainUrl":"https://shikimori.io/p/56805.webp"},"genres":[],"studios":[],"description":null},
              {"id":"57000","name":"Sousou no Frieren Recap","russian":"","kind":"tv_special","status":"released","episodes":1,"airedOn":null,"score":0,"poster":null}
            ]}}
            """;
        var site = new FakeShikimori(results)
        {
            Posters =
            {
                ["https://shikimori.io/p/52991.webp"] = FakeShikimori.Poster(0xFF8FB8E8, 0xFF2E4A6B),
                ["https://shikimori.io/p/59978.webp"] = FakeShikimori.Poster(0xFFE8C48F, 0xFF6B4A2E),
                ["https://shikimori.io/p/56805.webp"] = FakeShikimori.Poster(0xFFB394FF, 0xFF3A2470),
            },
        };
        using var http = new HttpClient(site);
        var picker = new ShikimoriPickerViewModel(new ShikimoriClient(http, null, (_, _) => Task.CompletedTask), "Sousou no Frieren");
        var dialog = new ShikimoriDialog(picker);
        dialog.Show();
        await AppFixture.WaitUntilAsync(() => picker.Results.Count == 4 && picker.Results.Count(r => r.Poster is not null) == 3, "результаты и постеры");

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

    [AvaloniaFact]
    public async Task Mka_mux_page()
    {
        using var app = new AppFixture().WithFiles(
        [
            .. Episodes.Take(6).Select(e => $"1. AniLibria.TV/{Path.GetFileNameWithoutExtension(e)}.mka"),
            .. Episodes.Take(6).Select(e => $"2. DEEP/{Path.GetFileNameWithoutExtension(e)}.mka"),
            .. Episodes.Take(6).Select(e => $"3. Original/{Path.GetFileNameWithoutExtension(e)}.mka"),
        ]);
        app.Runner.FfprobeJson = _ => """{"streams":[{"index":0,"codec_type":"audio","codec_name":"aac","channels":2}],"format":{}}""";
        var (vm, window) = await OpenAsync(app);
        var page = vm.MkaMuxPage;
        vm.SelectedNav = page;
        await AppFixture.WaitUntilAsync(() => page.Labels.Count == 3 && page.Preview.Rows.Count == 6, "озвучки");
        page.Labels[2].Title = "Оригинальная";

        Capture(window, "mka-mux");
    }

    [AvaloniaFact]
    public async Task Audio_shift_page()
    {
        using var app = new AppFixture().WithFiles([.. Episodes.Take(8).Select(e => Path.ChangeExtension(e, ".mka")), "audio_fixed/Sousou no Frieren - 01.mka"]);
        var (vm, window) = await OpenAsync(app);
        vm.SelectedNav = vm.AudioShiftPage;
        vm.AudioShiftPage.Seconds = "-1.5";
        await AppFixture.WaitUntilAsync(() => vm.AudioShiftPage.Preview.Rows.Count == 8, "план");

        Capture(window, "audio-shift");
    }

    [AvaloniaFact]
    public async Task Audio_convert_page()
    {
        using var app = new AppFixture().WithFiles([.. Episodes.Take(6).Select(e => Path.ChangeExtension(e, ".flac")), "OST - 01.wav"]);
        var (vm, window) = await OpenAsync(app);
        vm.SelectedNav = vm.AudioConvertPage;
        vm.AudioConvertPage.Format = Anitools.Core.Operations.AudioTools.AudioFormat.Mov;
        await AppFixture.WaitUntilAsync(() => vm.AudioConvertPage.Preview.Rows.Count == 7, "план");

        Capture(window, "audio-convert");
    }

    [AvaloniaFact]
    public async Task Track_list_page()
    {
        using var app = new AppFixture().WithFiles("MKA/Sousou no Frieren - 01.mka", "Sousou no Frieren - 01.mka", "Sousou no Frieren - 02.mka");
        app.Runner.FfprobeJson = _ => PagesTests.ThreeVoices;
        var (vm, window) = await OpenAsync(app);
        vm.SelectedNav = vm.TrackListPage;
        await AppFixture.WaitUntilAsync(() => vm.TrackListPage.Rows.Count == 3, "дорожки");

        Capture(window, "track-list");
    }

    [AvaloniaFact]
    public async Task Sub_shift_page()
    {
        using var app = new AppFixture().WithFiles([.. Episodes.Take(6).Select(e => Path.ChangeExtension(e, ".ass")), .. Episodes.Take(3).Select(e => Path.ChangeExtension(e, ".srt"))]);
        var (vm, window) = await OpenAsync(app);
        vm.SelectedNav = vm.SubShiftPage;
        await AppFixture.WaitUntilAsync(() => vm.SubShiftPage.Files.Count == 9, "файлы");

        Capture(window, "sub-shift");
    }

    [AvaloniaFact]
    public async Task Ass_edit_page()
    {
        using var app = new AppFixture();
        string[] styles = ["Default", "Default - Italic", "Signs", "Signs - Top", "OP Romaji", "OP Russian", "ED Romaji", "ED Russian", "Notes"];
        for (var i = 1; i <= 6; i++)
        {
            var lines = string.Concat(styles.Select((style, n) => $"Dialogue: 0,0:0{n}:01.00,0:0{n}:02.00,{style},,0,0,0,,Строка {n}\n"));
            File.WriteAllText(Path.Combine(app.Folder, $"Sousou no Frieren - {i:00}.ass"),
                "[Events]\nFormat: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\n" + lines);
        }

        var (vm, window) = await OpenAsync(app);
        var page = vm.AssEditPage;
        vm.SelectedNav = page;
        await AppFixture.WaitUntilAsync(() => page.Values.Count == styles.Length, "стили");
        foreach (var value in page.Values.Where(v => v.Value.StartsWith("Default", StringComparison.Ordinal) || v.Value.StartsWith("Signs", StringComparison.Ordinal)))
        {
            value.IsChecked = true;
        }

        Capture(window, "ass-edit");
    }

    [AvaloniaFact]
    public async Task Ass_fonts_page()
    {
        using var app = new AppFixture();
        string[] fonts = ["Arial", "Times New Roman", "Komika Axis", "Calibri", "Segoe Print", "Trebuchet MS", "Georgia", "Bad Script", "Marck Script", "Ubuntu"];
        File.WriteAllText(Path.Combine(app.Folder, "Sousou no Frieren - 01.ass"),
            "[V4+ Styles]\nFormat: Name, Fontname, Fontsize\n" + string.Concat(fonts.Select((f, i) => $"Style: S{i},{f},48\n")));
        var (vm, window) = await OpenAsync(app);
        vm.SelectedNav = vm.AssFontsPage;
        await AppFixture.WaitUntilAsync(() => vm.AssFontsPage.FontNames.Count == fonts.Length, "шрифты");

        Capture(window, "ass-fonts");
    }

    [AvaloniaFact]
    public async Task Video_fonts_page()
    {
        using var app = new AppFixture().WithFiles([.. Episodes]);
        var (vm, window) = await OpenAsync(app);
        vm.SelectedNav = vm.VideoFontsPage;
        await AppFixture.WaitUntilAsync(() => vm.VideoFontsPage.Videos.Count == 12, "видео");

        Capture(window, "video-fonts");
    }

    [AvaloniaFact]
    public async Task Hardsub_page()
    {
        using var app = new AppFixture().WithFiles([.. Episodes.Take(6), .. Episodes.Take(5).Select(e => Path.ChangeExtension(e, ".ass")), "Fonts/KOMIKAX_.ttf", "Hardsub/Sousou no Frieren - 01.mkv"]);
        var (vm, window) = await OpenAsync(app);
        vm.SelectedNav = vm.HardsubPage;
        await AppFixture.WaitUntilAsync(() => vm.HardsubPage.Preview.Rows.Count == 5, "пары");

        Capture(window, "hardsub");
    }

    private static async Task<(MainWindowViewModel Vm, MainWindow Window)> OpenAsync(AppFixture app)
    {
        var vm = app.CreateViewModel();
        var window = new MainWindow { DataContext = vm };
        window.Show();
        await vm.CheckToolsAsync();
        return (vm, window);
    }

    private static void Capture(MainWindow window, string name)
    {
        AppFixture.Flush();
        var frame = Screenshots.Capture(window, name);
        Assert.True(Screenshots.CountColors(frame) > 50, $"Экран «{name}» отрисовался пустым");
        window.Close();
    }
}
