using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Avalonia.Threading;
using Myra.App.Services;
using Myra.App.ViewModels;
using Myra.App.Views;
using Myra.Core;

namespace Myra.App;

public partial class App : Application
{
    private PosixSignalRegistration[]? _signals;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    /// Builds the secondary windows and dialogs. Checks can replace it with <see cref="NullAppDialogs"/>.
    public static Func<AppServices, IAppDialogs> CreateDialogs { get; set; } = services => new AppDialogs(services);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var services = new AppServices();
            ApplyTheme(services.Store.Settings.Theme);
            var main = new MainViewModel(services, CreateDialogs(services));
            desktop.MainWindow = new MainWindow { DataContext = main };
            desktop.ShutdownRequested += (_, _) => services.Shutdown();
            // Closing the main window ends the app without ShutdownRequested on some platforms. Shutdown runs once.
            desktop.Exit += (_, _) => services.Shutdown();
            HandleDataErrors(main);
            // Logout, systemd and `kill` send SIGTERM; shut down through the normal path so state is saved.
            _signals =
            [
                PosixSignalRegistration.Create(PosixSignal.SIGTERM, OnSignal),
                PosixSignalRegistration.Create(PosixSignal.SIGINT, OnSignal),
            ];

            void OnSignal(PosixSignalContext context)
            {
                context.Cancel = true;
                Dispatcher.UIThread.Post(() => desktop.Shutdown());
            }
            _ = main.StartAsync();
            // "Myra.exe <video URL or file>" plays it straight away, e.g. from "Open with".
            if (desktop.Args?.FirstOrDefault() is { } target) main.PlayTarget(target);
        }
        base.OnFrameworkInitializationCompleted();
    }

    /// Last line of defence: a failure of the index, a data file or a credential store in a command
    /// or background task is shown as an error instead of ending the app.
    private static void HandleDataErrors(MainViewModel main)
    {
        static bool IsDataError(Exception error) => error is LibraryIndexException or Microsoft.Data.Sqlite.SqliteException
            or IOException or UnauthorizedAccessException or InvalidImportException or SecretStoreException;

        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            if (!IsDataError(e.Exception)) return;
            e.Handled = true;
            main.ErrorMessage = e.Exception.Message;
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            if (e.Exception.InnerExceptions.All(IsDataError)) e.SetObserved();
        };
    }

    public static void ApplyTheme(AppThemeMode mode)
    {
        if (Current is null) return;
        Current.RequestedThemeVariant = mode switch
        {
            AppThemeMode.Light => ThemeVariant.Light,
            AppThemeMode.Dark => ThemeVariant.Dark,
            _ => ThemeVariant.Default,
        };
    }
}
