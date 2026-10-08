using System.Net;
using System.Security.Cryptography;
using System.Text;
using Anitools.App.ViewModels;
using Anitools.Core;
using Anitools.Core.Install;
using Avalonia.Headless.XUnit;

namespace Anitools.App.Tests;

/// <summary>Этап 9: обновления anitools и плашка «не хватает программ» (GitHub и сайты программ — фейковые).</summary>
public sealed class UpdatesTests
{
    private const string Latest = "https://api.github.com/repos/sh1guchi/anitools/releases/latest";
    private const string SetupUrl = "https://github.com/sh1guchi/anitools/releases/download/v9.9.9/anitools-setup.exe";

    private static readonly byte[] Setup = Encoding.UTF8.GetBytes("MZ новая версия");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [AvaloniaFact]
    public async Task New_version_is_found_once_a_day_downloaded_and_installed()
    {
        using var app = new AppFixture();
        var github = new FakeGitHub(Release("v9.9.9"));
        string? started = null;
        app.Services.Updates = new UpdateChecker(new HttpClient(github));
        app.Services.IsInstalled = true;
        app.Services.UpdatesFolder = Path.Combine(app.Root, "updates");
        app.Services.RunInstaller = setup =>
        {
            started = setup;
            return true;
        };
        var vm = app.CreateViewModel();
        var exited = false;
        vm.ExitRequested += () => exited = true;

        await vm.CheckUpdatesAsync(manual: false);
        Assert.Equal("9.9.9", vm.Update?.Version);
        Assert.Equal("Обновить до 9.9.9", vm.UpdateText);
        Assert.Contains(vm.Toasts, t => t.Text == "Вышла версия 9.9.9 — «Обновить до 9.9.9» сверху.");
        Assert.NotNull(app.Services.Settings.LastUpdateCheck);

        // сама — не чаще раза в день; выключена в настройках — не проверяет
        await vm.CheckUpdatesAsync(manual: false);
        Assert.Equal(1, github.Requests);

        await vm.InstallUpdateCommand.ExecuteAsync(null);

        Assert.Equal(Path.Combine(app.Root, "updates", "anitools-setup-9.9.9.exe"), started);
        Assert.Equal(Setup, await File.ReadAllBytesAsync(started!, Ct));
        Assert.True(exited); // установщик запущен — anitools закрывается, установщик откроет его снова
        Assert.Contains("Обновить anitools до 9.9.9?", app.Dialogs.Messages[^1], StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task Update_waits_for_jobs_and_portable_exe_opens_the_release_page()
    {
        using var app = new AppFixture();
        app.Services.Updates = new UpdateChecker(new HttpClient(new FakeGitHub(Release("v9.9.9"))));
        app.Services.RunInstaller = _ => throw new InvalidOperationException("установщик не должен запускаться");
        var vm = app.CreateViewModel();
        await vm.CheckUpdatesAsync(manual: true);

        // exe без установки — страница выпуска
        app.Services.IsInstalled = false;
        await vm.InstallUpdateCommand.ExecuteAsync(null);
        Assert.Equal("https://github.com/sh1guchi/anitools/releases/tag/v9.9.9", app.Dialogs.Opened[^1]);

        // идёт задача — не обновляемся
        app.Services.IsInstalled = true;
        var release = new TaskCompletionSource();
        app.Services.Jobs.Enqueue("долгая", app.Folder, async _ =>
        {
            await release.Task;
            return new Core.Jobs.JobOutcome(true, "готово");
        });
        await vm.InstallUpdateCommand.ExecuteAsync(null);
        Assert.Contains(vm.Toasts, t => t.Text == "Дождитесь конца задач: при обновлении anitools закроется.");
        release.SetResult();
    }

    [AvaloniaFact]
    public async Task Manual_check_says_when_there_is_nothing_new()
    {
        using var app = new AppFixture();
        app.Services.Updates = new UpdateChecker(new HttpClient(new FakeGitHub(Release("v" + AppInfo.Version))));
        var vm = app.CreateViewModel();

        await vm.SettingsPage.CheckUpdatesNowCommand.ExecuteAsync(null);

        Assert.Null(vm.Update);
        Assert.Contains(vm.Toasts, t => t.Text == $"У вас последняя версия — {AppInfo.Version}.");

        app.Services.Updates = new UpdateChecker(new HttpClient(new FakeGitHub(null)));
        await vm.CheckUpdatesAsync(manual: true);
        Assert.Contains(vm.Toasts, t => t.Text == "Не удалось узнать о новых версиях — нет связи с GitHub.");
    }

    [AvaloniaFact]
    public async Task Missing_ffmpeg_banner_installs_it_from_settings()
    {
        using var app = new AppFixture();
        File.Delete(Path.Combine(app.Root, "bin", "ffmpeg"));
        app.Services.RefreshTools();
        app.Services.Installer = FakeToolSite.WithFfmpeg("9.0.2").Installer(app.Root);
        var vm = app.CreateViewModel();
        await vm.CheckToolsAsync();

        Assert.Equal("ffmpeg", vm.MissingTools);
        Assert.Equal("Не найден ffmpeg — без него большинство инструментов не работает.", vm.MissingToolsText);
        Assert.Equal("Установить", vm.MissingToolsButtonText);

        await vm.InstallMissingToolsCommand.ExecuteAsync(null);

        Assert.Same(vm.SettingsPage, vm.SelectedNav);
        Assert.Equal(Path.Combine(app.Root, "tools", "ffmpeg-9.0.2", "ffmpeg.exe"), app.Services.Tools.Ffmpeg);
        await AppFixture.WaitUntilAsync(() => vm.MissingTools is null, "плашка ушла после установки");
    }

    [AvaloniaFact]
    public async Task Missing_tools_banner_can_be_hidden_and_points_to_settings_without_installer()
    {
        using var app = new AppFixture();
        File.Delete(Path.Combine(app.Root, "bin", "mkvmerge"));
        File.Delete(Path.Combine(app.Root, "bin", "ffprobe"));
        app.Services.RefreshTools();
        app.Services.Installer = null; // не Windows — ставить нечем
        var vm = app.CreateViewModel();
        await vm.CheckToolsAsync();

        Assert.Equal("ffmpeg и MKVToolNix", vm.MissingTools);
        Assert.Equal("Не найдены ffmpeg и MKVToolNix — без них большинство инструментов не работает.", vm.MissingToolsText);
        Assert.Equal("Настройки", vm.MissingToolsButtonText);
        await vm.InstallMissingToolsCommand.ExecuteAsync(null);
        Assert.Same(vm.SettingsPage, vm.SelectedNav);

        vm.DismissMissingToolsCommand.Execute(null);
        await vm.CheckToolsAsync();
        Assert.Null(vm.MissingTools); // скрыли — до следующего запуска не показывается
    }

    private static string Release(string tag) => $$"""
        {"html_url": "https://github.com/sh1guchi/anitools/releases/tag/{{tag}}", "tag_name": "{{tag}}",
         "assets": [{"name": "anitools-setup.exe", "size": {{Setup.Length}}, "digest": "sha256:{{Convert.ToHexString(SHA256.HashData(Setup))}}",
                     "browser_download_url": "{{SetupUrl}}"}]}
        """;

    /// <summary>GitHub: releases/latest (null — 404) и файл установщика; считает запросы к API.</summary>
    private sealed class FakeGitHub(string? latest) : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.AbsoluteUri;
            if (url == Latest)
            {
                Requests++;
                return Task.FromResult(latest is null
                    ? new HttpResponseMessage(HttpStatusCode.NotFound)
                    : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(latest, Encoding.UTF8, "application/json") });
            }

            return Task.FromResult(url == SetupUrl
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Setup) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
