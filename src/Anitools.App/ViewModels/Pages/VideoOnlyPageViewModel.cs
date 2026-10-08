using Anitools.Core.Jobs;
using Anitools.Core.Operations.VideoOnly;
using CommunityToolkit.Mvvm.Input;
using Material.Icons;

namespace Anitools.App.ViewModels.Pages;

/// <summary>«Только видео» (§4.3): первая видеодорожка без звука и субтитров → «Video only».</summary>
public sealed partial class VideoOnlyPageViewModel(IShell shell) : PageViewModel(shell, "Только видео", MaterialIconKind.Filmstrip)
{
    public PlanPreviewViewModel Preview { get; } = new();

    public string Description => "Первая видеодорожка без звука, субтитров и вложений — в папку «Video only». Без перекодирования.";

    protected override async Task LoadAsync(string folder, CancellationToken cancellationToken)
    {
        var plan = await Task.Run(() => VideoOnlyOperation.Plan(folder), cancellationToken);
        Preview.Show(plan, Shell.FileSize);
    }

    protected override void Clear() => Preview.Clear();

    [RelayCommand]
    private void Run()
    {
        if (Preview.Selected() is { } plan)
        {
            Enqueue(plan.Title, PlanJobs.Run(plan, Shell.Services.CreateExecutor()));
        }
    }
}
