namespace Myra.Core;

public enum MyraIconFamily { Signature, Cinema, Orbit, Minimal }

/// Automatic follows the system theme (Light or Dark). Glass is the rendered glass-style image.
public enum MyraIconVariant { Automatic, Light, Dark, Glass }

/// Names the bundled icon images. The settings store the two names as text, so an unknown or
/// damaged value falls back to Signature and Automatic.
public static class MyraIcons
{
    public const string DefaultFamily = "Signature";
    public const string DefaultVariant = "Automatic";

    public static MyraIconFamily ParseFamily(string? value) =>
        Enum.TryParse<MyraIconFamily>(value, ignoreCase: true, out var family) && Enum.IsDefined(family) ? family : MyraIconFamily.Signature;

    public static MyraIconVariant ParseVariant(string? value) =>
        Enum.TryParse<MyraIconVariant>(value, ignoreCase: true, out var variant) && Enum.IsDefined(variant) ? variant : MyraIconVariant.Automatic;

    /// File name stem such as "cinema-dark". Automatic resolves to Light or Dark by the system theme.
    public static string AssetName(MyraIconFamily family, MyraIconVariant variant, bool systemIsDark)
    {
        var resolved = variant == MyraIconVariant.Automatic ? (systemIsDark ? MyraIconVariant.Dark : MyraIconVariant.Light) : variant;
        return $"{family.ToString().ToLowerInvariant()}-{resolved.ToString().ToLowerInvariant()}";
    }
}
