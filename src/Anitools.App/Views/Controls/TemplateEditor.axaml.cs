using System.Windows.Input;
using Anitools.Core.Templates;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using CommunityToolkit.Mvvm.Input;

namespace Anitools.App.Views.Controls;

/// <summary>Строка ошибки под полем: сообщение и кусок шаблона вокруг места ошибки (txSnippet в app.js).</summary>
public sealed record TemplateIssueRow(TemplateIssue Issue, string Before, string Marked, string After)
{
    public string Message => Issue.Message;

    public bool IsWarning => Issue.IsWarning;

    public bool HasSnippet => Marked.Length > 0;

    /// <summary>«…Сезон {сзн} Серия…»: по 18 символов вокруг ошибки.</summary>
    public static TemplateIssueRow For(TemplateIssue issue, string source)
    {
        var start = Math.Clamp(issue.Start, 0, source.Length);
        var end = Math.Clamp(issue.End, start, source.Length);
        if (end == start)
        {
            return new TemplateIssueRow(issue, "", "", "");
        }

        var from = Math.Max(0, start - 18);
        var to = Math.Min(source.Length, end + 18);
        return new TemplateIssueRow(issue, (from > 0 ? "…" : "") + source[from..start], source[start..end], source[end..to] + (to < source.Length ? "…" : ""));
    }
}

