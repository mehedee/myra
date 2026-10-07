using Avalonia.Controls;

namespace Myra.App.Views;

public partial class ConfirmDialog : Window
{
    public ConfirmDialog() : this("")
    {
    }

    public ConfirmDialog(string message)
    {
        InitializeComponent();
        MessageText.Text = message;
        CancelButton.Click += (_, _) => Close(false);
        OkButton.Click += (_, _) => Close(true);
    }
}
