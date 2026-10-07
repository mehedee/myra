using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Myra.App.ViewModels;

namespace Myra.App.Views;

public partial class MainWindow : Window
{
    private PlayerWindow? _playerWindow;

    public MainWindow()
    {
        InitializeComponent();
        EntryList.DoubleTapped += OnEntryDoubleTapped;
        ResultList.DoubleTapped += OnResultDoubleTapped;
        KeyDown += OnKeyDown;
    }

    private MainViewModel? ViewModel => DataContext as MainViewModel;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (ViewModel is not { } vm) return;
        vm.ShowSourceDialog = category => new SourceDialog(category).ShowDialog<SourceInput?>(this);
        vm.Confirm = message => new ConfirmDialog(message).ShowDialog<bool>(this);
        vm.CopyText = async text =>
        {
            if (Clipboard is not null) await Clipboard.SetTextAsync(text);
        };
        vm.ShowSettingsDialog = () => new SettingsWindow(vm.Store.Settings, vm.Services.PlayerState).ShowDialog(this);
        vm.ShowPlayer = ShowPlayer;
    }

    private void ShowPlayer(PlayerViewModel player)
    {
        if (_playerWindow is null)
        {
            _playerWindow = new PlayerWindow { DataContext = player };
            _playerWindow.Closed += (_, _) => _playerWindow = null;
            _playerWindow.Show();
        }
        _playerWindow.Activate();
    }

    private void OnEntryDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (EntryList.SelectedItem is EntryViewModel item) ViewModel?.OpenEntryCommand.Execute(item);
    }

    private void OnResultDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (ResultList.SelectedItem is GlobalResultViewModel item) ViewModel?.PlayResultCommand.Execute(item);
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (ViewModel is not { } vm) return;
        if (e.Key == Key.F && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            SearchBox.Focus();
            e.Handled = true;
        }
        else if (e.Key == Key.Back && e.Source is not TextBox)
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
        base.OnClosing(e);
    }
}
