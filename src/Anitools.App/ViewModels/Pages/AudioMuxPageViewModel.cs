using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using Anitools.Core.Jobs;
using Anitools.Core.Media;
using Anitools.Core.Operations.AudioMux;
using Anitools.Core.Operations.Common;
using Anitools.Core.Parsing;
using Anitools.Core.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Material.Icons;

namespace Anitools.App.ViewModels.Pages;

/// <summary>Дорожка набора: откуда, тайтл (пусто — настоящий, если есть), язык; первая — по умолчанию.</summary>
public sealed partial class AudioSlotRowViewModel : ObservableObject
{
    private bool _languageEdited;
    private bool _settingLanguage;

    public AudioSlotRowViewModel(AudioSlot slot, string? title, string? language)
    {
        Slot = slot;
        Title = title ?? (slot.HasTitle ? slot.DefaultTitle : "");
        SetLanguage(language ?? "");
        _languageEdited = language is not null && language != GuessLanguage(Title);
    }

    public AudioSlot Slot { get; }

    public string Source => Slot.Label;

    /// <summary>Подсказка в пустом поле: «Track 2», имя файла — такой тайтл не пишется.</summary>
    public string TitlePlaceholder => Slot.DefaultTitle;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDefault))]
    public partial int Position { get; set; }

    public bool IsDefault => Position == 1;

    [ObservableProperty]
    public partial string Title { get; set; } = "";

    [ObservableProperty]
    public partial string Language { get; set; } = "";

    /// <summary>Свой тайтл: непустой и не совпадает с тем, что и так запишется.</summary>
    public string? OwnTitle => Title.Trim() is { Length: > 0 } t && t != Slot.DefaultTitle ? t : null;

    partial void OnTitleChanged(string value)
    {
        if (!_languageEdited)
        {
            SetLanguage(GuessLanguage(value));
        }
    }

    partial void OnLanguageChanged(string value)
    {
        if (!_settingLanguage)
        {
            _languageEdited = true;
        }
    }

    /// <summary>Как язык по умолчанию в оригинале: по подписи, своему и настоящему тайтлу, иначе rus.</summary>
    private string GuessLanguage(string title) =>
        LanguageGuess.Detect(string.Join(" ", new[] { Slot.Label, title, Slot.HasTitle ? Slot.DefaultTitle : null }.Where(t => !string.IsNullOrEmpty(t))))
        ?? AudioMuxPageViewModel.DefaultLanguage;

    private void SetLanguage(string language)
    {
        _settingLanguage = true;
        Language = language;
        _settingLanguage = false;
    }
}

/// <summary>Набор серий с одинаковыми дорожками: свой порядок, тайтлы и язык (§4.5).</summary>
public sealed partial class AudioSetViewModel : ObservableObject
{
    private readonly AudioMuxPageViewModel _page;

    public AudioSetViewModel(AudioMuxPageViewModel page, AudioSlotSet set, int number, AudioSlotSet main)
    {
        _page = page;
        Set = set;
        var count = RuText.Plural(set.Episodes.Count, "серия", "серии", "серий");
        var ranges = EpisodeRanges.Format(set.Episodes.Select(e => Path.GetFileName(e.Video)));
        Header = $"Набор {number} — {count}" + (ranges is null ? "" : $" ({ranges})");
        var missing = main.Slots.Where(s => set.Slots.All(x => x.Key != s.Key)).Select(s => s.Label).ToList();
        var extra = set.Slots.Where(s => main.Slots.All(x => x.Key != s.Key)).Select(s => s.Label).ToList();
        Difference = string.Join("; ", new[]
        {
            missing.Count > 0 ? "нет: " + string.Join(", ", missing) : null,
            extra.Count > 0 ? "есть ещё: " + string.Join(", ", extra) : null,
        }.Where(s => s is not null));
    }

    public AudioSlotSet Set { get; }

    public bool IsMain => Set.IsMain;

    public string Header { get; }

    /// <summary>Чем набор отличается от основного.</summary>
    public string Difference { get; }

    public bool HasDifference => Difference.Length > 0;

    public ObservableCollection<AudioSlotRowViewModel> Slots { get; } = [];

    public AudioSlotSetConfig Config() => new()
    {
        Order = [.. Slots.Select(r => r.Slot.Key)],
        Titles = Slots.Where(r => r.OwnTitle is not null).ToDictionary(r => r.Slot.Key, r => r.OwnTitle!),
        Languages = Slots.Where(r => r.Language.Trim().Length > 0).ToDictionary(r => r.Slot.Key, r => r.Language.Trim()),
    };

