using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Myra.Core;

namespace Myra.App.ViewModels;

public sealed record AiFeatureChoice(AiFeature Feature)
{
    public string Title => Feature.Title();
    public override string ToString() => Title;
}

/// The Myra AI workspace (macOS MyraAIWorkspaceView). It runs a request only when the user presses Ask Myra.
/// Playback and personal changes stay explicit: result cards play on click, and actions need Apply.
public sealed partial class AiWorkspaceViewModel : ObservableObject, IDisposable
{
    private readonly MainViewModel _main;
    private readonly HashSet<string> _selected = new(StringComparer.Ordinal);
    private readonly HashSet<string> _applied = new(StringComparer.Ordinal);
    private string? _subtitleText;
    private AiErrorKind? _errorKind;
    private bool _disposed;

    public AiWorkspaceViewModel(MainViewModel main, AiFeature feature = AiFeature.SmartPick, EntertainmentTitle? title = null, AiService? ai = null)
    {
        _main = main;
        Ai = ai ?? main.Services.Ai;
        Features = AiFeatures.All.Select(f => new AiFeatureChoice(f)).ToList();
        _selectedFeature = Features.First(f => f.Feature == feature);
        if (title is not null) _selected.Add(title.Id);
        Ai.Changed += OnAiChanged;
        Store.Changed += OnStoreChanged;
        Ai.RefreshAvailability();
        RefreshTitles();
        RefreshEpisodes();
    }

    public AiService Ai { get; }
    internal EntertainmentStore Store => _main.Entertainment;

    // ---------- Hooks set by the window (checks replace them) ----------

    /// Shows the consent prompt for one permission. Returns true when the user allows it.
    public Func<AiPermission, string, Task<bool>>? AskConsent { get; set; }

    /// Lets the user choose a subtitle file. Returns its path, or null.
    public Func<Task<string?>>? PickSubtitleFile { get; set; }

    /// Opens AI Settings over the workspace.
    public Func<Task>? ShowSettings { get; set; }

    /// Opens Details over the workspace. Returns the title to play, or null.
    public Func<DetailViewModel, Task<EntertainmentTitle?>>? ShowDetails { get; set; }

    /// Play was chosen on a result. The window closes and the main window starts playback.
    public event Action<EntertainmentTitle>? PlayRequested;

    /// Close (or Esc when nothing is running).
    public event Action? CloseRequested;

    // ---------- Task ----------

    public IReadOnlyList<AiFeatureChoice> Features { get; }

    [ObservableProperty] private AiFeatureChoice _selectedFeature;

