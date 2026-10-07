using System.Collections.ObjectModel;
using Anitools.Core.Jobs;
using Anitools.Core.Media;
using Anitools.Core.Operations.Common;
using Anitools.Core.Operations.Subtitles;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Material.Icons;

namespace Anitools.App.ViewModels.Pages;

/// <summary>Дорожка субтитров первого файла: «1  Надписи  S_TEXT/ASS → .ass  rus».</summary>
public sealed class SubtitleTrackRowViewModel(SubtitleTrack track)
{
    public SubtitleTrack Track { get; } = track;

    public string Id => Track.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public string Name => Track.Name;

    public string Codec => $"{Track.CodecId} → {SubtitleTrackMatcher.CodecIdToExtension(Track.CodecId)}";

    public string Language => Track.Language.Length > 0 ? Track.Language : Track.LanguageIetf;
}

/// <summary>
/// П.4 «Субтитры» (§4.6): дорожка по первому файлу; в каждой серии — та же по ID, тайтлу или языку;
/// надписи → «надписи\… .надписи.ass», сабы → «сабы\… .сабы.ass».
/// </summary>
public sealed partial class SubtitlesPageViewModel(IShell shell) : PageViewModel(shell, "Субтитры", MaterialIconKind.SubtitlesOutline)
{
    private CachedMediaProbe _probe = new(shell.Services.Probe);
    private SubtitleSource? _source;
    private CancellationTokenSource? _planning;

    public ObservableCollection<SubtitleTrackRowViewModel> Tracks { get; } = [];

    public PlanPreviewViewModel Preview { get; } = new();

    [ObservableProperty]
    public partial SubtitleTrackRowViewModel? SelectedTrack { get; set; }

    [ObservableProperty]
    public partial SubtitleMatchMode Mode { get; set; } = SubtitleMatchMode.ById;

    [ObservableProperty]
    public partial SubtitleKind Kind { get; set; } = SubtitleKind.Signs;

    [ObservableProperty]
    public partial string TracksCaption { get; set; } = "";

    [ObservableProperty]
    public partial bool IsPlanning { get; set; }

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

    public bool IsSigns
    {
        get => Kind == SubtitleKind.Signs;
        set
        {
            if (value)
            {
                Kind = SubtitleKind.Signs;
            }
        }
    }

    public bool IsSubs
    {
        get => Kind == SubtitleKind.Subs;
        set
        {
            if (value)
            {
                Kind = SubtitleKind.Subs;
            }
        }
    }

    protected override async Task LoadAsync(string folder, CancellationToken cancellationToken)
    {
        _probe = new CachedMediaProbe(Shell.Services.Probe);
        var source = await SubtitleExtractOperation.InspectAsync(folder, _probe, cancellationToken);
        _source = source;
        Tracks.Clear();
        foreach (var track in source.Tracks)
        {
            Tracks.Add(new SubtitleTrackRowViewModel(track));
        }

        TracksCaption = $"по первому файлу: {Path.GetFileName(source.Files[0])} · всего файлов: {source.Files.Count}";
        SelectedTrack = Tracks.FirstOrDefault();
        await ReplanAsync();
    }

    protected override void Clear()
    {
        _source = null;
        Tracks.Clear();
        Preview.Clear();
        TracksCaption = "";
    }

    partial void OnSelectedTrackChanged(SubtitleTrackRowViewModel? value) => _ = ReplanAsync();

    partial void OnModeChanged(SubtitleMatchMode value)
    {
        OnPropertyChanged(nameof(IsById));
        OnPropertyChanged(nameof(IsByTitle));
        OnPropertyChanged(nameof(IsByLanguage));
        _ = ReplanAsync();
    }

    partial void OnKindChanged(SubtitleKind value)
    {
        OnPropertyChanged(nameof(IsSigns));
        OnPropertyChanged(nameof(IsSubs));
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
        if (_source is not { } source || SelectedTrack is not { } track)
        {
            return;
        }

        _planning?.Cancel();
        var cts = new CancellationTokenSource();
        _planning = cts;
        IsPlanning = true;
        try
        {
            var plan = await SubtitleExtractOperation.PlanAsync(source, new SubtitleExtractOptions(track.Track.Id, Mode, Kind), _probe, cts.Token);
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
