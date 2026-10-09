using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Myra.Core;

namespace Myra.App.ViewModels;

/// One folder in the Index Management tree.
public sealed partial class IndexNode : ObservableObject
{
    private static readonly IndexSchedule[] Schedules = [IndexSchedule.Daily, IndexSchedule.Weekly, IndexSchedule.Manual];
    private readonly Action<IndexNode, IndexSchedule?> _setSchedule;
    private int _scheduleIndex;

    public IndexNode(IndexFolderRow row, string name, IndexSchedule inherited, IndexSchedule? explicitSchedule, string? failure, Action<IndexNode, IndexSchedule?> setSchedule)
    {
        Row = row;
        Name = name;
        Failure = failure;
        _setSchedule = setSchedule;
        ScheduleChoices = [$"Inherit ({inherited.Label()})", .. Schedules.Select(s => s.Label())];
        _scheduleIndex = explicitSchedule is { } s ? Array.IndexOf(Schedules, s) + 1 : 0;
    }

    public IndexFolderRow Row { get; }
    public IndexScope Scope => Row.Scope;
    public string Id => Row.Id;
    public string Name { get; }
    public string Location => Row.Scope.Folder.AbsoluteUri;
    public string? Failure { get; }
    public bool HasFailure => !string.IsNullOrEmpty(Failure);
    public IReadOnlyList<string> ScheduleChoices { get; }
    public ObservableCollection<IndexNode> Children { get; } = [];
    public string FilesText => Row.Files == 1 ? "1 file" : $"{Row.Files} files";
    public string CheckedText => Row.Checked?.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) ?? "Never checked";

    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private bool _isExpanded;

    public int ScheduleIndex
    {
        get => _scheduleIndex;
        set
        {
            if (value < 0 || value == _scheduleIndex) return;
            SetProperty(ref _scheduleIndex, value);
            _setSchedule(this, value == 0 ? null : Schedules[value - 1]);
        }
    }

    public IEnumerable<IndexNode> Descendants()
    {
        yield return this;
        foreach (var node in Children.SelectMany(c => c.Descendants())) yield return node;
    }
}
