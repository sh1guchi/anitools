using System.Collections.ObjectModel;
using System.Globalization;
using Anitools.Core.Install;
using Anitools.Core.Operations.AudioTools;
using Anitools.Core.Operations.Hardsub;
using Anitools.Core.Operations.Hls;
using Anitools.Core.Processes;
using Anitools.Core.Settings;
using Anitools.Core.WorkDir;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Material.Icons;

namespace Anitools.App.ViewModels;

/// <summary>Путь к программе: что задано в настройках и что по нему (или без него) нашлось.</summary>
public sealed partial class ToolPathRow : ObservableObject
{
    private readonly IShell _shell;

    public ToolPathRow(IShell shell, Tool tool, string name, string configured)
    {
        _shell = shell;
        Tool = tool;
        Name = name;
        Configured = configured;
    }

    public Tool Tool { get; }

    public string Name { get; }

    /// <summary>Файл или папка с программой; пусто — искать самим.</summary>
    [ObservableProperty]
    public partial string Configured { get; set; }

    [ObservableProperty]
    public partial string Found { get; set; } = "";

    [ObservableProperty]
    public partial bool IsFound { get; set; }

    partial void OnConfiguredChanged(string value) => Refresh();

    /// <summary>Заново проверить, есть ли программа (после установки ImDisk путь тот же, а программа появилась).</summary>
    public void Refresh()
    {
        var value = Configured;
        var locator = _shell.Services.Locator;
        var path = locator.Find(Tool, value);
        if (value.Trim().Length > 0 && locator.ConfiguredPath(Tool, value.Trim()) is null)
        {
            // Неверный путь не ломает работу (программа ищется дальше), но его надо исправить
            IsFound = false;
            Found = path is null ? "по этому пути программы нет" : $"по этому пути программы нет — пока берётся {path}";
            return;
        }

        IsFound = path is not null;
        Found = path ?? "не найдена — укажите путь";
    }

    [RelayCommand]
    private async Task BrowseAsync()
    {
        if (await _shell.Dialogs.PickFileAsync($"Где {Name}?", Configured) is { } path)
        {
            Configured = path;
        }
    }
}

/// <summary>
/// Программа в настройках целиком (FFmpeg — ffmpeg и ffprobe, MKVToolNix, ImDisk): пути её exe и кнопка
/// «Установить» / «Обновить» — скачать последнюю версию с сайта и сразу прописать пути.
/// </summary>
public sealed partial class ToolPackageViewModel : ObservableObject
{
    private readonly SettingsPageViewModel _page;
    private CancellationTokenSource? _installing;

