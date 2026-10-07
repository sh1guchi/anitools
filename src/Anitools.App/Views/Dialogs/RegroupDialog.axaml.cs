using Anitools.App.ViewModels;
using Avalonia.Controls;

namespace Anitools.App.Views.Dialogs;

/// <summary>Окно перегруппировки; результат ShowDialog — нажато ли «Применить».</summary>
public sealed partial class RegroupDialog : Window
{
    public RegroupDialog() => InitializeComponent();

    public RegroupDialog(RegroupViewModel viewModel)
        : this()
    {
        DataContext = viewModel;
        viewModel.Closed += ok => Close(ok);
    }
}