    public void Apply(AudioSlotSetConfig config)
    {
        foreach (var row in Slots)
        {
            row.PropertyChanged -= OnRowChanged;
        }

        Slots.Clear();
        var byKey = Set.Slots.ToDictionary(s => s.Key);
        foreach (var key in config.Order)
        {
            var row = new AudioSlotRowViewModel(byKey[key], config.Titles.GetValueOrDefault(key), config.Languages.GetValueOrDefault(key) ?? "");
            row.PropertyChanged += OnRowChanged;
            Slots.Add(row);
        }

        Renumber();
    }

    [RelayCommand]
    private void MoveUp(AudioSlotRowViewModel row) => Move(row, -1);

    [RelayCommand]
    private void MoveDown(AudioSlotRowViewModel row) => Move(row, 1);

    /// <summary>«Взять из набора 1»: общие дорожки — в том же порядке, с теми же тайтлами и языком.</summary>
    [RelayCommand]
    private void CopyFromMain() => _page.CopyFromMain(this);

    private void Move(AudioSlotRowViewModel row, int delta)
    {
        var index = Slots.IndexOf(row);
        var target = index + delta;
        if (index < 0 || target < 0 || target >= Slots.Count)
        {
            return;
        }

        Slots.Move(index, target);
        Renumber();
        _page.Replan();
    }

    private void Renumber()
    {
        for (var i = 0; i < Slots.Count; i++)
        {
            Slots[i].Position = i + 1;
        }
    }

    private void OnRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AudioSlotRowViewModel.Title) or nameof(AudioSlotRowViewModel.Language))
        {
            _page.Replan();
        }
    }
}

/// <summary>
/// П.3 «Сборка аудио» (§4.5): видео + выбранные дорожки исходника + внешние аудио серии → «Processed Audio».
/// Серии с разным набором дорожек — разные наборы, у каждого свой порядок, тайтлы и язык (§2.8 #6).
/// </summary>
public sealed partial class AudioMuxPageViewModel(IShell shell) : PageViewModel(shell, "Сборка аудио", MaterialIconKind.PlaylistMusic)
{
    public const string DefaultLanguage = "rus";

    /// <summary>ffprobe по сериям и внешним файлам — один раз на загрузку папки, а не при каждой галочке.</summary>
    private CachedMediaProbe _probe = new(shell.Services.Probe);
    private AudioMuxSource? _source;
    private AudioMuxAnalysis? _analysis;
    private CancellationTokenSource? _analyzing;
    private bool _rebuilding;

    public ObservableCollection<AudioTrackRowViewModel> SourceTracks { get; } = [];

    public ObservableCollection<AudioSetViewModel> Sets { get; } = [];

    public PlanPreviewViewModel Preview { get; } = new();

    public IReadOnlyList<string> Voices => Shell.Services.Settings.Voices;

    public IReadOnlyList<string> LanguageCodes { get; } = ["rus", "jpn", "eng", "ukr", "kor", "chi", "und"];

    /// <summary>Искать внешние аудио серии (папки «Audio only», озвучки рядом и т.п.).</summary>
    [ObservableProperty]
    public partial bool UseExternal { get; set; } = true;

    [ObservableProperty]
    public partial string TracksCaption { get; set; } = "";

    [ObservableProperty]
    public partial bool IsAnalyzing { get; set; }

    protected override async Task LoadAsync(string folder, CancellationToken cancellationToken)
    {
        _probe = new CachedMediaProbe(Shell.Services.Probe);
        var source = await AudioMuxOperation.InspectAsync(folder, _probe, cancellationToken);
        _source = source;
        _rebuilding = true;
        foreach (var row in SourceTracks)
        {
            row.PropertyChanged -= OnSourceTrackChanged;
        }

        SourceTracks.Clear();
        foreach (var track in source.Tracks)
        {
            var row = new AudioTrackRowViewModel(track) { IsSelected = true };
            row.PropertyChanged += OnSourceTrackChanged;
            SourceTracks.Add(row);
        }

        _rebuilding = false;
        var done = source.Done.Count > 0 ? $" · уже готово: {source.Done.Count}" : "";
        TracksCaption = $"по первому файлу: {Path.GetFileName(source.Videos[0])} · к обработке: {source.Videos.Count}{done}";
        await AnalyzeAsync(cancellationToken);
    }

