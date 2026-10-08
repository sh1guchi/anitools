using System.Globalization;
using Anitools.Core.Jobs;
using Anitools.Core.Operations.AudioTools;
using Anitools.Core.Operations.Common;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Material.Icons;

namespace Anitools.App.ViewModels.Pages;

/// <summary>
/// «Сдвиг аудио» (§4.11, delay±1s.py): плюс — звук позже, минус — обрезать начало; → audio_fixed. По умолчанию без
/// перекодирования (mkvmerge), по желанию — в AAC, как в оригинале.
/// </summary>
public sealed partial class AudioShiftPageViewModel(IShell shell) : PageViewModel(shell, "Сдвиг аудио", MaterialIconKind.ClockOutline)
{
    public PlanPreviewViewModel Preview { get; } = new();

    [ObservableProperty]
    public partial string Seconds { get; set; } = Text(shell.Services.Settings.AudioShift.Seconds);

    [ObservableProperty]
    public partial string Bitrate { get; set; } = shell.Services.Settings.AudioShift.Bitrate;

    [ObservableProperty]
    public partial string Workers { get; set; } = Text(shell.Services.Settings.AudioShift.Workers);

    /// <summary>Перекодировать в AAC (как в оригинале); нет — сдвиг без потерь.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLossless))]
    public partial bool Reencode { get; set; } = shell.Services.Settings.AudioShift.Reencode;

    public bool IsLossless
    {
        get => !Reencode;
        set => Reencode = !value;
    }

    public string Description => Reencode
        ? "Перекодирование в AAC, как в оригинале: плюс — тишина в начало, минус — начало обрезается. Выход — папка «audio_fixed», .mka."
        : "Без перекодирования (mkvmerge): плюс — звук начинается позже, минус — начало отбрасывается. Кодек и качество — как в исходнике. Выход — папка «audio_fixed», .mka.";

    protected override Task LoadAsync(string folder, CancellationToken cancellationToken)
    {
        Replan();
        return Task.CompletedTask;
    }

    protected override void Clear() => Preview.Clear();

    partial void OnSecondsChanged(string value) => Replan();

    partial void OnBitrateChanged(string value) => Replan();

    partial void OnReencodeChanged(bool value)
    {
        OnPropertyChanged(nameof(Description));
        Replan();
    }

    [RelayCommand]
    private void Run()
    {
        if (Options() is { } options && Preview.Selected() is { } plan)
        {
            Enqueue(plan.Title, PlanJobs.Run(plan, Shell.Services.CreateExecutor(), options.Workers));
        }
    }

    private AudioShiftOptions? Options()
    {
        var errors = new List<string>();
        var options = new AudioShiftOptions
        {
            Seconds = SettingsPageViewModel.Number(Seconds, "Сдвиг", errors, -3600, 3600),
            Bitrate = Bitrate.Trim().Length > 0 ? Bitrate.Trim() : "256k",
            Workers = SettingsPageViewModel.Int(Workers, "Файлов сразу", errors, 1, 64),
            Reencode = Reencode,
        };
        Message = errors.Count > 0 ? string.Join("\n", errors) : null;
        return errors.Count > 0 ? null : options;
    }

    private void Replan()
    {
        if (Folder.Length == 0)
        {
            return;
        }

        if (Options() is not { } options)
        {
            Preview.Clear();
            return;
        }

        try
        {
            Preview.Show(AudioShiftOperation.Plan(Folder, options), Shell.FileSize);
        }
        catch (PlanException ex)
        {
            Preview.Clear();
            Message = ex.Message;
        }
    }

    internal static string Text(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}

/// <summary>«Перекодировать» (§4.11, audio_decod.py): аудио папки → converted\ в выбранный формат.</summary>
public sealed partial class AudioConvertPageViewModel(IShell shell) : PageViewModel(shell, "Перекодировать", MaterialIconKind.Waveform)
{
    public PlanPreviewViewModel Preview { get; } = new();

    public IReadOnlyList<AudioFormat> Formats { get; } = Enum.GetValues<AudioFormat>();

    [ObservableProperty]
    public partial AudioFormat Format { get; set; } = shell.Services.Settings.AudioConvert.Format;

    [ObservableProperty]
    public partial string Bitrate { get; set; } = shell.Services.Settings.AudioConvert.Bitrate;

    /// <summary>Пусто — как в исходнике.</summary>
    [ObservableProperty]
    public partial string Channels { get; set; } = shell.Services.Settings.AudioConvert.Channels is { } c ? c.ToString(CultureInfo.InvariantCulture) : "";

    [ObservableProperty]
    public partial bool FixTimestamps { get; set; } = shell.Services.Settings.AudioConvert.FixTimestamps;

    [ObservableProperty]
    public partial string Workers { get; set; } = shell.Services.Settings.AudioConvert.Workers.ToString(CultureInfo.InvariantCulture);

    /// <summary>У форматов без потерь битрейта нет.</summary>
    public bool HasBitrate => Format is not (AudioFormat.Flac or AudioFormat.Wav);

    protected override Task LoadAsync(string folder, CancellationToken cancellationToken)
    {
        Replan();
        return Task.CompletedTask;
    }

    protected override void Clear() => Preview.Clear();

    partial void OnFormatChanged(AudioFormat value)
    {
        OnPropertyChanged(nameof(HasBitrate));
        Replan();
    }

    partial void OnBitrateChanged(string value) => Replan();

    partial void OnChannelsChanged(string value) => Replan();

    partial void OnFixTimestampsChanged(bool value) => Replan();

    [RelayCommand]
    private void Run()
    {
        if (Options() is { } options && Preview.Selected() is { } plan)
        {
            Enqueue(plan.Title, PlanJobs.Run(plan, Shell.Services.CreateExecutor(), options.Workers));
        }
    }

    private AudioConvertOptions? Options()
    {
        var errors = new List<string>();
        var options = new AudioConvertOptions
        {
            Format = Format,
            Bitrate = Bitrate.Trim().Length > 0 ? Bitrate.Trim() : "256k",
            Channels = Channels.Trim().Length == 0 ? null : SettingsPageViewModel.Int(Channels, "Каналов", errors, 1, 8),
            FixTimestamps = FixTimestamps,
            Workers = SettingsPageViewModel.Int(Workers, "Файлов сразу", errors, 1, 64),
        };
        Message = errors.Count > 0 ? string.Join("\n", errors) : null;
        return errors.Count > 0 ? null : options;
    }

    private void Replan()
    {
        if (Folder.Length == 0)
        {
            return;
        }

        if (Options() is not { } options)
        {
            Preview.Clear();
            return;
        }

        try
        {
            Preview.Show(AudioConvertOperation.Plan(Folder, options), Shell.FileSize);
        }
        catch (PlanException ex)
        {
            Preview.Clear();
            Message = ex.Message;
        }
    }
}
