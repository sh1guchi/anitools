using Anitools.App.Startup;
using Anitools.Core.WorkDir;
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
        // Помощник с правами администратора для RAM-диска: без окна и без проверки «один экземпляр»
        if (args is [ImDiskHelper.Argument, var pipe, var app] && int.TryParse(app, out var appProcessId))
        {
            return RunImDiskHelper(pipe, appProcessId);
        }

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

    /// <summary>
    /// Помощник выходит тихо при любом исходе: упавший процесс с правами администратора показал бы окно Windows
    /// «программа не работает». Свои RAM-диски он снимает до выхода (finally в ImDiskHelper.ServeAsync).
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031", Justification = "Процесс-помощник не должен падать с окном ошибки.")]
    private static int RunImDiskHelper(string pipe, int appProcessId)
    {
        try
        {
            return ImDiskHelper.RunAsync(pipe, appProcessId).GetAwaiter().GetResult();
        }
        catch (Exception)
        {
            return 1;
        }
    }

    // Используется и дизайнером Avalonia
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .UseAnitoolsFonts()
            .LogToTrace();

    /// <summary>
    /// Шрифт как у Anime Uploader: Segoe UI на Windows, встроенный Inter в остальных системах (и в headless-тестах
    /// на Linux) и для символов, которых в Segoe UI нет. Общая настройка для приложения и тестов.
    /// </summary>
    internal static AppBuilder UseAnitoolsFonts(this AppBuilder builder) =>
        builder
            .WithInterFont()
            .With(new FontManagerOptions
            {
                DefaultFamilyName = OperatingSystem.IsWindows() ? "Segoe UI" : "fonts:Inter#Inter",
                FontFallbacks = [new FontFallback { FontFamily = new FontFamily("fonts:Inter#Inter") }],
            });
}