    public AiFeature Feature => SelectedFeature.Feature;
    public string Hint => Feature.Hint();
    public bool IsFeatureDisabled => !Ai.Settings.IsEnabled(Feature);
    public bool IsProviderDisabled => Ai.CurrentProvider is null;
    public string ProviderStatus => Ai.Status;
    public bool IsCloud => AiWorkspace.IsCloud(Ai);
    public string ProviderName => Ai.CurrentProvider?.Title ?? "No provider";

    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(RunCommand))] private string _prompt = "";
    [ObservableProperty] private decimal? _availableMinutes = 120;

    public bool IsPlan => Feature == AiFeature.Plan;
    public bool IsRecap => Feature == AiFeature.Recap;
    public bool ShowTitleSelection => Feature.RequiresTitles() || Feature is AiFeature.Collection or AiFeature.Action;
    public string TitleSelectionHeading => Feature == AiFeature.Compare ? "Choose two or more titles" : IsRecap ? "Choose a series" : "Choose titles";

    partial void OnSelectedFeatureChanged(AiFeatureChoice value)
    {
        LocalMessage = null;
        _applied.Clear();
        ClearResult();
        if (IsRecap && _selected.Count > 1)
        {
            var first = SelectedTitlesList().FirstOrDefault(t => t.Kind == EntertainmentKind.Series);
            _selected.Clear();
            if (first is not null) _selected.Add(first.Id);
        }
        RefreshTitles();
        RefreshEpisodes();
        OnPropertyChanged(string.Empty);
        RunCommand.NotifyCanExecuteChanged();
    }

    // ---------- Title selection ----------

    [ObservableProperty] private string _titleQuery = "";
    public ObservableCollection<AiTitleChoiceViewModel> MatchingTitles { get; } = [];
    public ObservableCollection<AiTitleChoiceViewModel> SelectedTitles { get; } = [];
    public IReadOnlyCollection<string> SelectedIds => _selected;
    public bool HasSelection => _selected.Count > 0;

    partial void OnTitleQueryChanged(string value) => RefreshTitles();

    private List<EntertainmentTitle> SelectedTitlesList() => Store.Catalogue.Where(t => _selected.Contains(t.Id)).ToList();

    public void Select(EntertainmentTitle title)
    {
        if (IsRecap)
        {
            if (title.Kind != EntertainmentKind.Series) return;
            _selected.Clear();
            _selected.Add(title.Id);
        }
        else if (_selected.Count < AiWorkspace.MaximumSelection)
        {
            _selected.Add(title.Id);
        }
        RefreshTitles();
        RefreshEpisodes();
    }

    public void Remove(EntertainmentTitle title)
    {
        _selected.Remove(title.Id);
        RefreshTitles();
        RefreshEpisodes();
    }

    private void RefreshTitles()
    {
        MatchingTitles.Clear();
        foreach (var title in AiWorkspace.MatchingTitles(Store.Catalogue, TitleQuery.Trim()))
        {
            var enabled = !_selected.Contains(title.Id) && (!IsRecap || title.Kind == EntertainmentKind.Series)
                          && (IsRecap || _selected.Count < AiWorkspace.MaximumSelection);
            MatchingTitles.Add(new AiTitleChoiceViewModel(title, _selected.Contains(title.Id), enabled, Select));
        }
        SelectedTitles.Clear();
        foreach (var title in SelectedTitlesList()) SelectedTitles.Add(new AiTitleChoiceViewModel(title, true, true, Remove));
        OnPropertyChanged(nameof(HasSelection));
        RunCommand.NotifyCanExecuteChanged();
    }

    // ---------- Recap ----------

    public ObservableCollection<AiEpisodeChoice> Episodes { get; } = [];
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanImportSubtitles))] private AiEpisodeChoice? _selectedEpisode;
    [ObservableProperty] private string _subtitleName = "";
    public bool HasNoEpisodes => IsRecap && HasSelection && Episodes.Count == 0;
    public bool CanImportSubtitles => SelectedEpisode is not null;
    public bool HasSubtitles => _subtitleText is not null;

    partial void OnSelectedEpisodeChanged(AiEpisodeChoice? value)
    {
        _subtitleText = null;
        SubtitleName = "";
        OnPropertyChanged(nameof(HasSubtitles));
        RunCommand.NotifyCanExecuteChanged();
    }

    private void RefreshEpisodes()
    {
        var previous = SelectedEpisode?.Version.Id;
        Episodes.Clear();
        if (IsRecap && SelectedTitlesList().FirstOrDefault() is { } series)
            foreach (var version in AiWorkspace.CompletedEpisodes(series, Store.Personal))
                Episodes.Add(new AiEpisodeChoice(version));
        SelectedEpisode = Episodes.FirstOrDefault(e => e.Version.Id == previous);
        OnPropertyChanged(nameof(HasNoEpisodes));
    }

    [RelayCommand]
    private async Task ImportSubtitlesAsync()
    {
        if (SelectedEpisode is null || PickSubtitleFile is null) return;
        var path = await PickSubtitleFile();
        if (path is null) return;
        ImportSubtitles(path);
    }

    /// Reads the subtitle file for this request only. The text is never saved.
    public void ImportSubtitles(string path)
    {
        try
        {
            _subtitleText = AiWorkspace.ReadSubtitleFile(path);
            SubtitleName = Path.GetFileName(path);
            LocalMessage = "Imported for this request only.";
        }
        catch (AiException error)
        {
            _subtitleText = null;
            SubtitleName = "";
            LocalMessage = error.Message;
        }
        OnPropertyChanged(nameof(HasSubtitles));
        RunCommand.NotifyCanExecuteChanged();
    }

    // ---------- Run and cancel ----------

    public bool IsBusy => Ai.IsBusy;
    public string RunLabel => IsBusy ? "Working…" : "Ask Myra";

    private bool CanRun() => !_running && AiWorkspace.CanAsk(Ai, Feature, Store.Catalogue.Count, _selected.Count, SelectedEpisode?.Version.Id, HasSubtitles);

    private bool _running;

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RunAsync()
    {
        LocalMessage = null;
        _applied.Clear();
        ClearResult();
        _running = true;
        RunCommand.NotifyCanExecuteChanged();
        try
        {
            if (!await EnsureConsentAsync()) return;
            var outcome = await Ai.ExecuteAsync(BuildRequest());
            // The core refuses a request while a permission is missing, before any generation is used.
            // Settings can change between the check and the request: ask for that permission once, then retry.
            if (outcome is { Status: AiOutcomeStatus.Failed, Error: { Kind: AiErrorKind.Consent, Permission: { } permission } })
            {
                if (!await AskAsync(permission)) return;
                outcome = await Ai.ExecuteAsync(BuildRequest());
            }
            if (_disposed) return;
            switch (outcome.Status)
            {
                case AiOutcomeStatus.Completed or AiOutcomeStatus.Cached when outcome.Response is { } response:
                    ShowResult(response);
                    if (outcome.Status == AiOutcomeStatus.Cached) LocalMessage = "Shown from this session's cache. No new request was sent.";
                    break;
                case AiOutcomeStatus.Failed:
                    _errorKind = outcome.Error?.Kind;
                    break;
                case AiOutcomeStatus.Cancelled:
                    LocalMessage = "Request cancelled. Nothing was changed.";
                    break;
                case AiOutcomeStatus.Busy:
                    LocalMessage = "Another request is running. Wait for it or cancel it.";
                    break;
            }
        }
        finally
        {
            _running = false;
            Refresh();
        }
    }

    private AiRequest BuildRequest() => new()
    {
        Feature = Feature,
        Prompt = AiWorkspace.ComposePrompt(Feature, Prompt, IsPlan ? (int)(AvailableMinutes ?? 120) : null, IsRecap ? SelectedEpisode?.Version : null, SubtitleName),
        Titles = Store.Catalogue,
        Personal = Store.Personal,
        Revision = Store.ProjectionRevision,
        SelectedIds = _selected.Order(StringComparer.Ordinal).ToList(),
        SubtitleText = IsRecap ? _subtitleText : null,
        RecapVersionId = IsRecap ? SelectedEpisode?.Version.Id : null,
    };

    /// Asks once for each cloud permission this request needs and is still missing (AiService.MissingPermissions).
    /// When the user declines one, nothing is sent. Local providers need none.
    private async Task<bool> EnsureConsentAsync()
    {
        foreach (var permission in Ai.MissingPermissions(BuildRequest()))
            if (!await AskAsync(permission)) return false;
        return true;
    }

    private async Task<bool> AskAsync(AiPermission permission)
    {
        var allowed = AskConsent is not null && await AskConsent(permission, ProviderName);
        if (!allowed)
        {
            LocalMessage = permission switch
            {
                AiPermission.Cloud => "Nothing was sent. Myra AI needs your permission to contact the cloud provider.",
                AiPermission.History => Feature == AiFeature.Insights
                    ? "Nothing was sent. Viewing Insights needs permission to include viewing history."
                    : "Nothing was sent. Allow history sharing, or ask without mentioning unwatched, unfinished or watchlist titles.",
                _ => "Nothing was sent. A cloud recap needs permission to send the subtitle excerpt.",
            };
            return false;
        }
        Ai.UpdateSettings(s => permission switch
        {
            AiPermission.Cloud => s with { CloudConsent = true },
            AiPermission.History => s with { ShareHistory = true },
            _ => s with { ShareSubtitles = true },
        });
        return true;
    }

    [RelayCommand]
    private void Cancel()
    {
        Ai.Cancel();
    }

    /// Esc: cancels a running request, otherwise closes the workspace.
    public void CancelOrClose()
    {
        if (IsBusy || _running) Cancel();
        else Close();
    }

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke();

    [RelayCommand]
    private async Task OpenSettingsAsync()
    {
        if (ShowSettings is not null) await ShowSettings();
        Ai.RefreshAvailability();
        Refresh();
    }

    // ---------- Results ----------

    [ObservableProperty] private string? _localMessage;
    [ObservableProperty] private AiResponse? _result;

    public bool HasResult => Result is not null;
    public bool ShowEmptyState => Result is null && !IsBusy && !HasError;
    public string Summary => Result?.Summary ?? "";
    public string TagsText => Result is { Tags.Count: > 0 } r ? "Suggested tags: " + string.Join(" · ", r.Tags) : "";
    public bool HasTags => Result is { Tags.Count: > 0 };
    public ObservableCollection<AiResultCardViewModel> Cards { get; } = [];
    public ObservableCollection<AiActionViewModel> Actions { get; } = [];
    public bool HasActions => Actions.Count > 0;
    public string SourceNote => Result?.SourceNote ?? "";
    public string GeneratedWith => Result is { } r ? $"Generated with {r.Provider}" : "";
    public bool CanUndo => Ai.CanUndo;

    public string? ErrorMessage => Ai.ErrorMessage;
    public bool HasError => Ai.ErrorMessage is not null;

    /// What the user can do about the last failure.
    public string RecoveryText => _errorKind switch
    {
        AiErrorKind.Unavailable => "Choose a provider and a model, and turn this tool on, in AI Settings.",
        AiErrorKind.MissingKey => "Open AI Settings and save your API key for this provider.",
        AiErrorKind.Consent => "Allow the permission in AI Settings, or press Ask Myra again to answer the permission prompt.",
        AiErrorKind.Limit => "Raise the daily limit in AI Settings, or try again tomorrow.",
        AiErrorKind.InvalidResponse => "Try again or rephrase the request. No changes were applied.",
        AiErrorKind.Http => "Check the model name and your provider account in AI Settings, then try again.",
        AiErrorKind.MissingContext => "Add the missing details and try again.",
        _ => "",
    };

    public bool ShowSettingsRecovery => _errorKind is AiErrorKind.Unavailable or AiErrorKind.MissingKey or AiErrorKind.Consent
        or AiErrorKind.Limit or AiErrorKind.Http;

    private void ClearResult()
    {
        _errorKind = null;
        Result = null;
        Cards.Clear();
        Actions.Clear();
        OnPropertyChanged(string.Empty);
    }

    private void ShowResult(AiResponse response)
    {
        Result = response;
        Cards.Clear();
        foreach (var recommendation in response.Recommendations)
            if (Store.Catalogue.FirstOrDefault(t => t.Id == recommendation.TitleId) is { } title)
                Cards.Add(new AiResultCardViewModel(this, title, recommendation));
        Actions.Clear();
        foreach (var action in response.Actions)
            Actions.Add(new AiActionViewModel(this, action, Store.Catalogue));
        OnPropertyChanged(string.Empty);
    }

    internal bool IsApplied(AiAction action) => _applied.Contains(action.Id);

    internal async Task ApplyAsync(AiAction action)
    {
        try
        {
            switch (action.Kind)
            {
                case AiActionKind.Play:
                    if (action.TitleIds.FirstOrDefault() is { } id && Store.Catalogue.FirstOrDefault(t => t.Id == id) is { } title)
                        PlayRequested?.Invoke(title);
                    return;
                case AiActionKind.ShowResults:
                    _selected.Clear();
                    foreach (var titleId in action.TitleIds.Take(AiWorkspace.MaximumSelection)) _selected.Add(titleId);
                    RefreshTitles();
                    LocalMessage = "Selected the matching titles. Use their Play buttons above.";
                    return;
                default:
                    await Ai.ApplyAsync(action, Store);
                    _applied.Add(action.Id);
                    LocalMessage = "Change saved. You can undo it below.";
                    break;
            }
        }
        catch (AiException error)
        {
            LocalMessage = error.Message;
        }
        finally
        {
            foreach (var item in Actions) item.Refresh();
            OnPropertyChanged(nameof(CanUndo));
        }
    }

    [RelayCommand]
    private async Task UndoAsync()
    {
        try
        {
            await Ai.UndoAsync(Store);
            _applied.Clear();
            LocalMessage = "Change undone.";
        }
        catch (AiException error)
        {
            LocalMessage = error.Message;
        }
        foreach (var item in Actions) item.Refresh();
        OnPropertyChanged(nameof(CanUndo));
    }

    internal void Play(EntertainmentTitle title) => PlayRequested?.Invoke(title);

    internal void ToggleWatchlist(EntertainmentTitle title)
    {
        Store.ToggleWatchlist(title.Id);
        foreach (var card in Cards) card.Refresh();
    }

    internal async Task OpenDetailsAsync(EntertainmentTitle title)
    {
        if (ShowDetails is null) return;
        using var details = new DetailViewModel(_main, title) { CanAskAi = true };
        AiFeature? requested = null;
        details.AiRequested += feature => requested = feature;
        var chosen = await ShowDetails(details);
        if (chosen is not null)
        {
            PlayRequested?.Invoke(chosen);
            return;
        }
        if (requested is { } next)
        {
            SelectedFeature = Features.First(f => f.Feature == next);
            _selected.Clear();
            Select(details.Title);
        }
    }

    // ---------- Usage ----------

    public string UsageText =>
        $"Requests today: {Ai.RequestsToday} of {Ai.Settings.DailyRequestLimit} · Estimated tokens: about {Ai.EstimatedInputTokens.ToString("N0", CultureInfo.CurrentCulture)} sent, {Ai.EstimatedOutputTokens.ToString("N0", CultureInfo.CurrentCulture)} received (estimate)";
    public string UsageNote => AiService.UsageNote;
    public bool IsLimitReached => Ai.RequestsToday >= Ai.Settings.DailyRequestLimit;
    public string LimitText => "Daily limit reached. Raise it in AI Settings or try again tomorrow.";
    public string TwoStepNote => Feature.ExtractsIntent() ? "This tool uses two generations: one to read your request, one for the answer." : "";
    public bool HasTwoStepNote => Feature.ExtractsIntent();

    // ---------- Change notification ----------

    private void OnAiChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(Refresh);

    private void OnStoreChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(() =>
    {
        if (_disposed) return;
        RefreshEpisodes();
        foreach (var card in Cards) card.Refresh();
    });

    private void Refresh()
    {
        if (_disposed) return;
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(RunLabel));
        OnPropertyChanged(nameof(ShowEmptyState));
        OnPropertyChanged(nameof(ProviderStatus));
        OnPropertyChanged(nameof(IsProviderDisabled));
        OnPropertyChanged(nameof(IsFeatureDisabled));
        OnPropertyChanged(nameof(IsCloud));
        OnPropertyChanged(nameof(ProviderName));
        OnPropertyChanged(nameof(ErrorMessage));
        OnPropertyChanged(nameof(HasError));
        OnPropertyChanged(nameof(RecoveryText));
        OnPropertyChanged(nameof(ShowSettingsRecovery));
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(UsageText));
        OnPropertyChanged(nameof(IsLimitReached));
        RunCommand.NotifyCanExecuteChanged();
    }

    /// Cancels the running request and forgets the imported subtitle text.
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Ai.Changed -= OnAiChanged;
        Store.Changed -= OnStoreChanged;
        if (Ai.IsBusy) Ai.Cancel();
        _subtitleText = null;
    }
}

