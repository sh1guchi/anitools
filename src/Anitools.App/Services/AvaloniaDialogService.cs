using Anitools.App.ViewModels;
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

    public async Task<string?> PickFileAsync(string title, string? start = null, IReadOnlyList<string>? patterns = null)
    {
        var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            SuggestedStartLocation = await StartAsync(start),
            FileTypeFilter = patterns is null ? null : [new FilePickerFileType(string.Join(", ", patterns)) { Patterns = [.. patterns] }, FilePickerFileTypes.All],
        });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    public async Task<string?> PickSaveFileAsync(string title, string suggestedName, string? start = null)
    {
        var extension = Path.GetExtension(suggestedName);
        var file = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedName,
            DefaultExtension = extension.TrimStart('.'),
            SuggestedStartLocation = await StartAsync(start),
            FileTypeChoices = extension.Length > 0 ? [new FilePickerFileType("*" + extension) { Patterns = ["*" + extension] }] : null,
        });
        return file?.TryGetLocalPath();
    }

    public Task<string?> PromptAsync(string title, string message, string initial = "", string confirm = "Сохранить") =>
        new PromptDialog(title, message, initial, confirm).ShowDialog<string?>(owner);

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

    public async Task<ShikimoriChoice> PickShikimoriAsync(ShikimoriPickerViewModel picker) =>
        await new ShikimoriDialog(picker).ShowDialog<ShikimoriChoice?>(owner) ?? ShikimoriChoice.Skip;

    public Task<bool> RegroupAsync(RegroupViewModel regroup) => new RegroupDialog(regroup).ShowDialog<bool>(owner);

    private async Task<IStorageFolder?> StartAsync(string? start)
    {
        var folder = start is null ? null : Directory.Exists(start) ? start : Path.GetDirectoryName(start);
        return folder is { Length: > 0 } && Directory.Exists(folder) ? await owner.StorageProvider.TryGetFolderFromPathAsync(folder) : null;
    }
}
