using Anitools.App.ViewModels;
using Anitools.App.Views;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace Anitools.App;

public sealed class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var folder = desktop.Args is { Length: > 0 } args ? args[0] : Environment.CurrentDirectory;
            desktop.MainWindow = new MainWindow { DataContext = new MainWindowViewModel(folder) };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
