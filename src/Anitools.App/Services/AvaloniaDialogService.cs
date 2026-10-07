using Anitools.App.Views.Dialogs;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;

namespace Anitools.App.Services;

/// <summary>Диалоги через окно приложения: системные выбор папки/файла, свои подтверждение и сообщение.</summary>
public sealed class AvaloniaDialogService(Window owner) : IDialogService
{
    public async Task<string?> PickFolderAsync(string title, string? start = null)
    {
        var folders = await owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            SuggestedStartLocation = await StartAsync(start),
        });
        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }

    public async Task<string?> PickFileAsync(string title, string? start = null)
    {
        var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            SuggestedStartLocation = await StartAsync(start),
        });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    public Task<bool> ConfirmAsync(string title, string message, string confirm = "Да", string cancel = "Отмена") =>
        new MessageDialog(title, message, confirm, cancel).ShowDialog<bool>(owner);

    public Task ShowMessageAsync(string title, string message) =>
        new MessageDialog(title, message, "OK", null).ShowDialog<bool>(owner);

    public async Task OpenPathAsync(string path)
    {
        if (Directory.Exists(path))
        {
            await owner.Launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(path));
        }
        else if (File.Exists(path))
        {
            await owner.Launcher.LaunchFileInfoAsync(new FileInfo(path));
        }
    }

    public async Task OpenUrlAsync(string url) => await owner.Launcher.LaunchUriAsync(new Uri(url));

    public async Task CopyTextAsync(string text)
    {
        if (owner.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(text);
        }
    }

    private async Task<IStorageFolder?> StartAsync(string? start)
    {
        var folder = start is null ? null : Directory.Exists(start) ? start : Path.GetDirectoryName(start);
        return folder is { Length: > 0 } && Directory.Exists(folder) ? await owner.StorageProvider.TryGetFolderFromPathAsync(folder) : null;
    }
}
