using Anitools.Core.Templates;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Input;

namespace Anitools.App.Views.Controls;

/// <summary>
/// Поле шаблона — как визуальный редактор шаблонов Anime Uploader (app.js, templatesCard), для одной строки:
/// {переменная} — неделимая плашка, {?условие}…{/} — пунктирная рамка, ошибки — красным. Стрелки перескакивают
/// плашку, Backspace/Delete удаляют её целиком (у условия — снимают условие, текст внутри остаётся), щелчок по
/// плашке — меню «удалить / заменить / сменить условие». Все правки идут как ввод — Ctrl+Z их отменяет.
/// Вид поля — стиль TextBox.tpl в App.axaml (свой TextPresenter и слой плашек под ним).
/// </summary>
public sealed class TemplateBox : TextBox
{
    public static readonly StyledProperty<IReadOnlyList<TemplateVariable>?> VariablesProperty =
        AvaloniaProperty.Register<TemplateBox, IReadOnlyList<TemplateVariable>?>(nameof(Variables));

    public static readonly StyledProperty<IReadOnlyList<TemplateCondition>?> ConditionsProperty =
        AvaloniaProperty.Register<TemplateBox, IReadOnlyList<TemplateCondition>?>(nameof(Conditions));

    public static readonly StyledProperty<IReadOnlyList<TemplateIssue>?> IssuesProperty =
        AvaloniaProperty.Register<TemplateBox, IReadOnlyList<TemplateIssue>?>(nameof(Issues));

    private TemplateMarkup? _markup;
    private TemplatePalette? _palette;
    private TemplateTokenLayer? _layer;
    private int _clickCount;
    private bool _touched;

    public TemplateBox()
    {
        Classes.Add("tpl");
        AcceptsReturn = false;
        // Меню и кнопки редактора забирают фокус — выделение должно дождаться их
        ClearSelectionOnLostFocus = false;
    }

    /// <summary>Переменные для меню «заменить на» и подсказок.</summary>
    public IReadOnlyList<TemplateVariable>? Variables
    {
        get => GetValue(VariablesProperty);
        set => SetValue(VariablesProperty, value);
    }

    /// <summary>Готовые условия для меню условия.</summary>
    public IReadOnlyList<TemplateCondition>? Conditions
    {
        get => GetValue(ConditionsProperty);
        set => SetValue(ConditionsProperty, value);
    }

    /// <summary>Ошибки шаблона — подсвечиваются красным.</summary>
    public IReadOnlyList<TemplateIssue>? Issues
    {
        get => GetValue(IssuesProperty);
        set => SetValue(IssuesProperty, value);
    }

    /// <summary>Открытое меню плашки (для тестов).</summary>
    internal MenuFlyout? OpenMenu { get; private set; }

    internal TemplateTextPresenter? Presenter { get; private set; }

    internal TemplatePalette Palette => _palette ??= new TemplatePalette(this);

    protected override Type StyleKeyOverride => typeof(TextBox);

    private string Source => Text ?? "";

    internal TemplateMarkup Markup(string text) => _markup = TemplateMarkup.For(_markup, text, Issues);

    /// <summary>Вставить {переменную} вместо выделения или у каретки (поле ещё не трогали — в конец).</summary>
    public void InsertVariable(string name)
    {
        var (start, end) = Snapped();
        if (!_touched)
        {
            start = end = Source.Length;
        }

        Replace(start, end, "{" + name + "}");
        Focus();
    }

    /// <summary>
    /// Выделенное — в условие {?выражение}…{/} (выделения нет — пустой блок с кареткой внутри);
    /// после — выделен текст внутри условия.
    /// </summary>
    public void WrapInCondition(string expression)
    {
        var (start, end) = Snapped();
        if (!_touched)
        {
            start = end = Source.Length;
        }

        var inner = Source[start..end];
        var open = "{?" + expression + "}";
        Replace(start, end, open + inner + "{/}");
        Select(start + open.Length, start + open.Length + inner.Length);
        Focus();
    }

    /// <summary>Заменить весь шаблон — тоже как ввод, Ctrl+Z вернёт прежний.</summary>
    public void ReplaceAll(string template)
    {
        if (template != Source)
        {
            Replace(0, Source.Length, template);
        }

        Focus();
    }

