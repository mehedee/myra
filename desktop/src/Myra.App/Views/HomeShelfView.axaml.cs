using Avalonia.Controls;
using Avalonia.Interactivity;
using Myra.App.ViewModels;
using Myra.Core;

namespace Myra.App.Views;

public partial class HomeShelfView : UserControl
{
    public HomeShelfView()
    {
        InitializeComponent();
    }

    /// Card overflow menu (macOS EntertainmentCard menu), built when opened so it shows current state.
    private void OnMoreClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: HomeCardViewModel card } button) return;
        var home = card.Home;
        var menu = new MenuFlyout();
        menu.Items.Add(new MenuItem
        {
            Header = home.IsWatched(card.Title) ? "Mark unwatched" : "Mark watched",
            Command = home.ToggleWatchedCommand,
            CommandParameter = card,
        });
        menu.Items.Add(new MenuItem { Header = "Details and versions", Command = home.DetailsCommand, CommandParameter = card });
        if (card.Title.Kind == EntertainmentKind.Series)
            menu.Items.Add(new MenuItem
            {
                Header = home.IsFollowed(card.Title) ? "Unfollow series" : "Follow series",
                Command = home.ToggleFollowCommand,
                CommandParameter = card,
            });
        if (home.Collections.Count > 0)
        {
            var collections = new MenuItem { Header = "Collections" };
            foreach (var collection in home.Collections)
            {
                var id = collection.Id;
                var item = new MenuItem { Header = (collection.TitleIds.Contains(card.Title.Id) ? "✓ " : "") + collection.Name };
                item.Click += (_, _) => home.ToggleCollection(card, id);
                collections.Items.Add(item);
            }
            menu.Items.Add(collections);
        }
        menu.ShowAt(button);
    }
}
