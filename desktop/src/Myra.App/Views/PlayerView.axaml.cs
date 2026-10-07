using System.ComponentModel;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Avalonia.Reactive;
using Avalonia.Threading;
using Myra.App.ViewModels;

namespace Myra.App.Views;

/// Reusable player surface: header, video, options menus, transport and volume.
/// Put it in any host (the main window, or PlayerWindow) and set DataContext to a PlayerViewModel.
/// Hosts handle PlayerViewModel.BackToLibraryRequested; the view handles fullscreen, keys and menus itself.
public partial class PlayerView : UserControl
{
    private readonly DispatcherTimer _cursorWatch;
    private readonly Dictionary<Control, object> _tooltips = [];
    private PlayerViewModel? _vm;
    private TopLevel? _topLevel;
    private Window? _window;
    private IDisposable? _windowStateSubscription;
    private WindowState _stateBeforeFullscreen = WindowState.Normal;
    private bool _windowIsFullscreen;
    private ContextMenu? _activeMenu;
    private bool? _wideControls;
    private bool _attachScheduled;
    private bool _attached;
    private bool _lastEffectivelyVisible;
    private PixelPoint _lastCursor;

    public PlayerView()
    {
        InitializeComponent();
        AudioMenuButton.Click += (_, _) => OpenMenu(AudioMenuButton, PlayerMenuKind.Audio);
        SubtitleMenuButton.Click += (_, _) => OpenMenu(SubtitleMenuButton, PlayerMenuKind.Subtitles);
        SpeedMenuButton.Click += (_, _) => OpenMenu(SpeedMenuButton, PlayerMenuKind.Speed);
        VideoMenuButton.Click += (_, _) => OpenMenu(VideoMenuButton, PlayerMenuKind.Video);
        ChapterMenuButton.Click += (_, _) => OpenMenu(ChapterMenuButton, PlayerMenuKind.Chapters);

        SeekBar.ScrubStarted += () =>
        {
            if (_vm is { } vm) vm.IsScrubbing = true;
        };
        SeekBar.ScrubChanged += value =>
        {
            if (_vm is { } vm) vm.ScrubPosition = value;
        };
        SeekBar.ScrubCommitted += value =>
        {
            if (_vm is not { } vm) return;
            vm.SeekToFraction(value);
            vm.IsScrubbing = false;
        };

        // Tunnel so shortcuts work even after a button or slider took focus.
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        OverlayRoot.AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        foreach (var surface in new Control[] { this, OverlayRoot })
        {
            surface.AddHandler(PointerMovedEvent, (_, _) => _vm?.NoteInteraction(), RoutingStrategies.Tunnel);
            surface.AddHandler(PointerPressedEvent, (_, _) => _vm?.NoteInteraction(), RoutingStrategies.Tunnel);
            surface.AddHandler(PointerWheelChangedEvent, (_, _) => _vm?.NoteInteraction(), RoutingStrategies.Tunnel);
        }
        foreach (var chrome in new Control[] { Header, Controls })
        {
            chrome.PointerEntered += (_, _) => _vm?.HoverControls(true);
            chrome.PointerExited += (_, _) => _vm?.HoverControls(false);
        }
        // Double-clicking the video toggles fullscreen.
        OverlayRoot.DoubleTapped += (_, e) =>
        {
            if (e.Source == OverlayRoot) _vm?.ToggleFullscreenCommand.Execute(null);
        };
        Video.DoubleTapped += (_, _) => _vm?.ToggleFullscreenCommand.Execute(null);

        // The native video surface swallows pointer-move events, so in fullscreen watch the cursor directly.
        _cursorWatch = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) =>
        {
            if (_vm is not { IsFullscreen: true } vm || !TryGetCursor(out var cursor)) return;
            if (cursor != _lastCursor && _lastCursor != default) vm.NoteInteraction();
            _lastCursor = cursor;
        });
        ControlRow.GetObservable(BoundsProperty).Subscribe(new AnonymousObserver<Rect>(bounds => ArrangeControls(bounds.Width)));
        // Visibility can change through any ancestor (the host hides the whole player), so watch it per layout pass.
        LayoutUpdated += (_, _) =>
        {
            if (_lastEffectivelyVisible == IsEffectivelyVisible) return;
            _lastEffectivelyVisible = IsEffectivelyVisible;
            SyncVideoSurface();
        };
    }

    // ---------- View model binding ----------

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is { } old)
        {
            old.PropertyChanged -= OnViewModelChanged;
            old.FullscreenChangeRequested -= OnFullscreenRequested;
            old.CancelMenu = null;
        }
        _vm = DataContext as PlayerViewModel;
        // The overlay is a window of its own, so it does not inherit this control's DataContext.
        Header.DataContext = _vm;
        Controls.DataContext = _vm;
        OverlayRoot.DataContext = _vm;
        if (_vm is not { } vm) return;
        vm.PropertyChanged += OnViewModelChanged;
        vm.FullscreenChangeRequested += OnFullscreenRequested;
        vm.CancelMenu = () => _activeMenu?.Close();
        vm.WaitForVideoSurface();
        ApplyFullscreenLayout(vm.IsFullscreen);
        SyncVideoSurface();
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_vm is not { } vm) return;
        switch (e.PropertyName)
        {
            case nameof(PlayerViewModel.IsFullscreen):
                ApplyFullscreenLayout(vm.IsFullscreen);
                break;
            case nameof(PlayerViewModel.ControlsVisible):
                FullscreenChrome.IsVisible = vm.IsFullscreen && vm.ControlsVisible;
                break;
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        _topLevel = TopLevel.GetTopLevel(this);
        _topLevel?.AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        _window = _topLevel as Window;
        if (_window is not null)
        {
            _windowIsFullscreen = _window.WindowState == WindowState.FullScreen;
            _windowStateSubscription = _window.GetObservable(Window.WindowStateProperty)
                .Subscribe(new AnonymousObserver<WindowState>(OnWindowStateChanged));
        }
        _cursorWatch.Start();
        SyncVideoSurface();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _attached = false;
        _cursorWatch.Stop();
        _topLevel?.RemoveHandler(KeyDownEvent, OnKeyDown);
        _windowStateSubscription?.Dispose();
        _windowStateSubscription = null;
        _topLevel = null;
        _window = null;
        ReleaseVideo();
        base.OnDetachedFromVisualTree(e);
    }

    // ---------- Video surface ----------

    /// VideoView passes its native window to libVLC only when MediaPlayer is assigned, and the native
    /// window exists only after the first layout. So assign it once visible, check, and retry if needed.
    /// Playback starts only once libVLC has the surface; otherwise it opens a separate window.
    private void SyncVideoSurface()
    {
        if (_vm is not { IsClosed: false } vm) return;
        if (!_attached || !IsEffectivelyVisible)
        {
            if (Video.MediaPlayer is not null && vm.Current is null)
            {
                vm.WaitForVideoSurface();
                Video.MediaPlayer = null;
            }
            return;
        }
        if (_attachScheduled) return;
        _attachScheduled = true;
        AttachVideo(attempt: 0);
    }

    private void AttachVideo(int attempt)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_vm is not { IsClosed: false } vm || !IsEffectivelyVisible || !_attached)
            {
                _attachScheduled = false;
                return;
            }
            Video.MediaPlayer = null;
            Video.MediaPlayer = vm.MediaPlayer;
            if (vm.HasVideoSurface || attempt >= 40)
            {
                _attachScheduled = false;
                vm.VideoSurfaceReady();
            }
            else
            {
                DispatcherTimer.RunOnce(() => AttachVideo(attempt + 1), TimeSpan.FromMilliseconds(50));
            }
        }, DispatcherPriority.Background);
    }

    /// Detaches the video surface from libVLC. A window host calls this after stopping playback and before
    /// the player is disposed: detaching makes a native call, which must not reach a disposed player.
    public void ReleaseVideo()
    {
        if (_vm is { IsClosed: false } vm) vm.WaitForVideoSurface();
        Video.MediaPlayer = null;
        _attachScheduled = false;
    }

    // ---------- Fullscreen ----------

    private void OnFullscreenRequested(bool enter)
    {
        if (_window is null)
        {
            _vm?.NotifyFullscreenChanged(false);
            return;
        }
        if (enter)
        {
            if (_window.WindowState != WindowState.FullScreen) _stateBeforeFullscreen = _window.WindowState;
            _window.WindowState = WindowState.FullScreen;
        }
        else
        {
            _window.WindowState = _stateBeforeFullscreen == WindowState.FullScreen ? WindowState.Normal : _stateBeforeFullscreen;
        }
    }

    private void OnWindowStateChanged(WindowState state)
    {
        var fullscreen = state == WindowState.FullScreen;
        if (fullscreen == _windowIsFullscreen) return;
        _windowIsFullscreen = fullscreen;
        _vm?.NotifyFullscreenChanged(fullscreen);
    }

    /// Fullscreen moves the header and controls over the video and hides them after two seconds of inactivity.
    private void ApplyFullscreenLayout(bool fullscreen)
    {
        SetTooltipsEnabled(!fullscreen);
        if (fullscreen)
        {
            Move(TopSlot, OverlayTop);
            Move(BottomSlot, OverlayBottom);
        }
        else
        {
            Move(OverlayTop, TopSlot);
            Move(OverlayBottom, BottomSlot);
        }
        FullscreenChrome.IsVisible = fullscreen && (_vm?.ControlsVisible ?? true);
    }

    private static void Move(ContentControl from, ContentControl to)
    {
        if (from.Content is not Control content) return;
        from.Content = null;
        to.Content = content;
    }

    /// Hover tooltips are off only in fullscreen; accessibility names stay (AutomationProperties.Name).
    private void SetTooltipsEnabled(bool enabled)
    {
        SeekBar.AllowTooltips = enabled;
        if (enabled)
        {
            foreach (var (control, tip) in _tooltips) ToolTip.SetTip(control, tip);
            _tooltips.Clear();
            return;
        }
        foreach (var root in new Control[] { Header, Controls })
            foreach (var control in root.GetSelfAndLogicalDescendants().OfType<Control>())
                if (ToolTip.GetTip(control) is { } tip && !_tooltips.ContainsKey(control))
                {
                    _tooltips[control] = tip;
                    ToolTip.SetTip(control, null);
                }
        ToolTip.SetTip(SeekBar, null);
    }

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

    // ---------- Keyboard ----------

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        // Text fields, open menus and dialogs keep their own keys.
        if (e.Handled || _vm is not { } vm || !IsEffectivelyVisible || e.Source is TextBox || e.Source is MenuItem || _activeMenu is { IsOpen: true }) return;
        if (vm.HandleShortcut(e.Key, e.KeyModifiers)) e.Handled = true;
    }

    // ---------- Menus ----------

    private void OpenMenu(Button button, PlayerMenuKind kind)
    {
        if (_vm is not { Current: not null } vm || _activeMenu is { IsOpen: true }) return;
        var menu = PlayerMenus.Build(vm, kind, () => _ = LoadSubtitleFileAsync());
        _activeMenu = menu;
        vm.SetMenuTracking(true);
        menu.Closed += (_, _) =>
        {
            if (_activeMenu == menu) _activeMenu = null;
            vm.SetMenuTracking(false);
            // Return focus to the player so shortcuts keep working after the menu closes.
            Focus();
        };
        menu.Open(button);
    }

    private async Task LoadSubtitleFileAsync()
    {
        if (TopLevel.GetTopLevel(this) is not { } top || _vm is not { } vm) return;
        vm.SetMenuTracking(true);
        try
        {
            var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Load subtitles",
                FileTypeFilter = [new FilePickerFileType("Subtitles") { Patterns = ["*.srt", "*.ass", "*.ssa", "*.vtt", "*.sub"] }],
            });
            if (files.FirstOrDefault()?.TryGetLocalPath() is { } path) vm.LoadSubtitleFile(path);
        }
        finally
        {
            vm.SetMenuTracking(false);
            Focus();
        }
    }

    // ---------- Layout ----------

    /// Tiling window managers often make the player narrow; then stack the control groups.
    private void ArrangeControls(double width)
    {
        var wide = width >= 900;
        if (_wideControls == wide) return;
        _wideControls = wide;
        Place(OptionsGroup, wide ? 0 : 1, 0, wide ? 1 : 3, wide ? HorizontalAlignment.Left : HorizontalAlignment.Center);
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
}
