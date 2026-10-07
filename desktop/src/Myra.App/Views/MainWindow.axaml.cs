using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Reactive;
using Myra.App.ViewModels;
using Myra.Core;

namespace Myra.App.Views;

/// Main window: sidebar, sections, embedded player, index status and download footer.
/// It also opens the shell's own windows (Details, Pick, choosers, collections) for the view models.
public partial class MainWindow : Window, IShellWindows
{
    private PlayerWindow? _playerWindow;

    public MainWindow()
    {
        InitializeComponent();
        EntryList.DoubleTapped += OnEntryDoubleTapped;
        KeyDown += OnKeyDown;
        // The inspector is 460 px wide, capped by the library width; it overlays the browser.
        LibraryArea.GetObservable(BoundsProperty).Subscribe(new AnonymousObserver<Rect>(bounds =>
            InspectorPanel.Width = Math.Max(0, Math.Min(460, bounds.Width))));
    }

    private MainViewModel? ViewModel => DataContext as MainViewModel;

    public Window? OwnerWindow => this;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (ViewModel is not { } vm) return;
        vm.Windows = this;
        vm.ShowSourceDialog = category => new SourceDialog(category).ShowDialog<SourceInput?>(this);
        vm.Confirm = ConfirmAsync;
        vm.CopyText = async text =>
        {
            if (Clipboard is not null) await Clipboard.SetTextAsync(text);
        };
        vm.ShowPlayerWindow = ShowPlayerWindow;
    }

    // ---------- IShellWindows ----------

    public Task<EntertainmentTitle?> ShowDetailsAsync(DetailViewModel details) => DetailWindow.ShowAsync(this, details);
    public Task<EntertainmentTitle?> ShowPickAsync(PickViewModel pick) => PickWindow.ShowAsync(this, pick);
    public Task<EntertainmentVersion?> ChooseTitleVersionAsync(DetailChooserViewModel chooser) => DetailChooserWindow.ShowAsync(this, chooser);
    public Task<EntertainmentVersion?> ChooseOfflineVersionAsync(OfflineChooserViewModel chooser) => OfflineChooserWindow.ShowAsync(this, chooser);
    public Task ShowCollectionsAsync(HomeCollectionsViewModel collections) => new HomeCollectionsWindow(collections).ShowDialog(this);
    public Task<bool> ConfirmAsync(string message) => new ConfirmDialog(message).ShowDialog<bool>(this);

    /// Command-line playback keeps its own window.
    private void ShowPlayerWindow(PlayerViewModel player)
    {
        _playerWindow?.Close();
        _playerWindow = new PlayerWindow { DataContext = player };
        _playerWindow.Closed += (_, _) => _playerWindow = null;
        _playerWindow.Show();
        _playerWindow.Activate();
    }

    private void OnEntryDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (EntryList.SelectedItem is EntryViewModel item) ViewModel?.OpenEntryCommand.Execute(item);
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (ViewModel is not { } vm || vm.IsPlayerVisible) return;
        if (e.Key == Key.F && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            vm.Section = ShellSection.Library;
            SearchBox.Focus();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && vm.ShowInspector && e.Source is not TextBox)
        {
            vm.Inspector.Close();
            e.Handled = true;
        }
        else if (e.Key == Key.Back && vm.IsLibrary && e.Source is not TextBox)
        {
            vm.NavigateUpCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && e.Source is ListBoxItem { DataContext: EntryViewModel entry })
        {
            vm.OpenEntryCommand.Execute(entry);
            e.Handled = true;
        }
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        _playerWindow?.Close();
        // Order matters for libVLC: stop while the surface exists, detach it, then dispose.
        if (ViewModel?.Player is { IsClosed: false } player)
        {
            player.StopPlayback();
            Player.ReleaseVideo();
            player.Close();
        }
        base.OnClosing(e);
    }
}
