using Avalonia.Controls.Presenters;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Utilities;

namespace Anitools.App.Views.Controls;

/// <summary>
/// Текст шаблона в <see cref="TemplateBox"/>: имя переменной — цветом плашки и полужирным, скобки прозрачные
/// (это поля плашки, которую рисует <see cref="TemplateTokenLayer"/> под текстом), ошибки — красным.
/// Раскладка та же, по которой поле ставит каретку, — поэтому всё совпадает.
/// </summary>
public sealed class TemplateTextPresenter : TextPresenter
{
    private static readonly IBrush Clear = Brushes.Transparent;

    internal TemplateBox? Box { get; set; }

    /// <summary>Пересобрать раскладку: сменились ошибки или вид.</summary>
    internal void Restyle() => InvalidateTextLayout();

    protected override TextLayout CreateTextLayout()
    {
        var layout = base.CreateTextLayout();
        var text = Text ?? "";
        if (Box is not { } box || text.Length == 0 || !string.IsNullOrEmpty(PreeditText) || PasswordChar != '\0')
        {
            return layout;
        }

        var markup = box.Markup(text);
        if (markup.Tokens.All(t => !t.IsPill) && markup.BadText.Count == 0)
        {
            return layout;
        }

        var styled = new TextLayout(text, Typeface(FontWeight), FontSize, Foreground, TextAlignment, TextWrapping, null, null, FlowDirection,
            layout.MaxWidth, layout.MaxHeight, LineHeight, LetterSpacing, 0, FontFeatures, RunStyles(markup, box.Palette));
        layout.Dispose();
        return styled;
    }

    /// <summary>Цвет каждого символа → непересекающиеся куски (выделение — поверх всего, как у обычного поля).</summary>
    private List<ValueSpan<TextRunProperties>> RunStyles(TemplateMarkup markup, TemplatePalette palette)
    {
        var text = markup.Text;
        var brushes = new IBrush?[text.Length];
        var bold = new bool[text.Length];
        for (var i = 0; i < markup.Tokens.Count; i++)
        {
            var token = markup.Tokens[i];
            if (!token.IsPill)
            {
                continue;
            }

            var color = palette.Pill(markup, i).Text;
            for (var k = token.Start; k < token.End; k++)
            {
                brushes[k] = k == token.Start || k == token.End - 1 ? Clear : color;
                bold[k] = true;
            }
        }

        foreach (var (start, end) in markup.BadText)
        {
            for (var k = start; k < end; k++)
            {
                brushes[k] = palette.Red;
            }
        }

        var selectionStart = Math.Clamp(Math.Min(SelectionStart, SelectionEnd), 0, text.Length);
        var selectionEnd = Math.Clamp(Math.Max(SelectionStart, SelectionEnd), 0, text.Length);
        if (ShowSelectionHighlight && selectionEnd > selectionStart)
        {
            // В выделении видны и скобки: копируется ведь текст шаблона целиком
            for (var k = selectionStart; k < selectionEnd; k++)
            {
                brushes[k] = SelectionForegroundBrush ?? (brushes[k] == Clear ? Foreground : brushes[k]);
            }
        }

        var spans = new List<ValueSpan<TextRunProperties>>();
        var regular = Typeface(FontWeight);
        var semiBold = Typeface(FontWeight.SemiBold);
        for (var start = 0; start < text.Length;)
        {
            var end = start + 1;
            while (end < text.Length && brushes[end] == brushes[start] && bold[end] == bold[start])
            {
                end++;
            }

            if (brushes[start] is not null || bold[start])
            {
                spans.Add(new ValueSpan<TextRunProperties>(start, end - start, new GenericTextRunProperties(
                    bold[start] ? semiBold : regular, FontSize, null, brushes[start] ?? Foreground, null, BaselineAlignment.Baseline, null, FontFeatures)));
            }

            start = end;
        }

        return spans;
    }

    private Typeface Typeface(FontWeight weight) => new(FontFamily, FontStyle, weight, FontStretch);
}
