using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LibVLCSharp.Shared;
using Myra.App.Services;
using Myra.Core;

namespace Myra.App.ViewModels;

public sealed record InspectorField(string Name, string Value);

/// Media inspector (macOS MediaInspectorModel): OMDb information first, then local technical details
/// probed on demand through libVLC media parsing.
public sealed partial class InspectorViewModel(AppServices services) : ObservableObject
{
    private static LibVLC? _probeVlc;
    private static readonly Lock ProbeLock = new();
    private CancellationTokenSource? _lookup;
    private CancellationTokenSource? _probe;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsOpen))] private GlobalSearchResult? _selected;
    [ObservableProperty] private MovieMetadata? _metadata;
    [ObservableProperty] private string? _metadataError;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _isProbing;
    [ObservableProperty] private string? _technicalError;
    [ObservableProperty] private MediaIdentity _identity = new("");
    [ObservableProperty] private string _correctionTitle = "";
    [ObservableProperty] private string _correctionYear = "";
    [ObservableProperty] private string _correctionImdbId = "";

    public bool IsOpen => Selected is not null;
    public ObservableCollection<InspectorField> OnlineFields { get; } = [];
    public ObservableCollection<InspectorField> LocalFields { get; } = [];
    public Dictionary<string, string> TechnicalDetails { get; private set; } = [];

    /// Set by the main view model: Play from the inspector.
    public Func<GlobalSearchResult, Task>? Play { get; set; }

    public string DisplayTitle => MovieMetadata.Available(Metadata?.Title) ?? Identity.Title;
    public string? ImdbRating => MovieMetadata.Available(Metadata?.ImdbRating) is { } rating ? $"IMDb {rating} / 10" : null;
    public bool HasRating => ImdbRating is not null;
    public string? Plot => MovieMetadata.Available(Metadata?.Plot);
    public bool HasPlot => Plot is not null;
    public bool HasMetadata => Metadata is not null;
    public bool HasMetadataError => MetadataError is not null;
    public bool HasTechnicalError => TechnicalError is not null;
    public string? ImdbUrl => Metadata?.ImdbId is { } id && Regex.IsMatch(id, "^tt[0-9]{7,10}$") ? $"https://www.imdb.com/title/{id}/" : null;
    public bool HasImdbUrl => ImdbUrl is not null;
    public string License => "Metadata via OMDb • CC BY-NC 4.0";

    public Uri? Artwork
    {
        get
        {
            if (Selected?.ArtworkUrl is { } local) return local;
            return MovieMetadata.Available(Metadata?.Poster) is { } poster && Uri.TryCreate(poster, UriKind.Absolute, out var url) && url.Scheme == "https"
                ? url : null;
        }
    }

    public bool HasArtwork => Artwork is not null;

    public void Select(GlobalSearchResult media)
    {
        _lookup?.Cancel();
        _probe?.Cancel();
        Selected = media;
        Metadata = null;
        TechnicalDetails = [];
        TechnicalError = null;
        Identity = MediaIdentity.Parse(media.Entry.Name);
        CorrectionTitle = Identity.Title;
        CorrectionYear = Identity.Year ?? "";
        CorrectionImdbId = "";
        Load();
        _ = ProbeAsync(media);
    }

    [RelayCommand]
    public void Close()
    {
        _lookup?.Cancel();
        _probe?.Cancel();
        Selected = null;
        IsLoading = false;
        IsProbing = false;
    }

    [RelayCommand]
    private void Load()
    {
        _lookup?.Cancel();
        var lookup = _lookup = new CancellationTokenSource();
        var identity = Identity;
        IsLoading = true;
        Metadata = null;
        MetadataError = null;
        Refresh();
        _ = Task.Run(async () =>
        {
            var key = services.Secrets.Read(SecretNames.OmdbApiKey);
            if (string.IsNullOrWhiteSpace(key)) throw new MetadataException(MetadataErrorKind.MissingKey);
            LibraryIndex? index = null;
            try
            {
                index = services.Index;
            }
            catch (Exception)
            {
                // The cache is optional.
            }
            return await services.Metadata.LookupAsync(identity, key, index, lookup.Token);
        }, lookup.Token).ContinueWith(task => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (lookup.IsCancellationRequested) return;
            if (task.IsCompletedSuccessfully) Metadata = task.Result;
            else MetadataError = task.Exception?.InnerException is { } error
                ? error is MetadataException or SecretStoreException ? error.Message : "Online metadata is unavailable. Check your connection and try again."
                : null;
            IsLoading = false;
            Refresh();
        }), TaskScheduler.Default);
    }

    [RelayCommand]
    private void LookUp()
    {
        var title = CorrectionTitle.Trim();
        var id = CorrectionImdbId.Trim();
        if (title.Length == 0 && id.Length == 0) return;
        var year = CorrectionYear.Trim();
        Identity = new MediaIdentity(title, year.Length == 0 ? null : year, ImdbId: id.Length == 0 ? null : id);
        Load();
    }

    [RelayCommand]
    private async Task PlayAsync()
    {
        if (Selected is { } media && Play is not null) await Play(media);
    }

    [RelayCommand]
    private void OpenImdb()
    {
        if (ImdbUrl is { } url) ShellLinks.Open(url);
    }

    private void Refresh()
    {
        OnlineFields.Clear();
        if (Metadata is { } m)
        {
            foreach (var (name, value) in new[]
                     {
                         ("Year", m.Year), ("Released", m.Released), ("Runtime", m.Runtime), ("Genre", m.Genre),
                         ("Director", m.Director), ("Cast", m.Actors), ("Language", m.Language), ("Country", m.Country),
                         ("Rated", m.Rated), ("Awards", m.Awards),
                     })
                if (MovieMetadata.Available(value) is { } shown) OnlineFields.Add(new InspectorField(name, shown));
        }
        LocalFields.Clear();
        if (Selected is { } media)
        {
            void Add(string name, string? value)
            {
                if (MovieMetadata.Available(value) is { } shown) LocalFields.Add(new InspectorField(name, shown));
            }
            Add("File name", media.Entry.Name);
            Add("Category", media.CategoryName);
            Add("Size", media.Entry.Size is { } size ? Format.Bytes(size) : "Unavailable");
            Add("Duration", TechnicalDetails.GetValueOrDefault("Duration") ?? "Unavailable");
            Add("FPS + Resolution", VideoFormat(TechnicalDetails));
            Add("Path", media.RelativePath);
            if (media.Entry.ModifiedAt is { } modified) Add("Modified", modified.ToLocalTime().ToString("g", CultureInfo.CurrentCulture));
            foreach (var key in TechnicalDetails.Keys.Order(StringComparer.Ordinal))
                if (key is not ("Duration" or "Resolution" or "Frame rate")) Add(key, TechnicalDetails[key]);
        }
        OnPropertyChanged(string.Empty);
    }

    public static string VideoFormat(IReadOnlyDictionary<string, string> details)
    {
        var resolution = details.GetValueOrDefault("Resolution")?.Replace("×", "x").Replace(" ", "");
        var fps = details.GetValueOrDefault("Frame rate")?.Replace("fps", "").Trim();
        if (resolution is not null && fps is not null) return $"{resolution} @ {fps}fps";
        return resolution ?? (fps is not null ? fps + "fps" : "Unavailable");
    }

    private async Task ProbeAsync(GlobalSearchResult media)
    {
        _probe?.Cancel();
        var probe = _probe = new CancellationTokenSource();
        IsProbing = true;
        Refresh();
        try
        {
            var details = await Task.Run(() => Probe(media.Entry.Url, probe.Token), probe.Token);
            if (Selected?.Id != media.Id) return;
            TechnicalDetails = details;
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception)
        {
            if (Selected?.Id != media.Id) return;
            TechnicalError = "Stream details unavailable. You can still try Play or VLC.";
        }
        if (Selected?.Id == media.Id)
        {
            IsProbing = false;
            Refresh();
        }
    }

    private static LibVLC ProbeVlc()
    {
        lock (ProbeLock)
        {
            if (_probeVlc is not null) return _probeVlc;
            var directory = Environment.GetEnvironmentVariable("MYRA_LIBVLC_DIR");
            if (!string.IsNullOrEmpty(directory) && OperatingSystem.IsWindows()) LibVLCSharp.Shared.Core.Initialize(directory);
            else LibVLCSharp.Shared.Core.Initialize();
            return _probeVlc = new LibVLC("--no-video", "--no-audio", "--quiet");
        }
    }

    /// Port of macOS VLCMediaProbe.inspect: duration, resolution, frame rate, codecs, audio and subtitle tracks.
    private static async Task<Dictionary<string, string>> Probe(Uri url, CancellationToken cancellationToken)
    {
        var vlc = ProbeVlc();
        using var media = new Media(vlc, url);
        var status = await media.Parse(MediaParseOptions.ParseNetwork | MediaParseOptions.ParseLocal, 10_000, cancellationToken);
        if (status != MediaParsedStatus.Done) throw new MetadataException(MetadataErrorKind.Network);
        var details = new Dictionary<string, string>();
        if (media.Duration > 0) details["Duration"] = PlayerTime.Format(media.Duration / 1000.0);
        var audio = new List<string>();
        var audioCodecs = new List<string>();
        var subtitles = new List<string>();
        foreach (var track in media.Tracks)
        {
            var language = string.IsNullOrEmpty(track.Language) ? "Unknown language" : track.Language;
            var label = string.IsNullOrEmpty(track.Description) ? language : $"{language} ({track.Description})";
            var codec = media.CodecDescription(track.TrackType, track.Codec);
            switch (track.TrackType)
            {
                case TrackType.Video:
                    var video = track.Data.Video;
                    if (video.Width > 0 && video.Height > 0) details["Resolution"] = $"{video.Width} × {video.Height}";
                    if (!string.IsNullOrEmpty(codec)) details["Video codec"] = codec;
                    if (video.FrameRateDen > 0)
                        details["Frame rate"] = ((double)video.FrameRateNum / video.FrameRateDen).ToString("G3", CultureInfo.InvariantCulture) + " fps";
                    break;
                case TrackType.Audio:
                    audio.Add(label);
                    if (!string.IsNullOrEmpty(codec)) audioCodecs.Add(codec);
                    break;
                case TrackType.Text:
                    subtitles.Add(label);
                    break;
            }
        }
        if (audio.Count > 0) details["Audio tracks"] = string.Join(", ", audio);
        if (audioCodecs.Count > 0) details["Audio codecs"] = string.Join(", ", audioCodecs);
        if (subtitles.Count > 0) details["Subtitle tracks"] = string.Join(", ", subtitles);
        if (details.Count == 0) throw new MetadataException(MetadataErrorKind.BadResponse);
        return details;
    }
}
