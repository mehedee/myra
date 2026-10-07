using Avalonia.Controls;
using Avalonia.Input;
using Myra.App.ViewModels;

namespace Myra.App.Views;

public partial class HomeCollectionsWindow : Window
{
    public HomeCollectionsWindow()
    {
        InitializeComponent();
    }

    public HomeCollectionsWindow(HomeCollectionsViewModel collections) : this()
    {
        DataContext = collections;
        collections.Confirm = message => new ConfirmDialog(message).ShowDialog<bool>(this);
        DoneButton.Click += (_, _) => Close();
        KeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            Close();
            e.Handled = true;
        };
        Closed += (_, _) => collections.Dispose();
    }
}
