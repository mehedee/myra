using Avalonia;
using Avalonia.Controls;
using Avalonia.Automation;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Myra.App.Services;
using Myra.Core;

namespace Myra.App.Views;

// Settings > Appearance: the Myra icon family and variant, and the AI section.
public partial class SettingsWindow
{
    private static readonly (MyraIconVariant Variant, string Label)[] VariantChoices =
        [(MyraIconVariant.Automatic, "Follow system"), (MyraIconVariant.Light, "Light"), (MyraIconVariant.Dark, "Dark"), (MyraIconVariant.Glass, "Glass")];

    private void BuildIconPicker(AppSettings settings, Action? changed = null)
    {
        MyraAppearance.Apply(settings);
        var family = MyraIcons.ParseFamily(settings.IconFamily);
        var variant = MyraIcons.ParseVariant(settings.IconVariant);
        var variantBox = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, ItemsSource = VariantChoices.Select(c => c.Label).ToList() };
        variantBox.SelectedIndex = Math.Max(0, Array.FindIndex(VariantChoices, c => c.Variant == variant));
        var tiles = new Dictionary<MyraIconFamily, (ToggleButton Button, Image Preview)>();
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };

        void Refresh()
        {
            foreach (var (option, tile) in tiles)
            {
                tile.Preview.Source = MyraAppearance.Image(option, variant, MyraAppearance.SystemIsDark);
                tile.Button.IsChecked = option == family;
            }
            settings.IconFamily = family.ToString();
            settings.IconVariant = variant.ToString();
            MyraAppearance.Apply(settings);
            changed?.Invoke();
        }

        foreach (var option in Enum.GetValues<MyraIconFamily>())
        {
            var preview = new Image { Width = 64, Height = 64 };
            var label = new TextBlock { Text = option.ToString(), FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center };
            var content = new StackPanel { Spacing = 6, Children = { preview, label } };
            var button = new ToggleButton { Content = content, Padding = new Thickness(10), Tag = option, Name = "IconFamily" + option };
            AutomationProperties.SetName(button, option + " icon");
            button.Click += (_, _) =>
            {
                family = option;
                Refresh();
            };
            tiles[option] = (button, preview);
            row.Children.Add(button);
        }
        variantBox.SelectionChanged += (_, _) =>
        {
            variant = VariantChoices[Math.Max(0, variantBox.SelectedIndex)].Variant;
            Refresh();
        };

        ServicesPanel.Children.Add(AppDialog.Heading("Myra Icon"));
        ServicesPanel.Children.Add(LabelRow("Icon style", variantBox));
        ServicesPanel.Children.Add(row);
        ServicesPanel.Children.Add(AppDialog.Muted("The choice changes the icon of every Myra window, its title bar and the taskbar, and is restored at launch. "
            + "The file icon of Myra.exe in Explorer stays the packaged one. Glass is a rendered glass-style image."));
        // Preview the tiles now, but keep the saved values untouched until the user picks something.
        foreach (var (option, tile) in tiles)
        {
            tile.Preview.Source = MyraAppearance.Image(option, variant, MyraAppearance.SystemIsDark);
            tile.Button.IsChecked = option == family;
        }
    }

    private void BuildAiSection(AppServices services)
    {
        ServicesPanel.Children.Add(AppDialog.Heading("AI"));
        Control? ai = null;
        try
        {
            ai = SettingsHooks.CreateAiSettings?.Invoke(services);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            ai = AppDialog.Muted("AI settings could not be shown: " + ex.Message);
        }
        ServicesPanel.Children.Add(ai ?? AppDialog.Muted("AI settings are not available in this build."));
    }
}
