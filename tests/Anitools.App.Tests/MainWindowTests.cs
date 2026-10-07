using Anitools.App.ViewModels;
using Anitools.App.Views;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;

namespace Anitools.App.Tests;

public sealed class MainWindowTests
{
    private const string DemoFolder = @"D:\anime\Sousou no Frieren";

    [AvaloniaFact]
    public void Opens_and_shows_working_folder()
    {
        var window = new MainWindow { DataContext = new MainWindowViewModel(DemoFolder) };
        window.Show();

        Assert.True(window.IsVisible);
        Assert.Equal(DemoFolder, window.FindControl<TextBlock>("FolderText")?.Text);
        Assert.StartsWith("anitools ", window.FindControl<TextBlock>("VersionText")?.Text);
        window.Close();
    }

    [AvaloniaFact]
    public void Renders_screenshot()
    {
        var window = new MainWindow { DataContext = new MainWindowViewModel(DemoFolder) };
        window.Show();

        var frame = Screenshots.Capture(window, "main-window");

        Assert.Equal(1280, frame.PixelSize.Width);
        Assert.Equal(800, frame.PixelSize.Height);
        // текст и рамки отрисованы — в кадре много оттенков (сглаживание шрифта)
        Assert.True(Screenshots.CountColors(frame) > 50, "Окно отрисовалось пустым");
        window.Close();
    }
}
