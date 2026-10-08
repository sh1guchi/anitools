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

namespace Anitools.App.ViewModels;

/// <summary>Программа в строке состояния: «ffmpeg 7.1.1 ✓», «NVENC ✗».</summary>
public sealed record ToolChip(string Name, string? Version, bool Ok, string Tip)
{
    public string Text => Version is null ? Name : $"{Name} {Version}";
}

/// <summary>
/// Главное окно (§4.2) в оформлении Anime Uploader: слева — меню по разделам и рабочая папка, сверху — название
/// страницы, текущая задача и найденные программы, справа сверху — всплывающие уведомления.
/// </summary>
public sealed partial class MainWindowViewModel : ObservableObject, IShell
{
    private const int MaxToasts = 5;
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
        HlsPage = new HlsPageViewModel(this);
        AudioExtractPage = new AudioExtractPageViewModel(this);
        AudioMuxPage = new AudioMuxPageViewModel(this);
        SubtitlesPage = new SubtitlesPageViewModel(this);
        RenamePage = new RenamePageViewModel(this);
        HardsubPage = new HardsubPageViewModel(this);
        MkaMuxPage = new MkaMuxPageViewModel(this);
        AudioShiftPage = new AudioShiftPageViewModel(this);
        AudioConvertPage = new AudioConvertPageViewModel(this);
        TrackListPage = new TrackListPageViewModel(this);
        SubShiftPage = new SubShiftPageViewModel(this);
        AssEditPage = new AssEditPageViewModel(this);
        AssFontsPage = new AssFontsPageViewModel(this);
        VideoFontsPage = new VideoFontsPageViewModel(this);

        Navigation =
        [
            new NavHeader("ВИДЕО"),
            VideoOnlyPage,
            RemuxPage,
            HlsPage,
            HardsubPage,
            new NavHeader("АУДИО"),
            AudioExtractPage,
            AudioMuxPage,
            MkaMuxPage,
            AudioShiftPage,
            AudioConvertPage,
            TrackListPage,
            new NavHeader("СУБТИТРЫ"),
            SubtitlesPage,
            SubShiftPage,
            AssEditPage,
            new NavHeader("ШРИФТЫ"),
            AssFontsPage,
            VideoFontsPage,
            new NavHeader("ФАЙЛЫ"),
            RenamePage,
            new NavSeparator(),
            JobsPage,
            SettingsPage,
        ];
        _pages.AddRange(Navigation.OfType<PageViewModel>());
        BottomNavigation = [JobsPage, SettingsPage];
        ToolNavigation = [.. Navigation.Where(n => n is not NavSeparator && !BottomNavigation.Contains(n))];

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
            ToastFinished(job);
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

    /// <summary>Инструменты по разделам — меню слева.</summary>
    public IReadOnlyList<INavItem> ToolNavigation { get; }

    /// <summary>«Задачи» и «Настройки» — внизу меню, всегда на виду.</summary>
    public IReadOnlyList<INavItem> BottomNavigation { get; }

    /// <summary>Выбранное в верхнем меню (null — выбрано в нижнем).</summary>
    public INavItem? SelectedTool
    {
        get => SelectedNav is { } nav && ToolNavigation.Contains(nav) ? nav : null;
        set
        {
            if (value is not null)
            {
                SelectedNav = value;
            }
        }
    }

    /// <summary>Выбранное в нижнем меню (null — выбрано в верхнем).</summary>
    public INavItem? SelectedBottom
    {
        get => SelectedNav is { } nav && BottomNavigation.Contains(nav) ? nav : null;
        set
        {
            if (value is not null)
            {
                SelectedNav = value;
            }
        }
    }

    public JobsPageViewModel JobsPage { get; }

    public SettingsPageViewModel SettingsPage { get; }

    public VideoOnlyPageViewModel VideoOnlyPage { get; }

    public RemuxPageViewModel RemuxPage { get; }

    public HlsPageViewModel HlsPage { get; }

    public AudioExtractPageViewModel AudioExtractPage { get; }

    public AudioMuxPageViewModel AudioMuxPage { get; }

    public SubtitlesPageViewModel SubtitlesPage { get; }

    public RenamePageViewModel RenamePage { get; }

    public HardsubPageViewModel HardsubPage { get; }

    public MkaMuxPageViewModel MkaMuxPage { get; }

    public AudioShiftPageViewModel AudioShiftPage { get; }

    public AudioConvertPageViewModel AudioConvertPage { get; }

    public TrackListPageViewModel TrackListPage { get; }

    public SubShiftPageViewModel SubShiftPage { get; }

    public AssEditPageViewModel AssEditPage { get; }

    public AssFontsPageViewModel AssFontsPage { get; }

    public VideoFontsPageViewModel VideoFontsPage { get; }

    public string Version { get; } = $"{AppInfo.Name} {AppInfo.Version}";

    /// <summary>Под названием слева сверху: «1.0.0 · by shiguchi».</summary>
    public string BrandSubtitle { get; } = $"{AppInfo.Version} · by shiguchi";

    /// <summary>Всплывающие уведомления, новые снизу (не больше пяти).</summary>
    public ObservableCollection<ToastViewModel> Toasts { get; } = [];

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

    /// <summary>Одна плашка вместо всех в узком окне: «программы» или чего нет — «ffmpeg, NVENC».</summary>
    [ObservableProperty]
    public partial string ToolsSummary { get; set; } = "";

    [ObservableProperty]
    public partial bool ToolsOk { get; set; } = true;

