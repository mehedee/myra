using Avalonia.Controls;
using Myra.App.Services;

namespace Myra.App.Views;

/// Extension points that other parts of the app plug into Settings.
public static class SettingsHooks
{
    /// Builds the AI settings control. Settings shows it in the "AI" section, or a "not available" note when null.
    public static Func<AppServices, Control>? CreateAiSettings { get; set; }
}
