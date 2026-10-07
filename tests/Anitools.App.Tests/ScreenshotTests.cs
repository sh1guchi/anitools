using Anitools.App.Views;
using Anitools.Core.Jobs;
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

    private static void Capture(MainWindow window, string name)
    {
        AppFixture.Flush();
        var frame = Screenshots.Capture(window, name);
        Assert.True(Screenshots.CountColors(frame) > 50, $"Экран «{name}» отрисовался пустым");
        window.Close();
    }
}
