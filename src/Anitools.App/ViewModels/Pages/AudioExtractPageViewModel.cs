using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using Anitools.Core.Jobs;
using Anitools.Core.Media;
using Anitools.Core.Operations.AudioExtract;
using Anitools.Core.Operations.Common;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Material.Icons;

namespace Anitools.App.ViewModels.Pages;

/// <summary>Аудиодорожка первого файла с галочкой «брать».</summary>
public sealed partial class AudioTrackRowViewModel(AudioTrackInfo track) : ObservableObject
{
    public AudioTrackInfo Track { get; } = track;

    public int Number => Track.Index + 1;

    public string Title => string.IsNullOrEmpty(Track.Title) ? "—" : Track.Title;

    public string Codec => Track.Description;

    public string Channels => ChannelsText(Track.Channels);

    public string Language => string.IsNullOrEmpty(Track.Language) ? "" : Track.Language;

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    /// <summary>2 → «2.0», 6 → «5.1», 8 → «7.1».</summary>
    public static string ChannelsText(int? channels) => channels switch
    {
        null => "",
        1 => "1.0",
        2 => "2.0",
        6 => "5.1",
        8 => "7.1",
        _ => channels.Value.ToString(CultureInfo.InvariantCulture) + " кан.",
    };
}

/// <summary>Дорожка будущего .mka («все в один»): исходная дорожка, тайтл и язык.</summary>
public sealed partial class OutputTrackRowViewModel : ObservableObject
{
    private readonly Func<string, string> _defaultLanguage;
    private bool _languageEdited;
    private bool _settingLanguage;

    public OutputTrackRowViewModel(AudioTrackInfo track, Func<string, string> defaultLanguage)
    {
        Track = track;
        _defaultLanguage = defaultLanguage;
        Title = track.Title ?? "";
        SetLanguage(defaultLanguage(Title));
    }

    public AudioTrackInfo Track { get; }

    public string Source => $"{Track.Index + 1} · {(string.IsNullOrEmpty(Track.Title) ? Track.Description : Track.Title)}";

    [ObservableProperty]
    public partial int Position { get; set; }

    [ObservableProperty]
    public partial string Title { get; set; } = "";

    /// <summary>ISO 639-2; пусто — язык не ставить.</summary>
    [ObservableProperty]
    public partial string Language { get; set; } = "";

    partial void OnTitleChanged(string value)
    {
        // Язык, который не меняли руками, следует за тайтлом: «… Original» → jpn
        if (!_languageEdited)
        {
            SetLanguage(_defaultLanguage(value));
        }
    }

    partial void OnLanguageChanged(string value)
    {
        if (!_settingLanguage)
        {
            _languageEdited = true;
        }
    }

    private void SetLanguage(string language)
    {
        _settingLanguage = true;
        Language = language;
        _settingLanguage = false;
    }
}

/// <summary>
/// «Только аудио» (§4.4): дорожки по первому файлу; отдельный файл на дорожку (папки «N. Тайтл») или все
/// выбранные в один .mka с тайтлами из войс-листа, языком и порядком.
/// </summary>
public sealed partial class AudioExtractPageViewModel(IShell shell) : PageViewModel(shell, "Только аудио", MaterialIconKind.Headphones)
{
    public const string DefaultLanguage = "rus";

    private AudioExtractSource? _source;
    private bool _rebuilding;

    public ObservableCollection<AudioTrackRowViewModel> Tracks { get; } = [];

    /// <summary>«Все в один .mka»: дорожки по выходному порядку.</summary>
    public ObservableCollection<OutputTrackRowViewModel> OutputTracks { get; } = [];

    public PlanPreviewViewModel Preview { get; } = new();

    public IReadOnlyList<string> Voices => Shell.Services.Settings.Voices;

    public IReadOnlyList<string> LanguageCodes { get; } = ["rus", "jpn", "eng", "ukr", "kor", "chi", "und"];

    [ObservableProperty]
    public partial AudioExtractMode Mode { get; set; } = AudioExtractMode.Separate;

    [ObservableProperty]
    public partial bool NumberTracks { get; set; } = true;

    /// <summary>«по первому файлу: Frieren - 01.mkv».</summary>
    [ObservableProperty]
    public partial string TracksCaption { get; set; } = "";

    public bool IsSeparate
    {
        get => Mode == AudioExtractMode.Separate;
        set
        {
            if (value)
            {
                Mode = AudioExtractMode.Separate;
            }
        }
    }

    public bool IsSingleMka
    {
        get => Mode == AudioExtractMode.SingleMka;
        set
        {
            if (value)
            {
                Mode = AudioExtractMode.SingleMka;
            }
        }
    }

    /// <summary>Выбрано больше одной дорожки — режимы различаются.</summary>
    public bool HasSeveralSelected => Tracks.Count(t => t.IsSelected) > 1;

    public bool ShowsNumbering => IsSeparate && HasSeveralSelected;

    public bool ShowsOutputTracks => IsSingleMka && HasSeveralSelected;