    protected override void Clear()
    {
        _source = null;
        _analysis = null;
        SourceTracks.Clear();
        Sets.Clear();
        Preview.Clear();
        TracksCaption = "";
    }

    partial void OnUseExternalChanged(bool value) => _ = AnalyzeAsync(CancellationToken.None);

    /// <summary>Пересобрать план по текущим дорожкам наборов.</summary>
    public void Replan()
    {
        if (_analysis is null || _rebuilding || Sets.Count != _analysis.Sets.Count)
        {
            return;
        }

        try
        {
            Preview.Show(AudioMuxOperation.Plan(_analysis, [.. Sets.Select(s => s.Config())]), Shell.FileSize);
            Message = null;
        }
        catch (PlanException ex)
        {
            Preview.Clear();
            Message = ex.Message;
        }
    }

    public void CopyFromMain(AudioSetViewModel set)
    {
        if (Sets.FirstOrDefault(s => s.IsMain) is { } main && main != set)
        {
            set.Apply(AudioSlotSetConfig.FromMain(main.Config(), set.Set, DefaultLanguage));
            Replan();
        }
    }

    [RelayCommand]
    private void Run()
    {
        if (Preview.Selected() is { } plan)
        {
            Enqueue(plan.Title, PlanJobs.Run(plan, Shell.Services.CreateExecutor()));
        }
    }

    private void OnSourceTrackChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!_rebuilding && e.PropertyName == nameof(AudioTrackRowViewModel.IsSelected))
        {
            _ = AnalyzeAsync(CancellationToken.None);
        }
    }

    /// <summary>Внешние файлы и дорожки каждой серии → наборы; настройки наборов — по умолчанию (как в оригинале).</summary>
    private async Task AnalyzeAsync(CancellationToken cancellationToken)
    {
        if (_source is not { } source)
        {
            return;
        }

        _analyzing?.Cancel();
        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _analyzing = cts;
        IsAnalyzing = true;
        try
        {
            var ids = SourceTracks.Where(t => t.IsSelected).Select(t => t.Track.Index).ToList();
            var analysis = await AudioMuxOperation.AnalyzeAsync(source, ids, UseExternal, _probe, cts.Token);
            if (cts.IsCancellationRequested)
            {
                return;
            }

            _analysis = analysis;
            _rebuilding = true;
            Sets.Clear();
            var main = analysis.Sets.FirstOrDefault(s => s.IsMain);
            AudioSlotSetConfig? mainConfig = null;
            for (var i = 0; i < analysis.Sets.Count; i++)
            {
                var set = analysis.Sets[i];
                var vm = new AudioSetViewModel(this, set, i + 1, main ?? set);
                var config = set.IsMain || mainConfig is null
                    ? AudioSlotSetConfig.Default(set, DefaultLanguage)
                    : AudioSlotSetConfig.FromMain(mainConfig, set, DefaultLanguage);
                mainConfig ??= set.IsMain ? config : null;
                vm.Apply(config);
                Sets.Add(vm);
            }

            _rebuilding = false;
            Message = null;
            Replan();
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is PlanException or ToolNotFoundException or MediaProbeException or IOException or UnauthorizedAccessException)
        {
            _analysis = null;
            Sets.Clear();
            Preview.Clear();
            Message = ex.Message;
        }
        finally
        {
            _rebuilding = false;
            if (_analyzing == cts)
            {
                IsAnalyzing = false;
            }
        }
    }
}

/// <summary>Номера серий диапазонами: «01–06, 08–12»; номер не у всех — null.</summary>
public static class EpisodeRanges
{
    public static string? Format(IEnumerable<string> fileNames)
    {
        var numbers = new List<int>();
        foreach (var name in fileNames)
        {
            if (EpisodeNumber.Extract(name) is not { } raw || !int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var n))
            {
                return null;
            }

            numbers.Add(n);
        }

        numbers = [.. numbers.Distinct().Order()];
        if (numbers.Count == 0)
        {
            return null;
        }

        var parts = new List<string>();
        var start = numbers[0];
        var prev = start;
        foreach (var n in numbers.Skip(1).Append(int.MinValue))
        {
            if (n == prev + 1)
            {
                prev = n;
                continue;
            }

            parts.Add(start == prev ? Pad(start) : $"{Pad(start)}–{Pad(prev)}");
            start = prev = n;
        }

        return string.Join(", ", parts);
    }

    private static string Pad(int n) => n.ToString("00", CultureInfo.InvariantCulture);
}
