using Anitools.Core;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Anitools.App.ViewModels;

public sealed partial class MainWindowViewModel(string folder) : ObservableObject
{
    /// <summary>Рабочая папка: файлы верхнего уровня в ней — вход для всех инструментов.</summary>
    [ObservableProperty]
    public partial string Folder { get; set; } = folder;

    public string Version { get; } = $"{AppInfo.Name} {AppInfo.Version}";
}
