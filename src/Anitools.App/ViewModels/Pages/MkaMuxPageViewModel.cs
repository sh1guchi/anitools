using System.Collections.ObjectModel;
using System.ComponentModel;
using Anitools.Core.Jobs;
using Anitools.Core.Media;
using Anitools.Core.Operations.Common;
using Anitools.Core.Operations.MkaMux;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Material.Icons;

namespace Anitools.App.ViewModels.Pages;

/// <summary>Озвучка: метка (папка «N. Имя», тайтл или [тег]), пример файла и тайтл дорожки.</summary>
public sealed partial class MkaLabelRowViewModel(MkaLabel label) : ObservableObject
{
    public string Label { get; } = label.Label;

    public string Example { get; } = label.ExampleFile;

    [ObservableProperty]
    public partial int Position { get; set; }

    [ObservableProperty]
    public partial string Title { get; set; } = label.Label;
}

/// <summary>
/// «Озвучки → .mka» (§4.11, mka_muxer.py): аудио из папок озвучек — в один .mka на серию (или всё в один файл);
/// порядок озвучек, тайтлы дорожек и язык — один раз на все серии.
/// </summary>
public sealed partial class MkaMuxPageViewModel(IShell shell) : PageViewModel(shell, "Озвучки → .mka", MaterialIconKind.MicrophoneOutline)
{
    private IReadOnlyList<MkaSource> _sources = [];
    private bool _rebuilding;

    public ObservableCollection<MkaLabelRowViewModel> Labels { get; } = [];

    public PlanPreviewViewModel Preview { get; } = new();

    public IReadOnlyList<string> Voices => Shell.Services.Settings.Voices;

    [ObservableProperty]
    public partial MkaMuxMode Mode { get; set; } = MkaMuxMode.ByEpisode;

    /// <summary>Имя файла для «всё в один».</summary>
    [ObservableProperty]
    public partial string SingleName { get; set; } = "";

    [ObservableProperty]
    public partial bool SetLanguage { get; set; } = true;

    /// <summary>Язык по умолчанию; оригинальная и английская озвучки определяются сами.</summary>
    [ObservableProperty]
    public partial string Language { get; set; } = "rus";

    [ObservableProperty]
    public partial string FilesCaption { get; set; } = "";

    public bool IsByEpisode
    {
        get => Mode == MkaMuxMode.ByEpisode;
        set
        {
            if (value)
            {
                Mode = MkaMuxMode.ByEpisode;
            }
        }
    }

    public bool IsSingleFile
    {
        get => Mode == MkaMuxMode.SingleFile;
        set
        {
            if (value)
            {
                Mode = MkaMuxMode.SingleFile;
            }
        }
    }

    protected override async Task LoadAsync(string folder, CancellationToken cancellationToken)
    {
        var sources = await MkaMuxOperation.InspectAsync(folder, new CachedMediaProbe(Shell.Services.Probe), cancellationToken);
        _sources = sources;
        _rebuilding = true;
        SingleName = MkaMuxOperation.DefaultSingleName(sources);
        _rebuilding = false;
        FilesCaption = $"аудиофайлов: {sources.Count}";
        RebuildLabels();
    }

    protected override void Clear()
    {
        _sources = [];
        Labels.Clear();
        Preview.Clear();
        FilesCaption = "";
    }

    partial void OnModeChanged(MkaMuxMode value)
    {
        OnPropertyChanged(nameof(IsByEpisode));
        OnPropertyChanged(nameof(IsSingleFile));
        RebuildLabels();
    }

    partial void OnSingleNameChanged(string value) => Replan();

    partial void OnSetLanguageChanged(bool value) => Replan();

    partial void OnLanguageChanged(string value) => Replan();

    [RelayCommand]
    private void MoveUp(MkaLabelRowViewModel row) => Move(row, -1);

    [RelayCommand]
    private void MoveDown(MkaLabelRowViewModel row) => Move(row, 1);

    [RelayCommand]
    private void Run()
    {
        if (Preview.Selected() is { } plan)
        {
            Enqueue(plan.Title, PlanJobs.Run(plan, Shell.Services.CreateExecutor()));
        }
    }

    private IReadOnlyList<MkaGroup> Groups() =>
        MkaMuxOperation.Groups(_sources, Mode, Mode == MkaMuxMode.SingleFile && SingleName.Trim().Length > 0 ? SingleName.Trim() : null);

    /// <summary>Метки для режима; порядок и тайтлы, уже выставленные для тех же меток, сохраняются.</summary>
    private void RebuildLabels()
    {
        if (_sources.Count == 0)
        {
            return;
        }

        var titles = Labels.ToDictionary(l => l.Label, l => l.Title, StringComparer.Ordinal);
        var order = Labels.Select((l, i) => (l.Label, i)).ToDictionary(p => p.Label, p => p.i, StringComparer.Ordinal);
        _rebuilding = true;
        foreach (var row in Labels)
        {
            row.PropertyChanged -= OnLabelChanged;
        }

        Labels.Clear();
        // Порядок, выставленный руками, сохраняется; новые метки — в конец
        foreach (var label in MkaMuxOperation.Labels(Groups()).OrderBy(l => order.GetValueOrDefault(l.Label, int.MaxValue)))
        {
            var row = new MkaLabelRowViewModel(label) { Title = titles.GetValueOrDefault(label.Label) ?? label.Label };
            row.PropertyChanged += OnLabelChanged;
            Labels.Add(row);
        }

        Renumber();
        _rebuilding = false;
        Replan();
    }

    private void OnLabelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MkaLabelRowViewModel.Title))
        {
            Replan();
        }
    }

    private void Move(MkaLabelRowViewModel row, int delta)
    {
        var index = Labels.IndexOf(row);
        var target = index + delta;
        if (index < 0 || target < 0 || target >= Labels.Count)
        {
            return;
        }

        Labels.Move(index, target);
        Renumber();
        Replan();
    }

    private void Renumber()
    {
        for (var i = 0; i < Labels.Count; i++)
        {
            Labels[i].Position = i + 1;
        }
    }

    private void Replan()
    {
        if (_sources.Count == 0 || _rebuilding)
        {
            return;
        }

        try
        {
            var options = new MkaLabelOptions
            {
                Order = [.. Labels.Select(l => l.Label)],
                Titles = Labels.ToDictionary(l => l.Label, l => l.Title.Trim(), StringComparer.Ordinal),
                Language = SetLanguage ? Language : null,
            };
            Preview.Show(MkaMuxOperation.Plan(Folder, Groups(), options), Shell.FileSize);
            Message = null;
        }
        catch (PlanException ex)
        {
            Preview.Clear();
            Message = ex.Message;
        }
    }
}