public sealed class AiTitleChoiceViewModel
{
    public AiTitleChoiceViewModel(EntertainmentTitle title, bool isSelected, bool isEnabled, Action<EntertainmentTitle> choose)
    {
        Title = title;
        IsSelected = isSelected;
        IsEnabled = isEnabled;
        ChooseCommand = new RelayCommand(() => choose(title), () => isEnabled);
    }

    public EntertainmentTitle Title { get; }
    public string Name => Title.DisplayName;
    public string Year => Title.Year ?? "";
    public bool IsSelected { get; }
    public bool IsEnabled { get; }
    public IRelayCommand ChooseCommand { get; }
}

public sealed record AiEpisodeChoice(EntertainmentVersion Version)
{
    public string Label => string.Format(CultureInfo.CurrentCulture, "Season {0} · Episode {1:00}", Version.Season ?? 0, Version.Episode ?? 0);
    public override string ToString() => Label;
}

/// A grounded result: a real catalogue title with the model's reason. Play starts the concrete planned version.
public sealed partial class AiResultCardViewModel : ObservableObject
{
    private readonly AiWorkspaceViewModel _owner;

    public AiResultCardViewModel(AiWorkspaceViewModel owner, EntertainmentTitle title, AiRecommendation recommendation)
    {
        _owner = owner;
        Title = title;
        Reason = recommendation.Reason;
        VersionId = recommendation.VersionId;
        PlaybackTitle = AiWorkspace.PlaybackTitle(title, recommendation.VersionId);
    }

