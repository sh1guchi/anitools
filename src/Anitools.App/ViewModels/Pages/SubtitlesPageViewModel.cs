using System.Collections.ObjectModel;
using System.ComponentModel;
using Anitools.Core.Jobs;
using Anitools.Core.Media;
using Anitools.Core.Operations.Common;
using Anitools.Core.Operations.Subtitles;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Material.Icons;

namespace Anitools.App.ViewModels.Pages;

/// <summary>
/// Дорожка субтитров первого файла: «1  Надписи  S_TEXT/ASS → .ass  rus» и куда её извлекать — в надписи, в сабы,
/// в обе папки сразу или никуда.
/// </summary>
public sealed partial class SubtitleTrackRowViewModel(SubtitleTrack track) : ObservableObject
{
    public SubtitleTrack Track { get; } = track;

    public string Id => Track.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public string Name => Track.Name;

    public string Codec => $"{Track.CodecId} → {SubtitleTrackMatcher.CodecIdToExtension(Track.CodecId)}";

    public string Language => Track.Language.Length > 0 ? Track.Language : Track.LanguageIetf;

    [ObservableProperty]
    public partial bool IsSigns { get; set; }

    [ObservableProperty]
    public partial bool IsSubs { get; set; }
}

/// <summary>
/// П.4 «Субтитры» (§4.6): дорожки по первому файлу; у надписей и у сабов — своя дорожка (или никакой; одна дорожка
/// может идти в обе папки), извлекаются за один запуск; в каждой серии — та же по ID, тайтлу или языку;
/// надписи → «надписи\… .надписи.ass», сабы → «сабы\… .сабы.ass».
/// </summary>
public sealed partial class SubtitlesPageViewModel(IShell shell) : PageViewModel(shell, "Субтитры", MaterialIconKind.SubtitlesOutline)
{
    private CachedMediaProbe _probe = new(shell.Services.Probe);
    private SubtitleSource? _source;
    private CancellationTokenSource? _planning;
    private bool _assigning;

    public ObservableCollection<SubtitleTrackRowViewModel> Tracks { get; } = [];

    public PlanPreviewViewModel Preview { get; } = new();

    [ObservableProperty]
    public partial SubtitleMatchMode Mode { get; set; } = SubtitleMatchMode.ById;

    [ObservableProperty]
    public partial string TracksCaption { get; set; } = "";

    [ObservableProperty]
    public partial bool IsPlanning { get; set; }

    public SubtitleTrackRowViewModel? SignsTrack => Tracks.FirstOrDefault(t => t.IsSigns);

    public SubtitleTrackRowViewModel? SubsTrack => Tracks.FirstOrDefault(t => t.IsSubs);

    public bool IsById
    {
        get => Mode == SubtitleMatchMode.ById;
        set => SetMode(value, SubtitleMatchMode.ById);
    }

    public bool IsByTitle
    {
        get => Mode == SubtitleMatchMode.ByTitle;
        set => SetMode(value, SubtitleMatchMode.ByTitle);
    }

    public bool IsByLanguage
    {
        get => Mode == SubtitleMatchMode.ByLanguage;
        set => SetMode(value, SubtitleMatchMode.ByLanguage);
    }

    protected override async Task LoadAsync(string folder, CancellationToken cancellationToken)
    {
        _probe = new CachedMediaProbe(Shell.Services.Probe);
        var source = await SubtitleExtractOperation.InspectAsync(folder, _probe, cancellationToken);
        _source = source;
        foreach (var row in Tracks)
        {
            row.PropertyChanged -= OnTrackChanged;
        }

        Tracks.Clear();
        var (signs, subs) = SubtitleTrackMatcher.GuessRoles(source.Tracks);
        _assigning = true;
        foreach (var track in source.Tracks)
        {
            var row = new SubtitleTrackRowViewModel(track) { IsSigns = track == signs, IsSubs = track == subs };
            row.PropertyChanged += OnTrackChanged;
            Tracks.Add(row);
        }

        _assigning = false;
        TracksCaption = $"по первому файлу: {Path.GetFileName(source.Files[0])} · всего файлов: {source.Files.Count}";
        await ReplanAsync();
    }

    protected override void Clear()
    {
        _source = null;
        Tracks.Clear();
        Preview.Clear();
        TracksCaption = "";
    }

    partial void OnModeChanged(SubtitleMatchMode value)
    {
        OnPropertyChanged(nameof(IsById));
        OnPropertyChanged(nameof(IsByTitle));
        OnPropertyChanged(nameof(IsByLanguage));
        _ = ReplanAsync();
    }

    /// <summary>
    /// У надписей и у сабов — по одной дорожке: отметили другую — прежняя в этой папке снимается. Одна и та же
    /// дорожка может стоять и в надписях, и в сабах.
    /// </summary>
    private void OnTrackChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_assigning || sender is not SubtitleTrackRowViewModel changed
            || e.PropertyName is not (nameof(SubtitleTrackRowViewModel.IsSigns) or nameof(SubtitleTrackRowViewModel.IsSubs)))
        {
            return;
        }

        _assigning = true;
        try
        {
            foreach (var other in Tracks.Where(t => t != changed))
            {
                if (e.PropertyName == nameof(SubtitleTrackRowViewModel.IsSigns) && changed.IsSigns)
                {
                    other.IsSigns = false;
                }
                else if (e.PropertyName == nameof(SubtitleTrackRowViewModel.IsSubs) && changed.IsSubs)
                {
                    other.IsSubs = false;
                }
            }
        }
        finally
        {
            _assigning = false;
        }

        OnPropertyChanged(nameof(SignsTrack));
        OnPropertyChanged(nameof(SubsTrack));
        _ = ReplanAsync();
    }

    [RelayCommand]
    private void Run()
    {
        if (Preview.Selected() is { } plan)
        {
            Enqueue(plan.Title, PlanJobs.Run(plan, Shell.Services.CreateExecutor()));
        }
    }

    private void SetMode(bool selected, SubtitleMatchMode mode)
    {
        if (selected)
        {
            Mode = mode;
        }
    }

    /// <summary>План читает дорожки каждой серии (mkvmerge -J) — один раз, дальше из кэша.</summary>
    private async Task ReplanAsync()
    {
        if (_source is not { } source)
        {
            return;
        }

        List<SubtitleExtractOptions> selections = [];
        if (SignsTrack is { } signs)
        {
            selections.Add(new SubtitleExtractOptions(signs.Track.Id, Mode, SubtitleKind.Signs));
        }

        if (SubsTrack is { } subs)
        {
            selections.Add(new SubtitleExtractOptions(subs.Track.Id, Mode, SubtitleKind.Subs));
        }

        _planning?.Cancel();
        if (selections.Count == 0)
        {
            Preview.Clear();
            Message = "Выберите дорожку для надписей или для сабов.";
            return;
        }

        var cts = new CancellationTokenSource();
        _planning = cts;
        IsPlanning = true;
        try
        {
            var plan = await SubtitleExtractOperation.PlanAsync(source, selections, _probe, cts.Token);
            if (!cts.IsCancellationRequested)
            {
                Preview.Show(plan, Shell.FileSize);
                Message = null;
            }
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is PlanException or ToolNotFoundException or MediaProbeException)
        {
            Preview.Clear();
            Message = ex.Message;
        }
        finally
        {
            if (_planning == cts)
            {
                IsPlanning = false;
            }
        }
    }
}
