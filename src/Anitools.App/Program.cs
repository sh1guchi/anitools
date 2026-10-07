using Avalonia;
using Avalonia.Media;

namespace Anitools.App;

internal static class Program
{
    // Аргумент командной строки — рабочая папка (как у ani.bat): Anitools.exe "D:\anime\X"
    [STAThread]
    public static int Main(string[] args) =>
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    // Используется и дизайнером Avalonia
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .UseAnitoolsFonts()
            .LogToTrace();

    /// <summary>
    /// Встроенный Inter по умолчанию: кириллица выглядит одинаково на Windows
    /// и в headless-тестах на Linux. Общая настройка для приложения и тестов.
    /// </summary>
    internal static AppBuilder UseAnitoolsFonts(this AppBuilder builder) =>
        builder
            .WithInterFont()
            .With(new FontManagerOptions { DefaultFamilyName = "fonts:Inter#Inter" });
}