    public EntertainmentTitle Title { get; }
    /// The title limited to the planned version when the plan chose one.
    public EntertainmentTitle PlaybackTitle { get; }
    public string? VersionId { get; }
    public string Reason { get; }
    public string Name => Title.DisplayName;
    public Uri? PosterUrl => Title.PosterUrl;
    public string InfoLine => string.Join(" · ", new[] { Title.Year, Title.Kind == EntertainmentKind.Series ? "Series" : "Movie" }.OfType<string>());
    public string EpisodeText => VersionId is not null && Title.Versions.FirstOrDefault(v => v.Id == VersionId) is { Episode: { } episode } version
        ? string.Format(CultureInfo.CurrentCulture, "Season {0} · Episode {1:00}", version.Season ?? 0, episode)
        : "";
    public bool HasEpisode => EpisodeText.Length > 0;
    public string RuntimeText => AiContext.Runtime(PlaybackTitle) is { } minutes ? $"Known runtime: {minutes} minutes" : "";
    public bool HasRuntime => RuntimeText.Length > 0;
    public string PlayLabel => PlaybackTitle.ResumeVersion is null ? "Play" : "Resume";
    public string WatchlistLabel => _owner.Store.Personal.Watchlist.Contains(Title.Id) ? "Remove from Watchlist" : "Add to Watchlist";

