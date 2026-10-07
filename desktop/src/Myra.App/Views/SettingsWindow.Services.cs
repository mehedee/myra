using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Styling;
using Avalonia.Threading;
using Myra.App.Services;
using Myra.Core;

namespace Myra.App.Views;

// Settings that need the shared services: appearance, credentials, Home Discovery and attribution.
public partial class SettingsWindow
{
    private static readonly (AppThemeMode Mode, string Label)[] ThemeChoices =
        [(AppThemeMode.System, "System"), (AppThemeMode.Light, "Light"), (AppThemeMode.Dark, "Dark")];

    private static TextBox SecretBox(string watermark) =>
        new() { PasswordChar = '•', Watermark = watermark, HorizontalAlignment = HorizontalAlignment.Stretch };

    private static Grid LabelRow(string label, Control field)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("170,*"), ColumnSpacing = 8 };
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

    private void BuildAppearance(AppSettings settings)
    {
        var box = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, ItemsSource = ThemeChoices.Select(c => c.Label).ToList() };
        box.SelectedIndex = Math.Max(0, Array.FindIndex(ThemeChoices, c => c.Mode == settings.Theme));
        box.SelectionChanged += (_, _) =>
        {
            settings.Theme = ThemeChoices[Math.Max(0, box.SelectedIndex)].Mode;
            ApplyTheme(settings.Theme);
        };
        ServicesPanel.Children.Add(AppDialog.Heading("Appearance"));
        ServicesPanel.Children.Add(LabelRow("Theme", box));
    }

    internal static void ApplyTheme(AppThemeMode mode) => App.ApplyTheme(mode);

    private void BuildServices(AppServices services)
    {
        var panel = ServicesPanel.Children;
        var secrets = services.Secrets;

        // Media Information (OMDb)
        var omdb = SecretBox("OMDb API Key (optional)");
        var omdbStatus = AppDialog.Message();
        omdbStatus.Classes.Add("muted");
        omdbStatus.FontSize = 12;
        try
        {
            omdb.Text = secrets.Read(SecretNames.OmdbApiKey);
        }
        catch (SecretStoreException ex)
        {
            AppDialog.Show(omdbStatus, ex.Message);
        }
        var saveOmdb = new Button { Content = "Save API Key" };
        saveOmdb.Click += (_, _) =>
        {
            try
            {
                var key = (omdb.Text ?? "").Trim();
                secrets.Save(SecretNames.OmdbApiKey, key);
                AppDialog.Show(omdbStatus, key.Length == 0 ? "API key removed." : "API key saved in the secure store.");
            }
            catch (SecretStoreException ex)
            {
                AppDialog.Show(omdbStatus, ex.Message);
            }
        };
        panel.Add(AppDialog.Heading("Media Information"));
        panel.Add(omdb);
        panel.Add(Buttons(saveOmdb, AppDialog.LinkButton("Get an OMDb Key", "https://www.omdbapi.com/apikey.aspx")));
        panel.Add(AppDialog.Muted("Only movie title/year or IMDb ID is sent to OMDb. Your media URL is never sent. Personal, noncommercial use."));
        panel.Add(omdbStatus);

        // Library index
        panel.Add(AppDialog.Heading("Library Index"));
        panel.Add(AppDialog.Muted("Balanced scans two folders at a time. Low Impact scans one; playback also reduces concurrency. "
            + "Home loads your saved library first. Only due folders are checked automatically, using their daily, weekly, or manual schedule. "
            + "Use Index Management to choose folders and schedules."));

        // Home Discovery (TMDB)
        var token = SecretBox("TMDB API Read Access Token");
        var tokenStatus = AppDialog.Message();
        tokenStatus.FontSize = 12;
        var updateStatus = AppDialog.Message();
        updateStatus.FontSize = 12;
        var saveToken = new Button { Content = "Save Token" };
        var update = new Button { Content = "Update Metadata" };
        string HasToken()
        {
            try
            {
                return secrets.Read(SecretNames.TmdbReadToken).Length > 0 ? "A token is saved." : "No token saved.";
            }
            catch (SecretStoreException ex)
            {
                return ex.Message;
            }
        }
        AppDialog.Show(tokenStatus, HasToken());
        saveToken.Click += (_, _) =>
        {
            try
            {
                var value = (token.Text ?? "").Trim();
                secrets.Save(SecretNames.TmdbReadToken, value);
                AppDialog.Show(tokenStatus, value.Length == 0 ? "Token removed." : "Token saved in the secure store.");
                token.Text = "";
                if (value.Length > 0) _ = RunEnrichmentAsync(services, update, updateStatus);
            }
            catch (SecretStoreException ex)
            {
                AppDialog.Show(tokenStatus, ex.Message);
            }
        };
        update.Click += async (_, _) => await RunEnrichmentAsync(services, update, updateStatus);
        var notify = new CheckBox
        {
            Content = "Notify me about new episodes of followed series",
            IsChecked = services.Entertainment.Personal.Preferences.Notifications,
        };
        notify.IsCheckedChanged += (_, _) => services.Entertainment.SetNotifications(notify.IsChecked == true);

        panel.Add(AppDialog.Heading("Home Discovery"));
        panel.Add(token);
        panel.Add(Buttons(saveToken, AppDialog.LinkButton("Get a TMDB token", "https://www.themoviedb.org/settings/api")));
        panel.Add(AppDialog.Muted("Use the API Read Access Token, rather than the API key. Only title information and provider IDs are sent; source video URLs are excluded."));
        panel.Add(tokenStatus);
        panel.Add(Buttons(update));
        panel.Add(updateStatus);
        panel.Add(notify);
        panel.Add(AppDialog.Muted("Following a show sets its current episodes as the baseline. Notifications appear as desktop notices while Myra runs."));

        // Online subtitles
        var apiKey = SecretBox("API Key");
        var user = new TextBox { Watermark = "Username (optional)" };
        var password = SecretBox("Password (optional)");
        var subtitleStatus = AppDialog.Message();
        subtitleStatus.Classes.Add("muted");
        subtitleStatus.FontSize = 12;
        try
        {
            var saved = secrets.ReadSubtitleCredentials();
            apiKey.Text = saved.ApiKey;
            user.Text = saved.Username;
            password.Text = saved.Password;
        }
        catch (SecretStoreException ex)
        {
            AppDialog.Show(subtitleStatus, ex.Message);
        }
        SubtitleCredentials Current() => new((apiKey.Text ?? "").Trim(), (user.Text ?? "").Trim(), password.Text ?? "");
        var saveSubtitles = new Button { Content = "Save Subtitle Credentials" };
        var signIn = new Button { Content = "Save & Sign In" };
        var progress = new ProgressBar { IsIndeterminate = true, Width = 60, IsVisible = false };
        void UpdateSignIn() => signIn.IsEnabled = Current() is { ApiKey.Length: > 0, Username.Length: > 0, Password.Length: > 0 };
        apiKey.TextChanged += (_, _) => UpdateSignIn();
        user.TextChanged += (_, _) => UpdateSignIn();
        password.TextChanged += (_, _) => UpdateSignIn();
        UpdateSignIn();
        saveSubtitles.Click += (_, _) =>
        {
            try
            {
                secrets.SaveSubtitleCredentials(Current());
                services.Subtitles.ResetSession();
                AppDialog.Show(subtitleStatus, "Subtitle credentials saved in the secure store.");
            }
            catch (SecretStoreException ex)
            {
                AppDialog.Show(subtitleStatus, ex.Message);
            }
        };
        signIn.Click += async (_, _) =>
        {
            var submitted = Current();
            signIn.IsEnabled = false;
            progress.IsVisible = true;
            AppDialog.Show(subtitleStatus, null);
            try
            {
                secrets.SaveSubtitleCredentials(submitted);
                services.Subtitles.ResetSession();
                var allowance = await services.Subtitles.SignInAsync(submitted);
                AppDialog.Show(subtitleStatus, allowance is { } n ? $"Signed in. Provider account allowance: {n} downloads." : "Signed in.");
            }
            catch (Exception ex) when (ex is SubtitleException or SecretStoreException)
            {
                AppDialog.Show(subtitleStatus, ex.Message);
            }
            finally
            {
                progress.IsVisible = false;
                UpdateSignIn();
            }
        };
        panel.Add(AppDialog.Heading("Online Subtitles — OpenSubtitles.com"));
        panel.Add(apiKey);
        panel.Add(user);
        panel.Add(password);
        panel.Add(Buttons(saveSubtitles, signIn, progress));
        panel.Add(AppDialog.LinkButton("Get an OpenSubtitles API Key", "https://www.opensubtitles.com/en/consumers"));
        panel.Add(AppDialog.Muted("Separate from OMDb. An API key is required; account sign-in is optional and may provide a different download allowance. No automatic subtitle downloads."));
        panel.Add(subtitleStatus);

        // Licence and attribution
        panel.Add(AppDialog.Heading("About Myra " + AboutAttribution.Version));
        panel.Add(AboutAttribution.TmdbBlock());
        foreach (var notice in AboutAttribution.Notices()) panel.Add(notice);

        // Core events can fire on worker threads.
        void OnChanged(object? s, EventArgs e) => Dispatcher.UIThread.Post(() => ShowEnrichment(services, update, updateStatus));
        services.Entertainment.Changed += OnChanged;
        Closed += (_, _) =>
        {
            services.Entertainment.Changed -= OnChanged;
            try
            {
                services.Store.Settings.Clamp();
                services.Store.Save();
                services.IndexRefresh.Mode = services.Store.Settings.IndexingMode;
            }
            catch (IOException)
            {
            }
        };
        update.IsEnabled = !services.Entertainment.IsEnriching;
    }

    private static void ShowEnrichment(AppServices services, Button update, TextBlock status)
    {
        var store = services.Entertainment;
        update.IsEnabled = !store.IsEnriching;
        if (store.IsEnriching) AppDialog.Show(status, "Updating metadata…");
        else if (store.ErrorMessage is { Length: > 0 } error) AppDialog.Show(status, error);
    }

    private static async Task RunEnrichmentAsync(AppServices services, Button update, TextBlock status)
    {
        var store = services.Entertainment;
        if (store.IsEnriching) return;
        update.IsEnabled = false;
        AppDialog.Show(status, "Updating metadata…");
        try
        {
            await store.EnrichAsync();
            AppDialog.Show(status, store.ErrorMessage is { Length: > 0 } error ? error : "Metadata is up to date for this batch (at most 50 titles per run).");
        }
        finally
        {
            update.IsEnabled = !store.IsEnriching;
        }
    }
}
