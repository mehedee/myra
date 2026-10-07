using System.Collections.ObjectModel;
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

/// Embedded libVLC player: resume, English track defaults, folder auto-next, buffering detection.
public sealed partial class PlayerViewModel : ObservableObject, IDisposable
{
    private static LibVLC? _libVlc;
    private readonly AppServices _services;
    private readonly PlayerPersonalState _preferences;
    private readonly PlaybackCompletionState _completion = new();
    private readonly DispatcherTimer _timer;
    private PlaybackSequence? _sequence;
    private Media? _media;
    private bool _userChoseAudio;
    private bool _userChoseSubtitle;
    private bool _updatingTracks;
    private double _lastTime = -1;
    private DateTime _lastProgress = DateTime.UtcNow;
    private DateTime _lastSaved = DateTime.MinValue;

    public PlayerViewModel(AppServices services)
    {
        _services = services;
        _preferences = services.PlayerState;
        _libVlc ??= CreateLibVlc();
        // With libVLC input off, keys and clicks over the video reach Myra's window instead of libVLC's.
        MediaPlayer = new MediaPlayer(_libVlc) { EnableHardwareDecoding = true, EnableKeyInput = false, EnableMouseInput = false };
        MediaPlayer.Playing += (_, _) => OnUi(() => SetState(PlayerState.Playing));
        MediaPlayer.Paused += (_, _) => OnUi(() => SetState(PlayerState.Paused));
        MediaPlayer.Stopped += (_, _) => OnUi(() => SetState(PlayerState.Stopped));
        MediaPlayer.EndReached += (_, _) => OnUi(() => SetState(PlayerState.Ended));
        MediaPlayer.EncounteredError += (_, _) => OnUi(() =>
        {
            SetState(PlayerState.Error);
            ErrorMessage = "This video could not be played. The source may be offline or the format unsupported.";
        });
        MediaPlayer.LengthChanged += (_, e) => OnUi(() => Duration = e.Length / 1000.0);
        MediaPlayer.TimeChanged += (_, e) => OnUi(() => OnTimeChanged(e.Time / 1000.0));
        MediaPlayer.ESAdded += (_, _) => OnUi(RefreshTracks);
        MediaPlayer.ESDeleted += (_, _) => OnUi(RefreshTracks);

        _volume = Math.Clamp(_preferences.Volume, 0, 100);
        _speed = _preferences.Speed is > 0 and <= 4 ? _preferences.Speed : 1;
        _autoplay = _preferences.Autoplay;
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background, (_, _) => Tick());
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
        return new LibVLC(["--no-video-title-show", "--no-snapshot-preview", .. extra]);
    }

    private static void OnUi(Action action) => Dispatcher.UIThread.Post(action);

    public MediaPlayer MediaPlayer { get; }
    public bool IsClosed { get; private set; }
    public ObservableCollection<TrackChoice> AudioTracks { get; } = [];
    public ObservableCollection<TrackChoice> SubtitleTracks { get; } = [];
    public IReadOnlyList<float> Speeds { get; } = [0.5f, 0.75f, 1f, 1.25f, 1.5f, 1.75f, 2f];

    public event Action? CloseRequested;

    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _subtitle = "";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsPlaying))] private PlayerState _state = PlayerState.Idle;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(DurationText))] private double _duration;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(TimeText))] private double _position;
    [ObservableProperty] private bool _isScrubbing;
    [ObservableProperty] private bool _isBuffering;
    [ObservableProperty] private int _volume;
    [ObservableProperty] private bool _isMuted;
    [ObservableProperty] private float _speed;
    [ObservableProperty] private bool _autoplay;
    [ObservableProperty] private bool _repeat;
    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private TrackChoice? _selectedAudio;
    [ObservableProperty] private TrackChoice? _selectedSubtitle;
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(PreviousCommand))] private bool _hasPrevious;
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(NextCommand))] private bool _hasNext;

    public bool IsPlaying => State == PlayerState.Playing;
    public string TimeText => PlayerTime.Format(Position);
    public string DurationText => PlayerTime.Format(Duration);
    public GlobalSearchResult? Current { get; private set; }

    private (GlobalSearchResult Result, PlaybackSequence Sequence, bool Resume)? _pendingLoad;
    private bool _waitingForVideo;

    /// The view calls this before showing: playback must not start until the video surface exists,
    /// otherwise libVLC opens a separate window of its own.
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
            Load(pending.Result, pending.Sequence, pending.Resume);
        }
    }

    public void Load(GlobalSearchResult result, PlaybackSequence sequence, bool resume = true)
    {
        if (_waitingForVideo)
        {
            _pendingLoad = (result, sequence, resume);
            Title = Path.GetFileNameWithoutExtension(result.Entry.Name);
            return;
        }
        SavePosition();
        Current = result;
        _sequence = sequence;
        Title = Path.GetFileNameWithoutExtension(result.Entry.Name);
        Subtitle = result.CategoryName + (result.ParentPath.Length > 0 ? " › " + result.ParentPath.Replace("/", " › ") : "");
        HasPrevious = sequence.Previous(result.Entry.Url) is not null;
        HasNext = sequence.Next(result.Entry.Url) is not null;
        ErrorMessage = null;
        Position = 0;
        Duration = 0;
        _completion.Reset();
        _userChoseAudio = false;
        _userChoseSubtitle = false;
        AudioTracks.Clear();
        SubtitleTracks.Clear();
        _lastTime = -1;
        _lastProgress = DateTime.UtcNow;

        var resumeAt = resume ? _services.Index.PlaybackPosition(result.Entry.Url) : null;
        var old = _media;
        _media = result.Entry.Url.IsFile
            ? new Media(_libVlc!, result.Entry.Url.LocalPath)
            : new Media(_libVlc!, result.Entry.Url);
        _media.AddOption(":network-caching=3000");
        // start-time works before the stream is seekable, unlike setting Time after Playing.
        if (resumeAt is { } seconds) _media.AddOption($":start-time={seconds.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        MediaPlayer.Play(_media);
        old?.Dispose();
        MediaPlayer.SetRate(Speed);
        MediaPlayer.Volume = IsMuted ? 0 : Volume;
        _services.IsPlaying = true;
    }

    private void SetState(PlayerState state)
    {
        State = state;
        if (state == PlayerState.Playing) RefreshTracks();
        if (_completion.Observe(state) && state == PlayerState.Ended) OnFinished();
    }

    private void OnFinished()
    {
        if (Current is null) return;
        _services.Index.SavePlaybackPosition(Current.Entry.Url, Duration, Duration);
        if (Repeat)
        {
            Load(Current, _sequence!, resume: false);
            return;
        }
        if (Autoplay && _sequence?.Next(Current.Entry.Url) is { } next)
        {
            Load(next, _sequence, resume: false);
            return;
        }
        CloseRequested?.Invoke();
    }

    private void OnTimeChanged(double seconds)
    {
        if (!IsScrubbing) Position = seconds;
        if (Math.Abs(seconds - _lastTime) > 0.001) _lastProgress = DateTime.UtcNow;
        _lastTime = seconds;
    }

    private void Tick()
    {
        // A two-second stall while playing means buffering; VLC's own buffering state is often stale.
        var active = State is PlayerState.Playing or PlayerState.Opening or PlayerState.Buffering;
        IsBuffering = active && !IsScrubbing && DateTime.UtcNow - _lastProgress >= TimeSpan.FromSeconds(2);
        if (State == PlayerState.Playing && DateTime.UtcNow - _lastSaved > TimeSpan.FromSeconds(5)) SavePosition();
    }

    private void SavePosition()
    {
        _lastSaved = DateTime.UtcNow;
        if (Current is null || Duration <= 0 || Position <= 0) return;
        var (url, position, duration) = (Current.Entry.Url, Position, Duration);
        _ = Task.Run(() =>
        {
            try
            {
                _services.Index.SavePlaybackPosition(url, position, duration);
            }
            catch (Exception)
            {
                // Resume points are a convenience; never interrupt playback for them.
            }
        });
    }

    // ---------- Tracks ----------

    private void RefreshTracks()
    {
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

            // Apply language preferences as tracks appear, unless the viewer already picked one.
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

    public void AddSubtitleFile(string path)
    {
        _userChoseSubtitle = true;
        MediaPlayer.AddSlave(MediaSlaveType.Subtitle, new Uri(path).AbsoluteUri, true);
    }

    // ---------- Transport ----------

    [RelayCommand]
    private void PlayPause()
    {
        if (State == PlayerState.Playing) MediaPlayer.SetPause(true);
        else if (State is PlayerState.Ended or PlayerState.Stopped && Current is not null) Load(Current, _sequence!, resume: false);
        else MediaPlayer.SetPause(false);
    }

    [RelayCommand]
    private void Stop() => CloseRequested?.Invoke();

    [RelayCommand(CanExecute = nameof(HasPrevious))]
    private void Previous()
    {
        if (Current is not null && _sequence?.Previous(Current.Entry.Url) is { } previous) Load(previous, _sequence);
    }

    [RelayCommand(CanExecute = nameof(HasNext))]
    private void Next()
    {
        if (Current is not null && _sequence?.Next(Current.Entry.Url) is { } next) Load(next, _sequence);
    }

    public void SeekBy(double seconds) => SeekTo(Position + seconds);

    public void SeekTo(double seconds)
    {
        if (Duration <= 0) return;
        seconds = Math.Clamp(seconds, 0, Math.Max(0, Duration - 1));
        MediaPlayer.Time = (long)(seconds * 1000);
        Position = seconds;
        _lastProgress = DateTime.UtcNow;
    }

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

    partial void OnSpeedChanged(float value)
    {
        MediaPlayer.SetRate(value);
        _preferences.Speed = value;
    }

    partial void OnAutoplayChanged(bool value) => _preferences.Autoplay = value;

    public void Close()
    {
        if (IsClosed) return;
        SavePosition();
        IsClosed = true;
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
}
