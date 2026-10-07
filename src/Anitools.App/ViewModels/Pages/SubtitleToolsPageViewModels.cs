using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using Anitools.Core.Operations.AssEdit;
using Anitools.Core.Operations.Common;
using Anitools.Core.Operations.SubShift;
using Anitools.Core.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Material.Icons;

namespace Anitools.App.ViewModels.Pages;

/// <summary>«Сдвиг» субтитров (§4.11, subtitle_delay+1s.py): .srt/.ass/.ssa папки → subs_fixed\ со сдвигом времени.</summary>
public sealed partial class SubShiftPageViewModel(IShell shell) : PageViewModel(shell, "Сдвиг субтитров", MaterialIconKind.TimerEditOutline)
{
    public ObservableCollection<string> Files { get; } = [];

    [ObservableProperty]
    public partial string Seconds { get; set; } = AudioShiftPageViewModel.Text(shell.Services.Settings.SubShiftSeconds);

    protected override async Task LoadAsync(string folder, CancellationToken cancellationToken)
    {
        var files = await Task.Run(() => SubtitleShift.ListFiles(folder), cancellationToken);
        if (files.Count == 0)
        {
            throw new PlanException("Нет субтитров (.srt, .ass, .ssa) в папке.");
        }

        Files.Clear();
        foreach (var file in files)
        {
            Files.Add($"{Path.GetFileName(file)} → {Path.Combine(SubtitleShift.OutputFolderName, Path.GetFileName(file))}");
        }
    }

    protected override void Clear() => Files.Clear();

    /// <summary>Сдвиг — сразу (файлы маленькие), готовые в subs_fixed перезаписываются.</summary>
    [RelayCommand]
    private async Task RunAsync()
    {
        var errors = new List<string>();
        var seconds = SettingsPageViewModel.Number(Seconds, "Сдвиг", errors, -3600, 3600);
        if (errors.Count > 0 || Files.Count == 0)
        {
            Message = errors.Count > 0 ? string.Join("\n", errors) : Message;
            return;
        }

        var folder = Folder;
        var results = await Task.Run(() => SubtitleShift.Execute(folder, seconds));
        var failed = results.Where(r => r.Error is not null).ToList();
        Message = failed.Count > 0 ? "Не вышло: " + string.Join("; ", failed.Select(f => $"{Path.GetFileName(f.Source)} — {f.Error}")) : null;
        Notice = $"Сдвинуто на {seconds.ToString("+0.###;−0.###", CultureInfo.InvariantCulture)} с: {results.Count - failed.Count} из {results.Count} → {SubtitleShift.OutputFolderName}";
    }
}

/// <summary>Значение поля (стиль или актёр) с галочкой и примером файла.</summary>
public sealed partial class AssValueRowViewModel(AssFieldValue value) : ObservableObject
{
    public string Value { get; } = value.Value;

    /// <summary>Пустое значение показывается как «(пусто)».</summary>
    public string Display => Value.Length > 0 ? Value : "(пусто)";

    public string Example { get; } = value.ExampleFile;

    [ObservableProperty]
    public partial bool IsChecked { get; set; }
}

/// <summary>
/// «Чистка стилей» (§4.11, edit_styles.py): строки Dialogue .ass папки — оставить или удалить по стилю или актёру;
/// оригиналы — в ass_backup_&lt;дата&gt;.
/// </summary>
public sealed partial class AssEditPageViewModel(IShell shell) : PageViewModel(shell, "Чистка стилей", MaterialIconKind.Broom)
{
    private AssEditInspection? _inspection;
    private Dictionary<string, string> _texts = [];

    public ObservableCollection<AssValueRowViewModel> Values { get; } = [];

    [ObservableProperty]
    public partial AssField Field { get; set; } = AssField.Style;

    /// <summary>true — оставить отмеченные (остальное удалить), false — удалить отмеченные.</summary>
    [ObservableProperty]
    public partial bool KeepSelected { get; set; } = true;

    /// <summary>«Будет удалено строк: 412 в 12 файлах · бэкап → ass_backup_…».</summary>
    [ObservableProperty]
    public partial string Summary { get; set; } = "";

    [ObservableProperty]
    public partial int RemoveCount { get; set; }

