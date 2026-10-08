using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using Anitools.Core.Operations.Common;
using Anitools.Core.Operations.Rename;
using Anitools.Core.Parsing;
using Anitools.Core.Text;
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
    public partial string NewName { get; set; } = "";

    [ObservableProperty]
    public partial RenameRowStatus Status { get; set; }

    [ObservableProperty]
    public partial string Note { get; set; } = "";

    public bool WillRename => Status == RenameRowStatus.Rename;

    public bool IsProblem => Status is RenameRowStatus.NoNumber or RenameRowStatus.Conflict;

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
/// П.5 «Переименовать» (§4.7): «Название - 01.ext» по номеру серии. Название — вручную или оригинальное с Shikimori;
/// номер в каждой строке можно поправить. Переименование сразу (не задачей), с журналом для отката.
/// </summary>
public sealed partial class RenamePageViewModel(IShell shell) : PageViewModel(shell, "Переименовать", MaterialIconKind.RenameOutline)
{
    private IReadOnlyList<string> _files = [];
    private string? _loadedFolder;
    private bool _rebuilding;

    public ObservableCollection<RenameRowViewModel> Rows { get; } = [];

    [ObservableProperty]
    public partial string BaseName { get; set; } = "";

    [ObservableProperty]
    public partial string NumberingStart { get; set; } = "1";

    /// <summary>Приписка через точку: «надписи» → «Тайтл - 01.надписи.ass».</summary>
    [ObservableProperty]
    public partial string Suffix { get; set; } = "";

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
        _files = files;
        _rebuilding = true;
        if (newFolder)
        {
            // Новая папка — поля заново; после переименования в той же папке они остаются
            NumberingStart = "1";
            Suffix = "";
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
            BaseName = TitleText.FileNameSafe(GuessTitle(files));
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

        var picker = new ShikimoriPickerViewModel(Shell.Services.Shikimori, GuessTitle(_files));
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
        var rows = Plan();
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
    /// первое видео по алфавиту. Видео нет — как у оригинала (<see cref="RenameOperation.TitleHint"/>).
    /// </summary>
    public static string GuessTitle(IReadOnlyList<string> files)
    {
        string[] videos = [".mkv", ".mp4", ".avi", ".mov", ".m2ts", ".ts", ".webm"];
        var common = files
            .Where(f => videos.Contains(MediaFiles.Suffix(f), StringComparer.OrdinalIgnoreCase))
            .Select(TitleText.AnimeTitle)
            .Where(t => t.Length > 0)
            .GroupBy(t => t, StringComparer.Ordinal)
            .OrderByDescending(g => g.Count())
            .Select(g => g.Key)
            .FirstOrDefault();
        return common ?? RenameOperation.TitleHint(files);
    }

    private int? Start() =>
        int.TryParse(NumberingStart.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n >= 1 ? n : null;

    /// <summary>Строки плана по текущим полям; неверный ввод — сообщение и null.</summary>
    private IReadOnlyList<RenameRow>? Plan()
    {
        if (_files.Count == 0)
        {
            return null;
        }

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

        var rows = Plan();
        if (rows is null)
        {
            foreach (var row in Rows)
            {
                row.Apply(new RenameRow(row.File, null, null, RenameRowStatus.NoNumber, "исправьте поля выше"));
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
        var parts = new List<string> { RuText.Plural(rows.Count, "файл", "файла", "файлов"), $"переименовать {RenameCount}" };
        if (skipped > 0)
        {
            parts.Add($"без номера {skipped}");
        }

        if (conflicts > 0)
        {
            parts.Add(RuText.Plural(conflicts, "конфликт", "конфликта", "конфликтов"));
        }

        Summary = string.Join(" · ", parts);
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
