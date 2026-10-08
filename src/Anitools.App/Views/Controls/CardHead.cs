using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Material.Icons;
using Material.Icons.Avalonia;

namespace Anitools.App.Views.Controls;

/// <summary>
/// Заголовок карточки (.card-head): значок акцентом, название, пояснение приглушённо и действия справа (Content).
/// Шаблон — в App.axaml.
/// </summary>
public sealed class CardHead : ContentControl
{
    public static readonly StyledProperty<MaterialIconKind?> IconProperty =
        AvaloniaProperty.Register<CardHead, MaterialIconKind?>(nameof(Icon));

    public static readonly StyledProperty<string?> TitleProperty =
        AvaloniaProperty.Register<CardHead, string?>(nameof(Title));

    public static readonly StyledProperty<string?> CaptionProperty =
        AvaloniaProperty.Register<CardHead, string?>(nameof(Caption));

    private MaterialIcon? _icon;

    public MaterialIconKind? Icon
    {
        get => GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    public string? Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string? Caption
    {
        get => GetValue(CaptionProperty);
        set => SetValue(CaptionProperty, value);
    }

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);
        _icon = e.NameScope.Find<MaterialIcon>("PART_Icon");
        UpdateIcon();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IconProperty)
        {
            UpdateIcon();
        }
    }

    private void UpdateIcon()
    {
        if (_icon is null)
        {
            return;
        }

        _icon.IsVisible = Icon is not null;
        if (Icon is { } kind)
        {
            _icon.Kind = kind;
        }
    }
}
