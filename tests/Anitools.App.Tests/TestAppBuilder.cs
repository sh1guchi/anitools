using Anitools.App;
using Anitools.App.Tests;
using Avalonia;
using Avalonia.Headless;

[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]

namespace Anitools.App.Tests;

/// <summary>Приложение для headless-тестов: настоящий рендер через Skia, чтобы делать скриншоты.</summary>
public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UseSkia()
            .UseAnitoolsFonts()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}