    /// <summary>Выделить кусок шаблона (переход к ошибке).</summary>
    public void Select(int start, int end)
    {
        start = Math.Clamp(start, 0, Source.Length);
        end = Math.Clamp(end, start, Source.Length);
        SetCurrentValue(SelectionStartProperty, start);
        SetCurrentValue(SelectionEndProperty, end);
        Presenter?.MoveCaretToTextPosition(end);
        _touched = true;
    }

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);
        Presenter = e.NameScope.Find<TextPresenter>("PART_TextPresenter") as TemplateTextPresenter;
        _layer = e.NameScope.Find<TemplateTokenLayer>("PART_TokenLayer");
        Presenter?.Box = this;
        _layer?.Box = this;
        Presenter?.LayoutUpdated += (_, _) => _layer?.InvalidateVisual();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IssuesProperty)
        {
            Presenter?.Restyle();
            _layer?.InvalidateVisual();
        }
        else if (change.Property == TextProperty || change.Property == SelectionStartProperty || change.Property == SelectionEndProperty)
        {
            _layer?.InvalidateVisual();
        }
    }

    protected override void OnGotFocus(FocusChangedEventArgs e)
    {
        base.OnGotFocus(e);
        _touched = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (!e.Handled && Presenter is not null && string.IsNullOrEmpty(Presenter.PreeditText) && PillKey(e))
        {
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
        if (e.Key is Key.Left or Key.Right or Key.Home or Key.End or Key.Up or Key.Down)
        {
            SnapCaret(forward: e.Key is Key.Right or Key.End or Key.Down);
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        _clickCount = e.ClickCount;
        base.OnPointerPressed(e);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (e.InitialPressMouseButton != MouseButton.Left || _clickCount != 1 || SelectionStart != SelectionEnd
            || Presenter is null || e.KeyModifiers != KeyModifiers.None)
        {
            return;
        }

        var markup = Markup(Source);
        var index = TemplateTokenLayer.TokenAt(markup, Presenter.TextLayout, e.GetPosition(Presenter));
        if (index >= 0)
        {
            MoveCaret(markup.Tokens[index].End, extend: false);
            ShowPillMenu(index);
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (Presenter is null)
        {
            return;
        }

        var markup = Markup(Source);
        var index = TemplateTokenLayer.TokenAt(markup, Presenter.TextLayout, e.GetPosition(Presenter));
        ToolTip.SetTip(this, index >= 0 ? Describe(markup, index) : null);
    }

    /// <summary>Подсказка к плашке: что это и что с ней не так.</summary>
    internal string Describe(TemplateMarkup markup, int index)
    {
        var token = markup.Tokens[index];
        var what = token.Kind switch
        {
            TemplateTokenKind.Variable => Variables?.FirstOrDefault(v => v.Name == token.Value) is { } v ? $"{v.Token} — {v.Label}" : $"{{{token.Value}}}",
            TemplateTokenKind.Condition => $"Условие: {ConditionLabel(token)}",
            _ => markup.Pairs.TryGetValue(index, out var open) ? $"Конец условия «{ConditionLabel(markup.Tokens[open])}»" : "{/} — конец условия",
        };
        var problem = Issues?.FirstOrDefault(i => !i.IsWarning && i.Start < token.End && token.Start < i.End)?.Message;
        return problem is null ? what : $"{what}\n{problem}";
    }

    /// <summary>Подпись условия: из готового списка или «если есть …» / «если … не …» (txCondLabel в app.js).</summary>
    internal string ConditionLabel(TemplateToken condition)
    {
        var expression = condition.NotEqual is null ? condition.Value : $"{condition.Value}≠{condition.NotEqual}";
        return Conditions?.FirstOrDefault(c => c.Expression == expression)?.Label
            ?? (condition.NotEqual is null ? $"если есть {condition.Value}" : $"если {condition.Value} не {condition.NotEqual}");
    }

    /// <summary>Стрелки, Backspace и Delete у края плашки: плашка — одно целое.</summary>
    private bool PillKey(KeyEventArgs e)
    {
        var markup = Markup(Source);
        var collapsed = SelectionStart == SelectionEnd;
        var caret = SelectionEnd;
        switch (e.Key)
        {
            case Key.Back when e.KeyModifiers == KeyModifiers.None && collapsed && markup.PillAt(caret, forward: false) is var i and >= 0:
                RemovePill(markup, i);
                return true;
            case Key.Delete when e.KeyModifiers == KeyModifiers.None && collapsed && markup.PillAt(caret, forward: true) is var i and >= 0:
                RemovePill(markup, i);
                return true;
            case Key.Left when (e.KeyModifiers == KeyModifiers.Shift || (e.KeyModifiers == KeyModifiers.None && collapsed))
                               && markup.PillAt(caret, forward: false) is var i and >= 0:
                MoveCaret(markup.Tokens[i].Start, extend: e.KeyModifiers == KeyModifiers.Shift);
                return true;
            case Key.Right when (e.KeyModifiers == KeyModifiers.Shift || (e.KeyModifiers == KeyModifiers.None && collapsed))
                                && markup.PillAt(caret, forward: true) is var i and >= 0:
                MoveCaret(markup.Tokens[i].End, extend: e.KeyModifiers == KeyModifiers.Shift);
                return true;
            default:
                return false;
        }
    }

    /// <summary>Каретка не стоит внутри плашки: после перехода по словам и строкам — к краю по ходу движения.</summary>
    private void SnapCaret(bool forward)
    {
        var markup = Markup(Source);
        var index = markup.PillAround(SelectionEnd);
        if (index >= 0)
        {
            MoveCaret(forward ? markup.Tokens[index].End : markup.Tokens[index].Start, extend: SelectionStart != SelectionEnd);
        }
    }

    private void MoveCaret(int position, bool extend)
    {
        Presenter?.MoveCaretToTextPosition(position);
        SetCurrentValue(extend ? SelectionEndProperty : CaretIndexProperty, position);
    }

    /// <summary>Выделение без обрезанных плашек: край внутри плашки — к её краю наружу.</summary>
    private (int Start, int End) Snapped()
    {
        var markup = Markup(Source);
        var start = Math.Clamp(Math.Min(SelectionStart, SelectionEnd), 0, Source.Length);
        var end = Math.Clamp(Math.Max(SelectionStart, SelectionEnd), 0, Source.Length);
        if (markup.PillAround(start) is var a and >= 0)
        {
            start = markup.Tokens[a].Start;
        }

        if (markup.PillAround(end) is var b and >= 0)
        {
            end = markup.Tokens[b].End;
        }

        return (start, end);
    }

    /// <summary>Переменная — прочь; условие или его {/} — снять условие, текст внутри оставить.</summary>
    private void RemovePill(TemplateMarkup markup, int index)
    {
        var token = markup.Tokens[index];
        if (token.Kind == TemplateTokenKind.Variable || !markup.Pairs.TryGetValue(index, out var other))
        {
            Replace(token.Start, token.End, "");
            return;
        }

        var (open, close) = index < other ? (token, markup.Tokens[other]) : (markup.Tokens[other], token);
        Replace(open.Start, close.End, Source[open.End..close.Start]);
    }

    /// <summary>Заменить кусок строки одной правкой — как набор с клавиатуры: попадает в отмену.</summary>
    private void Replace(int start, int end, string text)
    {
        _touched = true;
        SetCurrentValue(SelectionStartProperty, start);
        SetCurrentValue(SelectionEndProperty, end);
        if (text.Length > 0 || end > start)
        {
            SelectedText = text;
        }
    }

    private void ShowPillMenu(int index)
    {
        var markup = Markup(Source);
        var token = markup.Tokens[index];
        var menu = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedLeft };
        if (token.Kind == TemplateTokenKind.Variable)
        {
            menu.Items.Add(TemplateMenus.Head(Describe(markup, index)));
            menu.Items.Add(Item("Удалить", null, () => RemovePill(Markup(Source), index), danger: true));
            var others = (Variables ?? []).Where(v => v.Name != token.Value).ToList();
            if (others.Count > 0)
            {
                menu.Items.Add(new Separator());
                menu.Items.Add(TemplateMenus.Head("Заменить на"));
                foreach (var v in others)
                {
                    menu.Items.Add(Item(v.Token, v.Label, () => Replace(token.Start, token.End, v.Token)));
                }
            }
        }
        else
        {
            var condition = token.Kind == TemplateTokenKind.Condition ? index : markup.Pairs.GetValueOrDefault(index, -1);
            if (condition < 0)
            {
                menu.Items.Add(TemplateMenus.Head("Лишний {/} — условие не открыто"));
                menu.Items.Add(Item("Удалить", null, () => RemovePill(Markup(Source), index), danger: true));
            }
            else
            {
                var open = markup.Tokens[condition];
                var current = open.NotEqual is null ? open.Value : $"{open.Value}≠{open.NotEqual}";
                menu.Items.Add(TemplateMenus.Head("Показывать, только…"));
                var options = (Conditions ?? []).ToList();
                if (options.All(c => c.Expression != current))
                {
                    options.Insert(0, new TemplateCondition(current, ConditionLabel(open)));
                }

                foreach (var c in options)
                {
                    menu.Items.Add(Item(c.Label, c.Token, () => Replace(open.Start, open.End, c.Token), check: c.Expression == current));
                }

                menu.Items.Add(new Separator());
                menu.Items.Add(Item("Убрать условие", "текст внутри останется", () => RemovePill(Markup(Source), condition)));
                if (markup.Pairs.TryGetValue(condition, out var close))
                {
                    var end = markup.Tokens[close].End;
                    menu.Items.Add(Item("Удалить вместе с текстом", null, () => Replace(open.Start, end, ""), danger: true));
                }
            }
        }

        var left = Presenter is { } presenter && presenter.TranslatePoint(default, this) is { } origin
            ? presenter.TextLayout.HitTestTextPosition(token.Start).X + origin.X
            : 0;
        menu.HorizontalOffset = Math.Max(0, left - 6);
        menu.Closed += (_, _) => OpenMenu = null;
        OpenMenu = menu;
        menu.ShowAt(this);
    }

    private MenuItem Item(string text, string? hint, Action action, bool check = false, bool danger = false) =>
        TemplateMenus.Item(text, hint, () =>
        {
            action();
            Focus();
        }, check, danger ? Palette.Red : null, Palette.Accent);
}
