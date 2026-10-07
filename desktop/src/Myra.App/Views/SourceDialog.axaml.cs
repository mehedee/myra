using Avalonia.Controls;
using Myra.App.ViewModels;
using Myra.Core;

namespace Myra.App.Views;

public partial class SourceDialog : Window
{
    public SourceDialog() : this(null)
    {
    }

    public SourceDialog(Category? category)
    {
        InitializeComponent();
        Title = category is null ? "Add source" : "Edit source";
        UrlBox.Text = category?.RootUrlString ?? "";
        NameBox.Text = category?.Name ?? "";
        CancelButton.Click += (_, _) => Close(null);
        SaveButton.Click += (_, _) => Close(new SourceInput(NameBox.Text ?? "", UrlBox.Text ?? ""));
        Opened += (_, _) => UrlBox.Focus();
    }
}
