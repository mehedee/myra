using System.Globalization;
using Avalonia.Data.Converters;
using CommunityToolkit.Mvvm.ComponentModel;
using Myra.Core;

namespace Myra.App.ViewModels;

public static class Format
{
    public static string Bytes(long bytes)
    {
        if (bytes <= 0) return "0 B";
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = (double)bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0 ? $"{bytes} B" : value.ToString(value >= 100 ? "0" : "0.0", CultureInfo.CurrentCulture) + " " + units[unit];
    }

    public static string Duration(double seconds)
    {
        var span = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return span.TotalHours >= 1 ? $"{(int)span.TotalHours}h {span.Minutes}m" : span.TotalMinutes >= 1 ? $"{span.Minutes}m {span.Seconds}s" : $"{span.Seconds}s";
    }

    public static readonly IValueConverter BytesConverter = new FuncValueConverter<long, string>(b => Bytes(b));
    public static readonly IValueConverter SpeedConverter = new FuncValueConverter<long, string>(b => b > 0 ? Bytes(b) + "/s" : "");
    public static readonly IValueConverter StatusConverter = new FuncValueConverter<TransferStatus, string>(s => s switch
    {
        TransferStatus.Preparing => "Preparing…",
        TransferStatus.Queued => "Queued",
        TransferStatus.Active => "Downloading",
        TransferStatus.Paused => "Paused",
        TransferStatus.Completed => "Completed",
        TransferStatus.Failed => "Failed",
        _ => "Cancelled",
    });
}

public sealed partial class EntryViewModel(DirectoryEntry entry) : ObservableObject
{
    public DirectoryEntry Entry { get; } = entry;
    [ObservableProperty] private bool _isSelected;

    public string Name => Entry.Name;
    public bool IsFolder => Entry.IsFolder;
    public bool IsVideo => Entry.IsVideo;
    public string SizeText => Entry.Size is { } size ? Format.Bytes(size) : "";
    public string DateText => Entry.ModifiedAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture) ?? "";
}

public sealed partial class GlobalResultViewModel(GlobalSearchResult result) : ObservableObject
{
    public GlobalSearchResult Result { get; } = result;
    [ObservableProperty] private bool _isSelected;

    public string Name => Result.Entry.Name;
    public string Location => Result.ParentPath.Length == 0 ? Result.CategoryName : $"{Result.CategoryName} › {Result.ParentPath.Replace("/", " › ")}";
    public string SizeText => Result.Entry.Size is { } size ? Format.Bytes(size) : "";
}

public sealed record Breadcrumb(string Name, Uri Url);
