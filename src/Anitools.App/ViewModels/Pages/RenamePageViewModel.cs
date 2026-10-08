using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using Anitools.Core.Media;
using Anitools.Core.Operations.Common;
using Anitools.Core.Operations.Rename;
using Anitools.Core.Parsing;
using Anitools.Core.Templates;
using Anitools.Core.Text;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Material.Icons;

namespace Anitools.App.ViewModels.Pages;

/// <summary>Строка переименования: старое имя, номер серии (можно поправить руками), новое имя или почему нет.</summary>
public sealed partial class RenameRowViewModel : ObservableObject
{
    private string? _auto;
    private bool _settingAuto;

    public RenameRowViewModel(string file, string? autoEpisode)
    {
        File = file;
        SetAuto(autoEpisode);
    }

    public string File { get; }

    /// <summary>Номер серии; правленый руками берётся как есть (без сдвига нумерации), пусто — файл пропускается.</summary>
    [ObservableProperty]
    public partial string Episode { get; set; } = "";

    /// <summary>Номер правили руками.</summary>
    [ObservableProperty]
    public partial bool IsManual { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNewName))]
    public partial string NewName { get; set; } = "";

    public bool HasNewName => NewName.Length > 0;

    [ObservableProperty]
    public partial RenameRowStatus Status { get; set; }

    [ObservableProperty]
    public partial string Note { get; set; } = "";

    public bool WillRename => Status == RenameRowStatus.Rename;

    public bool IsProblem => Status is RenameRowStatus.NoNumber or RenameRowStatus.Conflict or RenameRowStatus.Invalid;

    /// <summary>Номер, найденный автоматически (с учётом начала нумерации); ручной не трогается.</summary>
    public void SetAuto(string? episode)
    {
        _auto = episode;
        if (!IsManual)
        {
            _settingAuto = true;
            Episode = episode ?? "";
            _settingAuto = false;
        }
    }

    public void Apply(RenameRow row)
    {
        NewName = row.NewName ?? "";
        Status = row.Status;
        Note = row.Status switch
        {
            RenameRowStatus.Unchanged => "уже так называется",
            RenameRowStatus.Rename => "",
            _ => "⚠ " + (row.Reason ?? ""),
        };
        OnPropertyChanged(nameof(WillRename));
        OnPropertyChanged(nameof(IsProblem));
    }

    partial void OnEpisodeChanged(string value)
    {
        if (!_settingAuto)
        {
            IsManual = value != (_auto ?? "");
        }
    }
}

/// <summary>
/// П.5 «Переименовать» (§4.7): новое имя — по шаблону (стандартный — «Название - 01.ext», как в оригинале).
/// Название — вручную или оригинальное с Shikimori; номер в каждой строке можно поправить. Шаблон правится в редакторе
/// (перенос из Anime Uploader) и запоминается сам, есть свои пресеты. Переименование сразу (не задачей), с журналом для отката.
/// </summary>
public sealed partial class RenamePageViewModel : PageViewModel
{
    private IReadOnlyList<string> _files = [];
    private string? _loadedFolder;
    private bool _rebuilding;

    /// <summary>Шаблон, который сейчас записан в настройках этой страницей (null — стандартный).</summary>
    private string? _savedTemplate;
    private CancellationTokenSource? _templateSave;

    /// <summary>Что в видео папки (для {разрешение}, {видео}…); читается, только если шаблону это нужно.</summary>
    private IReadOnlyDictionary<string, MediaInfo> _media = new Dictionary<string, MediaInfo>();
    private bool _mediaRead;
    private CachedMediaProbe? _probe;
    private CancellationTokenSource? _reading;

    public RenamePageViewModel(IShell shell)
        : base(shell, "Переименовать", MaterialIconKind.RenameOutline)
    {
        var rename = shell.Services.Settings.Rename;
        _savedTemplate = rename.Template;
        Template = rename.Template ?? RenameTemplate.Default;
        Presets = rename.Presets;
        shell.Services.SettingsChanged += () => Dispatcher.UIThread.Post(OnSettingsChanged);
    }

