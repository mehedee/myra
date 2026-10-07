using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Reactive;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Myra.App.ViewModels;

namespace Myra.App.Views;

public partial class PlayerWindow : Window
{
    private readonly DispatcherTimer _hideControls;
    private WindowState _stateBeforeFullscreen = WindowState.Normal;

    public PlayerWindow()
    {
        InitializeComponent();
        // Tunnel so shortcuts work even after a button or slider took focus.
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        PointerMoved += (_, _) => ShowControlsBriefly();
        FullscreenButton.Click += (_, _) => ToggleFullscreen();
        BackButton.Click += (_, _) => ViewModel?.SeekBy(-10);
        ForwardButton.Click += (_, _) => ViewModel?.SeekBy(10);
        LoadSubtitle.Click += async (_, _) => await PickSubtitleAsync();

        SeekBar.AddHandler(PointerPressedEvent, (_, _) => SetScrubbing(true), RoutingStrategies.Tunnel);
        SeekBar.AddHandler(PointerReleasedEvent, (_, _) => SetScrubbing(false), RoutingStrategies.Tunnel);
        SeekBar.AddHandler(PointerCaptureLostEvent, (_, _) => SetScrubbing(false), RoutingStrategies.Tunnel);

        _hideControls = new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Normal, (_, _) =>
        {
            if (WindowState == WindowState.FullScreen && !Controls.IsPointerOver) Controls.IsVisible = false;
        });
        // The native video surface swallows pointer-move events, so in fullscreen watch the cursor directly.
        _cursorWatch = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) =>
        {
            if (WindowState != WindowState.FullScreen || !TryGetCursor(out var cursor)) return;
            if (cursor != _lastCursor && _lastCursor != default) ShowControlsBriefly();
            _lastCursor = cursor;
        });
        _cursorWatch.Start();
        Opened += (_, _) => AttachVideo(attempt: 0);
        ControlRow.GetObservable(BoundsProperty).Subscribe(new AnonymousObserver<Rect>(bounds => ArrangeControls(bounds.Width)));
    }

    /// VideoView passes its native window to libVLC only when MediaPlayer is assigned, and the native
    /// window exists only after the first layout. So assign it after opening, check, and retry if needed.
    /// Playback starts only once libVLC has the surface; otherwise it opens a separate window.
    private void AttachVideo(int attempt)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (ViewModel is not { IsClosed: false } vm) return;
            Video.MediaPlayer = null;
            Video.MediaPlayer = vm.MediaPlayer;
            if (vm.HasVideoSurface || attempt >= 40) vm.VideoSurfaceReady();
            else DispatcherTimer.RunOnce(() => AttachVideo(attempt + 1), TimeSpan.FromMilliseconds(50));
        }, DispatcherPriority.Background);
    }

    private bool? _wideControls;

    /// Tiling window managers often make the player narrow; then stack the control groups.
    private void ArrangeControls(double width)
    {
        var wide = width >= 900;
        if (_wideControls == wide) return;
        _wideControls = wide;
        Place(OptionsGroup, wide ? 0 : 1, wide ? 0 : 0, wide ? 1 : 3, wide ? HorizontalAlignment.Left : HorizontalAlignment.Center);
        Place(TransportGroup, 0, wide ? 1 : 0, wide ? 1 : 3, HorizontalAlignment.Center);
        Place(VolumeGroup, wide ? 0 : 2, wide ? 2 : 0, wide ? 1 : 3, wide ? HorizontalAlignment.Right : HorizontalAlignment.Center);
    }

    private static void Place(Control control, int row, int column, int span, HorizontalAlignment alignment)
    {
        Grid.SetRow(control, row);
        Grid.SetColumn(control, column);
        Grid.SetColumnSpan(control, span);
        control.HorizontalAlignment = alignment;
    }

    private readonly DispatcherTimer _cursorWatch;
    private PixelPoint _lastCursor;

    private static bool TryGetCursor(out PixelPoint point)
    {
        point = default;
        if (!OperatingSystem.IsWindows() || !GetCursorPos(out var native)) return false;
        point = new PixelPoint(native.X, native.Y);
        return true;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    private PlayerViewModel? ViewModel => DataContext as PlayerViewModel;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (ViewModel is not { } vm) return;
        vm.WaitForVideoSurface();
        vm.CloseRequested += Close;
    }

    private void SetScrubbing(bool scrubbing)
    {
        if (ViewModel is not { } vm || vm.IsScrubbing == scrubbing) return;
        vm.IsScrubbing = scrubbing;
        if (!scrubbing) vm.SeekTo(SeekBar.Value);
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (ViewModel is not { } vm || e.Source is TextBox) return;
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        var alt = e.KeyModifiers.HasFlag(KeyModifiers.Alt);
        var step = shift ? 60 : alt ? 3 : 10;
        switch (e.Key)
        {
            case Key.Space:
                vm.PlayPauseCommand.Execute(null);
                break;
            case Key.Left:
                vm.SeekBy(-step);
                break;
            case Key.Right:
                vm.SeekBy(step);
                break;
            case Key.Up:
                vm.ChangeVolume(5);
                break;
            case Key.Down:
                vm.ChangeVolume(-5);
                break;
            case Key.M:
                vm.ToggleMuteCommand.Execute(null);
                break;
            case Key.N:
                if (vm.NextCommand.CanExecute(null)) vm.NextCommand.Execute(null);
                break;
            case Key.P:
                if (vm.PreviousCommand.CanExecute(null)) vm.PreviousCommand.Execute(null);
                break;
            case Key.F:
            case Key.F11:
                ToggleFullscreen();
                break;
            case Key.Escape when WindowState == WindowState.FullScreen:
                ToggleFullscreen();
                break;
            default:
                return;
        }
        ShowControlsBriefly();
        e.Handled = true;
    }

    private void ToggleFullscreen()
    {
        if (WindowState == WindowState.FullScreen)
        {
            WindowState = _stateBeforeFullscreen;
            Controls.IsVisible = true;
            _hideControls.Stop();
        }
        else
        {
            _stateBeforeFullscreen = WindowState;
            WindowState = WindowState.FullScreen;
            ShowControlsBriefly();
        }
    }

    private void ShowControlsBriefly()
    {
        Controls.IsVisible = true;
        if (WindowState != WindowState.FullScreen) return;
        _hideControls.Stop();
        _hideControls.Start();
    }

    private async Task PickSubtitleAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Load subtitles",
            FileTypeFilter = [new FilePickerFileType("Subtitles") { Patterns = ["*.srt", "*.ass", "*.ssa", "*.vtt", "*.sub"] }],
        });
        if (files.FirstOrDefault()?.TryGetLocalPath() is { } path) ViewModel?.AddSubtitleFile(path);
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        _hideControls.Stop();
        _cursorWatch.Stop();
        // Order matters for libVLC: stop, detach the video surface, then dispose.
        // Shutdown can raise Closing twice (main window, then the app); only the first pass may touch libVLC.
        if (ViewModel is { IsClosed: false } vm)
        {
            vm.CloseRequested -= Close;
            vm.StopPlayback();
            Video.MediaPlayer = null;
            vm.Close();
        }
        base.OnClosing(e);
    }
}
