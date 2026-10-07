using Material.Icons;

namespace Anitools.App.ViewModels.Pages;

/// <summary>Страница, которая появится в следующих частях этапа 7.</summary>
public sealed class PlaceholderPageViewModel(IShell shell, string title, MaterialIconKind icon, string description)
    : PageViewModel(shell, title, icon)
{
    public string Description { get; } = description;

    public override bool UsesFolder => false;

    protected override Task LoadAsync(string folder, CancellationToken cancellationToken) => Task.CompletedTask;
}