    [ObservableProperty]
    public partial string ToolsSummaryTip { get; set; } = "";

    [ObservableProperty]
    public partial JobViewModel? CurrentJob { get; set; }

    public bool HasFolder => Folder.Length > 0;

    public string FolderDisplay => HasFolder ? Folder : "Папка не выбрана — «Обзор…» или перетащите папку на окно";

    /// <summary>Имя рабочей папки для карточки слева внизу.</summary>
    public string FolderName => HasFolder ? Path.GetFileName(Folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) is { Length: > 0 } name ? name : Folder : "Папка не выбрана";

    /// <summary>Где она лежит; без папки — подсказка.</summary>
    public string FolderParent => HasFolder ? Path.GetDirectoryName(Folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) ?? "" : "«Обзор…» или перетащите на окно";

    /// <summary>Строка рядом с названием страницы: рабочая папка или пояснение страницы без папки.</summary>
    public string TopSubtitle => CurrentPage is { UsesFolder: false } page ? page.Subtitle ?? "" : FolderDisplay;

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

    /// <summary>Уведомление справа сверху; такое же только что было — не повторять.</summary>
    public void Toast(string text, ToastKind kind = ToastKind.Info)
    {
        if (Toasts.Any(t => t.Text == text && !t.IsLeaving))
        {
            return;
        }

        var toast = new ToastViewModel(text, kind, CloseToast);
        Toasts.Add(toast);
        while (Toasts.Count > MaxToasts)
        {
            Toasts.RemoveAt(0);
        }

        DispatcherTimer.RunOnce(() => CloseToast(toast), ToastViewModel.DefaultDuration(kind));
    }

    public async Task CheckToolsAsync()
    {
        var status = await Task.Run(() => Services.CheckToolsAsync());
        ToolChips.Clear();
        var paths = status.Paths;
        ToolChips.Add(new ToolChip("ffmpeg", status.FfmpegVersion, paths.Ffmpeg is not null && paths.Ffprobe is not null,
            paths.Ffmpeg is null ? "ffmpeg не найден — «Настройки» → «Установить» или путь" : paths.Ffprobe is null ? "ffprobe не найден" : paths.Ffmpeg));
        ToolChips.Add(new ToolChip("mkvmerge", status.MkvmergeVersion, paths.Mkvmerge is not null && paths.Mkvextract is not null,
            paths.Mkvmerge is null ? "MKVToolNix не найден — нужен для субтитров и шрифтов; «Настройки» → «Установить»" : paths.Mkvmerge));
        if (OperatingSystem.IsWindows())
        {
            ToolChips.Add(new ToolChip("ImDisk", null, status.Imdisk is not null,
                status.Imdisk ?? "ImDisk не найден — RAM-диск для HLS недоступен; «Настройки» → «Установить»"));
        }

        ToolChips.Add(new ToolChip("NVENC", null, status.Nvenc == true,
            status.Nvenc == true ? "h264_nvenc и scale_cuda есть" : "в ffmpeg нет h264_nvenc/scale_cuda — HLS только на процессоре"));
        UpdateMissingTools(paths);
        var missing = ToolChips.Where(c => !c.Ok).Select(c => c.Name).ToList();
        ToolsOk = missing.Count == 0;
        ToolsSummary = ToolsOk ? "программы" : string.Join(", ", missing);
        ToolsSummaryTip = string.Join("\n", ToolChips.Select(c => $"{(c.Ok ? "✓" : "!")} {c.Text} — {c.Tip}"));
    }

    partial void OnFolderChanged(string value)
    {
        OnPropertyChanged(nameof(HasFolder));
        OnPropertyChanged(nameof(FolderDisplay));
        OnPropertyChanged(nameof(FolderName));
        OnPropertyChanged(nameof(FolderParent));
        OnPropertyChanged(nameof(TopSubtitle));
        foreach (var page in _pages)
        {
            page.SetFolder(value);
        }
    }

    partial void OnSelectedNavChanged(INavItem? value)
    {
        OnPropertyChanged(nameof(SelectedTool));
        OnPropertyChanged(nameof(SelectedBottom));
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
        OnPropertyChanged(nameof(TopSubtitle));
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

    /// <summary>Итог задачи — уведомлением: видно, даже если открыта другая страница.</summary>
    private void ToastFinished(Job job)
    {
        var snapshot = job.Snapshot;
        switch (snapshot.State)
        {
            case JobState.Done:
                Toast($"{job.Title} — {(string.IsNullOrEmpty(snapshot.Summary) ? "готово" : snapshot.Summary)}", ToastKind.Ok);
                break;
            case JobState.Failed:
                Toast($"{job.Title} — {(string.IsNullOrEmpty(snapshot.Summary) ? "ошибка" : snapshot.Summary)}", ToastKind.Error);
                break;
            case JobState.Cancelled:
                Toast($"{job.Title} — отменено", ToastKind.Warn);
                break;
        }
    }

    /// <summary>Погасить и убрать.</summary>
    private void CloseToast(ToastViewModel toast)
    {
        if (toast.IsLeaving || !Toasts.Contains(toast))
        {
            return;
        }

        toast.IsLeaving = true;
        DispatcherTimer.RunOnce(() => Toasts.Remove(toast), TimeSpan.FromMilliseconds(180));
    }

    private void UpdateCurrentJob()
    {
        var current = Services.Jobs.Current;
        CurrentJob = current is null || current.IsFinished ? null : JobsPage.Jobs.FirstOrDefault(j => j.Job == current);
    }

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