    public bool IsStyle
    {
        get => Field == AssField.Style;
        set
        {
            if (value)
            {
                Field = AssField.Style;
            }
        }
    }

    public bool IsActor
    {
        get => Field == AssField.Actor;
        set
        {
            if (value)
            {
                Field = AssField.Actor;
            }
        }
    }

    public bool RemovesSelected
    {
        get => !KeepSelected;
        set => KeepSelected = !value;
    }

    protected override async Task LoadAsync(string folder, CancellationToken cancellationToken)
    {
        var field = Field;
        var (inspection, texts) = await Task.Run(
            () =>
            {
                var inspection = AssEditOperation.Inspect(folder, field);
                return (inspection, inspection.Files.ToDictionary(f => f, f => File.ReadAllText(f)));
            },
            cancellationToken);
        if (inspection.Files.Count == 0 && inspection.Unreadable.Count == 0)
        {
            throw new PlanException("Нет .ass файлов в папке.");
        }

        _inspection = inspection;
        _texts = texts;
        foreach (var row in Values)
        {
            row.PropertyChanged -= OnValueChanged;
        }

        Values.Clear();
        foreach (var value in inspection.Values)
        {
            var row = new AssValueRowViewModel(value);
            row.PropertyChanged += OnValueChanged;
            Values.Add(row);
        }

        Message = inspection.Unreadable.Count > 0
            ? "Не в UTF-8, не тронутся: " + string.Join(", ", inspection.Unreadable.Select(u => Path.GetFileName(u.File)))
            : null;
        Recount();
    }

    protected override void Clear()
    {
        _inspection = null;
        _texts = [];
        Values.Clear();
        Summary = "";
        RemoveCount = 0;
    }

    partial void OnFieldChanged(AssField value)
    {
        OnPropertyChanged(nameof(IsStyle));
        OnPropertyChanged(nameof(IsActor));
        Invalidate();
    }

    partial void OnKeepSelectedChanged(bool value)
    {
        OnPropertyChanged(nameof(RemovesSelected));
        Recount();
    }

    [RelayCommand]
    private async Task RunAsync()
    {
        if (_inspection is not { } inspection || RemoveCount == 0)
        {
            return;
        }

        var remove = Remove(inspection);
        if (!await Shell.Dialogs.ConfirmAsync("Чистка стилей", $"{Summary}\n\nПравить файлы?", "Править", "Отмена"))
        {
            return;
        }

        var result = await Task.Run(() => AssEditOperation.Execute(inspection, remove));
        Notice = $"Удалено строк: {result.TotalRemoved} · оригиналы — в {Path.GetFileName(result.BackupFolder)}"
            + (result.Failed.Count > 0 ? " · не вышло: " + string.Join("; ", result.Failed.Select(f => $"{Path.GetFileName(f.File)} — {f.Error}")) : "");
        await RefreshAsync();
    }

    private IReadOnlySet<string> Remove(AssEditInspection inspection) =>
        AssEditOperation.ValuesToRemove(inspection, Values.Where(v => v.IsChecked).Select(v => v.Value), KeepSelected);

    private void OnValueChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AssValueRowViewModel.IsChecked))
        {
            Recount();
        }
    }

    /// <summary>Сколько строк уйдёт — по текстам, прочитанным при загрузке.</summary>
    private void Recount()
    {
        if (_inspection is not { } inspection)
        {
            return;
        }

        // Ничего не отмечено — ничего не делаем (иначе «оставить отмеченные» стёр бы все строки)
        if (!Values.Any(v => v.IsChecked))
        {
            RemoveCount = 0;
            Summary = KeepSelected ? "Отметьте, что оставить." : "Отметьте, что удалить.";
            return;
        }

        var remove = Remove(inspection);
        var counts = _texts.Values.Select(t => AssEditOperation.RemoveLines(t, inspection.Field, remove).Removed).ToList();
        RemoveCount = counts.Sum();
        Summary = RemoveCount == 0
            ? "Ни одна строка не удаляется."
            : $"Будет удалено строк: {RemoveCount} в {RuText.Plural(counts.Count(c => c > 0), "файле", "файлах", "файлах")} · оригиналы — в ass_backup_<дата>";
    }
}