    [RelayCommand] private void Play() => _owner.Play(PlaybackTitle);
    [RelayCommand] private Task DetailsAsync() => _owner.OpenDetailsAsync(Title);
    [RelayCommand] private void ToggleWatchlist() => _owner.ToggleWatchlist(Title);

    public void Refresh() => OnPropertyChanged(string.Empty);
}

/// Preview of one proposed action. Nothing changes until Apply.
public sealed partial class AiActionViewModel : ObservableObject
{
    private readonly AiWorkspaceViewModel _owner;

    public AiActionViewModel(AiWorkspaceViewModel owner, AiAction action, IReadOnlyList<EntertainmentTitle> catalogue)
    {
        _owner = owner;
        Action = action;
        TitleNames = action.TitleIds.Select(id => catalogue.FirstOrDefault(t => t.Id == id)?.DisplayName).OfType<string>().ToList();
    }

    public AiAction Action { get; }
    public string Label => Action.Label;
    public IReadOnlyList<string> TitleNames { get; }
    public string TitlesText => string.Join("\n", TitleNames.Select(n => "• " + n));
    public bool HasTitles => TitleNames.Count > 0;
    public bool ChangesPersonalData => Action.ChangesPersonalData;
    public bool IsApplied => _owner.IsApplied(Action);
    public string ApplyLabel => IsApplied ? "Applied" : Action.Kind switch
    {
        AiActionKind.Play => "Play",
        AiActionKind.ShowResults => "Show Titles",
        _ => "Apply",
    };

