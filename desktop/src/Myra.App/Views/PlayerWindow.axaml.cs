using Avalonia.Controls;
using Myra.App.ViewModels;

namespace Myra.App.Views;

/// Stand-alone host for PlayerView: command-line playback and any caller that wants a separate window.
/// Back to Library (or Stop) closes the window.
public partial class PlayerWindow : Window
{
    public PlayerWindow()
    {
        InitializeComponent();
    }

    private PlayerViewModel? ViewModel => DataContext as PlayerViewModel;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (ViewModel is not { } vm) return;
        vm.BackToLibraryRequested -= Close;
        vm.BackToLibraryRequested += Close;
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        // Order matters for libVLC: stop, detach the video surface, then dispose.
        // Shutdown can raise Closing twice (main window, then the app); only the first pass may touch libVLC.
        if (ViewModel is { IsClosed: false } vm)
        {
            vm.BackToLibraryRequested -= Close;
            vm.StopPlayback();
            Player.ReleaseVideo();
            vm.Close();
        }
        base.OnClosing(e);
    }
}
