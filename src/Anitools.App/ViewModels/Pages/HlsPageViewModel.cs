using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using Anitools.Core.Jobs;
using Anitools.Core.Media;
using Anitools.Core.Operations.Common;
using Anitools.Core.Operations.Hls;
using Anitools.Core.Platform;
using Anitools.Core.Text;
using Anitools.Core.WorkDir;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Material.Icons;

namespace Anitools.App.ViewModels.Pages;

/// <summary>Дорожка набора: брать ли её и под каким названием озвучки (имя папки audio\&lt;озвучка&gt;).</summary>
public sealed partial class HlsVoiceRowViewModel : ObservableObject
{
    public HlsVoiceRowViewModel(HlsAudioTrack track)
    {
        Track = track;
        Voice = VoiceAssignment.SuggestedName(track);
    }

    public HlsAudioTrack Track { get; }

    public int Number => Track.Index + 1;

    public string Title => Track.Title;

    public string Language => Track.Language;

    public string Channels => AudioTrackRowViewModel.ChannelsText(Track.Channels == 0 ? null : Track.Channels);

    /// <summary>Снята — дорожку не брать (комментарии и т.п.).</summary>
    [ObservableProperty]
    public partial bool IsTaken { get; set; } = true;

    [ObservableProperty]
    public partial string Voice { get; set; }

    public VoiceChoice Choice => new(Track.Index, IsTaken && Voice.Trim().Length > 0 ? Voice.Trim() : null);
}

/// <summary>Набор дорожек тайтла: озвучки назначаются один раз на всех, у кого такой набор.</summary>
public sealed class HlsLayoutViewModel
{
    public HlsLayoutViewModel(HlsLayoutInfo layout, int number, int totalFiles, bool several)
    {
        Layout = layout;
        Header = several
            ? $"Набор дорожек {number}: {RuText.Plural(layout.Files.Count, "файл", "файла", "файлов")} из {totalFiles}"
              + (layout.Files.Count <= 3 ? " — " + string.Join(", ", layout.Files.Select(f => f.Name)) : "")
            : "Озвучки";
        Rows = new ObservableCollection<HlsVoiceRowViewModel>(layout.Tracks.Select(t => new HlsVoiceRowViewModel(t)));
    }

    public HlsLayoutInfo Layout { get; }

    public string Header { get; }

    public ObservableCollection<HlsVoiceRowViewModel> Rows { get; }
}

/// <summary>Тайтл: конвертировать ли, папка (с ID Shikimori или просто название), озвучки по наборам дорожек.</summary>
public sealed partial class HlsGroupViewModel : ObservableObject
{
    private readonly HlsPageViewModel _page;

    public HlsGroupViewModel(HlsPageViewModel page, HlsGroup group)
    {
        _page = page;
        Group = group;
        var layouts = group.Layouts;
        Layouts = new ObservableCollection<HlsLayoutViewModel>(
            layouts.Select((l, i) => new HlsLayoutViewModel(l, i + 1, group.Files.Count, layouts.Count > 1)));
        foreach (var row in Layouts.SelectMany(l => l.Rows))
        {
            row.PropertyChanged += OnChanged;
        }
    }

    public HlsGroup Group { get; }

    public string Title => Group.Title;

    public string FilesText => RuText.Plural(Group.Files.Count, "файл", "файла", "файлов");

    public ObservableCollection<HlsLayoutViewModel> Layouts { get; }

    [ObservableProperty]
    public partial bool IsIncluded { get; set; } = true;