    public ToolPackageViewModel(SettingsPageViewModel page, ToolPackage package, string title, string source, IReadOnlyList<ToolPathRow> rows)
    {
        _page = page;
        Package = package;
        Title = title;
        Source = source;
        Rows = rows;
        foreach (var row in rows)
        {
            row.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(ToolPathRow.IsFound))
                {
                    OnPropertyChanged(nameof(InstallText));
                }
            };
        }
    }

    public ToolPackage Package { get; }

    public string Title { get; }

    /// <summary>Откуда ставится: «сборка essentials с gyan.dev».</summary>
    public string Source { get; }

    public IReadOnlyList<ToolPathRow> Rows { get; }

    /// <summary>Установка есть только на Windows (сборки программ — под Windows).</summary>
    public bool CanInstall => _page.Shell.Services.Installer is not null;

    /// <summary>Всё нашлось — «Обновить» (поставить последнюю), иначе «Установить».</summary>
    public string InstallText => Rows.All(r => r.IsFound) ? "Обновить" : "Установить";

    /// <summary>«последняя: 9.0.2» с сайта; не узнали — null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Caption))]
    public partial string? Latest { get; set; }

    /// <summary>«сборка «essentials» с gyan.dev · последняя: 9.0.2».</summary>
    public string Caption => Latest is null ? Source : $"{Source} · {Latest}";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InstallCommand))]
    public partial bool IsInstalling { get; set; }

    /// <summary>«скачиваю ffmpeg 9.0.2 — 34 из 115 МБ».</summary>
    [ObservableProperty]
    public partial string? Stage { get; set; }

    [ObservableProperty]
    public partial double Percent { get; set; }

    [ObservableProperty]
    public partial bool IsIndeterminate { get; set; } = true;

    /// <summary>Узнать последнюю версию (при первом открытии настроек); нет связи — молча.</summary>
    public async Task CheckLatestAsync()
    {
        if (_page.Shell.Services.Installer is not { } installer)
        {
            return;
        }

        try
        {
            var version = await installer.LatestVersionAsync(Package);
            Latest = version is null ? "последняя — с сайта автора" : $"последняя: {version}";
        }
        catch (InstallException)
        {
            Latest = null;
        }
    }

    [RelayCommand(CanExecute = nameof(CanStartInstall))]
    private async Task InstallAsync()
    {
        if (_page.Shell.Services.Installer is not { } installer)
        {
            return;
        }

        using var cts = new CancellationTokenSource();
        _installing = cts;
        IsInstalling = true;
        Percent = 0;
        IsIndeterminate = true;
        Stage = "подготовка";
        try
        {
            var result = await installer.InstallAsync(Package, new Progress<InstallProgress>(Show), cts.Token);
            _page.ApplyInstalled(result);
            var version = result.Version is { } v ? $" {v}" : "";
            Latest = result.Version is { } latest ? $"последняя: {latest}" : Latest;
            _page.Shell.Toast($"{Title}{version} установлен — пути прописаны в настройках.", ToastKind.Ok);
        }
        catch (InstallException ex)
        {
            _page.Shell.Toast(ex.Message, ToastKind.Error);
        }
        catch (OperationCanceledException)
        {
            _page.Shell.Toast($"Установка {Title} отменена.", ToastKind.Warn);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _page.Shell.Toast($"{Title} установлен, но настройки не сохранились: {ex.Message}", ToastKind.Error);
        }
        finally
        {
            _installing = null;
            IsInstalling = false;
            Stage = null;
        }
    }

    private bool CanStartInstall() => !IsInstalling;

    [RelayCommand]
    private void CancelInstall() => _installing?.Cancel();

    private void Show(InstallProgress progress)
    {
        IsIndeterminate = progress.Total is not > 0;
        Percent = progress.Total is > 0 and var total ? progress.Downloaded * 100.0 / total : 0;
        Stage = progress.Total is > 0 and var size
            ? $"{progress.Stage} — {Megabytes(progress.Downloaded)} из {Megabytes(size)} МБ"
            : progress.Stage;
    }

    private static string Megabytes(long bytes) => (bytes / (1024.0 * 1024)).ToString("0", CultureInfo.InvariantCulture);
}

/// <summary>Качество лестницы HLS в настройках: битрейт в кбит/с.</summary>
public sealed partial class LadderRowViewModel(HlsRung rung) : ObservableObject
{
    public string Name { get; } = rung.Name;

    [ObservableProperty]
    public partial string Width { get; set; } = rung.Width.ToString(CultureInfo.InvariantCulture);

    [ObservableProperty]
    public partial string Height { get; set; } = rung.Height.ToString(CultureInfo.InvariantCulture);

    [ObservableProperty]
    public partial string BitrateKbps { get; set; } = (rung.Bitrate / 1000).ToString(CultureInfo.InvariantCulture);
}

/// <summary>
/// «Настройки» (§4.10): программы, войс-лист, HLS, временные файлы, Shikimori, шрифты, хардсаб, аудио, субтитры.
/// Поля — текстом; «Сохранить» проверяет их и пишет settings.json.
/// </summary>
public sealed partial class SettingsPageViewModel : PageViewModel
{
    public SettingsPageViewModel(IShell shell)
        : base(shell, "Настройки", MaterialIconKind.Cog)
    {
        Load(shell.Services.Settings);
    }

    public override bool UsesFolder => false;

    public override string Subtitle => "общие для всех инструментов";

    public ObservableCollection<ToolPathRow> ToolPaths { get; } = [];

    /// <summary>Те же пути, по программам: FFmpeg, MKVToolNix, ImDisk — с кнопкой «Установить».</summary>
    public ObservableCollection<ToolPackageViewModel> Packages { get; } = [];

    public ObservableCollection<LadderRowViewModel> Ladder { get; } = [];

