using Avalonia.Controls;

namespace Anitools.App.Views.Dialogs;

/// <summary>Сообщение или вопрос «да/нет»: результат ShowDialog — нажата ли первая кнопка.</summary>
public sealed partial class MessageDialog : Window
{
    public MessageDialog()
        : this("anitools", "", "OK", null)
    {
    }

    /// <param name="cancel">null — кнопка одна (просто сообщение).</param>
    public MessageDialog(string title, string message, string confirm, string? cancel)
    {
        InitializeComponent();
        Title = title;
        TitleText.Text = title;
        MessageText.Text = message;
        ConfirmButton.Content = confirm;
        ConfirmButton.Click += (_, _) => Close(true);
        CancelButton.IsVisible = cancel is not null;
        CancelButton.Content = cancel;
        CancelButton.Click += (_, _) => Close(false);
    }
}
