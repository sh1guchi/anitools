using Anitools.App.Services;

namespace Anitools.App.ViewModels;

/// <summary>Что страницам нужно от приложения: службы, диалоги и переход к задачам.</summary>
public interface IShell
{
    AppServices Services { get; }

    IDialogService Dialogs { get; }

    /// <summary>Размер файла для списков (в тестах подменяется, чтобы не создавать гигабайтные файлы).</summary>
    Func<string, long?> FileSize { get; }

    void ShowJobs();
}
