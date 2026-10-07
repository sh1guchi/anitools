using System.Collections.ObjectModel;
using Anitools.App.Services;
using Anitools.App.Startup;
using Anitools.App.ViewModels.Pages;
using Anitools.Core;
using Anitools.Core.Jobs;
using Anitools.Core.Processes;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Material.Icons;

namespace Anitools.App.ViewModels;

/// <summary>Программа в строке состояния: «ffmpeg 7.1.1 ✓», «NVENC ✗».</summary>
public sealed record ToolChip(string Name, string? Version, bool Ok, string Tip)
{
    public string Text => Version is null ? Name : $"{Name} {Version}";
}

/// <summary>
/// Главное окно (§4.2): рабочая папка сверху, навигация по инструментам слева, страница справа,
/// внизу — найденные программы и текущая задача.
/// </summary>
public sealed partial class MainWindowViewModel : ObservableObject, IShell
{
    private readonly List<PageViewModel> _pages = [];
    private INavItem? _lastPage;

    public MainWindowViewModel(AppServices services, IDialogService dialogs, StartupRequest? startup = null, Func<string, long?>? fileSize = null)
    {
        Services = services;
        Dialogs = dialogs;
        FileSize = fileSize ?? DefaultFileSize;
        JobsPage = new JobsPageViewModel(this);
        SettingsPage = new SettingsPageViewModel(this);
        VideoOnlyPage = new VideoOnlyPageViewModel(this);
        RemuxPage = new RemuxPageViewModel(this);

        Navigation =
        [
            new NavHeader("ВИДЕО"),
            VideoOnlyPage,
            RemuxPage,
            Soon("HLS", MaterialIconKind.LayersTripleOutline, "HLS мульти-разрешение: 360p…4K в zip + озвучки .mka."),
            Soon("Хардсаб", MaterialIconKind.Subtitles, "Вшить .ass в видео (hevc_nvenc, 10 бит)."),
            new NavHeader("АУДИО"),
            Soon("Только аудио", MaterialIconKind.Headphones, "Аудиодорожки отдельными файлами или в один .mka."),
            Soon("Сборка аудио", MaterialIconKind.PlaylistMusic, "Видео + выбранные и внешние дорожки в нужном порядке."),
            Soon("Озвучки → .mka", MaterialIconKind.MicrophoneOutline, "Озвучки из папок в один .mka на серию."),
            Soon("Сдвиг аудио", MaterialIconKind.ClockOutline, "Задержка или обрезка начала аудио."),
            Soon("Перекодировать", MaterialIconKind.Waveform, "Аудио в AAC, MP3, Opus, FLAC…"),
            Soon("Дорожки файла", MaterialIconKind.FileMusicOutline, "Таблица аудиодорожек и список для копирования."),
            new NavHeader("СУБТИТРЫ"),
            Soon("Извлечь", MaterialIconKind.SubtitlesOutline, "Субтитры из серий: по ID, тайтлу или языку."),
            Soon("Сдвиг", MaterialIconKind.TimerEditOutline, "Сдвиг .srt/.ass на N секунд."),
            Soon("Чистка стилей", MaterialIconKind.Broom, "Убрать строки .ass по стилю или актёру (с бэкапом)."),
            new NavHeader("ШРИФТЫ"),
            Soon("Для .ass", MaterialIconKind.FormatFont, "Шрифты из .ass → fonts.zip (своя папка, система, скачивание)."),
            Soon("Из видео", MaterialIconKind.ArchiveArrowDownOutline, "Вложенные шрифты из MKV → fonts.zip."),
            new NavHeader("ФАЙЛЫ"),
            Soon("Переименовать", MaterialIconKind.RenameOutline, "«Название - 01.ext» по номерам серий, с откатом."),
            new NavSeparator(),
            JobsPage,
            SettingsPage,
        ];
        _pages.AddRange(Navigation.OfType<PageViewModel>());

        RecentFolders = new ObservableCollection<string>(services.Settings.RecentFolders);
        FolderMessage = startup?.Error ?? services.SettingsError;
        var initial = startup?.Folder ?? services.Settings.RecentFolders.FirstOrDefault(Directory.Exists);
        if (initial is not null)
        {
            SetFolder(initial, remember: startup?.Folder is not null);
        }
        else
        {
            foreach (var page in _pages)
            {
                page.SetFolder("");
            }
        }

        services.Jobs.JobStarted += job => Dispatcher.UIThread.Post(UpdateCurrentJob);
        services.Jobs.JobFinished += job => Dispatcher.UIThread.Post(() =>
        {
            UpdateCurrentJob();
            // в папке задачи что-то появилось — «уже готово» и т.п. надо пересчитать
            foreach (var page in _pages.Where(p => p.UsesFolder && SamePath(p.Folder, job.Folder)))
            {
                page.Invalidate();
            }
        });
        services.SettingsChanged += () => Dispatcher.UIThread.Post(() => _ = CheckToolsAsync());
        SelectedNav = VideoOnlyPage;
    }

    public AppServices Services { get; }

    public IDialogService Dialogs { get; }

    public Func<string, long?> FileSize { get; }

    public IReadOnlyList<INavItem> Navigation { get; }

    public JobsPageViewModel JobsPage { get; }

    public SettingsPageViewModel SettingsPage { get; }

    public VideoOnlyPageViewModel VideoOnlyPage { get; }

    public RemuxPageViewModel RemuxPage { get; }

    public string Version { get; } = $"{AppInfo.Name} {AppInfo.Version}";

