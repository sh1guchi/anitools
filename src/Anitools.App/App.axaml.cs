using Anitools.App.Services;
using Anitools.App.Startup;
using Anitools.App.ViewModels;
using Anitools.App.Views;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;

namespace Anitools.App;

public sealed class App : Application
{
    /// <summary>Первая копия приложения (Program.Main); null — проверка одной копии не делалась (тесты, дизайнер).</summary>
    internal static SingleInstance? Instance { get; set; }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var services = AppServices.CreateDefault();
            var startup = StartupArguments.Parse(desktop.Args ?? [], Environment.CurrentDirectory);
            var window = new MainWindow();
            var viewModel = new MainWindowViewModel(services, new AvaloniaDialogService(window), startup);
            window.DataContext = viewModel;
            desktop.MainWindow = window;
            _ = viewModel.CheckToolsAsync();
            _ = viewModel.CheckUpdatesLaterAsync(TimeSpan.FromSeconds(5));
            viewModel.ExitRequested += () => desktop.Shutdown(); // установщик обновления запущен

            // ani в другой папке: вторая копия передаёт папку сюда и выходит
            Instance?.StartListening(request => Dispatcher.UIThread.Post(() =>
            {
                viewModel.Apply(request);
                window.BringToFront();
            }));
            desktop.Exit += (_, _) =>
            {
                // Выход без закрытия окна (выключение Windows и т.п.): отменить задачи и дать им убрать за собой
                services.Jobs.CancelAll();
                services.Jobs.WhenIdleAsync().Wait(TimeSpan.FromSeconds(10));
                Instance?.Dispose();
                services.Dispose();
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
