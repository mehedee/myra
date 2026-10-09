using Avalonia.Controls;
using Myra.App.ViewModels;

namespace Myra.App.Views;

/// DataContext: DetailViewModel. Pick Something sets AnotherPick.
public partial class DetailView : UserControl
{
    public DetailView()
    {
        InitializeComponent();
        CollectionsButton.Click += (_, _) =>
        {
            if (DataContext is not DetailViewModel vm) return;
            var menu = new MenuFlyout();
            foreach (var (id, label) in vm.CollectionChoices)
                menu.Items.Add(new MenuItem { Header = label, Command = vm.ToggleCollectionCommand, CommandParameter = id });
            menu.ShowAt(CollectionsButton);
        };
        AnotherPickButton.Click += (_, _) => AnotherPick?.Invoke();
    }

    public Action? AnotherPick { get; set; }
}