    /// <summary>Сколько ждать после правки шаблона, прежде чем записать его в настройки.</summary>
    private static readonly TimeSpan TemplateSaveDelay = TimeSpan.FromSeconds(0.6);

    public ObservableCollection<RenameRowViewModel> Rows { get; } = [];

    [ObservableProperty]
    public partial string BaseName { get; set; } = "";

    [ObservableProperty]
    public partial string NumberingStart { get; set; } = "1";

    /// <summary>Приписка через точку: «надписи» → «Тайтл - 01.надписи.ass».</summary>
    [ObservableProperty]
    public partial string Suffix { get; set; } = "";

    /// <summary>Сезон для {сезон}: из имён видео («S2») или 1.</summary>
    [ObservableProperty]
    public partial string Season { get; set; } = "1";

    /// <summary>Шаблон имени без расширения (<see cref="RenameTemplate"/>).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UsesTitle), nameof(UsesEpisode), nameof(UsesSeason), nameof(UsesSuffix))]
    public partial string Template { get; set; } = "";

    /// <summary>Ошибки и предупреждения шаблона — для редактора.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<TemplateIssue> TemplateIssues { get; set; } = [];

    /// <summary>Свои пресеты шаблонов (встроенные — <see cref="BuiltInPresets"/>).</summary>
    [ObservableProperty]
    public partial IReadOnlyList<TemplatePreset> Presets { get; set; } = [];

    /// <summary>Читаются параметры видео (ffprobe) — переименовать пока нельзя.</summary>
    [ObservableProperty]
    public partial bool IsReadingVideo { get; set; }

    public static string DefaultTemplate => RenameTemplate.Default;

    public static IReadOnlyList<TemplateVariable> TemplateVariables => RenameTemplate.Variables;

    public static IReadOnlyList<TemplateVariable> TemplateFormatted => RenameTemplate.Formatted;

    public static IReadOnlyList<TemplateCondition> TemplateConditions => RenameTemplate.Conditions;

    public static IReadOnlyList<TemplatePreset> BuiltInPresets => RenameTemplate.BuiltInPresets;

    // Поля — только для переменных, которые есть в шаблоне (как блок {озвучки} в Anime Uploader)
    public bool UsesTitle => TextTemplate.Uses(Template, RenameTemplate.Title);

    public bool UsesEpisode => TextTemplate.Uses(Template, RenameTemplate.Episode);

    public bool UsesSeason => TextTemplate.Uses(Template, RenameTemplate.Season);

    public bool UsesSuffix => TextTemplate.Uses(Template, RenameTemplate.Suffix);

    [ObservableProperty]
    public partial int RenameCount { get; set; }

    [ObservableProperty]
    public partial string Summary { get; set; } = "";

    /// <summary>«Откатить последнее переименование: 12 файлов в D:\anime\X».</summary>
    [ObservableProperty]
    public partial string? UndoText { get; set; }

    [ObservableProperty]
    public partial bool IsSearching { get; set; }

    private string JournalDirectory => Shell.Services.Logs.Directory;