    public IReadOnlyList<string> NvencPresets { get; } = ["p1", "p2", "p3", "p4", "p5", "p6", "p7"];

    public IReadOnlyList<string> AudioFormats { get; } = [.. Enum.GetNames<AudioFormat>()];

    public bool ShowsImdisk => OperatingSystem.IsWindows();

    [ObservableProperty]
    public partial string VoicesText { get; set; } = "";

    [ObservableProperty]
    public partial bool UseNvenc { get; set; }

    [ObservableProperty]
    public partial bool UseFixedCq { get; set; }

    [ObservableProperty]
    public partial string FixedCq { get; set; } = "";

    [ObservableProperty]
    public partial string FixedCqPeak { get; set; } = "";

    [ObservableProperty]
    public partial string NvencPreset { get; set; } = "";

    [ObservableProperty]
    public partial string SegmentSeconds { get; set; } = "";

    [ObservableProperty]
    public partial string CalibrationWindows { get; set; } = "";

    [ObservableProperty]
    public partial string CalibrationTolerancePercent { get; set; } = "";

    [ObservableProperty]
    public partial string CalibrationMaxPasses { get; set; } = "";

    [ObservableProperty]
    public partial string CqStart { get; set; } = "";

    [ObservableProperty]
    public partial string CqMin { get; set; } = "";

    [ObservableProperty]
    public partial string CqMax { get; set; } = "";

    [ObservableProperty]
    public partial string SeparateTopZipGb { get; set; } = "";

    [ObservableProperty]
    public partial WorkDirMode WorkDirMode { get; set; }

    [ObservableProperty]
    public partial string WorkDirFolder { get; set; } = "";

    [ObservableProperty]
    public partial string RamDiskGb { get; set; } = "";

    [ObservableProperty]
    public partial string ShikimoriBaseUrl { get; set; } = "";

    [ObservableProperty]
    public partial string FontsCustomDir { get; set; } = "";

    [ObservableProperty]
    public partial string HardsubArgs { get; set; } = "";

    [ObservableProperty]
    public partial string HardsubFontsDir { get; set; } = "";

    [ObservableProperty]
    public partial string AudioShiftSeconds { get; set; } = "";

    [ObservableProperty]
    public partial string AudioShiftBitrate { get; set; } = "";

    [ObservableProperty]
    public partial string AudioShiftWorkers { get; set; } = "";

    /// <summary>Сдвиг аудио с перекодированием в AAC (как в оригинале); по умолчанию — без.</summary>
    [ObservableProperty]
    public partial bool AudioShiftReencode { get; set; }

    [ObservableProperty]
    public partial string AudioConvertFormat { get; set; } = "";

    [ObservableProperty]
    public partial string AudioConvertBitrate { get; set; } = "";

    /// <summary>Каналов на выходе; пусто — как в исходнике.</summary>
    [ObservableProperty]
    public partial string AudioConvertChannels { get; set; } = "";

    [ObservableProperty]
    public partial bool AudioConvertFixTimestamps { get; set; }

    [ObservableProperty]
    public partial string AudioConvertWorkers { get; set; } = "";

    [ObservableProperty]
    public partial string SubShiftSeconds { get; set; } = "";

    public bool IsRamDisk
    {
        get => WorkDirMode == WorkDirMode.RamDisk;
        set => SetMode(value, WorkDirMode.RamDisk);
    }

    public bool IsFolder
    {
        get => WorkDirMode == WorkDirMode.Folder;
        set => SetMode(value, WorkDirMode.Folder);
    }

    public bool IsNearOutput
    {
        get => WorkDirMode == WorkDirMode.NearOutput;
        set => SetMode(value, WorkDirMode.NearOutput);
    }

    public string SettingsPath => Shell.Services.Store.Path;

    /// <summary>Первое открытие настроек — узнать последние версии программ (для «Установить»).</summary>
    protected override Task LoadAsync(string folder, CancellationToken cancellationToken)
    {
        foreach (var package in Packages)
        {
            _ = package.CheckLatestAsync();
        }

        return Task.CompletedTask;
    }

