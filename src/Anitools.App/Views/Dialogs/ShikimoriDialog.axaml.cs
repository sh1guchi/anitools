using Anitools.App.ViewModels;
using Avalonia;
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
        // другой тайтл — карточку с начала; окно закрыли — постеры больше не нужны
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ShikimoriPickerViewModel.Selected))
            {
                DetailScroll.Offset = default(Vector);
            }
        };
        Closed += (_, _) => viewModel.StopPosters();
    }

    private async void OnOpenSite(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { Tag: ShikimoriResultViewModel row } && DataContext is ShikimoriPickerViewModel vm)
        {
            await Launcher.LaunchUriAsync(new Uri(row.Anime.Url(vm.Site)));
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
