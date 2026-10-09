using Avalonia;
using Avalonia.Controls;
using Myra.App.ViewModels;

namespace Myra.App.Views;

public partial class HomeView : UserControl
{
    public HomeView()
    {
        InitializeComponent();
        TmdbLink.Click += (_, _) => ShellLinks.Open("https://www.themoviedb.org");
        // Narrow windows show the Home actions as icons only (macOS ViewThatFits).
        this.GetObservable(BoundsProperty).Subscribe(new Avalonia.Reactive.AnonymousObserver<Rect>(bounds =>
        {
            var labels = bounds.Width >= 900;
            PickLabel.IsVisible = labels;
            AiLabel.IsVisible = labels;
            MetadataLabel.IsVisible = labels;
            PersonalLabel.IsVisible = labels;
        }));
    }
}
