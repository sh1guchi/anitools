using System.Collections.ObjectModel;
using System.ComponentModel;
using Anitools.Core.Operations.Common;
using Anitools.Core.Text;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Anitools.App.ViewModels;

/// <summary>Строка превью: файл, размер и что с ним будет («→ Video only\…», «уже готово — пропуск», «✗ причина»).</summary>
public sealed partial class PlanRowViewModel : ObservableObject
{
    public PlanRowViewModel(PlanItem item, string folder, long? size)
    {
        Item = item;
        Size = size is { } bytes ? RuText.FileSize(bytes) : "";
        Target = item.Status switch
        {
            // Пишется во временный «.part» и переименовывается после успеха — показываем итоговое имя
            PlanItemStatus.Run when item.RenameOnSuccess is var (_, final) =>
                "→ " + Relative(folder, final) + (item.Reason is { } note ? $" ({note})" : ""),
            PlanItemStatus.Run => "→ " + string.Join(", ", item.Outputs.Select(o => Relative(folder, o))) + (item.Reason is { } why ? $" ({why})" : ""),
            PlanItemStatus.Skip => $"{item.Reason ?? "нечего делать"} — пропуск",
            _ => "✗ " + (item.Reason ?? "ошибка"),
        };
        IsChecked = item.Status == PlanItemStatus.Run;
    }

    public PlanItem Item { get; }

    public string Label => Item.Label;

    public string Size { get; }

    public string Target { get; }

    public bool CanRun => Item.Status == PlanItemStatus.Run;

    public bool IsSkip => Item.Status == PlanItemStatus.Skip;

    public bool IsError => Item.Status == PlanItemStatus.Error;

    [ObservableProperty]
    public partial bool IsChecked { get; set; }

    private static string Relative(string folder, string path)
    {
        var relative = Path.GetRelativePath(folder, path);
        return relative.StartsWith("..", StringComparison.Ordinal) ? path : relative;
    }
}

/// <summary>Превью плана с галочками: снятая галочка — файл не трогать.</summary>
public sealed partial class PlanPreviewViewModel : ObservableObject
{
    private bool _updating;

    public ObservableCollection<PlanRowViewModel> Rows { get; } = [];

    public OperationPlan? Plan { get; private set; }

    /// <summary>Сколько шагов отмечено к запуску.</summary>
    [ObservableProperty]
    public partial int CheckedCount { get; set; }

    /// <summary>«12 файлов · к запуску 11 · пропуск 1».</summary>
    [ObservableProperty]
    public partial string Summary { get; set; } = "";

    /// <summary>Галочка «все»: null — отмечены не все.</summary>
    public bool? AllChecked
    {
        get
        {
            var runnable = Rows.Where(r => r.CanRun).ToList();
            return runnable.Count == 0 || runnable.All(r => r.IsChecked) ? true : runnable.Any(r => r.IsChecked) ? null : false;
        }

        set
        {
            _updating = true;
            foreach (var row in Rows.Where(r => r.CanRun))
            {
                row.IsChecked = value != false;
            }

            _updating = false;
            Recount();
        }
    }

    public bool HasRows => Rows.Count > 0;

    public void Show(OperationPlan plan, Func<string, long?> sizeOf)
    {
        Detach();
        Rows.Clear();
        Plan = plan;
        foreach (var item in plan.Items)
        {
            var row = new PlanRowViewModel(item, plan.Folder, sizeOf(item.Source));
            row.PropertyChanged += OnRowChanged;
            Rows.Add(row);
        }

        Recount();
    }

    public void Clear()
    {
        Detach();
        Rows.Clear();
        Plan = null;
        Recount();
    }

    /// <summary>План только с отмеченными шагами (снятые — пропуск); отмеченных нет — null.</summary>
    public OperationPlan? Selected()
    {
        if (Plan is null || CheckedCount == 0)
        {
            return null;
        }

        var excluded = Rows.Where(r => r.CanRun && !r.IsChecked).Select(r => r.Item).ToHashSet();
        return Plan with
        {
            Items = [.. Plan.Items.Select(i => excluded.Contains(i) ? i with { Status = PlanItemStatus.Skip, Reason = "снята галочка" } : i)],
        };
    }

    private void OnRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!_updating && e.PropertyName == nameof(PlanRowViewModel.IsChecked))
        {
            Recount();
        }
    }

    private void Recount()
    {
        CheckedCount = Rows.Count(r => r.CanRun && r.IsChecked);
        var parts = new List<string>();
        if (Plan is not null)
        {
            parts.Add(RuText.Plural(Rows.Count, "шаг", "шага", "шагов"));
            parts.Add($"к запуску {CheckedCount}");
            if (Plan.SkipCount > 0)
            {
                parts.Add($"пропуск {Plan.SkipCount}");
            }

            if (Plan.ErrorCount > 0)
            {
                parts.Add(RuText.Plural(Plan.ErrorCount, "ошибка", "ошибки", "ошибок"));
            }
        }

        Summary = string.Join(" · ", parts);
        OnPropertyChanged(nameof(AllChecked));
        OnPropertyChanged(nameof(HasRows));
    }

    private void Detach()
    {
        foreach (var row in Rows)
        {
            row.PropertyChanged -= OnRowChanged;
        }
    }
}
