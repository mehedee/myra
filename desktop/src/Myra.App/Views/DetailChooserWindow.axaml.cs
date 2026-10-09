using Avalonia.Controls;
using Avalonia.Input;
using Myra.App.ViewModels;
using Myra.Core;

namespace Myra.App.Views;

/// "Choose an episode" / "Choose a version" for a Home title. Closes with the chosen file, or null.
public partial class DetailChooserWindow : Window
{
    public DetailChooserWindow()
    {
        InitializeComponent();
    }

    public DetailChooserWindow(DetailChooserViewModel chooser) : this()
    {
        DataContext = chooser;
        chooser.Chosen += version => Close(version);
        CancelButton.Click += (_, _) => Close(null);
        KeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            Close(null);
            e.Handled = true;
        };
    }

    public static Task<EntertainmentVersion?> ShowAsync(Window owner, DetailChooserViewModel chooser) =>
        new DetailChooserWindow(chooser).ShowDialog<EntertainmentVersion?>(owner);
}
