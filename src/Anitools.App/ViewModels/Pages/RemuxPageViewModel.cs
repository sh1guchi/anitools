using Anitools.Core.Jobs;
using Anitools.Core.Operations.Remux;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Material.Icons;

namespace Anitools.App.ViewModels.Pages;

public enum RemuxProfile
{
    /// <summary>В MP4 (папка converted_mp4).</summary>
    Mp4,

    /// <summary>В MKV (папка та же — converted_mp4).</summary>
    Mkv,

    /// <summary>Blu-ray M2TS → MKV рядом с исходником, PCM → FLAC.</summary>
    M2tsToMkv,
}

/// <summary>«Ремукс» (§4.3) и профиль Blu-ray M2TS (§2.9.10): смена контейнера без перекодирования видео.</summary>
public sealed partial class RemuxPageViewModel(IShell shell) : PageViewModel(shell, "Ремукс", MaterialIconKind.FileSwapOutline)
{
    public PlanPreviewViewModel Preview { get; } = new();

    [ObservableProperty]
    public partial RemuxProfile Profile { get; set; } = RemuxProfile.Mp4;

    /// <summary>Только для MKV (по умолчанию — да).</summary>
    [ObservableProperty]
    public partial bool CopySubtitles { get; set; } = true;

    public bool IsMp4
    {
        get => Profile == RemuxProfile.Mp4;
        set => Select(value, RemuxProfile.Mp4);
    }

    public bool IsMkv
    {
        get => Profile == RemuxProfile.Mkv;
        set => Select(value, RemuxProfile.Mkv);
    }

    public bool IsM2tsToMkv
    {
        get => Profile == RemuxProfile.M2tsToMkv;
        set => Select(value, RemuxProfile.M2tsToMkv);
    }

    /// <summary>Какие файлы берутся: «MKV», «MP4», «AVI»… (у профиля Blu-ray — только M2TS).</summary>
    public IReadOnlyList<string> InputFormats => Profile == RemuxProfile.M2tsToMkv
        ? ["M2TS"]
        : [.. RemuxOperation.Extensions.Select(e => e.TrimStart('.').ToUpperInvariant())];

    public string Description => Profile switch
    {
        RemuxProfile.Mp4 => "Видео и все аудиодорожки — в MP4, без перекодирования. Выход: папка «converted_mp4».",
        RemuxProfile.Mkv => "Видео, аудио и (по желанию) субтитры — в MKV, без перекодирования. Выход: папка «converted_mp4».",
        _ => "Blu-ray M2TS → MKV: видео и звук копируются, PCM сжимается в FLAC без потерь. Выход — рядом с исходником.",
    };

    protected override async Task LoadAsync(string folder, CancellationToken cancellationToken)
    {
        var profile = Profile;
        var subtitles = CopySubtitles;
        var plan = await Task.Run(
            () => profile switch
            {
                RemuxProfile.Mp4 => RemuxOperation.Plan(folder, new RemuxOptions(RemuxFormat.Mp4)),
                RemuxProfile.Mkv => RemuxOperation.Plan(folder, new RemuxOptions(RemuxFormat.Mkv, subtitles)),
                _ => RemuxPresets.M2tsToMkv(folder),
            },
            cancellationToken);
        Preview.Show(plan, Shell.FileSize);
    }

    protected override void Clear() => Preview.Clear();

    partial void OnProfileChanged(RemuxProfile value)
    {
        OnPropertyChanged(nameof(IsMp4));
        OnPropertyChanged(nameof(IsMkv));
        OnPropertyChanged(nameof(IsM2tsToMkv));
        OnPropertyChanged(nameof(Description));
        OnPropertyChanged(nameof(InputFormats));
        Invalidate();
    }

    partial void OnCopySubtitlesChanged(bool value) => Invalidate();

    private void Select(bool selected, RemuxProfile profile)
    {
        if (selected)
        {
            Profile = profile;
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
}
