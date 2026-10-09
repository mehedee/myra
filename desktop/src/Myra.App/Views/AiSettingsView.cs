using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Myra.App.Services;
using Myra.Core;

namespace Myra.App.Views;

/// AI Settings (macOS MyraAISettingsSection): provider, API key, model, permissions, limits, usage,
/// preference text and the enabled tools. Each change is saved at once through AiService.
/// API keys go only to the secret store (ISecretStore); they are never shown again or written to settings files.
public sealed class AiSettingsView : UserControl
{
    private readonly AiService _ai;
    private readonly ComboBox _provider = new() { HorizontalAlignment = HorizontalAlignment.Stretch, Name = "AiProviderBox" };
    private readonly TextBlock _status = AppDialog.Muted("");
    private readonly StackPanel _cloud = new() { Spacing = 8 };
    private readonly TextBox _key = new() { PasswordChar = '•', Watermark = "API key", Name = "AiKeyBox", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBlock _keyState = AppDialog.Muted("");
    private readonly TextBox _model = new() { Watermark = "Model identifier", Name = "AiModelBox", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox _models = new() { PlaceholderText = "Available models", HorizontalAlignment = HorizontalAlignment.Stretch, Name = "AiModelList" };
    private readonly Button _refresh = new() { Content = "Refresh Models", Name = "AiRefreshModels" };
    private readonly Button _test = new() { Content = "Test Connection" };
    private readonly CheckBox _consent = new() { Content = "Allow requests to this cloud provider", Name = "AiCloudConsent" };
    private readonly CheckBox _history = new() { Content = "Include watch-history information in cloud requests", Name = "AiShareHistory" };
    private readonly CheckBox _subtitles = new() { Content = "Allow selected subtitle text in cloud recap requests", Name = "AiShareSubtitles" };
    private readonly CheckBox _personalize = new() { Content = "Use my saved preferences for recommendations" };
    private readonly TextBox _language = new() { Watermark = "en", Width = 120, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly NumericUpDown _limit = new() { Minimum = 1, Maximum = 500, Increment = 1, FormatString = "0", Width = 160, HorizontalAlignment = HorizontalAlignment.Left, Name = "AiDailyLimit" };
    private readonly NumericUpDown _tokens = new() { Minimum = 256, Maximum = 2000, Increment = 128, FormatString = "0", Width = 160, HorizontalAlignment = HorizontalAlignment.Left, Name = "AiOutputTokens" };
    private readonly TextBlock _usage = AppDialog.Muted("");
    private readonly TextBlock _message = AppDialog.Message();
    private readonly TextBlock _error = AppDialog.Message();
    private readonly TextBox _feedback = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 75, Watermark = "Explicit viewing preferences" };
    private readonly List<(AiFeature Feature, CheckBox Box)> _features = [];
    private readonly List<string?> _providerIds = [];
    private bool _updating;

    public AiSettingsView(AppServices services) : this(services.Ai)
    {
    }

    public AiSettingsView(AiService ai)
    {
        _ai = ai;
        _error.Foreground = Brushes.Orange;
        _message.FontSize = 12;
        _error.FontSize = 12;
        Content = Build();
        Load();
        AttachedToVisualTree += (_, _) =>
        {
            _ai.Changed += OnChanged;
            _ai.RefreshAvailability();
        };
        DetachedFromVisualTree += (_, _) => _ai.Changed -= OnChanged;
    }

    /// Short text that tells the user what happened (for checks and screen readers).
    public string? LastMessage => _message.Text;

    private Control Build()
    {
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(AppDialog.Heading("Myra AI"));
        _providerIds.Add(null);
        var names = new List<string> { "Disabled" };
        foreach (var provider in _ai.Providers)
        {
            _providerIds.Add(provider.Id);
            names.Add(provider.Title);
        }
        _provider.ItemsSource = names;
        _provider.SelectionChanged += (_, _) =>
        {
            if (_updating || _provider.SelectedIndex < 0) return;
            var id = _providerIds[_provider.SelectedIndex] ?? AiProviderIds.Disabled;
            _key.Text = "";
            _ai.UpdateSettings(s => s.WithProvider(id));
            Say(null);
        };
        panel.Children.Add(Row("Provider", _provider));
        panel.Children.Add(_status);

        // Cloud provider: key, model and permissions.
        var save = new Button { Content = "Save Key", Name = "AiSaveKey" };
        save.Click += (_, _) => SaveKey();
        _key.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            SaveKey();
            e.Handled = true;
        };
        var remove = new Button { Content = "Remove Key" };
        remove.Click += (_, _) =>
        {
            try
            {
                _ai.RemoveKey();
                Say("Key removed.");
            }
            catch (AiException error)
            {
                Say(error.Message);
            }
            Load();
        };
        _refresh.Click += async (_, _) => await ListModelsAsync();
        _test.Click += async (_, _) => await ListModelsAsync();
        _model.LostFocus += (_, _) => CommitModel();
        _model.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            CommitModel();
            e.Handled = true;
        };
        _models.SelectionChanged += (_, _) =>
        {
            if (_updating || _models.SelectedItem is not string model) return;
            _ai.UpdateSettings(s => s with { Model = model });
        };
        _consent.IsCheckedChanged += (_, _) => Update(s => s with { CloudConsent = _consent.IsChecked == true });
        _history.IsCheckedChanged += (_, _) => Update(s => s with { ShareHistory = _history.IsChecked == true });
        _subtitles.IsCheckedChanged += (_, _) => Update(s => s with { ShareSubtitles = _subtitles.IsChecked == true });
        _cloud.Children.Add(Row("API key", _key));
        _cloud.Children.Add(Buttons(save, remove, _test));
        _cloud.Children.Add(_keyState);
        _cloud.Children.Add(Row("Model", _model));
        _cloud.Children.Add(Row("", Buttons(_refresh, _models)));
        _cloud.Children.Add(AppDialog.Muted("Refresh Models and Test Connection list the models of your account. They do not test generation."));
        _cloud.Children.Add(_consent);
        _cloud.Children.Add(_history);
        _cloud.Children.Add(_subtitles);
        _cloud.Children.Add(AppDialog.Muted(
            "Cloud requests may incur charges. Compact catalogue metadata and your request are sent to the selected provider. "
            + "Media URLs, private paths and network credentials are excluded. Subtitle text is sent only when you import it and allow it. "
            + "Keys are kept in this computer's secure store and are never included in settings or exports."));
        panel.Children.Add(_cloud);

        _personalize.IsCheckedChanged += (_, _) => Update(s => s with { Personalize = _personalize.IsChecked == true });
        panel.Children.Add(_personalize);
        _language.LostFocus += (_, _) => Update(s => s with { Language = _language.Text ?? "" });
        panel.Children.Add(Row("Response language", _language));
        panel.Children.Add(AppDialog.Muted("For example en or bn. Translate Description also uses this language."));
        _limit.ValueChanged += (_, _) => Update(s => s with { DailyRequestLimit = (int)(_limit.Value ?? 20) });
        panel.Children.Add(Row("Daily request limit", _limit));
        _tokens.ValueChanged += (_, _) => Update(s => s with { MaximumOutputTokens = (int)(_tokens.Value ?? 1200) });
        panel.Children.Add(Row("Maximum response tokens", _tokens));
        panel.Children.Add(_usage);
        panel.Children.Add(AppDialog.Muted(AiService.UsageNote));
        var clear = new Button { Content = "Clear AI Cache" };
        clear.Click += (_, _) =>
        {
            _ai.ClearCache();
            Say("AI cache cleared.");
        };
        panel.Children.Add(clear);
        panel.Children.Add(_message);
        panel.Children.Add(_error);

        // Personal preferences.
        panel.Children.Add(AppDialog.Heading("Personal Preferences"));
        panel.Children.Add(_feedback);
        var saveFeedback = new Button { Content = "Save Preferences" };
        saveFeedback.Click += (_, _) =>
        {
            _ai.SaveFeedback(_feedback.Text ?? "");
            Say("Preferences saved.");
        };
        var resetFeedback = new Button { Content = "Reset Preferences" };
        resetFeedback.Click += (_, _) =>
        {
            _feedback.Text = "";
            _ai.SaveFeedback("");
            Say("Preferences reset.");
        };
        panel.Children.Add(Buttons(saveFeedback, resetFeedback));
        panel.Children.Add(AppDialog.Muted(
            "Examples: less horror, more mysteries, prefer shorter movies. You control this text; Myra does not silently infer permanent preferences."));

        // Enabled tools.
        var tools = new StackPanel { Spacing = 4 };
        foreach (var feature in AiFeatures.All)
        {
            var box = new CheckBox { Content = feature.Title() };
            box.IsCheckedChanged += (_, _) => Update(s => s.WithFeature(feature, box.IsChecked == true));
            _features.Add((feature, box));
            tools.Children.Add(box);
        }
        panel.Children.Add(AppDialog.Heading("AI Features"));
        panel.Children.Add(new Expander { Header = "Enabled tools", Content = tools, HorizontalAlignment = HorizontalAlignment.Stretch });
        return panel;
    }

