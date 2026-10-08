using Anitools.App.ViewModels;
using Anitools.App.ViewModels.Pages;
using Anitools.App.Views;
using Anitools.Core.Jobs;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;

namespace Anitools.App.Tests;

public sealed class MainWindowTests
{
    [AvaloniaFact]
    public async Task Opens_on_folder_with_tools_and_navigation()
    {
        using var app = new AppFixture().WithFiles("Sousou no Frieren - 01.mkv");
        var vm = app.CreateViewModel();
        var window = new MainWindow { DataContext = vm };
        window.Show();
        await vm.CheckToolsAsync();
        AppFixture.Flush();

        Assert.Equal(app.Folder, window.FindControl<TextBlock>("FolderText")?.Text);
        Assert.StartsWith("anitools ", window.FindControl<TextBlock>("VersionText")?.Text);
        Assert.Same(vm.VideoOnlyPage, vm.CurrentPage);
        Assert.Equal(["ffmpeg 7.1.1", "mkvmerge 82.0", "NVENC"], vm.ToolChips.Where(c => c.Name != "ImDisk").Select(c => c.Text));
        Assert.All(vm.ToolChips.Where(c => c.Name != "ImDisk"), c => Assert.True(c.Ok, c.Text));
        // Разделы: ВИДЕО, АУДИО, СУБТИТРЫ, ШРИФТЫ, ФАЙЛЫ; внизу — задачи и настройки
        Assert.Equal(["ВИДЕО", "АУДИО", "СУБТИТРЫ", "ШРИФТЫ", "ФАЙЛЫ"], vm.Navigation.OfType<NavHeader>().Select(h => h.Title));
        Assert.Same(vm.SettingsPage, vm.Navigation[^1]);
        Assert.Equal(18, vm.Navigation.OfType<PageViewModel>().Count());
        window.Close();
    }

    [AvaloniaFact]
    public void Section_headers_are_not_selectable()
    {
        using var app = new AppFixture();
        var vm = app.CreateViewModel();

        vm.SelectedNav = vm.Navigation[0];
        AppFixture.Flush();

        Assert.Same(vm.VideoOnlyPage, vm.SelectedNav);
        Assert.Same(vm.VideoOnlyPage, vm.CurrentPage);
    }

    [AvaloniaFact]
    public async Task Browse_and_recent_folders_switch_pages_to_new_folder()
    {
        using var app = new AppFixture().WithFiles("other/Another Show - 01.mkv");
        var other = Path.Combine(app.Folder, "other");
        var vm = app.CreateViewModel();
        vm.SelectedNav = vm.RemuxPage;
        app.Dialogs.NextFolder = other;

        await vm.BrowseCommand.ExecuteAsync(null);
        await AppFixture.WaitUntilAsync(() => vm.RemuxPage.Preview.Rows.Count == 1, "план ремукса в новой папке");

        Assert.Equal(other, vm.Folder);
        Assert.Equal(other, vm.RemuxPage.Folder);
        Assert.Equal([other, app.Folder], vm.RecentFolders);
        Assert.Equal([other, app.Folder], app.Services.Store.Load().Settings.RecentFolders);
        Assert.False(vm.OpenFolder(Path.Combine(app.Root, "нет такой")));
        Assert.StartsWith("Папка не найдена: ", vm.FolderMessage);
        Assert.Equal(other, vm.Folder);
    }

    [AvaloniaFact]
    public void Without_folder_pages_ask_to_choose_one()
    {
        using var app = new AppFixture();
        var vm = new MainWindowViewModel(app.Services, app.Dialogs, Startup.StartupRequest.None);
        AppFixture.Flush();

        Assert.False(vm.HasFolder);
        Assert.Equal(PageViewModel.NoFolderMessage, vm.VideoOnlyPage.Message);
        Assert.StartsWith("Папка не выбрана", vm.FolderDisplay);
    }

    [AvaloniaFact]
    public void Startup_error_is_shown_and_second_instance_request_applies()
    {
        using var app = new AppFixture();
        var vm = new MainWindowViewModel(app.Services, app.Dialogs, new Startup.StartupRequest(null, "Папка не найдена: X:\\нет"));

        Assert.Equal("Папка не найдена: X:\\нет", vm.FolderMessage);
        vm.Apply(new Startup.StartupRequest(app.Folder, null));
        Assert.Equal(app.Folder, vm.Folder);
        Assert.Null(vm.FolderMessage);
    }

