using Anitools.Core.Templates;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Anitools.App.Views.Controls;

/// <summary>
/// Слой под текстом <see cref="TemplateBox"/>: плашки переменных (.tx-var), пунктирная рамка условного куска (.tx-cond),
/// красная подложка у ошибок. Координаты — из той же раскладки текста, что у поля.
/// </summary>
public sealed class TemplateTokenLayer : Control
{
    internal TemplateBox? Box { get; set; }

    public override void Render(DrawingContext context)
    {
        if (Box is not { Presenter: { } presenter } box || string.IsNullOrEmpty(presenter.Text))
        {
            return;
        }

        var markup = box.Markup(presenter.Text);
        var palette = box.Palette;
        var layout = presenter.TextLayout;
        var origin = presenter.TranslatePoint(default, this) ?? default;

        // Рамка условного куска: от {?…} до его {/}, вложенная — бирюзовая
        foreach (var (start, end) in markup.Pairs.Where(p => p.Key < p.Value))
        {
            var from = markup.Tokens[start];
            var to = markup.Tokens[end];
            var cyan = markup.Depth[start] > 0;
            var pen = new Pen(cyan ? palette.CyanLine : palette.YellowLine, 1.5, new DashStyle([3, 2], 0));
            foreach (var rect in Rects(layout, from.Start, to.End - from.Start, origin, 2, 3.5))
            {
                context.DrawRectangle(cyan ? palette.GroupCyan : palette.GroupYellow, pen, rect, 7, 7);
            }
        }

        for (var i = 0; i < markup.Tokens.Count; i++)
        {
            var token = markup.Tokens[i];
            if (!token.IsPill)
            {
                continue;
            }

            var (_, fill, line) = palette.Pill(markup, i);
            var pen = new Pen(line, 1);
            foreach (var rect in Rects(layout, token.Start, token.Length, origin, 0, 1.5))
            {
                context.DrawRectangle(fill, pen, rect, 6, 6);
            }
        }

        foreach (var (start, end) in markup.BadText)
        {
            foreach (var rect in Rects(layout, start, end - start, origin, 0.5, 1))
            {
                context.DrawRectangle(palette.RedBg, null, rect, 3, 3);
                context.FillRectangle(palette.Red, new Rect(rect.X, rect.Bottom - 1.5, rect.Width, 1.5));
            }
        }
    }

    /// <summary>Прямоугольники куска текста; не вылезают за левый край — там поле обрезает.</summary>
    private static IEnumerable<Rect> Rects(Avalonia.Media.TextFormatting.TextLayout layout, int start, int length, Point origin, double dx, double dy)
    {
        foreach (var r in layout.HitTestTextRange(start, length))
        {
            var rect = r.Translate(origin).Inflate(new Thickness(dx, dy));
            yield return rect.X < 0 ? new Rect(0, rect.Y, Math.Max(0, rect.Right), rect.Height) : rect;
        }
    }

    /// <summary>Кусок шаблона, на который наведена мышь: плашка или текст с ошибкой (для подсказки и меню).</summary>
    internal static int TokenAt(TemplateMarkup markup, Avalonia.Media.TextFormatting.TextLayout layout, Point point)
    {
        for (var i = 0; i < markup.Tokens.Count; i++)
        {
            var token = markup.Tokens[i];
            if (token.Kind != TemplateTokenKind.Text
                && layout.HitTestTextRange(token.Start, token.Length).Any(r => r.Inflate(new Thickness(0, 2)).Contains(point)))
            {
                return i;
            }
        }

        return -1;
    }
}