    /// <summary>Выбранный на Shikimori тайтл (ID и название) или null — папка будет просто названием группы.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShikimoriText), nameof(TitleFolder), nameof(HasShikimori))]
    public partial long? ShikimoriId { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShikimoriText))]
    public partial string? ShikimoriName { get; set; }

    public bool HasShikimori => ShikimoriId is not null;

    public string ShikimoriText => ShikimoriId is { } id
        ? $"Shikimori: {id}" + (ShikimoriName is { Length: > 0 } name ? $" · {name}" : "")
        : "Shikimori: не выбрано";

    /// <summary>hls_multi\&lt;эта папка&gt;.</summary>
    public string TitleFolder => HlsOperation.TitleFolder(ShikimoriId, Title);

    public HlsGroupOptions Options() => new()
    {
        Group = Group,
        TitleFolder = TitleFolder,
        Voices = Layouts.ToDictionary(l => l.Layout.Layout.Key, l => (IReadOnlyList<VoiceChoice>)[.. l.Rows.Select(r => r.Choice)]),
    };

    partial void OnIsIncludedChanged(bool value) => _page.Replan();

    partial void OnShikimoriIdChanged(long? value) => _page.Replan();

    [RelayCommand]
    private Task PickShikimoriAsync() => _page.PickShikimoriAsync(this);

    [RelayCommand]
    private void ClearShikimori()
    {
        ShikimoriName = null;
        ShikimoriId = null;
    }

    private void OnChanged(object? sender, PropertyChangedEventArgs e) => _page.Replan();
}

/// <summary>
/// П.7 «HLS» (§4.8): тайтлы (перегруппировка, Shikimori), озвучки по наборам дорожек, качество (постоянный CQ или
/// подбор), временные файлы (RAM-диск / папка / рядом с выходом), выключение по завершении; запуск — после сводки.
/// </summary>
public sealed partial class HlsPageViewModel(IShell shell) : PageViewModel(shell, "HLS", MaterialIconKind.LayersTripleOutline)
{
    private HlsInspection? _inspection;
    private HlsPlan? _plan;
    private bool _rebuilding;

    public ObservableCollection<HlsGroupViewModel> Groups { get; } = [];

    public PlanPreviewViewModel Preview { get; } = new();

    public IReadOnlyList<string> Voices => Shell.Services.Settings.Voices;

    public IReadOnlyList<string> NvencPresets { get; } = ["p1", "p2", "p3", "p4", "p5", "p6", "p7"];

    public bool CanShutdown => ShutdownService.IsSupported;

    [ObservableProperty]
    public partial bool UseNvenc { get; set; } = shell.Services.Settings.Hls.Encoder == EncoderProfile.Nvenc;

    [ObservableProperty]
    public partial bool UseFixedCq { get; set; } = shell.Services.Settings.Hls.FixedCq is not null;

    [ObservableProperty]
    public partial string FixedCq { get; set; } = (shell.Services.Settings.Hls.FixedCq ?? HlsSettings.Default.FixedCq ?? 21).ToString("0.##", CultureInfo.InvariantCulture);

    [ObservableProperty]
    public partial string NvencPreset { get; set; } = shell.Services.Settings.Hls.NvencPreset;

    [ObservableProperty]
    public partial WorkDirMode WorkDirMode { get; set; } = shell.Services.Settings.WorkDir.Mode;

    [ObservableProperty]
    public partial string WorkDirFolder { get; set; } = shell.Services.Settings.WorkDir.Folder;

    [ObservableProperty]
    public partial string RamDiskGb { get; set; } = shell.Services.Settings.WorkDir.RamDiskGb.ToString(CultureInfo.InvariantCulture);

    [ObservableProperty]
    public partial bool Shutdown { get; set; }

    /// <summary>«13 серий × 6 качеств → hls_multi\… · 2 уже готовы».</summary>
    [ObservableProperty]
    public partial string Summary { get; set; } = "";

    /// <summary>Что может помешать: нет NVENC, нет ImDisk.</summary>
    [ObservableProperty]
    public partial string? Warning { get; set; }

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

    protected override async Task LoadAsync(string folder, CancellationToken cancellationToken)
    {
        var inspection = await HlsOperation.InspectAsync(folder, new CachedMediaProbe(Shell.Services.Probe), cancellationToken);
        _inspection = inspection;
        SetGroups(inspection.Groups);
        UpdateWarning();
    }