    /// <summary>Программа поставилась: пути — в строки и сразу в настройки (остальные несохранённые поля не трогаются).</summary>
    /// <exception cref="IOException">Настройки не записались.</exception>
    /// <exception cref="UnauthorizedAccessException">Нет прав на запись настроек.</exception>
    public void ApplyInstalled(InstallResult result)
    {
        foreach (var row in ToolPaths)
        {
            if (result.Paths.TryGetValue(row.Tool, out var path))
            {
                row.Configured = path;
            }
        }

        var current = Shell.Services.Settings;
        Shell.Services.SaveSettings(current with { Tools = result.Paths.Aggregate(current.Tools, (tools, p) => tools.With(p.Key, p.Value)) });
        foreach (var row in ToolPaths)
        {
            row.Refresh(); // ImDisk: путь тот же (ищется сам), а программа появилась
        }
    }

    /// <summary>Поля → настройки; ошибки — список понятных сообщений (тогда настройки null).</summary>
    public (AppSettings? Settings, IReadOnlyList<string> Errors) Build()
    {
        var errors = new List<string>();
        var current = Shell.Services.Settings;
        var tools = ToolPaths.Aggregate(current.Tools, (t, row) => t.With(row.Tool, row.Configured.Trim()));

        var ladder = new List<HlsRung>();
        foreach (var row in Ladder)
        {
            var w = Int(row.Width, $"{row.Name}: ширина", errors, 2);
            var h = Int(row.Height, $"{row.Name}: высота", errors, 2);
            var kbps = Int(row.BitrateKbps, $"{row.Name}: битрейт", errors, 1);
            ladder.Add(new HlsRung(row.Name, w, h, kbps * 1000L));
        }

        var cqMin = Number(CqMin, "Минимальный CQ", errors, 1, 51);
        var cqMax = Number(CqMax, "Максимальный CQ", errors, 1, 51);
        if (cqMin >= cqMax)
        {
            errors.Add("Минимальный CQ должен быть меньше максимального.");
        }

        var hls = current.Hls with
        {
            Ladder = ladder,
            Encoder = UseNvenc ? EncoderProfile.Nvenc : EncoderProfile.Software,
            FixedCq = UseFixedCq ? Number(FixedCq, "Постоянный CQ", errors, 1, 51) : null,
            FixedCqPeak = Number(FixedCqPeak, "Потолок битрейта", errors, 1, 20),
            NvencPreset = NvencPreset,
            SegmentSeconds = Int(SegmentSeconds, "Длина сегмента", errors, 1, 60),
            CalibrationWindows = Int(CalibrationWindows, "Окон для подбора", errors, 1, 50),
            CalibrationTolerance = Number(CalibrationTolerancePercent, "Допуск подбора", errors, 0.1, 50) / 100,
            CalibrationMaxPasses = Int(CalibrationMaxPasses, "Проходов подбора", errors, 1, 20),
            CqStart = Number(CqStart, "Начальный CQ", errors, 1, 51),
            CqMin = cqMin,
            CqMax = cqMax,
            SeparateTopZipBytes = (long)(Number(SeparateTopZipGb, "Порог отдельного архива", errors, 0.1, 1000) * 1024 * 1024 * 1024),
        };
        int? channels = AudioConvertChannels.Trim().Length == 0 ? null : Int(AudioConvertChannels, "Каналов при перекодировании", errors, 1, 8);
        var settings = current with
        {
            Tools = tools,
            Voices = [.. VoicesText.Split('\n').Select(v => v.Trim()).Where(v => v.Length > 0).Distinct(StringComparer.Ordinal)],
            Hls = hls,
            WorkDir = new WorkDirSettings
            {
                Mode = WorkDirMode,
                Folder = Required(WorkDirFolder, "Временная папка", errors),
                RamDiskGb = Int(RamDiskGb, "Размер RAM-диска", errors, 1, 512),
            },
            ShikimoriBaseUrl = Uri.TryCreate(ShikimoriBaseUrl.Trim(), UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http"
                ? uri.ToString()
                : Error(errors, "Адрес Shikimori: нужен полный адрес вида https://shikimori.io/api/", current.ShikimoriBaseUrl),
            Fonts = new FontSettings { CustomDir = Required(FontsCustomDir, "Своя папка шрифтов", errors) },
            Hardsub = new HardsubOptions
            {
                EncodeArgs = HardsubArgs.Split(' ', StringSplitOptions.RemoveEmptyEntries) is { Length: > 0 } args
                    ? args
                    : Error(errors, "Параметры хардсаба не могут быть пустыми.", HardsubOptions.DefaultEncodeArgs),
                FontsDirName = Required(HardsubFontsDir, "Папка шрифтов хардсаба", errors),
            },
            AudioShift = new AudioShiftOptions
            {
                Seconds = Number(AudioShiftSeconds, "Сдвиг аудио", errors, -3600, 3600),
                Bitrate = Required(AudioShiftBitrate, "Битрейт сдвига аудио", errors),
                Workers = Int(AudioShiftWorkers, "Файлов сразу (сдвиг аудио)", errors, 1, 64),
                Reencode = AudioShiftReencode,
            },
            AudioConvert = new AudioConvertOptions
            {
                Format = Enum.TryParse<AudioFormat>(AudioConvertFormat, out var format) ? format : current.AudioConvert.Format,
                Bitrate = Required(AudioConvertBitrate, "Битрейт перекодирования", errors),
                Channels = channels,
                FixTimestamps = AudioConvertFixTimestamps,
                Workers = Int(AudioConvertWorkers, "Файлов сразу (перекодирование)", errors, 1, 64),
            },
            SubShiftSeconds = Number(SubShiftSeconds, "Сдвиг субтитров", errors, -3600, 3600),
        };
        return errors.Count > 0 ? (null, errors) : (settings, errors);
    }

    [RelayCommand]
    private void Save()
    {
        var (settings, errors) = Build();
        if (settings is null)
        {
            Message = "Не сохранено:\n• " + string.Join("\n• ", errors);
            return;
        }

        try
        {
            Shell.Services.SaveSettings(settings);
            Message = null;
            Shell.Toast($"Сохранено в {Shell.Services.Store.Path}", ToastKind.Ok);
            Load(Shell.Services.Settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Message = $"Не удалось записать настройки: {ex.Message}";
        }
    }

    [RelayCommand]
    private void Revert()
    {
        Load(Shell.Services.Settings);
        Message = null;
        Shell.Toast("Изменения отменены.");
    }

    [RelayCommand]
    private void Defaults()
    {
        // Пути к программам и недавние папки — не трогаем, это не «значения по умолчанию»
        var current = Shell.Services.Settings;
        Load(new AppSettings { Tools = current.Tools, RecentFolders = current.RecentFolders });
        Message = null;
        Shell.Toast("Подставлены значения по умолчанию — нажмите «Сохранить», чтобы применить.");
    }

    [RelayCommand]
    private async Task BrowseWorkDirAsync()
    {
        if (await Shell.Dialogs.PickFolderAsync("Временная папка для HLS", WorkDirFolder) is { } path)
        {
            WorkDirFolder = path;
        }
    }

    [RelayCommand]
    private async Task BrowseFontsDirAsync()
    {
        if (await Shell.Dialogs.PickFolderAsync("Своя папка со шрифтами", FontsCustomDir) is { } path)
        {
            FontsCustomDir = path;
        }
    }

    [RelayCommand]
    private Task OpenSettingsFolderAsync() =>
        Shell.Dialogs.OpenPathAsync(Path.GetDirectoryName(Shell.Services.Store.Path) is { } dir && Directory.Exists(dir) ? dir : Shell.Services.Store.Path);

    partial void OnWorkDirModeChanged(WorkDirMode value)
    {
        OnPropertyChanged(nameof(IsRamDisk));
        OnPropertyChanged(nameof(IsFolder));
        OnPropertyChanged(nameof(IsNearOutput));
    }

    private void Load(AppSettings s)
    {
        ToolPaths.Clear();
        Packages.Clear();
        ToolPathRow Row(Tool tool, string name)
        {
            var row = new ToolPathRow(Shell, tool, name, s.Tools.Get(tool));
            ToolPaths.Add(row);
            return row;
        }

        Packages.Add(new ToolPackageViewModel(this, ToolPackage.Ffmpeg, "FFmpeg", "сборка «essentials» с gyan.dev",
            [Row(Tool.Ffmpeg, "ffmpeg"), Row(Tool.Ffprobe, "ffprobe")]));
        Packages.Add(new ToolPackageViewModel(this, ToolPackage.MkvToolNix, "MKVToolNix", "portable-сборка с mkvtoolnix.download",
            [Row(Tool.Mkvmerge, "mkvmerge"), Row(Tool.Mkvextract, "mkvextract")]));
        if (ShowsImdisk)
        {
            Packages.Add(new ToolPackageViewModel(this, ToolPackage.ImDisk, "ImDisk", "драйвер RAM-диска с ltr-data.se, нужны права администратора",
                [Row(Tool.Imdisk, "imdisk")]));
        }

        VoicesText = string.Join('\n', s.Voices);
        Ladder.Clear();
        foreach (var rung in s.Hls.Ladder)
        {
            Ladder.Add(new LadderRowViewModel(rung));
        }

        UseNvenc = s.Hls.Encoder == EncoderProfile.Nvenc;
        UseFixedCq = s.Hls.FixedCq is not null;
        FixedCq = Text(s.Hls.FixedCq ?? HlsSettings.Default.FixedCq ?? 21);
        FixedCqPeak = Text(s.Hls.FixedCqPeak);
        NvencPreset = s.Hls.NvencPreset;
        SegmentSeconds = Text(s.Hls.SegmentSeconds);
        CalibrationWindows = Text(s.Hls.CalibrationWindows);
        CalibrationTolerancePercent = Text(s.Hls.CalibrationTolerance * 100);
        CalibrationMaxPasses = Text(s.Hls.CalibrationMaxPasses);
        CqStart = Text(s.Hls.CqStart);
        CqMin = Text(s.Hls.CqMin);
        CqMax = Text(s.Hls.CqMax);
        SeparateTopZipGb = Text(Math.Round(s.Hls.SeparateTopZipBytes / (1024.0 * 1024 * 1024), 2));
        WorkDirMode = s.WorkDir.Mode;
        WorkDirFolder = s.WorkDir.Folder;
        RamDiskGb = Text(s.WorkDir.RamDiskGb);
        ShikimoriBaseUrl = s.ShikimoriBaseUrl;
        FontsCustomDir = s.Fonts.CustomDir;
        HardsubArgs = string.Join(' ', s.Hardsub.EncodeArgs);
        HardsubFontsDir = s.Hardsub.FontsDirName;
        AudioShiftSeconds = Text(s.AudioShift.Seconds);
        AudioShiftBitrate = s.AudioShift.Bitrate;
        AudioShiftWorkers = Text(s.AudioShift.Workers);
        AudioShiftReencode = s.AudioShift.Reencode;
        AudioConvertFormat = s.AudioConvert.Format.ToString();
        AudioConvertBitrate = s.AudioConvert.Bitrate;
        AudioConvertChannels = s.AudioConvert.Channels is { } ch ? Text(ch) : "";
        AudioConvertFixTimestamps = s.AudioConvert.FixTimestamps;
        AudioConvertWorkers = Text(s.AudioConvert.Workers);
        SubShiftSeconds = Text(s.SubShiftSeconds);
    }

    private void SetMode(bool selected, WorkDirMode mode)
    {
        if (selected)
        {
            WorkDirMode = mode;
        }
    }

    private static string Text(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static string Text(int value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>Число с точкой или запятой в пределах; не так — ошибка в список и 0.</summary>
    internal static double Number(string text, string what, List<string> errors, double min, double max)
    {
        if (double.TryParse(text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            && double.IsFinite(value) && value >= min && value <= max)
        {
            return value;
        }

        errors.Add($"{what}: нужно число от {Text(min)} до {Text(max)}.");
        return 0;
    }

    internal static int Int(string text, string what, List<string> errors, int min, int max = int.MaxValue)
    {
        if (int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value >= min && value <= max)
        {
            return value;
        }

        errors.Add(max == int.MaxValue ? $"{what}: нужно целое число от {min}." : $"{what}: нужно целое число от {min} до {max}.");
        return 0;
    }

    private static string Required(string text, string what, List<string> errors) =>
        text.Trim() is { Length: > 0 } value ? value : Error(errors, $"{what}: не может быть пустым.", "");

    private static T Error<T>(List<string> errors, string message, T fallback)
    {
        errors.Add(message);
        return fallback;
    }
}
