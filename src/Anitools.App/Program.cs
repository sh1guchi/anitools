using Anitools.App.Startup;
using Avalonia;
using Avalonia.Media;

namespace Anitools.App;

internal static class Program
{
    /// <summary>Сколько ждать ответа уже открытой копии, прежде чем запуститься самим.</summary>
    private static readonly TimeSpan ForwardTimeout = TimeSpan.FromSeconds(3);

    // Аргумент командной строки — рабочая папка (как у ani.bat): Anitools.exe "D:\anime\X"
    [STAThread]
    public static int Main(string[] args)
    {
        var instance = SingleInstance.TryBecomePrimary(SingleInstance.DefaultName);
        if (instance is null)
        {
            // Уже открыто — передать папку туда (путь считается от текущей папки этой консоли) и выйти
            var request = StartupArguments.Parse(args, Environment.CurrentDirectory);
            if (SingleInstance.TryForward(SingleInstance.DefaultName, request, ForwardTimeout))
            {
                return 0;
            }
        }

        App.Instance = instance;
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

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
