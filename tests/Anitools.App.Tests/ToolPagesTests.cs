using Anitools.App.ViewModels;
using Anitools.Core.Operations.AudioTools;
using Anitools.Core.Jobs;
using Anitools.Core.Processes;
using Avalonia.Headless.XUnit;

namespace Anitools.App.Tests;

/// <summary>Экраны доп. инструментов (§4.11): что показывают и какие команды строят.</summary>
public sealed class ToolPagesTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [AvaloniaFact]
    public async Task Mka_mux_orders_voices_and_switches_to_single_file()
    {
        using var app = new AppFixture().WithFiles(
            "1. AniLibria/Show - 01.mka", "1. AniLibria/Show - 02.mka", "2. DEEP/Show - 01.mka", "2. DEEP/Show - 02.mka");
        app.Runner.FfprobeJson = _ => """{"streams":[{"index":0,"codec_type":"audio","codec_name":"aac","channels":2}],"format":{}}""";
        var vm = app.CreateViewModel();
        var page = vm.MkaMuxPage;
        vm.SelectedNav = page;
        await AppFixture.WaitUntilAsync(() => page.Labels.Count == 2 && page.Preview.Rows.Count == 2, "озвучки");

        Assert.Equal(["AniLibria", "DEEP"], page.Labels.Select(l => l.Label));
        Assert.Equal("→ " + Path.Combine("MKA", "Show - 01.mka"), page.Preview.Rows[0].Target);
        page.Labels[1].Title = "DEEP 5.1";
        page.MoveUpCommand.Execute(page.Labels[1]);
        var args = page.Preview.Plan!.Items[0].Command!.Arguments;
        Assert.EndsWith(Path.Combine("2. DEEP", "Show - 01.mka"), args[args.ToList().IndexOf("-i") + 1]);
        Assert.Contains("title=DEEP 5.1", args);
        Assert.Contains("language=rus", args);

        page.IsSingleFile = true;
        Assert.Equal("Show - 01", page.SingleName);
        Assert.Single(page.Preview.Rows);
        Assert.Equal(["DEEP", "AniLibria"], page.Labels.Select(l => l.Label));
        page.SetLanguage = false;
        Assert.DoesNotContain(page.Preview.Plan!.Items[0].Command!.Arguments, a => a.StartsWith("language=", StringComparison.Ordinal));
    }

    [AvaloniaFact]
    public async Task Audio_shift_and_convert_build_commands()
    {
        using var app = new AppFixture().WithFiles("Show - 01.mka", "Show - 02.flac");
        var vm = app.CreateViewModel();
        var shift = vm.AudioShiftPage;
        vm.SelectedNav = shift;
        await AppFixture.WaitUntilAsync(() => shift.Preview.Rows.Count == 2, "сдвиг");

        // по умолчанию — без перекодирования (mkvmerge --sync), AAC — по выбору
        Assert.False(shift.Reencode);
        Assert.Contains("-1:1000", shift.Preview.Plan!.Items[0].Command!.Arguments);
        Assert.Equal(Tool.Mkvmerge, shift.Preview.Plan!.Items[0].Command!.Tool);
        shift.Reencode = true;
        Assert.Contains("adelay=delays=1000:all=1", shift.Preview.Plan!.Items[0].Command!.Arguments);
        shift.Seconds = "-0,5";
        Assert.Equal(["-ss", "0.5"], shift.Preview.Plan!.Items[0].Command!.Arguments.SkipWhile(a => a != "-ss").Take(2));
        shift.Seconds = "абв";
        Assert.False(shift.Preview.HasRows);
        Assert.Contains("Сдвиг: нужно число", shift.Message);

        var convert = vm.AudioConvertPage;
        vm.SelectedNav = convert;
        await AppFixture.WaitUntilAsync(() => convert.Preview.Rows.Count == 2, "перекодирование");
        convert.Format = AudioFormat.Flac;
        Assert.False(convert.HasBitrate);
        Assert.Equal("→ " + Path.Combine("converted", "Show - 01.flac"), convert.Preview.Rows[0].Target);
        convert.Channels = "";
        Assert.DoesNotContain("-ac", convert.Preview.Plan!.Items[0].Command!.Arguments);

        // MOV — AAC в QuickTime, только звук: обложка (attached_pic) не переносится
        convert.Format = AudioFormat.Mov;
        Assert.True(convert.HasBitrate);
        Assert.Equal("→ " + Path.Combine("converted", "Show - 01.mov"), convert.Preview.Rows[0].Target);
        Assert.DoesNotContain("attached_pic", convert.Preview.Plan!.Items[0].Command!.Arguments);
        Assert.Equal("MOV · AAC, только звук", Views.Converters.AudioFormatLabel.Convert(AudioFormat.Mov, typeof(string), null, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal("FLAC", Views.Converters.AudioFormatLabel.Convert("Flac", typeof(string), null, System.Globalization.CultureInfo.InvariantCulture));
    }

    [AvaloniaFact]
    public async Task Track_list_copies_titles()
    {
        using var app = new AppFixture().WithFiles("Show - 01.mka");
        app.Runner.FfprobeJson = _ => PagesTests.ThreeVoices;
        var vm = app.CreateViewModel();
        var page = vm.TrackListPage;
        vm.SelectedNav = page;
        await AppFixture.WaitUntilAsync(() => page.Rows.Count == 3, "дорожки");

        Assert.Equal("• AniLibria.TV\n• Оригинальная\n• DEEP\n", page.CopyText);
        page.IsNumbers = true;
        await page.CopyCommand.ExecuteAsync(null);
        Assert.Equal("1. AniLibria.TV\n2. Оригинальная\n3. DEEP\n", app.Dialogs.Clipboard);
        // через запятую: русские озвучки, потом English, потом Original
        page.IsComma = true;
        Assert.Equal("AniLibria.TV, DEEP, Original", page.CopyText);
    }

    [AvaloniaFact]
    public async Task Subtitle_shift_and_style_cleanup_edit_files()
    {
        const string ass = "[Events]\nFormat: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\n"
            + "Dialogue: 0,0:00:01.00,0:00:02.00,Default,,0,0,0,,Привет\n"
            + "Dialogue: 0,0:00:03.00,0:00:04.00,Signs,,0,0,0,,Табличка\n";
        using var app = new AppFixture().WithFiles();
        File.WriteAllText(Path.Combine(app.Folder, "Show - 01.ass"), ass);
        File.WriteAllText(Path.Combine(app.Folder, "Show - 01.srt"), "1\n00:00:01,000 --> 00:00:02,000\nПривет\n");
        var vm = app.CreateViewModel();

        var shift = vm.SubShiftPage;
        vm.SelectedNav = shift;
        await AppFixture.WaitUntilAsync(() => shift.Files.Count == 2, "субтитры");
        shift.Seconds = "2";
        await shift.RunCommand.ExecuteAsync(null);
        Assert.Contains("00:00:03,000 --> 00:00:04,000", File.ReadAllText(Path.Combine(app.Folder, "subs_fixed", "Show - 01.srt")));
        Assert.Contains(vm.Toasts, t => t.Text.StartsWith("Сдвинуто на +2 с: 2 из 2", StringComparison.Ordinal) && t.Kind == ToastKind.Ok);

        var clean = vm.AssEditPage;
        vm.SelectedNav = clean;
        await AppFixture.WaitUntilAsync(() => clean.Values.Count == 2, "стили");
        Assert.Equal(["Default", "Signs"], clean.Values.Select(v => v.Value));
        Assert.Equal(0, clean.RemoveCount);
        // поиск по словам: найденные видны и отмечаются одной кнопкой (Enter)
        clean.Search = "def, нет такого";
        Assert.Equal(1, clean.MatchCount);
        Assert.Equal([true, false], clean.Values.Select(v => v.IsMatch));
        clean.CheckFoundCommand.Execute(null);
        Assert.Equal([true, false], clean.Values.Select(v => v.IsChecked));
        clean.UncheckFoundCommand.Execute(null);
        clean.Search = "SIGN";
        clean.CheckFoundCommand.Execute(null);
        clean.Search = "";
        Assert.All(clean.Values, v => Assert.True(v.IsMatch));
        Assert.Equal([false, true], clean.Values.Select(v => v.IsChecked));
        Assert.Equal(1, clean.RemoveCount);
        Assert.StartsWith("Будет удалено строк: 1 в 1 файле", clean.Summary);

        await clean.RunCommand.ExecuteAsync(null);
        await AppFixture.WaitUntilAsync(() => clean.Values.Count == 1, "после правки");
        var edited = File.ReadAllText(Path.Combine(app.Folder, "Show - 01.ass"));
        Assert.DoesNotContain("Привет", edited);
        Assert.Contains("Табличка", edited);
        Assert.Single(Directory.GetDirectories(app.Folder, "ass_backup_*"));
    }

    [AvaloniaFact]
    public async Task Fonts_jobs_report_results_on_page()
    {
        using var app = new AppFixture().WithFiles("Show - 01.mkv");
        File.WriteAllText(Path.Combine(app.Folder, "Show - 01.ass"),
            "[V4+ Styles]\nFormat: Name, Fontname, Fontsize\nStyle: Default,Zzz Nonexistent Font,48\n[Events]\nDialogue: 0,0:00:01.00,0:00:02.00,Default,,0,0,0,,{\\fnOther Missing}Привет\n");
        var vm = app.CreateViewModel();
        var fonts = vm.AssFontsPage;
        fonts.CustomDir = Path.Combine(app.Root, "custom-fonts");
        fonts.SystemDirs = [Path.Combine(app.Root, "system-fonts")];
        fonts.Download = false;
        vm.SelectedNav = fonts;
        await AppFixture.WaitUntilAsync(() => fonts.FontNames.Count == 2, "шрифты из .ass");
        Assert.Equal(["Other Missing", "Zzz Nonexistent Font"], fonts.FontNames);

        fonts.RunCommand.Execute(null);
        await AppFixture.WaitUntilAsync(() => fonts.Result is not null, "итог сбора");
        Assert.Equal("Архив не собран — нечего паковать.", fonts.Result);
        Assert.Equal(["Other Missing", "Zzz Nonexistent Font"], fonts.NotFound.Select(f => f.Name));
        await fonts.NotFound[0].OpenFontSquirrelCommand.ExecuteAsync(null);
        Assert.StartsWith("https://www.fontsquirrel.com/search?q=Other+Missing", app.Dialogs.Opened[^1]);
        var job = app.Services.Jobs.Jobs[^1];
        Assert.Equal(JobState.Failed, job.State);
        Assert.Equal("в архиве 0 · не найдено 2", job.Snapshot.Summary);

        var video = vm.VideoFontsPage;
        vm.SelectedNav = video;
        await AppFixture.WaitUntilAsync(() => video.Videos.Count == 1, "видео");
        video.RunCommand.Execute(null);
        await AppFixture.WaitUntilAsync(() => video.Result is not null, "итог из видео");
        Assert.Equal("Шрифтов во вложениях нет.", video.Result);
    }

    [AvaloniaFact]
    public async Task Hardsub_pairs_and_final_names()
    {
        using var app = new AppFixture().WithFiles("Show - 01.mkv", "Show - 01.ass", "Show - 02.mkv", "Fonts/a.ttf");
        var vm = app.CreateViewModel();
        var page = vm.HardsubPage;
        vm.SelectedNav = page;
        await AppFixture.WaitUntilAsync(() => page.Preview.Rows.Count == 1, "пары");

        Assert.Equal("→ " + Path.Combine("Hardsub", "Show - 01.mkv"), page.Preview.Rows[0].Target);
        Assert.Equal("Шрифты: папка «Fonts» рядом с видео ✓", page.FontsInfo);
        Assert.Contains("hevc_nvenc", page.Profile);
        await Task.CompletedTask.WaitAsync(Ct);
    }
}