    protected override async Task LoadAsync(string folder, CancellationToken cancellationToken)
    {
        var files = await Task.Run(() => RenameOperation.ListFiles(folder), cancellationToken);
        if (files.Count == 0)
        {
            throw new PlanException("Не найдено подходящих файлов в папке.");
        }

        var newFolder = folder != _loadedFolder;
        _loadedFolder = folder;
        _reading?.Cancel();
        IsReadingVideo = false;
        _probe = newFolder || _probe is null ? new CachedMediaProbe(Shell.Services.Probe) : _probe;
        _media = RenameTemplate.UsesMedia(Template) ? await ReadVideoAsync(folder, files, cancellationToken) : new Dictionary<string, MediaInfo>();
        _mediaRead = RenameTemplate.UsesMedia(Template);
        _files = files;
        _rebuilding = true;
        if (newFolder)
        {
            // Новая папка — поля заново; после переименования в той же папке они остаются
            NumberingStart = "1";
            Suffix = "";
            Season = RenameOperation.SeasonHint(files) is { Length: > 0 } season ? season : "1";
        }

        foreach (var row in Rows)
        {
            row.PropertyChanged -= OnRowChanged;
        }

        Rows.Clear();
        var start = Start() ?? 1;
        foreach (var file in files)
        {
            var row = new RenameRowViewModel(file, RenameOperation.AutoEpisode(file, start));
            row.PropertyChanged += OnRowChanged;
            Rows.Add(row);
        }

        if (newFolder || BaseName.Trim().Length == 0)
        {
            BaseName = TitleText.FileNameSafe(GuessTitle(files, folder));
        }

        _rebuilding = false;
        UpdateUndo();
        Replan();
    }

    protected override void Clear()
    {
        _files = [];
        Rows.Clear();
        RenameCount = 0;
        Summary = "";
        UpdateUndo();
    }

    partial void OnBaseNameChanged(string value) => Replan();

    partial void OnSuffixChanged(string value) => Replan();

    partial void OnSeasonChanged(string value) => Replan();

    partial void OnTemplateChanged(string value)
    {
        TemplateIssues = RenameTemplate.Check(value);
        if (RenameTemplate.UsesMedia(value) && !_mediaRead && _files.Count > 0)
        {
            _ = ReadVideoInBackgroundAsync(Folder, _files);
        }

        Replan();
        SaveTemplateLater();
    }

    /// <summary>
    /// Параметры видео (ffprobe, по 4 файла сразу, с кэшем по файлу). Не прочиталось ни одно — подсказка:
    /// переменные из видео будут пустыми.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, MediaInfo>> ReadVideoAsync(string folder, IReadOnlyList<string> files, CancellationToken cancellationToken)
    {
        var probe = _probe ??= new CachedMediaProbe(Shell.Services.Probe);
        var videos = files.Where(RenameOperation.IsVideo).ToList();
        var media = new ConcurrentDictionary<string, MediaInfo>();
        await Parallel.ForEachAsync(videos, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = cancellationToken }, async (file, ct) =>
        {
            try
            {
                media[file] = await probe.ProbeAsync(Path.Combine(folder, file), ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // нет ffprobe или файл не читается — переменные из видео у него пустые
            }
        });
        if (videos.Count > 0 && media.IsEmpty)
        {
            Shell.Toast("Не удалось прочитать видео (ffprobe) — {разрешение}, {видео}, {аудио}, {каналы} и {мульти} будут пустыми.", ToastKind.Warn);
        }

        return media;
    }

