using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Myra.App.ViewModels;
using Myra.Core;

namespace Myra.App.Views;

/// The Myra AI workspace as a modal window. Closes with the title to play, or null.
/// Enter asks Myra; Esc cancels a running request, otherwise closes.
public partial class AiWorkspaceWindow : Window
{
    public AiWorkspaceWindow()
    {
        InitializeComponent();
    }

    public AiWorkspaceWindow(AiWorkspaceViewModel workspace) : this()
    {
        DataContext = workspace;
        workspace.PlayRequested += title => Close(title);
        workspace.CloseRequested += () => Close(null);
        workspace.AskConsent = (kind, provider) => new AiConsentDialog(kind, provider).ShowDialog<bool>(this);
        workspace.PickSubtitleFile = PickSubtitleFileAsync;
        workspace.ShowSettings = () => AiSettingsWindow.ShowAsync(this, workspace.Ai);
        workspace.ShowDetails = details => new DetailWindow(details).ShowDialog<EntertainmentTitle?>(this);
        // Tunnel: Enter in the request box must reach the window before the text box sees it.
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        Opened += (_, _) => PromptBox.Focus();
    }

    private AiWorkspaceViewModel? Workspace => DataContext as AiWorkspaceViewModel;

    public static Task<EntertainmentTitle?> ShowAsync(Window owner, AiWorkspaceViewModel workspace) =>
        new AiWorkspaceWindow(workspace).ShowDialog<EntertainmentTitle?>(owner);

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (Workspace is not { } workspace || e.KeyModifiers != KeyModifiers.None) return;
        // Buttons, lists and open drop-downs keep their own Enter and Esc behaviour.
        if (e.Source is Button or MenuItem or ComboBoxItem || e.Source is ComboBox { IsDropDownOpen: true }) return;
        if (e.Key == Key.Enter && e.Source is not ComboBox)
        {
            if (workspace.RunCommand.CanExecute(null)) workspace.RunCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            workspace.CancelOrClose();
            e.Handled = true;
        }
    }

    private async Task<string?> PickSubtitleFileAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import This Episode's Subtitles",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Subtitles") { Patterns = AiWorkspace.SubtitleExtensions.Select(e => "*." + e).ToList() },
            ],
        });
        return files.FirstOrDefault()?.TryGetLocalPath();
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        // Closing the workspace cancels its request; the result is discarded.
        if (Workspace?.Ai.IsBusy == true) Workspace.Ai.Cancel();
        base.OnClosing(e);
    }
}
