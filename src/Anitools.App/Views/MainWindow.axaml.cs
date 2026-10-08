using Anitools.App.ViewModels;
using Anitools.App.Views.Controls;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Platform.Storage;

namespace Anitools.App.Views;

public sealed partial class MainWindow : Window
{
    private bool _closeConfirmed;

    public MainWindow()
    {
        InitializeComponent();
        RecentButton.Click += (_, _) => ShowRecentMenu();
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
        Closing += OnClosing;
    }

    private MainWindowViewModel? ViewModel => DataContext as MainWindowViewModel;


    /// <summary>Вывести окно на передний план (вторая копия приложения передала папку).</summary>
    public void BringToFront()
    {
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
        Topmost = true;
        Topmost = false;
    }

    /// <summary>Перетащенная папка — рабочая; перетащенный файл — его папка.</summary>
    public static string? DroppedFolder(string? path) =>
        path is null ? null : Directory.Exists(path) ? path : File.Exists(path) ? Path.GetDirectoryName(path) : null;

    /// <summary>
    /// Недавние папки: имя, ниже полный путь, у текущей — галочка. Меню собирается до показа: пункты, добавленные
    /// в Opening, Avalonia 12 не показывает (меню было пустой полоской).
    /// </summary>
    private void ShowRecentMenu()
    {
        if (ViewModel is not { RecentFolders.Count: > 0 } vm)
        {
            return;
        }

        var menu = new MenuFlyout { Placement = PlacementMode.RightEdgeAlignedBottom };
        foreach (var folder in vm.RecentFolders)
        {
            var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(folder));
            menu.Items.Add(Menus.Item(name.Length > 0 ? name : folder, folder, () => vm.OpenRecentCommand.Execute(folder),
                check: string.Equals(folder, vm.Folder, StringComparison.Ordinal),
                accent: this.TryFindResource("Accent2Brush", ActualThemeVariant, out var accent) ? accent as Avalonia.Media.IBrush : null));
        }

        menu.ShowAt(RecentButton);
    }

    private void OnDragOver(object? sender, DragEventArgs e) =>
        e.DragEffects = e.DataTransfer.Contains(DataFormat.File) ? DragDropEffects.Link : DragDropEffects.None;

    private void OnDrop(object? sender, DragEventArgs e)
    {
        if (DroppedFolder(e.DataTransfer.TryGetFile()?.TryGetLocalPath()) is { } folder)
        {
            ViewModel?.OpenFolder(folder);
        }
    }

    /// <summary>Идёт задача — спросить; согласились — отменить всё (процессы будут убиты, RAM-диск снят) и закрыть.</summary>
    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_closeConfirmed || ViewModel is not { } vm || !vm.Services.Jobs.IsBusy)
        {
            return;
        }

        e.Cancel = true;
        var close = await vm.Dialogs.ConfirmAsync(
            "Задача ещё идёт",
            "Если закрыть окно, задачи будут отменены: ffmpeg остановится, недописанные файлы удалятся.\n\nЗакрыть?",
            "Отменить задачи и закрыть",
            "Не закрывать");
        if (!close)
        {
            return;
        }

        vm.Services.Jobs.CancelAll();
        await Task.WhenAny(vm.Services.Jobs.WhenIdleAsync(), Task.Delay(TimeSpan.FromSeconds(15)));
        _closeConfirmed = true;
        Close();
    }
}
