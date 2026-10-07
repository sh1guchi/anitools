using Anitools.App.ViewModels;
using Anitools.Core.Shikimori;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Anitools.App.Views.Dialogs;

/// <summary>Окно выбора тайтла на Shikimori; результат ShowDialog — <see cref="ShikimoriChoice"/>.</summary>
public sealed partial class ShikimoriDialog : Window
{
    public ShikimoriDialog()
    {
        InitializeComponent();
        ResultsList.DoubleTapped += (_, _) => (DataContext as ShikimoriPickerViewModel)?.ChooseCommand.Execute(null);
        Opened += async (_, _) =>
        {
            QueryBox.Focus();
            if (DataContext is ShikimoriPickerViewModel vm)
            {
                await vm.SearchAsync();
            }
        };
    }

    public ShikimoriDialog(ShikimoriPickerViewModel viewModel)
        : this()
    {
        DataContext = viewModel;
        viewModel.Chosen += choice => Close(choice);
    }

    private async void OnOpenSite(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { Tag: ShikimoriAnime anime } && DataContext is ShikimoriPickerViewModel vm)
        {
            await Launcher.LaunchUriAsync(new Uri(anime.Url(vm.Site)));
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && DataContext is ShikimoriPickerViewModel vm)
        {
            vm.SkipCommand.Execute(null);
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }
}
