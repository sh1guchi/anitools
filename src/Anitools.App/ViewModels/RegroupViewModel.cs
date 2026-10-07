using System.Collections.ObjectModel;
using System.ComponentModel;
using Anitools.Core.Operations.Hls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Anitools.App.ViewModels;

/// <summary>Файл и название его группы (тайтла); пусто — файл не конвертировать.</summary>
public sealed partial class RegroupRowViewModel(HlsSourceFile file, string group) : ObservableObject
{
    public HlsSourceFile File { get; } = file;

    public string Name => File.Name;

    [ObservableProperty]
    public partial string Group { get; set; } = group;
}

/// <summary>
/// Перегруппировка (§4.9, _regroup_manually): у каждого файла — название группы; одинаковые названия — один тайтл,
/// пустое — файл пропускается. Порядок групп — по первому файлу.
/// </summary>
public sealed partial class RegroupViewModel : ObservableObject
{
    public RegroupViewModel(IReadOnlyList<HlsSourceFile> files, IReadOnlyList<HlsGroup> groups)
    {
        var groupOf = groups.SelectMany(g => g.Files.Select(f => (f.Path, g.Title))).ToDictionary(x => x.Path, x => x.Title, StringComparer.Ordinal);
        foreach (var file in files)
        {
            var row = new RegroupRowViewModel(file, groupOf.GetValueOrDefault(file.Path) ?? "");
            row.PropertyChanged += OnRowChanged;
            Rows.Add(row);
        }

        UpdateNames();
    }

    /// <summary>Закрыть диалог: true — применить.</summary>
    public event Action<bool>? Closed;

    public ObservableCollection<RegroupRowViewModel> Rows { get; } = [];

    /// <summary>Названия групп для выпадающего списка.</summary>
    public ObservableCollection<string> GroupNames { get; } = [];

    [ObservableProperty]
    public partial RegroupRowViewModel? Selected { get; set; }

    /// <summary>Группы по названиям (порядок — по первому файлу); файлы без названия не входят никуда.</summary>
    public IReadOnlyList<HlsGroup> Result() =>
        [.. Rows.Where(r => r.Group.Trim().Length > 0)
            .GroupBy(r => r.Group.Trim(), StringComparer.Ordinal)
            .Select(g => new HlsGroup(g.Key, [.. g.Select(r => r.File)]))];

    [RelayCommand]
    private void Apply() => Closed?.Invoke(true);

    [RelayCommand]
    private void Cancel() => Closed?.Invoke(false);

    /// <summary>Файлы без группы — в отдельную группу «Прочее» (как «остаток в отдельную группу» в оригинале).</summary>
    [RelayCommand]
    private void RestToOther()
    {
        foreach (var row in Rows.Where(r => r.Group.Trim().Length == 0))
        {
            row.Group = "Прочее";
        }
    }

    private void OnRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(RegroupRowViewModel.Group))
        {
            UpdateNames();
        }
    }

    private void UpdateNames()
    {
        var names = Rows.Select(r => r.Group.Trim()).Where(n => n.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        if (!names.SequenceEqual(GroupNames))
        {
            GroupNames.Clear();
            foreach (var name in names)
            {
                GroupNames.Add(name);
            }
        }
    }
}
