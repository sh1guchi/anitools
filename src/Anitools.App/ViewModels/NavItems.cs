namespace Anitools.App.ViewModels;

/// <summary>Строка навигации слева: заголовок раздела, разделитель или страница.</summary>
public interface INavItem
{
    bool IsPage { get; }
}

/// <summary>«ВИДЕО», «АУДИО»… — не выбирается.</summary>
public sealed record NavHeader(string Title) : INavItem
{
    public bool IsPage => false;
}

/// <summary>Черта перед «Задачами» и «Настройками».</summary>
public sealed record NavSeparator : INavItem
{
    public bool IsPage => false;
}
