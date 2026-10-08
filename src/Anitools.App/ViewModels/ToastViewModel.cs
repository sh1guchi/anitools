using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Material.Icons;

namespace Anitools.App.ViewModels;

public enum ToastKind
{
    Info,
    Ok,
    Warn,
    Error,
}

/// <summary>Всплывающее уведомление справа сверху: исчезает само, можно закрыть ✕.</summary>
public sealed partial class ToastViewModel(string text, ToastKind kind, Action<ToastViewModel> close) : ObservableObject
{
    public string Text { get; } = text;

    public ToastKind Kind { get; } = kind;

    public MaterialIconKind Icon => Kind switch
    {
        ToastKind.Ok => MaterialIconKind.CheckCircleOutline,
        ToastKind.Warn => MaterialIconKind.AlertOutline,
        ToastKind.Error => MaterialIconKind.CloseCircleOutline,
        _ => MaterialIconKind.InformationOutline,
    };

    public bool IsOk => Kind == ToastKind.Ok;

    public bool IsWarn => Kind == ToastKind.Warn;

    public bool IsError => Kind == ToastKind.Error;

    /// <summary>Гаснет перед удалением.</summary>
    [ObservableProperty]
    public partial bool IsLeaving { get; set; }

    /// <summary>Сколько показывать: ошибки дольше (как в Anime Uploader).</summary>
    public static TimeSpan DefaultDuration(ToastKind kind) => kind switch
    {
        ToastKind.Error => TimeSpan.FromSeconds(9),
        ToastKind.Warn => TimeSpan.FromSeconds(6),
        _ => TimeSpan.FromSeconds(3.5),
    };

    [RelayCommand]
    private void Close() => close(this);
}
