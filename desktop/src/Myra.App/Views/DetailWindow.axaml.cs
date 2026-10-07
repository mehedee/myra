using Avalonia.Controls;
using Avalonia.Input;
using Myra.App.ViewModels;
using Myra.Core;

namespace Myra.App.Views;

/// Title Details as a modal window. Closes with the title to play, or null.
public partial class DetailWindow : Window
{
    public DetailWindow()
    {
        InitializeComponent();
    }

    public DetailWindow(DetailViewModel details) : this()
    {
        DataContext = details;
        details.PlayRequested += title => Close(title);
        details.CloseRequested += () => Close(null);
        KeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            Close(null);
            e.Handled = true;
        };
    }

    public static Task<EntertainmentTitle?> ShowAsync(Window owner, DetailViewModel details) =>
        new DetailWindow(details).ShowDialog<EntertainmentTitle?>(owner);
}
