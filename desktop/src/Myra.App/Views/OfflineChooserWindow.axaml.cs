using Avalonia.Controls;
using Avalonia.Input;
using Myra.App.ViewModels;
using Myra.Core;

namespace Myra.App.Views;

/// Files and versions of a downloaded title. Closes with the chosen file, or null.
public partial class OfflineChooserWindow : Window
{
    public OfflineChooserWindow()
    {
        InitializeComponent();
    }

    public OfflineChooserWindow(OfflineChooserViewModel chooser) : this()
    {
        DataContext = chooser;
        chooser.Chosen += version => Close(version);
        DoneButton.Click += (_, _) => Close(null);
        KeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            Close(null);
            e.Handled = true;
        };
    }

    public static Task<EntertainmentVersion?> ShowAsync(Window owner, OfflineChooserViewModel chooser) =>
        new OfflineChooserWindow(chooser).ShowDialog<EntertainmentVersion?>(owner);
}
