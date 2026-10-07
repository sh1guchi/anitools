using Anitools.App.ViewModels;

namespace Anitools.App.Services;

/// <summary>Диалоги и действия с системой — отдельно от моделей представления, чтобы их можно было тестировать.</summary>
public interface IDialogService
{
    /// <summary>Выбрать папку; отказ — null.</summary>
    Task<string?> PickFolderAsync(string title, string? start = null);

    /// <summary>Выбрать файл; отказ — null.</summary>
    Task<string?> PickFileAsync(string title, string? start = null);

    Task<bool> ConfirmAsync(string title, string message, string confirm = "Да", string cancel = "Отмена");

    Task ShowMessageAsync(string title, string message);

    /// <summary>Открыть папку или файл в проводнике / программе по умолчанию.</summary>
    Task OpenPathAsync(string path);

    Task OpenUrlAsync(string url);

    Task CopyTextAsync(string text);

    /// <summary>Поиск тайтла на Shikimori (§4.9); закрыли окно — «пропустить».</summary>
    Task<ShikimoriChoice> PickShikimoriAsync(ShikimoriPickerViewModel picker);
}