    /// <summary>Шаблону понадобилось видео, а папка уже открыта — читаем, пока строки ждут.</summary>
    private async Task ReadVideoInBackgroundAsync(string folder, IReadOnlyList<string> files)
    {
        _reading?.Cancel();
        var cts = _reading = new CancellationTokenSource();
        _mediaRead = true;
        IsReadingVideo = true;
        try
        {
            var media = await ReadVideoAsync(folder, files, cts.Token);
            if (_reading == cts && folder == _loadedFolder)
            {
                _media = media;
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (_reading == cts)
            {
                IsReadingVideo = false;
                Replan();
            }
        }
    }

    partial void OnNumberingStartChanged(string value)
    {
        if (Start() is { } start)
        {
            var rebuilding = _rebuilding;
            _rebuilding = true;
            foreach (var row in Rows)
            {
                row.SetAuto(RenameOperation.AutoEpisode(row.File, start));
            }

            _rebuilding = rebuilding;
        }

        Replan();
    }

    [RelayCommand]
    private async Task SearchShikimoriAsync()
    {
        if (_files.Count == 0)
        {
            return;
        }

        // Ищем то, что в поле: его могли поправить руками (имена файлов бывают вида «01. A.mkv»)
        var picker = BaseName.Trim() is { Length: > 0 } typed
            ? new ShikimoriPickerViewModel(Shell.Services.Shikimori, typed, "Базовое название")
            : new ShikimoriPickerViewModel(Shell.Services.Shikimori, GuessTitle(_files, Folder));
        var choice = await Shell.Dialogs.PickShikimoriAsync(picker);
        string? original = null;
        if (choice.Anime is { } anime)
        {
            original = anime.Name;
        }
        else if (choice.ManualId is { } id)
        {
            IsSearching = true;
            try
            {
                original = await Shell.Services.Shikimori.OriginalNameAsync(id);
            }
            finally
            {
                IsSearching = false;
            }
        }

        if (original is { Length: > 0 })
        {
            BaseName = TitleText.FileNameSafe(original);
            Shell.Toast($"Оригинальное название с Shikimori: {original}");
        }
        else if (!choice.IsSkip)
        {
            Shell.Toast("Не удалось получить название с Shikimori — введите вручную.", ToastKind.Warn);
        }
    }

    [RelayCommand]
    private async Task RenameAsync()
    {
        if (IsReadingVideo)
        {
            return;
        }

        var rows = Plan(out _);
        if (rows is null || rows.All(r => r.Status != RenameRowStatus.Rename))
        {
            return;
        }

        if (Shell.Services.Jobs.Jobs.Any(j => !j.IsFinished && SamePath(j.Folder, Folder))
            && !await Shell.Dialogs.ConfirmAsync(
                "В папке идёт задача",
                "В этой папке выполняется или ждёт задача — переименование может ей помешать. Всё равно переименовать?",
                "Переименовать",
                "Отмена"))
        {
            return;
        }

        SaveTemplateNow();
        var result = await Task.Run(() => RenameOperation.Execute(Folder, rows, JournalDirectory));
        Toast(result, "Переименовано");
        await RefreshAsync();
    }

    [RelayCommand]
    private async Task UndoAsync()
    {
        if (RenameJournal.Latest(JournalDirectory) is not { } journal)
        {
            return;
        }

        var result = await Task.Run(() => RenameJournal.Undo(journal));
        Toast(result, "Возвращено старое имя");
        await RefreshAsync();
    }

    private void OnRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!_rebuilding && e.PropertyName == nameof(RenameRowViewModel.Episode))
        {
            Replan();
        }
    }

    /// <summary>
    /// Название, которое встречается в именах видео чаще всего: в папке бывают NCOP.mkv и т.п., а оригинал брал
    /// первое видео по алфавиту. У каждой серии своё «название» (имена вида «01. Kill the King.mkv») — берётся
    /// название из имени папки. Видео нет — как у оригинала (<see cref="RenameOperation.TitleHint"/>).
    /// </summary>
    public static string GuessTitle(IReadOnlyList<string> files, string? folder = null)
    {
        string[] videos = [".mkv", ".mp4", ".avi", ".mov", ".m2ts", ".ts", ".webm"];
        var titles = files
            .Where(f => videos.Contains(MediaFiles.Suffix(f), StringComparer.OrdinalIgnoreCase))
            .Select(TitleText.AnimeTitle)
            .Where(t => t.Length > 0)
            .GroupBy(t => t, StringComparer.Ordinal)
            .OrderByDescending(g => g.Count())
            .ToList();
        if (titles.Count > 1 && titles[0].Count() == 1
            && Path.GetFileName(Path.TrimEndingDirectorySeparator(folder ?? "")) is { Length: > 0 } name
            && TitleText.AnimeTitle(name) is { Length: > 0 } fromFolder)
        {
            return fromFolder;
        }

        return titles.Count > 0 ? titles[0].Key : RenameOperation.TitleHint(files);
    }

    private int? Start() =>
        int.TryParse(NumberingStart.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n >= 1 ? n : null;

    /// <summary>Строки плана по текущим полям; неверный ввод — сообщение, что поправить в строках, и null.</summary>
    private IReadOnlyList<RenameRow>? Plan(out string problem)
    {
        problem = "";
        if (_files.Count == 0)
        {
            return null;
        }

        if (RenameTemplate.HasErrors(TemplateIssues))
        {
            // Ошибки видны в редакторе шаблона — дублировать их сообщением не нужно
            Message = null;
            problem = "исправьте шаблон имени";
            return null;
        }

        problem = "исправьте поля выше";
        if (Start() is not { } start)
        {
            Message = "Начало нумерации — положительное целое число.";
            return null;
        }

        try
        {
            var manual = Rows.Where(r => r.IsManual).ToDictionary(r => r.File, r => r.Episode);
            var rows = RenameOperation.Plan(Folder, _files, new RenameOptions
            {
                BaseName = BaseName,
                NumberingStart = start,
                Suffix = Suffix.Trim().Length > 0 ? Suffix.Trim() : null,
                Season = Season,
                Template = Template,
                Media = _media,
                ManualNumbers = manual,
            });
            Message = null;
            return rows;
        }
        catch (PlanException ex)
        {
            Message = ex.Message;
            return null;
        }
    }

    private void Replan()
    {
        if (_rebuilding || _files.Count == 0)
        {
            return;
        }

        if (IsReadingVideo && RenameTemplate.UsesMedia(Template))
        {
            RenameCount = 0;
            Summary = "Читаю параметры видео…";
            return;
        }

        var rows = Plan(out var problem);
        if (rows is null)
        {
            foreach (var row in Rows)
            {
                row.Apply(new RenameRow(row.File, null, null, RenameRowStatus.NoNumber, problem));
            }

            RenameCount = 0;
            Summary = "";
            return;
        }

        for (var i = 0; i < rows.Count && i < Rows.Count; i++)
        {
            Rows[i].Apply(rows[i]);
        }

        RenameCount = rows.Count(r => r.Status == RenameRowStatus.Rename);
        var skipped = rows.Count(r => r.Status == RenameRowStatus.NoNumber);
        var conflicts = rows.Count(r => r.Status == RenameRowStatus.Conflict);
        var invalid = rows.Count(r => r.Status == RenameRowStatus.Invalid);
        var parts = new List<string> { RuText.Plural(rows.Count, "файл", "файла", "файлов"), $"переименовать {RenameCount}" };
        if (skipped > 0)
        {
            parts.Add($"без номера {skipped}");
        }

        if (conflicts > 0)
        {
            parts.Add(RuText.Plural(conflicts, "конфликт", "конфликта", "конфликтов"));
        }

        if (invalid > 0)
        {
            parts.Add($"негодное имя {invalid}");
        }

        Summary = string.Join(" · ", parts);
    }

    /// <summary>Шаблон без ошибок запоминается сам — чуть погодя, чтобы не писать файл на каждую букву.</summary>
    private void SaveTemplateLater()
    {
        _templateSave?.Cancel();
        if (RenameTemplate.HasErrors(TemplateIssues))
        {
            return;
        }

        var cts = _templateSave = new CancellationTokenSource();
        _ = SaveAfterDelayAsync(cts.Token);

        async Task SaveAfterDelayAsync(CancellationToken ct)
        {
            try
            {
                await Task.Delay(TemplateSaveDelay, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            SaveTemplateNow();
        }
    }

    /// <summary>Записать шаблон в настройки (с ошибкой — не записывается, остаётся прошлый); стандартный — как null.</summary>
    internal void SaveTemplateNow()
    {
        _templateSave?.Cancel();
        if (RenameTemplate.HasErrors(TemplateIssues))
        {
            return;
        }

        var template = Template == RenameTemplate.Default ? null : Template;
        _savedTemplate = template;
        if (Shell.Services.Settings.Rename.Template != template)
        {
            Shell.Services.UpdateSettingsQuietly(s => s with { Rename = s.Rename with { Template = template } });
        }
    }

    /// <summary>Настройки сменили снаружи (импорт): свежие пресеты, и шаблон — если он теперь другой.</summary>
    private void OnSettingsChanged()
    {
        var rename = Shell.Services.Settings.Rename;
        Presets = rename.Presets;
        if (rename.Template != _savedTemplate)
        {
            _savedTemplate = rename.Template;
            Template = rename.Template ?? RenameTemplate.Default;
        }
    }

    /// <summary>«Сохранить как пресет…»: имя спрашивается; такое же имя у своего — заменить после вопроса.</summary>
    [RelayCommand]
    private async Task SavePresetAsync(string? template)
    {
        template ??= Template;
        if (RenameTemplate.HasErrors(RenameTemplate.Check(template)))
        {
            Shell.Toast("В шаблоне ошибка — сначала исправьте его.", ToastKind.Warn);
            return;
        }

        if (await Shell.Dialogs.PromptAsync("Сохранить пресет", $"Имя для шаблона {template}", "", "Сохранить") is not { } name)
        {
            return;
        }

        if (RenameTemplate.BuiltInPresets.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            Shell.Toast($"«{name}» — готовый пресет, выберите другое имя.", ToastKind.Warn);
            return;
        }

        var own = Shell.Services.Settings.Rename.Presets;
        var same = own.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        if (same is not null && !await Shell.Dialogs.ConfirmAsync("Пресет уже есть", $"Заменить пресет «{same.Name}»?\nБыло: {same.Template}\nСтанет: {template}", "Заменить", "Отмена"))
        {
            return;
        }

        if (SavePresets([.. own.Where(p => p != same), new TemplatePreset(name, template)]))
        {
            Shell.Toast($"Пресет «{name}» сохранён — он в «Пресеты ▾».", ToastKind.Ok);
        }
    }

    [RelayCommand]
    private async Task DeletePresetAsync(TemplatePreset? preset)
    {
        if (preset is null || !await Shell.Dialogs.ConfirmAsync("Удалить пресет?", $"«{preset.Name}»: {preset.Template}", "Удалить", "Отмена"))
        {
            return;
        }

        if (SavePresets([.. Shell.Services.Settings.Rename.Presets.Where(p => p != preset)]))
        {
            Shell.Toast($"Пресет «{preset.Name}» удалён.");
        }
    }

    private bool SavePresets(IReadOnlyList<TemplatePreset> presets)
    {
        try
        {
            var settings = Shell.Services.Settings;
            Shell.Services.SaveSettings(settings with { Rename = settings.Rename with { Presets = presets } });
            Presets = Shell.Services.Settings.Rename.Presets;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Shell.Toast($"Не удалось записать настройки: {ex.Message}", ToastKind.Warn);
            return false;
        }
    }

    private void UpdateUndo()
    {
        UndoText = RenameJournal.Latest(JournalDirectory) is { } journal && RenameJournal.Describe(journal) is var (folder, count)
            ? $"Откатить последнее переименование: {RuText.Plural(count, "файл", "файла", "файлов")} в {folder}"
            : null;
    }

    private void Toast(RenameResult result, string what)
    {
        var text = $"{what}: {result.Renamed.Count}.";
        if (result.Failed.Count > 0)
        {
            text += " Не вышло: " + string.Join("; ", result.Failed.Select(f => $"{f.File} — {f.Error}"));
        }

        Shell.Toast(text, result.Failed.Count > 0 ? ToastKind.Warn : ToastKind.Ok);
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(a), Path.TrimEndingDirectorySeparator(b),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
