using Avalonia;
using Avalonia.Controls;
using Myra.Core;

namespace Myra.App.Views;

/// Asks for one cloud permission before the first request that needs it. Returns true for Allow.
/// Esc and Not Now return false; nothing is sent then.
public sealed class AiConsentDialog : AppDialog
{
    public AiConsentDialog(AiPermission kind, string provider) : base(Heading(kind), 480)
    {
        Kind = kind;
        var allow = Action(AllowLabel(kind), accent: true, isDefault: true);
        var later = Action("Not Now", isCancel: true);
        allow.Name = "AllowButton";
        later.Name = "NotNowButton";
        allow.Click += (_, _) => Close(true);
        later.Click += (_, _) => Close(false);
        var panel = new StackPanel { Margin = new Thickness(22), Spacing = 12 };
        panel.Children.Add(Title2(Heading(kind)));
        panel.Children.Add(new TextBlock { Text = Body(kind, provider), TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        panel.Children.Add(Muted(Footnote(kind)));
        panel.Children.Add(ButtonRow(later, allow));
        Content = panel;
    }

    public AiPermission Kind { get; }

    private static string Heading(AiPermission kind) => kind switch
    {
        AiPermission.Cloud => "Send library information to the cloud?",
        AiPermission.History => "Include your viewing history?",
        _ => "Send subtitle text?",
    };

    private static string AllowLabel(AiPermission kind) => kind switch
    {
        AiPermission.Cloud => "Allow Cloud Requests",
        AiPermission.History => "Include History",
        _ => "Send Subtitle Excerpt",
    };

    public static string Body(AiPermission kind, string provider) => kind switch
    {
        AiPermission.Cloud =>
            $"Myra will send your request and a short list of matching titles to {provider}: names, years, genres, ratings and short descriptions. "
            + "Media links, file paths, folder names and network addresses are not sent. Your provider may charge your account for each request.",
        AiPermission.History =>
            $"This request needs to know which titles you watched, have not finished, or saved to your Watchlist. "
            + $"Myra will send these markers for the listed titles, and simple viewing counts, to {provider}.",
        _ =>
            $"Myra will send a short excerpt (up to 3,000 characters) of the subtitle file you imported to {provider}. "
            + "Subtitles can contain spoilers. The text is used for this request only and is not saved.",
    };

    private static string Footnote(AiPermission kind) => kind switch
    {
        AiPermission.Cloud => "Myra remembers this choice. You can turn it off at any time in AI Settings.",
        AiPermission.History => "Myra remembers this choice. Turn off \"Include watch-history information\" in AI Settings to stop.",
        _ => "Myra remembers this choice. Turn off \"Allow selected subtitle text\" in AI Settings to stop.",
    };
}