    protected override async Task LoadAsync(string folder, CancellationToken cancellationToken)
    {
        var source = await AudioExtractOperation.InspectAsync(folder, Shell.Services.Probe, cancellationToken);
        _source = source;
        _rebuilding = true;
        foreach (var row in Tracks)
        {
            row.PropertyChanged -= OnTrackChanged;
        }

        Tracks.Clear();
        OutputTracks.Clear();
        foreach (var track in source.Tracks)
        {
            // По умолчанию — первая дорожка
            var row = new AudioTrackRowViewModel(track) { IsSelected = track.Index == 0 };
            row.PropertyChanged += OnTrackChanged;
            Tracks.Add(row);
        }

        _rebuilding = false;
        TracksCaption = source.Tracks.Count == 0
            ? $"В {Path.GetFileName(source.Files[0])} нет аудиодорожек."
            : $"по первому файлу: {Path.GetFileName(source.Files[0])} · всего файлов: {source.Files.Count}";
        SyncOutputTracks();
        Replan();
    }

    protected override void Clear()
    {
        _source = null;
        Tracks.Clear();
        OutputTracks.Clear();
        Preview.Clear();
        TracksCaption = "";
    }

    partial void OnModeChanged(AudioExtractMode value)
    {
        OnPropertyChanged(nameof(IsSeparate));
        OnPropertyChanged(nameof(IsSingleMka));
        OnLayoutChanged();
        Replan();
    }

    partial void OnNumberTracksChanged(bool value) => Replan();

    [RelayCommand]
    private void MoveUp(OutputTrackRowViewModel row) => Move(row, -1);

    [RelayCommand]
    private void MoveDown(OutputTrackRowViewModel row) => Move(row, 1);

    [RelayCommand]
    private void Run()
    {
        if (Preview.Selected() is { } plan)
        {
            Enqueue(plan.Title, PlanJobs.Run(plan, Shell.Services.CreateExecutor()));
        }
    }

    private void OnTrackChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!_rebuilding && e.PropertyName == nameof(AudioTrackRowViewModel.IsSelected))
        {
            SyncOutputTracks();
            Replan();
        }
    }

    /// <summary>Выходные дорожки = выбранные: новые — в конец, снятые — прочь, порядок остальных не трогаем.</summary>
    private void SyncOutputTracks()
    {
        var selected = Tracks.Where(t => t.IsSelected).Select(t => t.Track).ToList();
        foreach (var row in OutputTracks.Where(r => !selected.Contains(r.Track)).ToList())
        {
            row.PropertyChanged -= OnOutputChanged;
            OutputTracks.Remove(row);
        }

        foreach (var track in selected.Where(t => OutputTracks.All(r => r.Track != t)))
        {
            var row = new OutputTrackRowViewModel(track, title => AudioExtractOperation.DefaultLanguage(track, title, DefaultLanguage));
            row.PropertyChanged += OnOutputChanged;
            OutputTracks.Add(row);
        }

        Renumber();
        OnLayoutChanged();
    }

    private void OnOutputChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(OutputTrackRowViewModel.Title) or nameof(OutputTrackRowViewModel.Language))
        {
            Replan();
        }
    }

    private void Move(OutputTrackRowViewModel row, int delta)
    {
        var index = OutputTracks.IndexOf(row);
        var target = index + delta;
        if (index < 0 || target < 0 || target >= OutputTracks.Count)
        {
            return;
        }

        OutputTracks.Move(index, target);
        Renumber();
        Replan();
    }

    private void Renumber()
    {
        for (var i = 0; i < OutputTracks.Count; i++)
        {
            OutputTracks[i].Position = i + 1;
        }
    }

    private void OnLayoutChanged()
    {
        OnPropertyChanged(nameof(HasSeveralSelected));
        OnPropertyChanged(nameof(ShowsNumbering));
        OnPropertyChanged(nameof(ShowsOutputTracks));
    }

    /// <summary>Превью — сразу при любом изменении: план строится без чтения файлов.</summary>
    private void Replan()
    {
        if (_source is null || _rebuilding)
        {
            return;
        }

        var single = IsSingleMka && HasSeveralSelected;
        var options = new AudioExtractOptions
        {
            // Отдельные файлы — по номеру дорожки; один .mka — в выходном порядке
            TrackIds = single ? [.. OutputTracks.Select(r => r.Track.Index)] : [.. Tracks.Where(t => t.IsSelected).Select(t => t.Track.Index)],
            Mode = Mode,
            NumberTracks = NumberTracks,
            Titles = single ? OutputTracks.ToDictionary(r => r.Track.Index, r => r.Title.Trim()) : null,
            Languages = single ? OutputTracks.ToDictionary(r => r.Track.Index, r => r.Language.Trim()) : null,
        };
        try
        {
            Preview.Show(AudioExtractOperation.Plan(_source, options), Shell.FileSize);
            Message = null;
        }
        catch (PlanException ex)
        {
            Preview.Clear();
            Message = ex.Message;
        }
    }
}
