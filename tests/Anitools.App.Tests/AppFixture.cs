using Anitools.App.Services;
using Anitools.App.ViewModels;
using Anitools.Core.Processes;
using Anitools.Core.Settings;
using Avalonia.Threading;

namespace Anitools.App.Tests;

/// <summary>
/// Приложение для тестов: временная рабочая папка «Sousou no Frieren», свои настройки и логи, фейковые программы
/// (пустые файлы в своей папке bin — их находит ToolLocator) и фейковый запуск процессов.
/// </summary>
internal sealed class AppFixture : IDisposable
{
    public AppFixture(string folderName = "Sousou no Frieren")
    {
        Root = Path.Combine(Path.GetTempPath(), "anitools-app-tests", Guid.NewGuid().ToString("N")[..8]);
        Folder = Path.Combine(Root, "anime", folderName);
        Directory.CreateDirectory(Folder);
        var bin = Path.Combine(Root, "bin");
        Directory.CreateDirectory(bin);
        foreach (var tool in new[] { "ffmpeg", "ffprobe", "mkvmerge", "mkvextract" })
        {
            File.WriteAllText(Path.Combine(bin, tool), "");
        }

        var environment = new ToolEnvironment { GetVariable = name => name == "PATH" ? bin : null, IsWindows = false };
        Services = new AppServices(
            new SettingsStore(Path.Combine(Root, "config", "settings.json")),
            Runner,
            new ToolLocator(environment),
            new HttpClient(new NoNetwork()),
            Path.Combine(Root, "logs"));
    }

    public string Root { get; }

    public string Folder { get; }

    public FakeRunner Runner { get; } = new();

    public FakeDialogs Dialogs { get; } = new();

    public AppServices Services { get; }

    /// <summary>Файлы рабочей папки (пустые).</summary>
    public AppFixture WithFiles(params string[] names)
    {
        foreach (var name in names)
        {
            var path = Path.Combine(Folder, name.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "x");
        }

        return this;
    }

    /// <summary>Окно на рабочей папке; размеры файлов — «как у настоящих серий» (1,4 ГБ), без гигабайтных файлов.</summary>
    public MainWindowViewModel CreateViewModel(string? folder = null) =>
        new(Services, Dialogs, new Startup.StartupRequest(folder ?? Folder, null), _ => 1_503_238_553);

    public void Dispose()
    {
        Services.Jobs.CancelAll();
        Services.Dispose();
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Выполнить отложенное в UI-потоке (обновления от задач и т.п.).</summary>
    public static void Flush() => Dispatcher.UIThread.RunJobs();

    /// <summary>Ждать условия, прокручивая UI-поток (задачи очереди идут в фоне).</summary>
    public static async Task WaitUntilAsync(Func<bool> condition, string what)
    {
        for (var i = 0; i < 500; i++)
        {
            Flush();
            if (condition())
            {
                return;
            }

            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        throw new TimeoutException("Не дождались: " + what);
    }

    private sealed class NoNetwork : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("в тестах нет сети");
    }
}

/// <summary>
/// Фейковые программы: ffmpeg пишет выходной файл (последний аргумент), версии и NVENC — как у сборки gyan.dev.
/// <see cref="Gate"/> — держать запуск, пока тест не отпустит.
/// </summary>
internal sealed class FakeRunner : IProcessRunner
{
    private readonly object _lock = new();

    public List<ProcessSpec> Calls { get; } = [];

    public TaskCompletionSource? Gate { get; set; }

    public int ExitCode { get; set; }

    public async Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            Calls.Add(spec);
        }

        var program = Path.GetFileName(spec.FileName);
        switch (spec.Arguments)
        {
            case ["-hide_banner", "-version"]:
                return Ok("ffmpeg version 7.1.1-full_build-www.gyan.dev Copyright (c) 2000-2025 the FFmpeg developers");
            case ["-hide_banner", "-encoders"]:
                return Ok(" V....D h264_nvenc           NVIDIA NVENC H.264 encoder (codec h264)");
            case ["-hide_banner", "-filters"]:
                return Ok(" ... scale_cuda        V->V       GPU accelerated video resizer");
            case ["--version"]:
                return Ok("mkvmerge v82.0 ('I'm The President') 64-bit");
        }

        if (program == "ffprobe")
        {
            return Ok("""{"streams":[],"format":{"duration":"1420.0"}}""");
        }

        if (Gate is { } gate)
        {
            await gate.Task.WaitAsync(cancellationToken);
        }

        if (ExitCode == 0 && spec.Arguments.Count > 0)
        {
            File.WriteAllText(spec.Arguments[^1], "out");
        }

        return new ProcessResult(ExitCode, "", ExitCode == 0 ? "" : "Invalid data found when processing input", TimeSpan.Zero);
    }

    private static ProcessResult Ok(string output) => new(0, output, "", TimeSpan.Zero);
}

/// <summary>Диалоги без окон: ответы задаются заранее, действия записываются.</summary>
internal sealed class FakeDialogs : IDialogService
{
    public string? NextFolder { get; set; }

    public string? NextFile { get; set; }

    public bool ConfirmResult { get; set; } = true;

    public List<string> Opened { get; } = [];

    public List<string> Messages { get; } = [];

    public string? Clipboard { get; private set; }

    public Task<string?> PickFolderAsync(string title, string? start = null) => Task.FromResult(NextFolder);

    public Task<string?> PickFileAsync(string title, string? start = null) => Task.FromResult(NextFile);

    public Task<bool> ConfirmAsync(string title, string message, string confirm = "Да", string cancel = "Отмена")
    {
        Messages.Add(message);
        return Task.FromResult(ConfirmResult);
    }

    public Task ShowMessageAsync(string title, string message)
    {
        Messages.Add(message);
        return Task.CompletedTask;
    }

    public Task OpenPathAsync(string path)
    {
        Opened.Add(path);
        return Task.CompletedTask;
    }

    public Task OpenUrlAsync(string url)
    {
        Opened.Add(url);
        return Task.CompletedTask;
    }

    public Task CopyTextAsync(string text)
    {
        Clipboard = text;
        return Task.CompletedTask;
    }
}
