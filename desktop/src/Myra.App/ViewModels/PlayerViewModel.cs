using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Input;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LibVLCSharp.Shared;
using LibVLCSharp.Shared.Structures;
using Myra.App.Services;
using Myra.Core;

namespace Myra.App.ViewModels;

public sealed record TrackChoice(int Id, string Name)
{
    public override string ToString() => Name;
}

/// Embedded libVLC player. It follows the macOS EmbeddedPlayerModel: resume prompt, English track defaults,
/// folder auto-next, two-second buffering detection, fullscreen overlay controls and queued fullscreen exit.
///
/// A host (main window or PlayerWindow) shows a PlayerView bound to this view model and handles
/// BackToLibraryRequested. Optional features attach through the hook properties further down.
public sealed partial class PlayerViewModel : ObservableObject, IDisposable
{
    private static LibVLC? _libVlc;
    private readonly AppServices _services;
    private readonly PlayerPersonalState _preferences;
    private readonly PlaybackCompletionState _completion = new();
    private readonly PlayerStallDetector _stall = new();
    private readonly PlayerControlsVisibility _controls = new();
    private readonly DispatcherTimer _timer;
    private PlaybackSequence? _sequence;
    private Media? _media;
    private bool _userChoseAudio;
    private bool _userChoseSubtitle;
    private bool _updatingTracks;
    private bool _detached = true;
    private bool _isClosingPlayer;
    private int _tickCount;
    private double _lastSavedAt = double.NegativeInfinity;