    protected override void Clear()
    {
        _inspection = null;
        _plan = null;
        Groups.Clear();
        Preview.Clear();
        Summary = "";
    }

    public async Task PickShikimoriAsync(HlsGroupViewModel group)
    {
        var picker = new ShikimoriPickerViewModel(Shell.Services.Shikimori, group.Title);
        var choice = await Shell.Dialogs.PickShikimoriAsync(picker);
        if (choice.IsSkip)
        {
            return;
        }

        group.ShikimoriName = choice.Anime?.Name;
        group.ShikimoriId = choice.Id;
    }

    /// <summary>План по текущим решениям; превью — серии с галочками.</summary>
    public void Replan()
    {
        if (_inspection is not { } inspection || _rebuilding)
        {
            return;
        }

        try
        {
            var plan = HlsOperation.Plan(inspection, [.. Groups.Where(g => g.IsIncluded).Select(g => g.Options())]);
            _plan = plan;
            var items = plan.Episodes.Select(e => new PlanItem
            {
                Source = e.Source.Path,
                Label = e.Status == PlanItemStatus.Run ? $"{e.Source.Name} · {string.Join(", ", e.Voices.Select(v => v.Folder))}" : e.Source.Name,
                Status = e.Status,
                Reason = e.Reason,
                Outputs = e.Status == PlanItemStatus.Run ? [e.ZipPath] : [],
            }).ToList();
            Preview.Show(new OperationPlan("HLS", inspection.Folder, items), Shell.FileSize);
            var rungs = Shell.Services.Settings.Hls.Ladder.Count;
            var folders = plan.Episodes.Where(e => e.Status == PlanItemStatus.Run).Select(e => e.TitleFolder).Distinct().ToList();
            Summary = plan.RunCount == 0
                ? "Нечего конвертировать."
                : $"{RuText.Plural(plan.RunCount, "серия", "серии", "серий")} × {RuText.Plural(rungs, "качество", "качества", "качеств")} → "
                  + Path.Combine(HlsOperation.OutputFolderName, folders.Count == 1 ? folders[0] : $"{folders.Count} папки")
                  + (plan.SkipCount > 0 ? $" · пропуск {plan.SkipCount}" : "");
            Message = null;
        }
        catch (PlanException ex)
        {
            _plan = null;
            Preview.Clear();
            Summary = "";
            Message = ex.Message;
        }
    }

    partial void OnWorkDirModeChanged(WorkDirMode value)
    {
        OnPropertyChanged(nameof(IsRamDisk));
        OnPropertyChanged(nameof(IsFolder));
        OnPropertyChanged(nameof(IsNearOutput));
        UpdateWarning();
    }

    partial void OnUseNvencChanged(bool value) => UpdateWarning();

