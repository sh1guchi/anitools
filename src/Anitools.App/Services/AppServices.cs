using Anitools.Core.Jobs;
using Anitools.Core.Logging;
using Anitools.Core.Media;
using Anitools.Core.Operations.Common;
using Anitools.Core.Processes;
using Anitools.Core.Settings;
using Anitools.Core.Shikimori;

namespace Anitools.App.Services;

/// <summary>
/// Общие службы приложения: настройки, найденные программы, запуск процессов, логи, очередь задач, HTTP.
/// В тестах собираются с фейковым запуском процессов и временной папкой настроек.
/// </summary>
public sealed class AppServices : IDisposable
{
    private readonly object _lock = new();
    private AppSettings _settings;
    private ToolPaths _tools = new(null, null, null, null);
    private string? _imdisk;

    public AppServices(SettingsStore store, IProcessRunner runner, ToolLocator locator, HttpClient http, string logsDirectory, JobQueue? jobs = null)
    {
        Store = store;
        Runner = runner;
        Locator = locator;
        Http = http;
        Logs = new ErrorLogWriter(logsDirectory);
        Jobs = jobs ?? new JobQueue();
        var loaded = store.Load();
        _settings = loaded.Settings;
        SettingsError = loaded.Error;
        RefreshTools();
    }

    /// <summary>Настройки изменились (из потока, который их сохранил).</summary>
    public event Action? SettingsChanged;

    public SettingsStore Store { get; }

    /// <summary>Файл настроек был, но не прочитался — показать пользователю.</summary>
    public string? SettingsError { get; }

    public IProcessRunner Runner { get; }

    public ToolLocator Locator { get; }

    public HttpClient Http { get; }

    public ErrorLogWriter Logs { get; }

    public JobQueue Jobs { get; }

    public AppSettings Settings
    {
        get
        {
            lock (_lock)
            {
                return _settings;
            }
        }
    }

    public ToolPaths Tools
    {
        get
        {
            lock (_lock)
            {
                return _tools;
            }
        }
    }

    /// <summary>imdisk.exe (только Windows) или null.</summary>
    public string? Imdisk
    {
        get
        {
            lock (_lock)
            {
                return _imdisk;
            }
        }
    }

    public IMediaProbe Probe => new MediaProbe(Runner, Tools);

    public ShikimoriClient Shikimori => new(Http, new Uri(Settings.ShikimoriBaseUrl));

    public static AppServices CreateDefault() =>
        new(new SettingsStore(SettingsStore.DefaultPath), new ProcessRunner(), new ToolLocator(), new HttpClient(), ErrorLogWriter.DefaultDirectory);

    public PlanExecutor CreateExecutor() => new(Runner, Tools, Logs, Probe);

    /// <summary>Сохранить настройки и пересчитать пути программ.</summary>
    /// <exception cref="IOException">Не удалось записать файл.</exception>
    /// <exception cref="UnauthorizedAccessException">Нет прав на запись.</exception>
    public void SaveSettings(AppSettings settings)
    {
        settings = settings.Normalized();
        Store.Save(settings);
        lock (_lock)
        {
            _settings = settings;
        }

        RefreshTools();
        SettingsChanged?.Invoke();
    }

    /// <summary>Мелочь вроде недавних папок: меняется сразу, а не записалось на диск — не беда.</summary>
    public void UpdateSettingsQuietly(Func<AppSettings, AppSettings> change)
    {
        AppSettings updated;
        lock (_lock)
        {
            updated = change(_settings);
            _settings = updated;
        }

        try
        {
            Store.Save(updated);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Заново найти программы: путь из настроек → переменные → PATH → известные папки.</summary>
    public void RefreshTools()
    {
        var settings = Settings;
        var tools = ToolPaths.Find(Locator, t => settings.Tools.Get(t));
        var imdisk = OperatingSystem.IsWindows() ? Locator.Find(Tool.Imdisk, settings.Tools.Imdisk) : null;
        lock (_lock)
        {
            _tools = tools;
            _imdisk = imdisk;
        }
    }

    public Task<ToolsStatus> CheckToolsAsync(CancellationToken cancellationToken = default) =>
        ToolStatusChecker.CheckAsync(Runner, Tools, Imdisk, cancellationToken);

    public void Dispose() => Http.Dispose();
}