    public PlayerViewModel(AppServices services)
    {
        _services = services;
        _preferences = services.PlayerState;
        _libVlc ??= CreateLibVlc();
        // With libVLC input off, keys and clicks over the video reach Myra's window instead of libVLC's.
        MediaPlayer = new MediaPlayer(_libVlc) { EnableHardwareDecoding = true, EnableKeyInput = false, EnableMouseInput = false };
        MediaPlayer.Opening += (_, _) => OnUi(() =>
        {
            if (State is PlayerState.Idle or PlayerState.Stopped or PlayerState.Ended) SetState(PlayerState.Opening);
        });
        MediaPlayer.Playing += (_, _) => OnUi(() => SetState(PlayerState.Playing));
        MediaPlayer.Paused += (_, _) => OnUi(() => SetState(PlayerState.Paused));
        MediaPlayer.Stopped += (_, _) => OnUi(() => SetState(PlayerState.Stopped));
        MediaPlayer.EndReached += (_, _) => OnUi(() => SetState(PlayerState.Ended));
        MediaPlayer.EncounteredError += (_, _) => OnUi(() =>
        {
            if (_detached) return;
            SetState(PlayerState.Error);
            ErrorMessage = "VLC could not play this stream. Check the source connection or open it in external VLC.";
        });
        MediaPlayer.LengthChanged += (_, e) => OnUi(() => Duration = e.Length / 1000.0);
        MediaPlayer.SeekableChanged += (_, e) => OnUi(() => IsSeekable = e.Seekable != 0);
        MediaPlayer.ESAdded += (_, _) => OnUi(RefreshTracks);
        MediaPlayer.ESDeleted += (_, _) => OnUi(RefreshTracks);

        _volume = Math.Clamp(_preferences.Volume, 0, 100);
        _speed = _preferences.Speed is >= 0.25f and <= 4 ? _preferences.Speed : 1;
        _autoplay = _preferences.Autoplay;
        Chapters.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasChapters));
        VersionChoices.CollectionChanged += (_, _) =>
        {
            PreviousCommand.NotifyCanExecuteChanged();
            NextCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(HasVersionChoices));
        };
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) => Tick());
        _timer.Start();
    }

    private static LibVLC CreateLibVlc()
    {
        // MYRA_LIBVLC_DIR and MYRA_VLC_ARGS exist for automated playback tests.
        // On Linux, LibVLCSharp only searches the loader path (LD_LIBRARY_PATH).
        var directory = Environment.GetEnvironmentVariable("MYRA_LIBVLC_DIR");
        if (!string.IsNullOrEmpty(directory) && OperatingSystem.IsWindows()) LibVLCSharp.Shared.Core.Initialize(directory);
        else LibVLCSharp.Shared.Core.Initialize();
        var extra = (Environment.GetEnvironmentVariable("MYRA_VLC_ARGS") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return new LibVLC(["--no-video-title-show", "--no-snapshot-preview", "--no-play-and-pause", .. extra]);
    }

    private static void OnUi(Action action) => Dispatcher.UIThread.Post(action);

    private static double Now => Environment.TickCount64 / 1000.0;

    public MediaPlayer MediaPlayer { get; }
    public bool IsClosed { get; private set; }
    public ObservableCollection<TrackChoice> AudioTracks { get; } = [];
    public ObservableCollection<TrackChoice> SubtitleTracks { get; } = [];
    public ObservableCollection<PlayerChapter> Chapters { get; } = [];
    public ObservableCollection<GlobalSearchResult> VersionChoices { get; } = [];
    public IReadOnlyList<float> Speeds { get; } = [0.25f, 0.5f, 0.75f, 1f, 1.25f, 1.5f, 2f, 3f, 4f];

    // ---------- Host contract ----------

    /// Stop, Back to Library, the last video finishing, or Autoplay off after a finish.
    /// Playback has stopped and any fullscreen mode has ended. The host shows its browser again.
    public event Action? BackToLibraryRequested;

    /// Subtitles > Find Online Subtitles... The host opens its search UI for this media,
    /// then calls LoadSubtitleFile with the downloaded file.
    public event Action<GlobalSearchResult>? OnlineSubtitlesRequested;

    /// The player asks its window to enter (true) or leave (false) fullscreen. PlayerView handles this.
    public event Action<bool>? FullscreenChangeRequested;

    /// A short message for the host's toast or status line (macOS onNotice).
    public event Action<string>? Notice;

    /// A video finished naturally (macOS onPlaybackEnded). Use it for watched state.
    public event Action<GlobalSearchResult>? PlaybackEnded;

    /// Resume point saved (macOS onPositionChanged): media, seconds, duration.
    /// The last argument is true for pause, stop, end of a file and close: write it to disk now.
    /// Periodic saves during playback pass false.
    public event Action<GlobalSearchResult, double, double, bool>? PositionSaved;

    // ---------- Optional features (null hides the control) ----------

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasPlaybackPreferences), nameof(ShowAutoplayToggle))] private IPlayerPreferencesHook? _playbackPreferences;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasSkipMarkers))] private IPlayerSkipMarkersHook? _skipMarkers;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasEpisodeQueue), nameof(CanTryAnotherVersion))] private IPlayerQueueHook? _episodeQueue;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanTryAnotherVersion))] private IPlayerVersionChooser? _versionChooser;

    public bool HasPlaybackPreferences => PlaybackPreferences is not null;
    public bool ShowAutoplayToggle => PlaybackPreferences is null;
    public bool HasSkipMarkers => SkipMarkers is not null;
    public bool HasEpisodeQueue => EpisodeQueue is not null;
    public bool HasChapters => Chapters.Count > 0;
    public bool HasVersionChoices => VersionChoices.Count > 0;
    public bool SequenceLoading => false;

    /// The error panel offers Try Another Version when other releases of this title exist and a chooser is attached.
    public bool CanTryAnotherVersion => VersionChooser is not null && CurrentVersions.Count > 1;

    // ---------- State ----------

    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _headerTitle = "Player";
    [ObservableProperty] private string _fileName = "";
    [ObservableProperty] private string _subtitle = "";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsPlaying), nameof(StatusText))] private PlayerState _state = PlayerState.Idle;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(DurationText), nameof(RemainingText), nameof(PositionFraction), nameof(EndText))] private double _duration;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(TimeText), nameof(RemainingText), nameof(PositionFraction), nameof(EndText))] private double _position;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(TimeText))] private bool _isScrubbing;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(TimeText))] private double _scrubPosition;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(StatusText), nameof(ShowBuffering))] private bool _isBuffering;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(SeekingUnsupported), nameof(ShowResumePrompt), nameof(ShowSkip))] private bool _isSeekable;
    [ObservableProperty] private int _volume;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsAudible))] private bool _isMuted;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(SpeedText))] private float _speed;
    [ObservableProperty] private double _audioDelay;
    [ObservableProperty] private double _subtitleDelay;
    [ObservableProperty] private bool _autoplay;
    [ObservableProperty] private bool _repeat;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasError), nameof(ShowBuffering), nameof(CanTryAnotherVersion))] private string? _errorMessage;
    [ObservableProperty] private TrackChoice? _selectedAudio;
    [ObservableProperty] private TrackChoice? _selectedSubtitle;
    [ObservableProperty] private int _selectedChapter = -1;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasResumePrompt), nameof(ResumePromptText), nameof(ShowResumePrompt))] private double? _resumePosition;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ShowSkip))] private PlayerSkipPrompt? _activeSkip;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ShowResumePrompt))] private bool _isFullscreen;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ShowResumePrompt))] private bool _controlsVisible = true;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(EndText))] private bool _showRemaining;
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(PreviousCommand))] private bool _hasPrevious;
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(NextCommand))] private bool _hasNext;

    public bool IsPlaying => State == PlayerState.Playing;
    public bool IsAudible => !IsMuted;
    public bool HasError => ErrorMessage is not null;
    public bool ShowBuffering => IsBuffering && ErrorMessage is null;
    public bool SeekingUnsupported => !IsSeekable && State == PlayerState.Playing;
    public bool HasResumePrompt => ResumePosition is not null;
    public bool ShowResumePrompt => HasResumePrompt && IsSeekable && (!IsFullscreen || ControlsVisible);
    public bool ShowSkip => ActiveSkip is not null && IsSeekable;
    public string EndText => ShowRemaining ? RemainingText : DurationText;
    public string ResumePromptText => ResumePosition is { } seconds ? $"Resume from {PlayerTime.Format(seconds)}?" : "";
    public string TimeText => PlayerTime.Format(IsScrubbing ? ScrubPosition * Duration : Position);
    public string DurationText => PlayerTime.Format(Duration);
    public string RemainingText => "−" + PlayerTime.Format(Math.Max(0, Duration - Position));
    public double PositionFraction => Duration > 0 ? Math.Clamp(Position / Duration, 0, 1) : 0;
    public string SpeedText => Speed.ToString("0.##", CultureInfo.InvariantCulture) + "×";

    public string StatusText => State switch
    {
        PlayerState.Opening => "Opening…",
        PlayerState.Buffering => IsBuffering ? "Buffering…" : Position > 0 ? "Playing" : "Opening…",
        PlayerState.Playing => IsBuffering ? "Buffering…" : "Playing",
        PlayerState.Paused => "Paused",
        PlayerState.Stopped => "Stopped",
        PlayerState.Ended => "Finished",
        PlayerState.Error => "Playback failed",
        _ => "",
    };

    public GlobalSearchResult? Current { get; private set; }

    /// All releases of the playing title (a group in the folder queue), or just the playing file.
    public IReadOnlyList<GlobalSearchResult> CurrentVersions =>
        Current is null ? []
        : _sequence?.Groups.FirstOrDefault(g => g.Any(v => v.Entry.Url == Current.Entry.Url)) is { } group ? group
        : [Current];

    /// The folder queue, for the Episodes / Files hook.
    public PlaybackSequence? Sequence => _sequence;

    private (GlobalSearchResult Result, PlaybackSequence Sequence, bool Resume, bool Automatic, double? ResumeSeconds)? _pendingLoad;
    private bool _waitingForVideo;

    /// The view calls this when it has no video surface (new view, hidden, or detached): playback must
    /// not start until the surface exists, otherwise libVLC opens a separate window of its own.
    public void WaitForVideoSurface() => _waitingForVideo = true;

    public bool HasVideoSurface => OperatingSystem.IsWindows() ? MediaPlayer.Hwnd != IntPtr.Zero
        : OperatingSystem.IsLinux() ? MediaPlayer.XWindow != 0
        : MediaPlayer.NsObject != IntPtr.Zero;

    public void VideoSurfaceReady()
    {
        _waitingForVideo = false;
        if (_pendingLoad is { } pending)
        {
            _pendingLoad = null;
            Load(pending.Result, pending.Sequence, pending.Resume, pending.Automatic, pending.ResumeSeconds);
        }
    }

    /// Starts a video. resume: offer the saved resume point (Resume / Start Over prompt, as on macOS).
    /// resumeSeconds: seek there automatically. automatic: an automatic episode change or repeat,
    /// which keeps speed and synchronization offsets.
    public void Load(GlobalSearchResult result, PlaybackSequence sequence, bool resume = true, bool automatic = false, double? resumeSeconds = null)
    {
        if (_waitingForVideo)
        {
            _pendingLoad = (result, sequence, resume, automatic, resumeSeconds);
            Title = Path.GetFileNameWithoutExtension(result.Entry.Name);
            return;
        }
        CancelMenu?.Invoke();
        if (!_detached) SavePosition(persist: true);
        _detached = false;
        var load = ++_loadGeneration;
        _isClosingPlayer = false;
        VersionChoices.Clear();
        Current = result;
        _sequence = sequence;
        var identity = MediaIdentity.Parse(result.Entry.Name);
        Title = Path.GetFileNameWithoutExtension(result.Entry.Name);
        HeaderTitle = identity.Title;
        FileName = result.Entry.Name;
        Subtitle = result.CategoryName + (result.ParentPath.Length > 0 ? " › " + result.ParentPath.Replace("/", " › ") : "");
        HasPrevious = sequence.Previous(result.Entry.Url) is not null;
        HasNext = sequence.Next(result.Entry.Url) is not null;
        OnPropertyChanged(nameof(CurrentVersions));
        OnPropertyChanged(nameof(CanTryAnotherVersion));
        ErrorMessage = null;
        ResumePosition = null;
        ActiveSkip = null;
        Position = 0;
        Duration = 0;
        IsSeekable = false;
        IsScrubbing = false;
        IsBuffering = false;
        _completion.Reset();
        _userChoseAudio = false;
        _userChoseSubtitle = false;
        AudioTracks.Clear();
        SubtitleTracks.Clear();
        Chapters.Clear();
        SelectedAudio = null;
        SelectedSubtitle = null;
        SelectedChapter = -1;
        if (!automatic)
        {
            Speed = _preferences.Speed is >= 0.25f and <= 4 ? _preferences.Speed : 1;
            AudioDelay = 0;
            SubtitleDelay = 0;
        }
        _stall.Reset(Now);
        NoteInteraction();

        var startAt = resumeSeconds is { } explicitSeconds && double.IsFinite(explicitSeconds) && explicitSeconds >= 5 ? explicitSeconds : (double?)null;
        var old = _media;
        _media = result.Entry.Url.IsFile
            ? new Media(_libVlc!, result.Entry.Url.LocalPath)
            : new Media(_libVlc!, result.Entry.Url);
        _media.AddOption(":network-caching=3000");
        // Initial language choice, as the macOS engine options. RefreshTracks then keeps it in step as tracks appear.
        if (_preferences.AudioLanguage != "default")
            _media.AddOption(":audio-language=" + (_preferences.AudioLanguage == "en" ? "eng,en" : _preferences.AudioLanguage));
        if (_preferences.SubtitleLanguage == "off") _media.AddOption(":sub-track=-1");
        else if (_preferences.SubtitleLanguage != "default")
            _media.AddOption(":sub-language=" + (_preferences.SubtitleLanguage == "en" ? "eng,en" : _preferences.SubtitleLanguage));
        // start-time works before the stream is seekable, unlike setting Time after Playing.
        if (startAt is { } seconds) _media.AddOption($":start-time={seconds.ToString(CultureInfo.InvariantCulture)}");
        // A fresh engine on macOS starts with default picture settings; do the same here.
        MediaPlayer.AspectRatio = null;
        MediaPlayer.CropGeometry = "";
        MediaPlayer.Play(_media);
        old?.Dispose();
        MediaPlayer.SetRate(Speed);
        MediaPlayer.Volume = IsMuted ? 0 : Volume;
        ApplyDelays();
        _services.IsPlaying = true;
        SetState(PlayerState.Opening);
        // The saved resume point is read off the UI thread (the index may be busy or unavailable).
        // The prompt appears after the new state is set so it is not cleared by it.
        if (resumeSeconds is null && resume) _ = ShowSavedResumePointAsync(result.Entry.Url, load);
    }

    private int _loadGeneration;

    private async Task ShowSavedResumePointAsync(Uri url, int load)
    {
        double? saved;
        try
        {
            saved = await Task.Run(() => _services.Index.PlaybackPosition(url));
        }
        catch (Exception)
        {
            // No index, no resume point: playback continues from the start.
            return;
        }
        if (saved is null || load != _loadGeneration || _detached || IsClosed) return;
        if (State is PlayerState.Ended or PlayerState.Error) return;
        ResumePosition = saved;
    }

    private void SetState(PlayerState state)
    {
        if (_detached) return;
        State = state;
        PreviousCommand.NotifyCanExecuteChanged();
        NextCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(SeekingUnsupported));
        if (state == PlayerState.Playing) RefreshTracks();
        if (state == PlayerState.Paused) SavePosition(persist: true);
        if (_completion.Observe(state) && state == PlayerState.Ended) OnFinished();
    }

    private void OnFinished()
    {
        if (Current is not { } finished) return;
        var (url, duration) = (finished.Entry.Url, Duration);
        _ = SaveToIndexAsync(url, duration, duration);
        PlaybackEnded?.Invoke(finished);
        if (Repeat)
        {
            Load(finished, _sequence!, resume: false, automatic: true);
            return;
        }
        if (Autoplay && _sequence?.Neighbours(finished.Entry.Url, 1) is { Count: > 0 } variants)
        {
            // Automatic episode changes keep the window (and fullscreen) as it is.
            if (variants.Count > 1 && VersionChooser is not null) RequestPlayback(variants);
            else Load(variants[0], _sequence, resume: false, automatic: true);
            return;
        }
        RequestClose();
    }

    private void Tick()
    {
        if (IsClosed || _detached) return;
        var now = Now;
        var time = Math.Max(0, MediaPlayer.Time / 1000.0);
        if (!IsScrubbing) Position = time;
        if (Duration <= 0 && MediaPlayer.Length > 0) Duration = MediaPlayer.Length / 1000.0;
        IsSeekable = MediaPlayer.IsSeekable;
        IsBuffering = _stall.Update(State, time, now, IsScrubbing);
        var visible = _controls.IsVisible(now, IsScrubbing);
        if (ControlsVisible != visible) ControlsVisible = visible;

        if (SkipMarkers is { } markers && Current is not null && IsSeekable && Duration > 0)
        {
            ActiveSkip = markers.ActiveSkip(Current, time, Duration);
            if (markers.AutomaticSkipping && ActiveSkip is { } skip) SeekTo(skip.End);
        }
        else if (ActiveSkip is not null)
        {
            ActiveSkip = null;
        }

        if (State == PlayerState.Playing && Math.Abs(MediaPlayer.Rate - Speed) > 0.001f) MediaPlayer.SetRate(Speed);
        // Track and chapter lists arrive late on network streams; checking about once a second also covers buffering.
        if (++_tickCount % 4 == 0 && State is PlayerState.Playing or PlayerState.Paused or PlayerState.Buffering) RefreshTracks();
        if (State == PlayerState.Playing && now - _lastSavedAt > 10) SavePosition();
    }

    /// Saves the resume point to the index (off the UI thread) and reports it through PositionSaved.
    /// wait bounds how long the caller blocks for the index write (used when the player closes).
    private void SavePosition(bool persist = false, TimeSpan? wait = null)
    {
        _lastSavedAt = Now;
        if (Current is not { } media || Duration <= 0 || Position <= 0 || State == PlayerState.Ended) return;
        var (url, position, duration) = (media.Entry.Url, Position, Duration);
        try
        {
            PositionSaved?.Invoke(media, position, duration, persist);
        }
        catch (Exception)
        {
            // History is a convenience; never interrupt playback for it.
        }
        var save = SaveToIndexAsync(url, position, duration);
        if (wait is { } timeout) save.Wait(timeout);
    }

    /// Resume points are a convenience: index failures are ignored and never reach the UI thread.
    private Task SaveToIndexAsync(Uri url, double position, double duration) => Task.Run(() =>
    {
        try
        {
            _services.Index.SavePlaybackPosition(url, position, duration);
        }
        catch (Exception)
        {
        }
    });

    // ---------- Tracks ----------

    private void RefreshTracks()
    {
        if (_detached) return;
        var languages = new Dictionary<int, string?>();
        if (_media is not null)
            foreach (var track in _media.Tracks)
                languages[track.Id] = track.Language;

        List<PlayerTrack> Describe(TrackDescription[] descriptions) =>
            descriptions.Select(d => new PlayerTrack(d.Id, d.Name ?? $"Track {d.Id}", languages.GetValueOrDefault(d.Id))).ToList();

        var audio = Describe(MediaPlayer.AudioTrackDescription).Where(t => t.Id >= 0).ToList();
        var subtitles = Describe(MediaPlayer.SpuDescription);

        _updatingTracks = true;
        try
        {
            Replace(AudioTracks, audio);
            Replace(SubtitleTracks, subtitles.Count == 0 ? [] : [new PlayerTrack(-1, "Off", null), .. subtitles.Where(s => s.Id >= 0)]);

            // Apply language preferences as tracks appear (also while buffering), unless the viewer already picked one.
            if (!_userChoseAudio && PlayerLanguagePreference.Preferred(audio, _preferences.AudioLanguage, false) is { } audioId and >= 0
                && MediaPlayer.AudioTrack != audioId)
                MediaPlayer.SetAudioTrack(audioId);
            if (!_userChoseSubtitle)
            {
                var preferred = PlayerLanguagePreference.Preferred(subtitles, _preferences.SubtitleLanguage, true);
                // No subtitle in the preferred language: turn subtitles off instead of showing a random one.
                var target = preferred ?? (_preferences.SubtitleLanguage == "default" ? null : -1);
                if (target is { } spu && MediaPlayer.Spu != spu) MediaPlayer.SetSpu(spu);
            }
            SelectedAudio = AudioTracks.FirstOrDefault(t => t.Id == MediaPlayer.AudioTrack);
            SelectedSubtitle = SubtitleTracks.FirstOrDefault(t => t.Id == MediaPlayer.Spu) ?? SubtitleTracks.FirstOrDefault();

            var chapters = MediaPlayer.Title >= 0 ? MediaPlayer.FullChapterDescriptions(MediaPlayer.Title) : [];
            var described = chapters.Select((c, index) => new PlayerChapter(index, string.IsNullOrWhiteSpace(c.Name) ? $"Chapter {index + 1}" : c.Name!)).ToList();
            if (!Chapters.SequenceEqual(described))
            {
                Chapters.Clear();
                foreach (var chapter in described) Chapters.Add(chapter);
            }
            SelectedChapter = MediaPlayer.Chapter;
        }
        finally
        {
            _updatingTracks = false;
        }
    }

    private static void Replace(ObservableCollection<TrackChoice> target, IEnumerable<PlayerTrack> source)
    {
        var items = source.Select(t => new TrackChoice(t.Id, t.Name)).ToList();
        if (target.SequenceEqual(items)) return;
        target.Clear();
        foreach (var item in items) target.Add(item);
    }

    partial void OnSelectedAudioChanged(TrackChoice? value)
    {
        if (_updatingTracks || value is null) return;
        _userChoseAudio = true;
        MediaPlayer.SetAudioTrack(value.Id);
    }

    partial void OnSelectedSubtitleChanged(TrackChoice? value)
    {
        if (_updatingTracks || value is null) return;
        _userChoseSubtitle = true;
        MediaPlayer.SetSpu(value.Id);
    }

    public void ChooseAudio(int id) => SelectedAudio = AudioTracks.FirstOrDefault(t => t.Id == id) ?? new TrackChoice(id, $"Track {id}");

    public void ChooseSubtitle(int id) => SelectedSubtitle = SubtitleTracks.FirstOrDefault(t => t.Id == id) ?? new TrackChoice(id, id < 0 ? "Off" : $"Track {id}");

    public void ChooseChapter(int id)
    {
        MediaPlayer.Chapter = id;
        SelectedChapter = id;
        _stall.Reset(Now);
        NoteInteraction();
    }

    /// Loads a subtitle file into the playing video. forMedia guards against a late download for another video.
    public bool LoadSubtitleFile(string path, GlobalSearchResultId? forMedia = null)
    {
        if (Current is null || _isClosingPlayer || (forMedia is { } id && Current.Id != id)) return false;
        if (!File.Exists(path)) return false;
        if (MediaPlayer.AddSlave(MediaSlaveType.Subtitle, new Uri(path).AbsoluteUri, true))
        {
            _userChoseSubtitle = true;
            return true;
        }
        ErrorMessage = "VLC could not load this subtitle file.";
        return false;
    }

    public void AddSubtitleFile(string path) => LoadSubtitleFile(path);

    public void FindOnlineSubtitles()
    {
        if (Current is { } media) OnlineSubtitlesRequested?.Invoke(media);
    }

    // ---------- Synchronization, aspect, crop, deinterlace ----------

    public void ShiftAudioDelay(double seconds)
    {
        AudioDelay = Math.Round(AudioDelay + seconds, 1);
        ApplyDelays();
    }

    public void ShiftSubtitleDelay(double seconds)
    {
        SubtitleDelay = Math.Round(SubtitleDelay + seconds, 1);
        ApplyDelays();
    }

    public void ResetAudioDelay()
    {
        AudioDelay = 0;
        ApplyDelays();
    }

    public void ResetSubtitleDelay()
    {
        SubtitleDelay = 0;
        ApplyDelays();
    }

    private void ApplyDelays()
    {
        MediaPlayer.SetAudioDelay((long)(AudioDelay * 1_000_000));
        MediaPlayer.SetSpuDelay((long)(SubtitleDelay * 1_000_000));
    }

    public void SetAspect(string? ratio) => MediaPlayer.AspectRatio = ratio;

    public void SetCrop(string? ratio) => MediaPlayer.CropGeometry = ratio ?? "";

    public void SetDeinterlace(PlayerDeinterlace mode)
    {
        // libVLC 3's API has no "automatic" state; Automatic is the filter off, which is also libVLC's behaviour
        // for progressive video. Interlaced sources need On.
        MediaPlayer.SetDeinterlace(mode == PlayerDeinterlace.On ? "yadif" : null);
    }

    // ---------- Versions and navigation ----------

    private bool CanNavigate() => State != PlayerState.Opening && VersionChoices.Count == 0;
    private bool CanGoPrevious() => HasPrevious && CanNavigate();
    private bool CanGoNext() => HasNext && CanNavigate();

    /// Plays the neighbour video; alternate releases of it go to the version chooser when one is attached.
    public void Navigate(int offset)
    {
        if (!CanNavigate() || Current is null || _sequence is null) return;
        RequestPlayback(_sequence.Neighbours(Current.Entry.Url, offset));
    }

    public void RequestPlayback(IReadOnlyList<GlobalSearchResult> choices)
    {
        if (choices.Count == 0 || _sequence is null) return;
        if (choices.Count > 1 && VersionChooser is { } chooser)
        {
            VersionChoices.Clear();
            foreach (var choice in choices) VersionChoices.Add(choice);
            MediaPlayer.SetPause(true);
            NoteInteraction();
            chooser.Choose(this, choices);
        }
        else
        {
            Load(choices[0], _sequence);
        }
    }

    public void ChooseVersion(GlobalSearchResult choice)
    {
        if (_sequence is null || !VersionChoices.Any(v => v.Id == choice.Id)) return;
        VersionChoices.Clear();
        Load(choice, _sequence);
    }

    public void CancelVersionChoice()
    {
        VersionChoices.Clear();
        NoteInteraction();
    }

    [RelayCommand]
    private void TryAnotherVersion() => RequestPlayback(CurrentVersions);

    // ---------- Transport ----------

    [RelayCommand]
    private void PlayPause()
    {
        if (State == PlayerState.Playing) MediaPlayer.SetPause(true);
        else if (State is PlayerState.Ended or PlayerState.Stopped && Current is not null) Load(Current, _sequence!, resume: false, automatic: true);
        else MediaPlayer.SetPause(false);
        NoteInteraction();
    }

    [RelayCommand]
    private void Stop() => RequestClose();

    [RelayCommand]
    private void BackToLibrary() => RequestClose();

    [RelayCommand(CanExecute = nameof(CanGoPrevious))]
    private void Previous() => Navigate(-1);

    [RelayCommand(CanExecute = nameof(CanGoNext))]
    private void Next() => Navigate(1);

    [RelayCommand]
    private void Jump(string seconds) => SeekBy(double.Parse(seconds, CultureInfo.InvariantCulture));

    [RelayCommand]
    private void ResumeSavedPosition()
    {
        if (ResumePosition is not { } seconds || Duration <= 0 || !IsSeekable) return;
        SeekTo(seconds);
        ResumePosition = null;
    }

    [RelayCommand]
    private void StartOver() => ResumePosition = null;

    [RelayCommand]
    private void ToggleRemaining() => ShowRemaining = !ShowRemaining;

    [RelayCommand]
    private void Skip()
    {
        if (ActiveSkip is { } skip) SeekTo(skip.End);
    }

    [RelayCommand]
    private void OpenPreferences() => PlaybackPreferences?.OpenPreferences(this);

    [RelayCommand]
    private void OpenQueue() => EpisodeQueue?.OpenQueue(this);

    [RelayCommand]
    private void EditMarkers() => SkipMarkers?.EditMarkers(this);

    public void SeekBy(double seconds) => SeekTo(Position + seconds);

    public void SeekTo(double seconds)
    {
        if (!IsSeekable || Duration <= 0) return;
        seconds = Math.Clamp(seconds, 0, Math.Max(0, Duration - 1));
        MediaPlayer.Time = (long)(seconds * 1000);
        Position = seconds;
        _stall.Reset(Now);
        NoteInteraction();
    }

    public void SeekToFraction(double fraction) => SeekTo(Math.Clamp(fraction, 0, 1) * Duration);

    public void ChangeVolume(int delta) => Volume = Math.Clamp(Volume + delta, 0, 100);

    partial void OnVolumeChanged(int value)
    {
        var clamped = Math.Clamp(value, 0, 100);
        if (clamped != value)
        {
            Volume = clamped;
            return;
        }
        if (!IsMuted) MediaPlayer.Volume = clamped;
        _preferences.Volume = clamped;
    }

    [RelayCommand]
    private void ToggleMute() => IsMuted = !IsMuted;

    partial void OnIsMutedChanged(bool value) => MediaPlayer.Volume = value ? 0 : Volume;

    public void SetSpeed(float value) => Speed = Math.Clamp(value, 0.25f, 4f);

    partial void OnSpeedChanged(float value)
    {
        MediaPlayer.SetRate(value);
        _preferences.Speed = value;
    }

    partial void OnAutoplayChanged(bool value) => _preferences.Autoplay = value;

    // ---------- Controls visibility and menus ----------

    /// The host view sets this so a new video can close an open options menu.
    public Action? CancelMenu { get; set; }

    /// Fullscreen controls appear on any pointer or key activity and hide after two seconds.
    public void NoteInteraction()
    {
        _controls.Interact(Now);
        if (!ControlsVisible) ControlsVisible = true;
    }

    public void HoverControls(bool hovering)
    {
        _controls.Hovering = hovering;
        NoteInteraction();
    }

    public void SetMenuTracking(bool tracking)
    {
        _controls.MenuTracking = tracking;
        NoteInteraction();
    }

    public bool IsMenuTracking => _controls.MenuTracking;

    // ---------- Fullscreen ----------

    private bool _fullscreenTransition;
    private bool _fullscreenExitRequested;
    private bool _returnAfterFullscreen;
    private DispatcherTimer? _fullscreenTimeout;

    [RelayCommand]
    private void ToggleFullscreen()
    {
        if (Current is null) return;
        if (IsFullscreen)
        {
            ExitFullscreen();
            return;
        }
        if (_fullscreenTransition) return;
        _fullscreenTransition = true;
        ScheduleFullscreenRecovery();
        FullscreenChangeRequested?.Invoke(true);
    }

    [RelayCommand]
    private void ExitFullscreen()
    {
        if (Current is null || _fullscreenExitRequested) return;
        if (IsFullscreen)
        {
            _fullscreenExitRequested = true;
            _fullscreenTransition = true;
            ScheduleFullscreenRecovery();
            FullscreenChangeRequested?.Invoke(false);
        }
        else if (_fullscreenTransition)
        {
            // A request during the enter transition is applied as soon as the window finishes entering.
            _fullscreenExitRequested = true;
        }
        else if (_returnAfterFullscreen)
        {
            FinishClosingPlayer();
        }
    }

    /// The view calls this when its window really entered or left fullscreen.
    public void NotifyFullscreenChanged(bool fullscreen)
    {
        var exitRequested = _fullscreenExitRequested;
        _fullscreenExitRequested = false;
        _fullscreenTransition = false;
        _fullscreenTimeout?.Stop();
        UpdateFullscreen(fullscreen);
        if (fullscreen && (exitRequested || _returnAfterFullscreen)) ExitFullscreen();
        else if (!fullscreen && _returnAfterFullscreen) FinishClosingPlayer();
    }

    private void UpdateFullscreen(bool value)
    {
        IsFullscreen = value;
        _controls.Fullscreen = value;
        _controls.Hovering = false;
        _controls.MenuTracking = false;
        NoteInteraction();
    }

    private void ScheduleFullscreenRecovery()
    {
        _fullscreenTimeout?.Stop();
        _fullscreenTimeout = new DispatcherTimer(TimeSpan.FromSeconds(5), DispatcherPriority.Normal, (_, _) =>
        {
            _fullscreenTimeout?.Stop();
            var exitRequested = _fullscreenExitRequested;
            _fullscreenExitRequested = false;
            _fullscreenTransition = false;
            if (_returnAfterFullscreen)
            {
                if (IsFullscreen)
                {
                    _isClosingPlayer = false;
                    _returnAfterFullscreen = false;
                    ErrorMessage = "The window did not leave fullscreen. Please try Back to Library again.";
                }
                else
                {
                    FinishClosingPlayer();
                }
            }
            else if (exitRequested && IsFullscreen)
            {
                ErrorMessage = "The window did not leave fullscreen. Please try Exit fullscreen again.";
            }
        });
        _fullscreenTimeout.Start();
    }

    /// Window-scoped shortcuts. The view skips text fields, open menus and dialogs, so those keep their own keys.
    public bool HandleShortcut(Key key, KeyModifiers modifiers)
    {
        if (Current is null || _isClosingPlayer || _detached || _controls.MenuTracking || HasVersionChoices) return false;
        if (modifiers.HasFlag(KeyModifiers.Control) || modifiers.HasFlag(KeyModifiers.Meta)) return false;
        var step = modifiers.HasFlag(KeyModifiers.Shift) ? 60 : modifiers.HasFlag(KeyModifiers.Alt) ? 3 : 10;
        switch (key)
        {
            case Key.Space:
                PlayPause();
                break;
            case Key.Left:
                SeekBy(-step);
                break;
            case Key.Right:
                SeekBy(step);
                break;
            case Key.Up:
                ChangeVolume(5);
                break;
            case Key.Down:
                ChangeVolume(-5);
                break;
            case Key.M:
                ToggleMute();
                break;
            case Key.N:
                if (NextCommand.CanExecute(null)) NextCommand.Execute(null);
                break;
            case Key.P:
                if (PreviousCommand.CanExecute(null)) PreviousCommand.Execute(null);
                break;
            case Key.F:
            case Key.F11:
                ToggleFullscreen();
                break;
            case Key.Escape:
                if (!IsFullscreen && !_fullscreenTransition) return false;
                ExitFullscreen();
                break;
            default:
                return false;
        }
        NoteInteraction();
        return true;
    }

    // ---------- Closing ----------

    /// Stop and Back to Library. Playback stops, fullscreen ends first, then the host shows its browser.
    /// The player stays usable: the next Load starts it again.
    private void RequestClose()
    {
        if (_isClosingPlayer || _detached) return;
        _isClosingPlayer = true;
        CancelMenu?.Invoke();
        SavePosition(persist: true);
        StopPlayback();
        if (_fullscreenTransition || IsFullscreen)
        {
            _returnAfterFullscreen = true;
            if (!_fullscreenTransition) ExitFullscreen();
            return;
        }
        FinishClosingPlayer();
    }

    private void FinishClosingPlayer()
    {
        _fullscreenTimeout?.Stop();
        _fullscreenTransition = false;
        _fullscreenExitRequested = false;
        _returnAfterFullscreen = false;
        _pendingLoad = null;
        _detached = true;
        _isClosingPlayer = false;
        Current = null;
        _sequence = null;
        State = PlayerState.Idle;
        ResumePosition = null;
        ActiveSkip = null;
        IsScrubbing = false;
        IsBuffering = false;
        IsSeekable = false;
        ErrorMessage = null;
        Position = 0;
        Duration = 0;
        HasPrevious = false;
        HasNext = false;
        VersionChoices.Clear();
        AudioTracks.Clear();
        SubtitleTracks.Clear();
        Chapters.Clear();
        _completion.Reset();
        _services.IsPlaying = false;
        UpdateFullscreen(false);
        BackToLibraryRequested?.Invoke();
    }

    /// Final shutdown by the owner of the player (window closed, or the app exits).
    public void Close()
    {
        if (IsClosed) return;
        // Synchronous, bounded: the app may close the index right after this.
        if (!_detached) SavePosition(persist: true, wait: TimeSpan.FromSeconds(2));
        IsClosed = true;
        _detached = true;
        _fullscreenTimeout?.Stop();
        _services.IsPlaying = false;
        try
        {
            _preferences.Save();
        }
        catch (IOException)
        {
        }
        Dispose();
    }

    /// Stops playback while the video surface still exists; libVLC must not draw into a destroyed window.
    /// Runs off the UI thread because closing a network stream can block.
    public bool StopPlayback() => _disposed || Task.Run(MediaPlayer.Stop).Wait(TimeSpan.FromSeconds(3));

    private bool _disposed;

    /// Call only after the view has detached this player (VideoView.MediaPlayer = null):
    /// detaching makes a native call, which must not reach a disposed player.
    public void Dispose()
    {
        if (_disposed) return;
        _timer.Stop();
        var media = _media;
        _media = null;
        if (StopPlayback())
        {
            _disposed = true;
            MediaPlayer.Dispose();
            media?.Dispose();
        }
    }

    internal void RaiseNotice(string message) => Notice?.Invoke(message);
}