    [RelayCommand]
    private async Task RegroupAsync()
    {
        if (_inspection is not { } inspection)
        {
            return;
        }

        var regroup = new RegroupViewModel(inspection.Files, [.. Groups.Select(g => g.Group)]);
        if (await Shell.Dialogs.RegroupAsync(regroup))
        {
            SetGroups(regroup.Result());
        }
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
    private async Task RunAsync()
    {
        if (_plan is not { } plan || Preview.Selected() is not { } selected)
        {
            return;
        }

        var errors = new List<string>();
        var settings = Shell.Services.Settings.Hls with
        {
            Encoder = UseNvenc ? EncoderProfile.Nvenc : EncoderProfile.Software,
            FixedCq = UseFixedCq ? SettingsPageViewModel.Number(FixedCq, "CQ", errors, 1, 51) : null,
            NvencPreset = NvencPreset,
        };
        var workDir = new WorkDirSettings
        {
            Mode = WorkDirMode,
            Folder = WorkDirFolder.Trim(),
            RamDiskGb = SettingsPageViewModel.Int(RamDiskGb, "Размер RAM-диска", errors, 1, 512),
        };
        if (WorkDirMode == WorkDirMode.Folder && workDir.Folder.Length == 0)
        {
            errors.Add("Укажите временную папку.");
        }

        if (errors.Count > 0)
        {
            Message = string.Join("\n", errors);
            return;
        }

        // Снятые галочки — пропуск (в том же порядке, что и серии плана)
        var episodes = plan.Episodes.Select((e, i) => selected.Items[i].Status == PlanItemStatus.Skip && e.Status == PlanItemStatus.Run
            ? e with { Status = PlanItemStatus.Skip, Reason = "снята галочка" }
            : e).ToList();
        var toRun = plan with { Episodes = episodes };
        var quality = settings.FixedCq is { } cq ? $"постоянное CQ {cq.ToString("0.##", CultureInfo.InvariantCulture)}" : "подбор CQ под размер";
        var temp = WorkDirMode switch
        {
            WorkDirMode.RamDisk => $"RAM-диск {workDir.RamDiskGb} ГБ",
            WorkDirMode.Folder => $"папка {workDir.Folder}",
            _ => "рядом с выходом",
        };
        var confirm = $"{Summary}\n\nКачество: {quality}, {(UseNvenc ? $"NVENC {NvencPreset}" : "процессор (libx264)")}.\n"
            + $"Временные файлы: {temp}." + (Shutdown ? "\nПо завершении компьютер выключится." : "") + "\n\nНачать?";
        if (!await Shell.Dialogs.ConfirmAsync("HLS", confirm, "Начать", "Отмена"))
        {
            return;
        }

        var services = Shell.Services;
        if (WorkDirMode == WorkDirMode.RamDisk && services.Imdisk is not null && services.ImDiskAdmin is ElevatedImDisk elevated)
        {
            // без прав администратора: разрешение Windows — сейчас, пока пользователь у экрана, а не когда дойдёт очередь
            try
            {
                await elevated.EnsureStartedAsync();
            }
            catch (WorkDirException ex)
            {
                Message = ex.Message;
                return;
            }
        }

        var runner = new HlsRunner(services.Runner, services.Tools, services.Probe, services.Logs, settings);
        var provider = WorkDirProviders.Create(workDir, services.Runner, services.Imdisk, services.ImDiskAdmin);
        Func<CancellationToken, Task<bool>>? shutdown = Shutdown ? ct => new ShutdownService(services.Runner).ScheduleAsync(ct) : null;
        Func<CancellationToken, Task<IReadOnlyList<char>>>? cleanup = OperatingSystem.IsWindows()
            ? ct => ImDiskRamDisk.CleanupOrphansAsync(
                new ImDisk(services.Runner, services.Imdisk), new RamDiskStateFile(RamDiskStateFile.DefaultPath), services.ImDiskAdmin, cancellationToken: ct)
            : null;
        Enqueue("HLS", HlsJobs.Run(toRun, runner, provider, calibrates: settings.FixedCq is null, shutdown, cleanup));
    }

    private void SetGroups(IReadOnlyList<HlsGroup> groups)
    {
        _rebuilding = true;
        Groups.Clear();
        foreach (var group in groups)
        {
            Groups.Add(new HlsGroupViewModel(this, group));
        }

        _rebuilding = false;
        Replan();
    }

    private void SetMode(bool selected, WorkDirMode mode)
    {
        if (selected)
        {
            WorkDirMode = mode;
        }
    }

    private void UpdateWarning()
    {
        var warnings = new List<string>();
        if (WorkDirMode == WorkDirMode.RamDisk && Shell.Services.Imdisk is null)
        {
            warnings.Add("ImDisk не найден — RAM-диск не создать: поставьте его в «Настройках» («Установить») или выберите папку / «рядом с выходом».");
        }

        if (UseNvenc && Shell.Services.Tools.Ffmpeg is null)
        {
            warnings.Add("ffmpeg не найден — поставьте его в «Настройках» («Установить») или укажите путь.");
        }

        Warning = warnings.Count > 0 ? string.Join("\n", warnings) : null;
    }
}
