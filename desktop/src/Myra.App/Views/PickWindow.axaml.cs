using Avalonia.Controls;
using Avalonia.Input;
using Myra.App.ViewModels;
using Myra.Core;

namespace Myra.App.Views;

/// Pick Something (2.0.5): a modal window that reuses Details. Another Pick replaces the content in place.
/// Play closes the window first; the caller then opens any version or episode chooser.
public partial class PickWindow : Window
{
    public PickWindow()
    {
        InitializeComponent();
    }

    public PickWindow(PickViewModel pick) : this()
    {
        DataContext = pick;
        pick.PlayRequested += title => Close(title);
        pick.CloseRequested += () => Close(null);
        Details.AnotherPick = () => pick.AnotherPickCommand.Execute(null);
        KeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            Close(null);
            e.Handled = true;
        };
    }

    public static Task<EntertainmentTitle?> ShowAsync(Window owner, PickViewModel pick) =>
        new PickWindow(pick).ShowDialog<EntertainmentTitle?>(owner);
}