    /// <summary>Рабочая папка; пусто — не выбрана.</summary>
    [ObservableProperty]
    public partial string Folder { get; set; } = "";

    /// <summary>Предупреждение под шапкой: папка из командной строки не найдена, настройки не прочитались…</summary>
    [ObservableProperty]
    public partial string? FolderMessage { get; set; }

    public ObservableCollection<string> RecentFolders { get; }

    public bool HasRecentFolders => RecentFolders.Count > 0;

    [ObservableProperty]
    public partial INavItem? SelectedNav { get; set; }

    [ObservableProperty]
    public partial PageViewModel? CurrentPage { get; set; }

    public ObservableCollection<ToolChip> ToolChips { get; } = [];

    [ObservableProperty]
    public partial JobViewModel? CurrentJob { get; set; }

    public bool HasFolder => Folder.Length > 0;

    public string FolderDisplay => HasFolder ? Folder : "Папка не выбрана — «Обзор…» или перетащите папку на окно";

    /// <summary>Открыть папку (Обзор, недавние, перетаскивание, вторая копия приложения); нет такой — сообщение.</summary>
    public bool OpenFolder(string path)
    {
        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            FolderMessage = $"Неверный путь: {path}";
            return false;
        }

        if (!Directory.Exists(full))
        {
            FolderMessage = $"Папка не найдена: {full}";
            return false;
        }

        FolderMessage = null;
        SetFolder(full, remember: true);
        return true;
    }

    /// <summary>Запрос второй копии приложения (команда ani в другой папке).</summary>
    public void Apply(StartupRequest request)
    {
        if (request.Folder is not null)
        {
            OpenFolder(request.Folder);
        }
        else if (request.Error is not null)
        {
            FolderMessage = request.Error;
        }
    }

    [RelayCommand]
    public void ShowJobs() => SelectedNav = JobsPage;

    public async Task CheckToolsAsync()
    {
        var status = await Task.Run(() => Services.CheckToolsAsync());
        ToolChips.Clear();
        var paths = status.Paths;
        ToolChips.Add(new ToolChip("ffmpeg", status.FfmpegVersion, paths.Ffmpeg is not null && paths.Ffprobe is not null,
            paths.Ffmpeg is null ? "ffmpeg не найден — укажите путь в настройках" : paths.Ffprobe is null ? "ffprobe не найден" : paths.Ffmpeg));
        ToolChips.Add(new ToolChip("mkvmerge", status.MkvmergeVersion, paths.Mkvmerge is not null && paths.Mkvextract is not null,
            paths.Mkvmerge is null ? "MKVToolNix не найден — нужен для субтитров и шрифтов" : paths.Mkvmerge));
        if (OperatingSystem.IsWindows())
        {
            ToolChips.Add(new ToolChip("ImDisk", null, status.Imdisk is not null,
                status.Imdisk ?? "ImDisk не найден — RAM-диск для HLS недоступен, временные файлы пойдут в папку"));
        }

        ToolChips.Add(new ToolChip("NVENC", null, status.Nvenc == true,
            status.Nvenc == true ? "h264_nvenc и scale_cuda есть" : "в ffmpeg нет h264_nvenc/scale_cuda — HLS только на процессоре"));
    }

    partial void OnFolderChanged(string value)
    {
        OnPropertyChanged(nameof(HasFolder));
        OnPropertyChanged(nameof(FolderDisplay));
        foreach (var page in _pages)
        {
            page.SetFolder(value);
        }
    }

    partial void OnSelectedNavChanged(INavItem? value)
    {
        if (value is PageViewModel page)
        {
            _lastPage = page;
            CurrentPage = page;
        }
        else
        {
            // заголовки разделов не выбираются
            Dispatcher.UIThread.Post(() => SelectedNav = _lastPage);
        }
    }

    partial void OnCurrentPageChanged(PageViewModel? oldValue, PageViewModel? newValue)
    {
        oldValue?.Deactivate();
        newValue?.Activate();
    }

    [RelayCommand]
    private async Task BrowseAsync()
    {
        if (await Dialogs.PickFolderAsync("Рабочая папка", HasFolder ? Folder : null) is { } path)
        {
            OpenFolder(path);
        }
    }

    [RelayCommand]
    private void OpenRecent(string path) => OpenFolder(path);

    [RelayCommand]
    private Task OpenFolderInExplorerAsync() => HasFolder ? Dialogs.OpenPathAsync(Folder) : Task.CompletedTask;

    [RelayCommand]
    private void DismissFolderMessage() => FolderMessage = null;

    private void SetFolder(string folder, bool remember)
    {
        Folder = folder;
        if (remember)
        {
            Services.UpdateSettingsQuietly(s => s.WithRecentFolder(folder));
            RecentFolders.Clear();
            foreach (var f in Services.Settings.RecentFolders)
            {
                RecentFolders.Add(f);
            }

            OnPropertyChanged(nameof(HasRecentFolders));
        }
    }

    private void UpdateCurrentJob()
    {
        var current = Services.Jobs.Current;
        CurrentJob = current is null || current.IsFinished ? null : JobsPage.Jobs.FirstOrDefault(j => j.Job == current);
    }

    private PlaceholderPageViewModel Soon(string title, MaterialIconKind icon, string description) =>
        new(this, title, icon, description);

    private static long? DefaultFileSize(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? info.Length : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(a.TrimEnd(Path.DirectorySeparatorChar), b.TrimEnd(Path.DirectorySeparatorChar),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
