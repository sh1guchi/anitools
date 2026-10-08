using Avalonia.Controls;

namespace Anitools.App.Views.Dialogs;

/// <summary>Вопрос с полем ввода (как formDialog в Anime Uploader): результат ShowDialog — введённое или null.</summary>
public sealed partial class PromptDialog : Window
{
    public PromptDialog()
        : this("anitools", "", "", "OK")
    {
    }

    public PromptDialog(string title, string message, string initial, string confirm)
    {
        InitializeComponent();
        Title = title;
        TitleText.Text = title;
        MessageText.Text = message;
        MessageText.IsVisible = message.Length > 0;
        ValueBox.Text = initial;
        ConfirmButton.Content = confirm;
        ConfirmButton.Click += (_, _) => Close(ValueBox.Text?.Trim() is { Length: > 0 } value ? value : null);
        CancelButton.Click += (_, _) => Close(null);
        Opened += (_, _) =>
        {
            ValueBox.Focus();
            ValueBox.SelectAll();
        };
    }
}