/// <summary>
/// Редактор шаблона имени — карточка «Шаблоны подписей» из Anime Uploader (app.js: templatesCard) для имён файлов:
/// панель «Пресеты / Условие / + Переменная», поле с плашками (<see cref="TemplateBox"/>), ошибки с куском шаблона,
/// переменные чипами, «Стандартный». Без HTML-разметки, эмодзи и вкладок — для имени файла они не нужны.
/// Шаблон и ошибки приходят снаружи (модель страницы проверяет шаблон и решает, когда его запомнить).
/// </summary>
public sealed partial class TemplateEditor : UserControl
{
    public static readonly StyledProperty<string> TextProperty =
        AvaloniaProperty.Register<TemplateEditor, string>(nameof(Text), "", defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<string> DefaultTextProperty =
        AvaloniaProperty.Register<TemplateEditor, string>(nameof(DefaultText), "");

    public static readonly StyledProperty<IReadOnlyList<TemplateVariable>?> VariablesProperty =
        AvaloniaProperty.Register<TemplateEditor, IReadOnlyList<TemplateVariable>?>(nameof(Variables));

    public static readonly StyledProperty<IReadOnlyList<TemplateVariable>?> FormattedProperty =
        AvaloniaProperty.Register<TemplateEditor, IReadOnlyList<TemplateVariable>?>(nameof(Formatted));

    public static readonly StyledProperty<IReadOnlyList<TemplateCondition>?> ConditionsProperty =
        AvaloniaProperty.Register<TemplateEditor, IReadOnlyList<TemplateCondition>?>(nameof(Conditions));

    public static readonly StyledProperty<IReadOnlyList<TemplateIssue>?> IssuesProperty =
        AvaloniaProperty.Register<TemplateEditor, IReadOnlyList<TemplateIssue>?>(nameof(Issues));

    public static readonly StyledProperty<IReadOnlyList<TemplatePreset>?> BuiltInPresetsProperty =
        AvaloniaProperty.Register<TemplateEditor, IReadOnlyList<TemplatePreset>?>(nameof(BuiltInPresets));

    public static readonly StyledProperty<IReadOnlyList<TemplatePreset>?> PresetsProperty =
        AvaloniaProperty.Register<TemplateEditor, IReadOnlyList<TemplatePreset>?>(nameof(Presets));

    public static readonly StyledProperty<ICommand?> SavePresetCommandProperty =
        AvaloniaProperty.Register<TemplateEditor, ICommand?>(nameof(SavePresetCommand));

    public static readonly StyledProperty<ICommand?> DeletePresetCommandProperty =
        AvaloniaProperty.Register<TemplateEditor, ICommand?>(nameof(DeletePresetCommand));

    public static readonly DirectProperty<TemplateEditor, IReadOnlyList<TemplateIssueRow>> IssueRowsProperty =
        AvaloniaProperty.RegisterDirect<TemplateEditor, IReadOnlyList<TemplateIssueRow>>(nameof(IssueRows), e => e.IssueRows);

    private IReadOnlyList<TemplateIssueRow> _issueRows = [];

    public TemplateEditor()
    {
        InsertCommand = new RelayCommand<string>(name =>
        {
            if (name is not null)
            {
                Box.InsertVariable(name);
            }
        });
        JumpCommand = new RelayCommand<TemplateIssueRow>(row =>
        {
            if (row is not null)
            {
                Box.Select(row.Issue.Start, row.Issue.End);
                Box.Focus();
            }
        });
        InitializeComponent();
        PresetsButton.Click += (_, _) => ShowMenu(PresetsButton, PresetsMenu());
        ConditionButton.Click += (_, _) => ShowMenu(ConditionButton, ConditionMenu());
        VariableButton.Click += (_, _) => ShowMenu(VariableButton, VariableMenu());
        ResetButton.Click += (_, _) => Box.ReplaceAll(DefaultText);
        UpdateState();
    }

    /// <summary>Текст шаблона.</summary>
    public string Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    /// <summary>Стандартный шаблон — к нему ведёт «Стандартный».</summary>
    public string DefaultText
    {
        get => GetValue(DefaultTextProperty);
        set => SetValue(DefaultTextProperty, value);
    }

    public IReadOnlyList<TemplateVariable>? Variables
    {
        get => GetValue(VariablesProperty);
        set => SetValue(VariablesProperty, value);
    }

    /// <summary>Переменные с преобразованием ({название:точки}…) — в меню «+ Переменная».</summary>
    public IReadOnlyList<TemplateVariable>? Formatted
    {
        get => GetValue(FormattedProperty);
        set => SetValue(FormattedProperty, value);
    }

    public IReadOnlyList<TemplateCondition>? Conditions
    {
        get => GetValue(ConditionsProperty);
        set => SetValue(ConditionsProperty, value);
    }

    public IReadOnlyList<TemplateIssue>? Issues
    {
        get => GetValue(IssuesProperty);
        set => SetValue(IssuesProperty, value);
    }

    public IReadOnlyList<TemplatePreset>? BuiltInPresets
    {
        get => GetValue(BuiltInPresetsProperty);
        set => SetValue(BuiltInPresetsProperty, value);
    }

    /// <summary>Свои пресеты — их можно удалить.</summary>
    public IReadOnlyList<TemplatePreset>? Presets
    {
        get => GetValue(PresetsProperty);
        set => SetValue(PresetsProperty, value);
    }

    /// <summary>«Сохранить как пресет…»: параметр — текущий шаблон.</summary>
    public ICommand? SavePresetCommand
    {
        get => GetValue(SavePresetCommandProperty);
        set => SetValue(SavePresetCommandProperty, value);
    }

    /// <summary>Удалить свой пресет: параметр — <see cref="TemplatePreset"/>.</summary>
    public ICommand? DeletePresetCommand
    {
        get => GetValue(DeletePresetCommandProperty);
        set => SetValue(DeletePresetCommandProperty, value);
    }

    public IReadOnlyList<TemplateIssueRow> IssueRows
    {
        get => _issueRows;
        private set => SetAndRaise(IssueRowsProperty, ref _issueRows, value);
    }

    public ICommand InsertCommand { get; }

    public ICommand JumpCommand { get; }

    /// <summary>Открытое меню панели (для тестов).</summary>
    internal MenuFlyout? OpenMenu { get; private set; }

    internal TemplateBox Editor => Box;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TextProperty || change.Property == IssuesProperty || change.Property == DefaultTextProperty)
        {
            UpdateState();
        }
    }

    private void UpdateState()
    {
        IssueRows = [.. (Issues ?? []).Select(i => TemplateIssueRow.For(i, Text ?? ""))];
        var custom = (Text ?? "") != DefaultText;
        CustomBadge.IsVisible = custom;
        ResetButton.IsVisible = custom;
    }

    private void ShowMenu(Control anchor, MenuFlyout menu)
    {
        menu.Placement = PlacementMode.BottomEdgeAlignedLeft;
        menu.Closed += (_, _) => OpenMenu = null;
        OpenMenu = menu;
        menu.ShowAt(anchor);
    }

    private MenuFlyout PresetsMenu()
    {
        var menu = new MenuFlyout();
        var current = Text ?? "";
        menu.Items.Add(Menus.Head("Готовые"));
        foreach (var preset in BuiltInPresets ?? [])
        {
            menu.Items.Add(Item(preset.Name, preset.Template, () => Box.ReplaceAll(preset.Template), preset.Template == current));
        }

        var own = Presets ?? [];
        if (own.Count > 0)
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(Menus.Head("Свои"));
            foreach (var preset in own)
            {
                menu.Items.Add(Item(preset.Name, preset.Template, () => Box.ReplaceAll(preset.Template), preset.Template == current));
            }
        }

        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Сохранить как пресет…", "этот шаблон под своим именем", () => SavePresetCommand?.Execute(current)));
        if (own.Count > 0)
        {
            var delete = new MenuItem { Header = "Удалить свой пресет", Foreground = Box.Palette.Red };
            foreach (var preset in own)
            {
                delete.Items.Add(Menus.Item(preset.Name, preset.Template, () => DeletePresetCommand?.Execute(preset)));
            }

            menu.Items.Add(delete);
        }

        return menu;
    }

    private MenuFlyout ConditionMenu()
    {
        var menu = new MenuFlyout();
        var collapsed = Box.SelectionStart == Box.SelectionEnd;
        menu.Items.Add(Menus.Head(collapsed ? "Пустой блок с условием — допишите текст внутрь" : "Выделенное покажу, только…"));
        foreach (var condition in Conditions ?? [])
        {
            menu.Items.Add(Item(condition.Label, condition.Token, () => Box.WrapInCondition(condition.Expression)));
        }

        return menu;
    }

    private MenuFlyout VariableMenu()
    {
        var menu = new MenuFlyout();
        menu.Items.Add(Menus.Head("Вставить там, где каретка"));
        foreach (var variable in Variables ?? [])
        {
            menu.Items.Add(Item(variable.Token, variable.Label, () => Box.InsertVariable(variable.Name)));
        }

        if (Formatted is { Count: > 0 } formatted)
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(Menus.Head("Для имён как у релизов: To.Be.Hero.X.S01E01"));
            foreach (var variable in formatted)
            {
                menu.Items.Add(Item(variable.Token, variable.Label, () => Box.InsertVariable(variable.Name)));
            }
        }

        return menu;
    }

    private MenuItem Item(string text, string? hint, Action action, bool check = false) =>
        Menus.Item(text, hint, action, check, accent: Box.Palette.Accent);
}
