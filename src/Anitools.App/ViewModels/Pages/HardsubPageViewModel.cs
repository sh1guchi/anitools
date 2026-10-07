using Anitools.Core.Jobs;
using Anitools.Core.Operations.Hardsub;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Material.Icons;

namespace Anitools.App.ViewModels.Pages;

/// <summary>
/// «Хардсаб» (§4.11, hardsub.py): пары «видео + .ass с тем же именем» → Hardsub\ с вшитыми субтитрами; шрифты —
/// из папки Fonts рядом, если она есть. Параметры кодирования — в настройках.
/// </summary>
public sealed partial class HardsubPageViewModel(IShell shell) : PageViewModel(shell, "Хардсаб", MaterialIconKind.Subtitles)
{
    public PlanPreviewViewModel Preview { get; } = new();

    [ObservableProperty]
    public partial string FontsInfo { get; set; } = "";

    public string Profile => string.Join(' ', Shell.Services.Settings.Hardsub.EncodeArgs);

    protected override async Task LoadAsync(string folder, CancellationToken cancellationToken)
    {
        var options = Shell.Services.Settings.Hardsub;
        var plan = await Task.Run(() => HardsubOperation.Plan(folder, options), cancellationToken);
        Preview.Show(plan, Shell.FileSize);
        FontsInfo = Directory.Exists(Path.Combine(folder, options.FontsDirName))
            ? $"Шрифты: папка «{options.FontsDirName}» рядом с видео ✓"
            : $"Шрифты: папки «{options.FontsDirName}» нет — из системы";
        OnPropertyChanged(nameof(Profile));
    }

    protected override void Clear()
    {
        Preview.Clear();
        FontsInfo = "";
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
