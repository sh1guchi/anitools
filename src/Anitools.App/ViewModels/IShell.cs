using Anitools.App.Services;

namespace Anitools.App.ViewModels;

/// <summary>Что страницам нужно от приложения: службы, диалоги, уведомления и переход к задачам.</summary>
public interface IShell
{
    AppServices Services { get; }

    IDialogService Dialogs { get; }

    /// <summary>Размер файла для списков (в тестах подменяется, чтобы не создавать гигабайтные файлы).</summary>
    Func<string, long?> FileSize { get; }

    void ShowJobs();

    /// <summary>Всплывающее уведомление: «Запущено…», «Переименовано: 3.», итог задачи; исчезает само.</summary>
    void Toast(string text, ToastKind kind = ToastKind.Info);
}
