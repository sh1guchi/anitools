using Anitools.App.ViewModels;

namespace Anitools.App.Services;

/// <summary>Диалоги и действия с системой — отдельно от моделей представления, чтобы их можно было тестировать.</summary>
public interface IDialogService
{
    /// <summary>Выбрать папку; отказ — null.</summary>
    Task<string?> PickFolderAsync(string title, string? start = null);

    /// <summary>Выбрать файл; patterns — «*.json» и т.п. (null — любые); отказ — null.</summary>
    Task<string?> PickFileAsync(string title, string? start = null, IReadOnlyList<string>? patterns = null);

    /// <summary>Куда сохранить файл (предлагается имя); отказ — null.</summary>
    Task<string?> PickSaveFileAsync(string title, string suggestedName, string? start = null);

    /// <summary>Спросить строку (имя пресета и т.п.); отказ — null.</summary>
    Task<string?> PromptAsync(string title, string message, string initial = "", string confirm = "Сохранить");

    Task<bool> ConfirmAsync(string title, string message, string confirm = "Да", string cancel = "Отмена");

    Task ShowMessageAsync(string title, string message);

    /// <summary>Открыть папку или файл в проводнике / программе по умолчанию.</summary>
    Task OpenPathAsync(string path);

    Task OpenUrlAsync(string url);

    Task CopyTextAsync(string text);

    /// <summary>Поиск тайтла на Shikimori (§4.9); закрыли окно — «пропустить».</summary>
    Task<ShikimoriChoice> PickShikimoriAsync(ShikimoriPickerViewModel picker);

    /// <summary>Перегруппировка файлов HLS по тайтлам (§4.9); true — применить.</summary>
    Task<bool> RegroupAsync(RegroupViewModel regroup);
}