    [Fact]
    public void Dropped_file_means_its_folder()
    {
        var dir = Path.Combine(Path.GetTempPath(), "anitools-app-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "Frieren - 01.mkv");
        File.WriteAllText(file, "");
        try
        {
            Assert.Equal(dir, MainWindow.DroppedFolder(dir));
            Assert.Equal(dir, MainWindow.DroppedFolder(file));
            Assert.Null(MainWindow.DroppedFolder(Path.Combine(dir, "нет")));
            Assert.Null(MainWindow.DroppedFolder(null));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task Video_only_runs_checked_files_in_queue_and_replans_after()
    {
        using var app = new AppFixture().WithFiles("Frieren - 01.mkv", "Frieren - 02.mkv", "Frieren - 03.mkv", "Video only/Frieren - 03.mkv");
        var vm = app.CreateViewModel();
        var page = vm.VideoOnlyPage;
        var window = new MainWindow { DataContext = vm };
        window.Show();
        await AppFixture.WaitUntilAsync(() => page.Preview.Rows.Count == 3, "план «Только видео»");

        Assert.Equal([true, true, false], page.Preview.Rows.Select(r => r.CanRun));
        Assert.Equal("уже готово — пропуск", page.Preview.Rows[2].Target);
        Assert.Equal("→ " + Path.Combine("Video only", "Frieren - 01.mkv"), page.Preview.Rows[0].Target);
        Assert.Equal("1,4 ГБ", page.Preview.Rows[0].Size);
        page.Preview.Rows[1].IsChecked = false;
        Assert.Equal(1, page.Preview.CheckedCount);
        Assert.Null(page.Preview.AllChecked);
        Assert.Equal("Запустить (1)", window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "RunButton").Content);

        page.RunCommand.Execute(null);
        var job = Assert.Single(app.Services.Jobs.Jobs);
        await job.Completion.WaitAsync(TestContext.Current.CancellationToken);
        await AppFixture.WaitUntilAsync(() => page.Preview.Rows.Count(r => r.CanRun) == 1, "перестроенный план");

        Assert.Equal(JobState.Done, job.State);
        Assert.StartsWith("Только видео · Sousou no Frieren", job.Title);
        Assert.Equal("1 готово · 2 пропуска", job.Snapshot.Summary);
        var ffmpeg = Assert.Single(app.Runner.Calls, c => c.Arguments.Contains("-map"));
        Assert.Contains(Path.Combine(app.Folder, "Frieren - 01.mkv"), ffmpeg.Arguments);
        // после задачи «01» стала «уже готово», а снятая «02» снова к запуску
        Assert.Equal([false, true, false], page.Preview.Rows.Select(r => r.CanRun));
        Assert.Equal("Запущено — ход работы в «Задачах» и внизу окна.", page.Notice);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Remux_profile_changes_plan()
    {
        using var app = new AppFixture().WithFiles("Frieren - 01.mkv", "Old - 01.avi", "Disc.m2ts");
        var vm = app.CreateViewModel();
        var page = vm.RemuxPage;
        vm.SelectedNav = page;
        await AppFixture.WaitUntilAsync(() => page.Preview.Rows.Count == 3, "план MP4");

        // AVI — обычный вход, с флагами меток времени
        Assert.Contains("+genpts", page.Preview.Plan!.Items.Single(i => i.Label == "Old - 01.avi").Command!.Arguments);

        page.IsM2tsToMkv = true;
        await AppFixture.WaitUntilAsync(() => page.Preview.Rows.Count == 1, "план M2TS → MKV");
        Assert.Equal("Disc.m2ts", page.Preview.Rows[0].Label);
        Assert.Equal("→ Disc.mkv", page.Preview.Rows[0].Target);

        page.IsMkv = true;
        await AppFixture.WaitUntilAsync(() => page.Preview.Rows.Count == 3, "план MKV");
        Assert.Contains("0:s?", page.Preview.Plan!.Items[0].Command!.Arguments);
        page.CopySubtitles = false;
        await AppFixture.WaitUntilAsync(() => !page.Preview.Plan!.Items[0].Command!.Arguments.Contains("0:s?"), "MKV без субтитров");
    }

    [AvaloniaFact]
    public async Task Jobs_page_shows_progress_and_cancels()
    {
        using var app = new AppFixture();
        var vm = app.CreateViewModel();
        var started = new TaskCompletionSource();
        var job = app.Services.Jobs.Enqueue("HLS · Frieren", app.Folder, async context =>
        {
            context.Report(0.41, "серия 3/12 · видео 41% · x2.3");
            started.SetResult();
            await Task.Delay(Timeout.Infinite, context.CancellationToken);
            return new JobOutcome(true, "");
        });
        await started.Task.WaitAsync(TestContext.Current.CancellationToken);
        await AppFixture.WaitUntilAsync(() => vm.JobsPage.Jobs.Count == 1 && vm.JobsPage.Jobs[0].Progress > 40, "задача в списке");

        var item = vm.JobsPage.Jobs[0];
        Assert.Equal("серия 3/12 · видео 41% · x2.3", item.Status);
        Assert.True(item.CanCancel);
        Assert.Equal("1", vm.JobsPage.Badge);
        Assert.Same(item, vm.CurrentJob);
        Assert.Equal("HLS · Frieren: серия 3/12 · видео 41% · x2.3", vm.CurrentJob?.Short);

        item.CancelCommand.Execute(null);
        await job.Completion.WaitAsync(TestContext.Current.CancellationToken);
        await AppFixture.WaitUntilAsync(() => item.State == JobState.Cancelled && vm.CurrentJob is null, "отмена");

        Assert.False(item.CanCancel);
        Assert.Null(vm.JobsPage.Badge);
        Assert.Null(vm.CurrentJob);
        vm.JobsPage.ClearFinishedCommand.Execute(null);
        await AppFixture.WaitUntilAsync(() => vm.JobsPage.Jobs.Count == 0, "очистка");
    }

    [AvaloniaFact]
    public void Settings_are_validated_and_saved()
    {
        using var app = new AppFixture();
        var vm = app.CreateViewModel();
        var settings = vm.SettingsPage;

        settings.FixedCq = "абв";
        settings.SegmentSeconds = "0";
        settings.SaveCommand.Execute(null);
        Assert.Contains("Постоянный CQ: нужно число от 1 до 51.", settings.Message);
        Assert.Contains("Длина сегмента: нужно целое число от 1 до 60.", settings.Message);
        Assert.Equal(21.0, app.Services.Store.Load().Settings.Hls.FixedCq);

        settings.FixedCq = "19,5";
        settings.SegmentSeconds = "4";
        settings.VoicesText = "DEEP\n  JAM \n\nDEEP";
        settings.IsFolder = true;
        settings.SaveCommand.Execute(null);

        Assert.Null(settings.Message);
        var saved = app.Services.Store.Load().Settings;
        Assert.Equal(19.5, saved.Hls.FixedCq);
        Assert.Equal(4, saved.Hls.SegmentSeconds);
        Assert.Equal(["DEEP", "JAM"], saved.Voices);
        Assert.Equal(Core.WorkDir.WorkDirMode.Folder, saved.WorkDir.Mode);
        Assert.Equal(19.5, app.Services.Settings.Hls.FixedCq);

        settings.UseFixedCq = false;
        settings.DefaultsCommand.Execute(null);
        Assert.True(settings.UseFixedCq);
        Assert.Equal("21", settings.FixedCq);
    }

    [AvaloniaFact]
    public void Tool_path_rows_show_what_is_found()
    {
        using var app = new AppFixture();
        var vm = app.CreateViewModel();
        var ffmpeg = vm.SettingsPage.ToolPaths[0];

        Assert.True(ffmpeg.IsFound);
        Assert.EndsWith(Path.Combine("bin", "ffmpeg"), ffmpeg.Found);
        ffmpeg.Configured = Path.Combine(app.Root, "нет", "ffmpeg");
        Assert.False(ffmpeg.IsFound);
        Assert.StartsWith("по этому пути программы нет — пока берётся ", ffmpeg.Found);
        ffmpeg.Configured = Path.Combine(app.Root, "bin");
        Assert.True(ffmpeg.IsFound);
    }
}
