using Avalonia.Controls;
using Avalonia.Media;
using Material.Icons;
using Material.Icons.Avalonia;

namespace Anitools.App.Views.Controls;

/// <summary>
/// Пункты всплывающих меню (как popMenu в app.js: заголовок, пункт с пояснением, галочка). Меню собирается
/// целиком до показа: пункты, добавленные в Opening, Avalonia 12 не показывает — меню выходит пустой полоской.
/// </summary>
internal static class Menus
{
    /// <summary>Серый заголовок группы — не нажимается.</summary>
    public static MenuItem Head(string text) => new()
    {
        Header = new TextBlock { Text = text, FontSize = 12, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap, MaxWidth = 340 },
        IsEnabled = false,
    };

    /// <param name="hint">Мелко второй строкой: пояснение или сам кусок шаблона.</param>
    /// <param name="check">Галочка — выбранный вариант.</param>
    /// <param name="foreground">Цвет пункта (красный — удаление).</param>
    public static MenuItem Item(string text, string? hint, Action action, bool check = false, IBrush? foreground = null, IBrush? accent = null)
    {
        var header = new StackPanel { Spacing = 1 };
        header.Children.Add(new TextBlock { Text = text });
        if (hint is not null)
        {
            header.Children.Add(new TextBlock { Text = hint, FontSize = 11.5, Classes = { "dim" }, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 340 });
        }

        var item = new MenuItem
        {
            Header = header,
            Icon = check ? new MaterialIcon { Kind = MaterialIconKind.Check, Width = 14, Height = 14, Foreground = accent } : null,
        };
        if (foreground is not null)
        {
            item.Foreground = foreground;
        }

        item.Click += (_, _) => action();
        return item;
    }
}