    private static Grid Row(string label, Control field)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("200,*"), ColumnSpacing = 8 };
        grid.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(field, 1);
        grid.Children.Add(field);
        return grid;
    }

    private static StackPanel Buttons(params Control[] controls)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row.Children.AddRange(controls);
        return row;
    }

    private void Update(Func<AiSettings, AiSettings> change)
    {
        if (_updating) return;
        _ai.UpdateSettings(change);
    }

    private void SaveKey()
    {
        var key = (_key.Text ?? "").Trim();
        if (key.Length == 0)
        {
            Say("Paste your API key first.");
            return;
        }
        try
        {
            _ai.SaveKey(key);
            _key.Text = "";
            Say("Key saved in this computer's secure store.");
        }
        catch (AiException error)
        {
            Say(error.Message);
        }
        Load();
    }

    private void CommitModel()
    {
        var model = (_model.Text ?? "").Trim();
        if (model != _ai.Settings.Model) Update(s => s with { Model = model });
    }

    private async Task ListModelsAsync()
    {
        _refresh.IsEnabled = _test.IsEnabled = false;
        try
        {
            await _ai.RefreshModelsAsync();
        }
        finally
        {
            _refresh.IsEnabled = _test.IsEnabled = true;
            Load();
        }
    }

    private void Say(string? text) => AppDialog.Show(_message, text);

    private void OnChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(Load);

    /// Shows the current settings. Never shows a saved key.
    private void Load()
    {
        _updating = true;
        try
        {
            var settings = _ai.Settings;
            var provider = _ai.CurrentProvider;
            _provider.SelectedIndex = Math.Max(0, _providerIds.IndexOf(provider?.Id));
            _status.Text = _ai.Status;
            _cloud.IsVisible = provider?.IsCloud == true;
            _keyState.Text = provider?.IsCloud != true ? ""
                : _ai.HasKey() ? $"Your {provider.Title} key is saved in the secure store." : $"No {provider.Title} key is saved.";
            if (!_model.IsFocused) _model.Text = settings.Model;
            _models.ItemsSource = _ai.Models;
            _models.IsVisible = _ai.Models.Count > 0;
            _models.SelectedItem = _ai.Models.Contains(settings.Model) ? settings.Model : null;
            _consent.IsChecked = settings.CloudConsent;
            _history.IsChecked = settings.ShareHistory;
            _subtitles.IsChecked = settings.ShareSubtitles;
            _personalize.IsChecked = settings.Personalize;
            if (!_language.IsFocused) _language.Text = settings.Language;
            _limit.Value = settings.DailyRequestLimit;
            _tokens.Value = settings.MaximumOutputTokens;
            if (!_feedback.IsFocused) _feedback.Text = settings.Feedback;
            foreach (var (feature, box) in _features) box.IsChecked = settings.IsEnabled(feature);
            _usage.Text = $"Requests today: {_ai.RequestsToday} of {settings.DailyRequestLimit} · Estimated input tokens: "
                          + _ai.EstimatedInputTokens.ToString("N0", CultureInfo.CurrentCulture)
                          + " · Estimated output tokens: " + _ai.EstimatedOutputTokens.ToString("N0", CultureInfo.CurrentCulture);
            AppDialog.Show(_error, _ai.ErrorMessage);
        }
        finally
        {
            _updating = false;
        }
    }
}

/// AI Settings on its own (macOS MyraAISettingsWindow), opened from the AI workspace and the app menu.
public sealed class AiSettingsWindow : AppDialog
{
    public AiSettingsWindow(AiService ai) : base("AI Settings", 640, 640, resizable: true)
    {
        View = new AiSettingsView(ai);
        var done = Action("Done", accent: true);
        done.Click += (_, _) => Close();
        var layout = new DockPanel { Margin = new Thickness(20) };
        var footer = ButtonRow(done);
        footer.Margin = new Thickness(0, 12, 0, 0);
        DockPanel.SetDock(footer, Dock.Bottom);
        layout.Children.Add(footer);
        layout.Children.Add(new ScrollViewer { Content = View, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled });
        Content = layout;
        MinWidth = 560;
        MinHeight = 480;
    }

    public AiSettingsView View { get; }

    public static Task ShowAsync(Window owner, AiService ai) => new AiSettingsWindow(ai).ShowDialog(owner);
}