    /// What Apply will change, in plain words.
    public string Preview => Action.Kind switch
    {
        AiActionKind.AddWatchlist => $"Adds {Count(TitleNames.Count)} to your Watchlist.",
        AiActionKind.MarkWatched => $"Marks {Count(TitleNames.Count)} as watched.",
        AiActionKind.Collection => $"Creates the collection “{Action.Value}” with {Count(TitleNames.Count)}.",
        AiActionKind.Correction => "Changes how this title is matched in your catalogue: " + string.Join(", ",
            new[] { $"title “{Action.Value}”", Action.Year is { } y ? $"year {y}" : null, Action.MediaKind is { } k ? $"type {k}" : null }.OfType<string>())
            + ". Files on your sources are not renamed.",
        AiActionKind.Preference => $"Saves this preference text for future requests: “{Action.Value}”.",
        AiActionKind.ShowResults => "Selects these titles in the workspace. Nothing is saved.",
        AiActionKind.Play => "Starts playback of this title. Nothing is saved.",
        _ => "",
    };

    private static string Count(int n) => n == 1 ? "1 title" : $"{n} titles";

    [RelayCommand(CanExecute = nameof(CanApply))]
    private Task ApplyAsync() => _owner.ApplyAsync(Action);

    private bool CanApply() => !IsApplied && !_owner.IsBusy;

    public void Refresh()
    {
        OnPropertyChanged(string.Empty);
        ApplyCommand.NotifyCanExecuteChanged();
    }
}
