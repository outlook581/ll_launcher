using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using ll_launcher.Native;
using Forms = System.Windows.Forms;

namespace ll_launcher.Services;

/// <summary>Owns the tray and edge tab. All methods and properties must be used on the window's UI thread.</summary>
public sealed class DesktopIntegration : IDisposable
{
    private readonly Window _window;
    private readonly FrameworkElement _titleBar;
    private readonly DispatcherTimer _hideTimer;
    private readonly DispatcherTimer _revealTimer;
    private readonly DispatcherTimer _layoutTimer;
    private HwndSource? _source;
    private IntPtr _handle;
    private Window? _tab;
    private IntPtr _tabHandle;
    private Forms.NotifyIcon? _tray;
    private Forms.ContextMenuStrip? _trayMenu;
    private System.Drawing.Icon? _trayIcon;
    private bool _initialized;
    private bool _disposed;
    private bool _isPinned;
    private bool _isInteractionActive;
    private bool _moving;
    private DesktopNative.Point? _dragOrigin;
    private double _grabXDip;
    private double _grabYDip;
    private bool _trackingNonClientLeave;
    private bool _relocating;
    private bool _edgeHidden;
    private string _dockEdge = "";

    public event Action? ShowRequested;
    public event Action? ExitRequested;
    public event Action? BackupRequested;
    public event Action? StateChanged;

    public string DockEdge => _dockEdge;

    public bool IsPinned
    {
        get => _isPinned;
        set
        {
            if (_isPinned == value) return;
            _isPinned = value;
            if (value)
            {
                _hideTimer.Stop();
                _window.Topmost = false;
                if (_tab?.IsVisible == true) Reveal(false);
            }
            else ScheduleHide();
            StateChanged?.Invoke();
        }
    }

    /// <summary>Set while editing, showing a context menu, importing, or dragging application items.</summary>
    public bool IsInteractionActive
    {
        get => _isInteractionActive;
        set
        {
            if (_isInteractionActive == value) return;
            _isInteractionActive = value;
            if (value) _hideTimer.Stop();
            else ScheduleHide();
        }
    }

    public DesktopIntegration(Window window, FrameworkElement titleBar)
    {
        _window = window ?? throw new ArgumentNullException(nameof(window));
        _titleBar = titleBar ?? throw new ArgumentNullException(nameof(titleBar));
        _hideTimer = MakeTimer(450, AutoHide);
        _revealTimer = MakeTimer(150, () => { if (_tab?.IsMouseOver == true) Reveal(false); });
        _layoutTimer = MakeTimer(120, ReconcileDisplay);
    }

    private DispatcherTimer MakeTimer(int milliseconds, Action callback)
    {
        var timer = new DispatcherTimer(DispatcherPriority.Background, _window.Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(milliseconds)
        };
        timer.Tick += (_, _) => { timer.Stop(); if (!_disposed) callback(); };
        return timer;
    }

    public void Initialize()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(DesktopIntegration));
        if (_initialized) return;
        _initialized = true;
        _window.MouseEnter += WindowMouseEnter;
        _window.MouseLeave += WindowMouseLeave;
        _window.Deactivated += WindowDeactivated;
        _window.SizeChanged += WindowSizeChanged;
        _window.StateChanged += WindowStateChanged;
        _window.IsVisibleChanged += WindowVisibilityChanged;
        _window.Closed += WindowClosed;
        _handle = new WindowInteropHelper(_window).EnsureHandle();
        _source = HwndSource.FromHwnd(_handle);
        _source?.AddHook(WindowMessage);

        _trayMenu = new Forms.ContextMenuStrip();
        _trayMenu.Items.Add("显示窗口", null, (_, _) => RequestShow());
        _trayMenu.Items.Add("备份数据…", null, (_, _) => BackupRequested?.Invoke());
        _trayMenu.Items.Add(new Forms.ToolStripSeparator());
        _trayMenu.Items.Add("退出", null, (_, _) => ExitRequested?.Invoke());
        try
        {
            _trayIcon = new System.Drawing.Icon(Path.Combine(App.InstallDirectory, "Assets", "Launcher.ico"), Forms.SystemInformation.SmallIconSize);
        }
        catch (Exception error) when (error is ArgumentException or System.IO.IOException or Win32Exception) { }
        _trayIcon ??= (System.Drawing.Icon)System.Drawing.SystemIcons.Application.Clone();
        _tray = new Forms.NotifyIcon
        {
            Text = "栖点",
            Icon = _trayIcon,
            ContextMenuStrip = _trayMenu,
            Visible = true
        };
        _tray.MouseClick += TrayMouseClick;
        QueueLayout();
    }

    public void ShowWindow() => Reveal(true);

    public void HideWindow()
    {
        if (_disposed) return;
        _hideTimer.Stop();
        _revealTimer.Stop();
        _edgeHidden = false;
        _tab?.Hide();
        _window.Hide();
    }

    private bool BeginTitleDrag(DesktopNative.Point point)
    {
        if (_disposed || IsPinned || _handle == IntPtr.Zero || !DesktopNative.GetWindowRect(_handle, out var rect))
            return false;

        var local = _titleBar.PointFromScreen(new System.Windows.Point(point.X, point.Y));
        if (!new System.Windows.Rect(_titleBar.RenderSize).Contains(local)) return false;
        var hit = VisualTreeHelper.HitTest(_titleBar, local)?.VisualHit;
        while (hit != null && hit != _titleBar)
        {
            if (hit is System.Windows.Controls.Primitives.ButtonBase) return false;
            hit = VisualTreeHelper.GetParent(hit);
        }
        if (hit == null) return false;

        // Capture the title gesture ourselves. A native caption move enters the Windows Snap/maximize loop.
        DesktopNative.SetCapture(_handle);
        if (DesktopNative.GetCapture() != _handle) return false;
        var scale = DpiScale(_handle);
        _grabXDip = (point.X - rect.Left) / scale;
        _grabYDip = (point.Y - rect.Top) / scale;
        _dragOrigin = point;
        _hideTimer.Stop();
        return true;
    }

    private void MoveWithPointer(DesktopNative.Point point)
    {
        if (_dragOrigin is not { } origin) return;
        var scale = DpiScale(_handle);
        if (!_moving)
        {
            if (Math.Abs(point.X - origin.X) < SystemParameters.MinimumHorizontalDragDistance * scale
                && Math.Abs(point.Y - origin.Y) < SystemParameters.MinimumVerticalDragDistance * scale) return;
            _moving = true;
            SetDockEdge("");
        }

        var x = point.X - (int)Math.Round(_grabXDip * scale);
        var y = point.Y - (int)Math.Round(_grabYDip * scale);
        DesktopNative.SetWindowPos(_handle, IntPtr.Zero, x, y, 0, 0,
            DesktopNative.SwpNoActivate | DesktopNative.SwpNoZOrder | DesktopNative.SwpNoSize);
    }

    private void EndDrag(DesktopNative.Point? releasePoint)
    {
        if (!_dragOrigin.HasValue) return;
        if (releasePoint.HasValue) MoveWithPointer(releasePoint.Value);
        bool moved = _moving;
        _dragOrigin = null;
        _moving = false;
        if (DesktopNative.GetCapture() == _handle) DesktopNative.ReleaseCapture();
        if (moved && releasePoint.HasValue) SnapAfterDrag(releasePoint.Value);
        else if (moved) ReconcileDisplay();
        ScheduleHide();
    }

    private DesktopNative.Point MouseMessagePosition(IntPtr parameter)
    {
        long packed = parameter.ToInt64();
        var point = new DesktopNative.Point { X = (short)(packed & 0xffff), Y = (short)((packed >> 16) & 0xffff) };
        DesktopNative.ClientToScreen(_handle, ref point);
        return point;
    }

    public void RestoreDock(string edge)
    {
        edge = (edge ?? "").Trim().ToLowerInvariant();
        if (edge is not ("left" or "right" or "top" or "bottom")) edge = "";
        SetDockEdge(edge);
        if (_initialized) { ReconcileDisplay(); ScheduleHide(); }
    }

    public void Detach()
    {
        _hideTimer.Stop();
        _revealTimer.Stop();
        SetDockEdge("");
        _tab?.Hide();
    }

    private void RequestShow()
    {
        if (_disposed) return;
        Reveal(true);
        ShowRequested?.Invoke();
    }

    private void Reveal(bool activate)
    {
        if (_disposed) return;
        _hideTimer.Stop();
        _revealTimer.Stop();
        bool revealingFromEdge = _edgeHidden && !IsPinned;
        _edgeHidden = false;
        _tab?.Hide();
        bool oldShowActivated = _window.ShowActivated;
        try
        {
            _window.ShowActivated = activate && !revealingFromEdge;
            if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
            if (!_window.IsVisible) _window.Show();
        }
        finally { _window.ShowActivated = oldShowActivated; }
        ReconcileDisplay();
        if (revealingFromEdge)
        {
            // Raise after Show() so the panel takes the edge tab's place above fullscreen windows.
            _window.Topmost = true;
            // The pointer can leave before the newly shown window receives its first mouse event.
            if (!activate) ScheduleHide();
        }
        if (activate) _window.Activate();
    }

    private void WindowStateChanged(object? sender, EventArgs e)
    {
        if (_window.WindowState == WindowState.Minimized) _window.Topmost = false;
        if (_window.WindowState == WindowState.Maximized) _window.WindowState = WindowState.Normal;
        if (_window.WindowState == WindowState.Normal) QueueLayout();
    }

    private void WindowMouseEnter(object sender, MouseEventArgs e) => _hideTimer.Stop();
    private void WindowMouseLeave(object sender, MouseEventArgs e) => ScheduleHide();
    private void WindowDeactivated(object? sender, EventArgs e) => ScheduleHide();
    private void WindowSizeChanged(object sender, SizeChangedEventArgs e) { if (!_relocating) QueueLayout(); }
    private void WindowClosed(object? sender, EventArgs e) => Dispose();

    private void WindowVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_disposed) return;
        if (_window.IsVisible) _tab?.Hide();
        else
        {
            _hideTimer.Stop();
            _window.Topmost = false;
            if (_edgeHidden && _dockEdge.Length != 0) ShowTab();
        }
    }

    private void TrayMouseClick(object? sender, Forms.MouseEventArgs e)
    {
        if (e.Button == Forms.MouseButtons.Left) RequestShow();
    }

    private bool CannotAutoHide()
    {
        if (IsPinned || IsInteractionActive || _dragOrigin.HasValue || _moving ||
            _window.WindowState == WindowState.Minimized || Mouse.Captured is not null) return true;
        foreach (Window owned in _window.OwnedWindows)
            if (owned.IsVisible) return true;
        return false;
    }

    private void ScheduleHide()
    {
        _hideTimer.Stop();
        if (_disposed || !_window.IsVisible || _dockEdge.Length == 0 || CannotAutoHide()) return;
        _hideTimer.Start();
    }

    private void AutoHide()
    {
        if (!_window.IsVisible || _dockEdge.Length == 0 || CannotAutoHide() || PointerWithin(_handle)) return;
        FoldToEdge();
    }

    private void FoldToEdge()
    {
        _edgeHidden = true;
        _window.Hide();
    }

    private static bool PointerWithin(IntPtr handle) => handle != IntPtr.Zero
        && DesktopNative.GetCursorPos(out var pointer)
        && DesktopNative.GetWindowRect(handle, out var rectangle)
        && rectangle.Contains(pointer);

    private IntPtr WindowMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (_disposed) return IntPtr.Zero;
        switch (message)
        {
            case DesktopNative.WmLButtonDown:
                handled = BeginTitleDrag(MouseMessagePosition(lParam));
                break;
            case DesktopNative.WmMouseMove when _dragOrigin.HasValue:
                if ((wParam.ToInt64() & 1) != 0) MoveWithPointer(MouseMessagePosition(lParam));
                else EndDrag(null);
                handled = true;
                break;
            case DesktopNative.WmLButtonUp when _dragOrigin.HasValue:
                EndDrag(MouseMessagePosition(lParam));
                handled = true;
                break;
            case DesktopNative.WmCaptureChanged:
            case DesktopNative.WmCancelMode:
                EndDrag(null);
                break;
            case DesktopNative.WmNcMouseMove when !_trackingNonClientLeave && _dockEdge.Length != 0:
                var tracking = new DesktopNative.MouseTracking
                {
                    Size = Marshal.SizeOf<DesktopNative.MouseTracking>(),
                    Flags = 0x12, // TME_LEAVE | TME_NONCLIENT: pointer may leave via the resize border.
                    Window = _handle
                };
                _trackingNonClientLeave = DesktopNative.TrackMouseEvent(ref tracking);
                break;
            case DesktopNative.WmNcMouseLeave:
                _trackingNonClientLeave = false;
                ScheduleHide();
                break;
            case DesktopNative.WmSysCommand when (wParam.ToInt64() & 0xFFF0) == DesktopNative.ScMaximize:
            case DesktopNative.WmNcLButtonDoubleClick:
                handled = true;
                break;
            case DesktopNative.WmSysCommand when IsPinned && (wParam.ToInt64() & 0xFFF0) == DesktopNative.ScMove:
            case DesktopNative.WmNcLButtonDown when IsPinned && wParam.ToInt64() == DesktopNative.HtCaption:
                handled = true;
                break;
            case DesktopNative.WmEnterSizeMove:
                _moving = true;
                _hideTimer.Stop();
                break;
            case DesktopNative.WmExitSizeMove:
                _moving = false;
                QueueLayout();
                StateChanged?.Invoke();
                break;
            case DesktopNative.WmDisplayChange:
            case DesktopNative.WmSettingChange:
            case DesktopNative.WmDpiChanged:
                QueueLayout();
                break;
        }
        return IntPtr.Zero;
    }

    private void QueueLayout()
    {
        if (_disposed) return;
        _layoutTimer.Stop();
        _layoutTimer.Start();
    }

    private bool TryGetGeometry(out DesktopNative.Rect rectangle, out DesktopNative.Rect work)
    {
        rectangle = default;
        work = default;
        if (_window.WindowState == WindowState.Minimized || _handle == IntPtr.Zero ||
            !DesktopNative.GetWindowRect(_handle, out rectangle)) return false;
        var monitor = DesktopNative.MonitorFromWindow(_handle, 2); // MONITOR_DEFAULTTONEAREST
        var info = new DesktopNative.MonitorInfo { Size = Marshal.SizeOf<DesktopNative.MonitorInfo>() };
        if (monitor == IntPtr.Zero || !DesktopNative.GetMonitorInfo(monitor, ref info)) return false;
        work = info.Work;
        return work.Width > 0 && work.Height > 0 && rectangle.Width > 0 && rectangle.Height > 0;
    }

    private void SnapAfterDrag(DesktopNative.Point releasePoint)
    {
        if (_disposed || IsPinned || !TryGetGeometry(out var rectangle, out var work)) return;
        var info = new DesktopNative.MonitorInfo { Size = Marshal.SizeOf<DesktopNative.MonitorInfo>() };
        var monitor = DesktopNative.MonitorFromPoint(releasePoint, 2);
        if (monitor != IntPtr.Zero && DesktopNative.GetMonitorInfo(monitor, ref info))
            work = info.Work;
        int x = Math.Clamp(rectangle.Left, work.Left, Math.Max(work.Left, work.Right - rectangle.Width));
        int y = Math.Clamp(rectangle.Top, work.Top, Math.Max(work.Top, work.Bottom - rectangle.Height));
        var candidates = new (string Edge, int Distance)[]
        {
            ("left", Math.Abs(x - work.Left)),
            ("right", Math.Abs(x + rectangle.Width - work.Right)),
            ("top", Math.Abs(y - work.Top)),
            ("bottom", Math.Abs(y + rectangle.Height - work.Bottom))
        };
        var best = candidates[0];
        foreach (var candidate in candidates) if (candidate.Distance < best.Distance) best = candidate;
        double scale = DpiScale(_handle);
        SetDockEdge(best.Distance <= 18 * scale ? best.Edge : "");
        if (_dockEdge.Length != 0)
            DesktopNative.SetWindowPos(_handle, IntPtr.Zero, x, y, 0, 0, DesktopNative.SwpNoActivate | DesktopNative.SwpNoZOrder | DesktopNative.SwpNoSize);
        ReconcileDisplay();
        StateChanged?.Invoke();
        ScheduleHide();
    }

    private void SetDockEdge(string edge)
    {
        if (_dockEdge == edge) return;
        _dockEdge = edge;
        if (edge.Length == 0)
        {
            _window.Topmost = false;
            _tab?.Hide();
        }
        StateChanged?.Invoke();
    }

    private static double DpiScale(IntPtr handle)
    {
        uint dpi = DesktopNative.GetDpiForWindow(handle);
        return dpi == 0 ? 1 : dpi / 96d;
    }

    private void ReconcileDisplay()
    {
        if (_disposed || _moving || _relocating || !TryGetGeometry(out var rectangle, out var work)) return;
        _relocating = true;
        try
        {
            // Fit the actual monitor work area. On extremely small screens, the UI's MinWidth/MinHeight still apply.
            double scale = DpiScale(_handle);
            if (rectangle.Width > work.Width && _window.MinWidth <= work.Width / scale) _window.Width = work.Width / scale;
            if (rectangle.Height > work.Height && _window.MinHeight <= work.Height / scale) _window.Height = work.Height / scale;
            if (!DesktopNative.GetWindowRect(_handle, out rectangle)) return;
            int x = Math.Clamp(rectangle.Left, work.Left, Math.Max(work.Left, work.Right - rectangle.Width));
            int y = Math.Clamp(rectangle.Top, work.Top, Math.Max(work.Top, work.Bottom - rectangle.Height));
            switch (_dockEdge)
            {
                case "left": x = work.Left; break;
                case "right": x = Math.Max(work.Left, work.Right - rectangle.Width); break;
                case "top": y = work.Top; break;
                case "bottom": y = Math.Max(work.Top, work.Bottom - rectangle.Height); break;
            }
            if (x != rectangle.Left || y != rectangle.Top)
            {
                DesktopNative.SetWindowPos(_handle, IntPtr.Zero, x, y, 0, 0,
                    DesktopNative.SwpNoActivate | DesktopNative.SwpNoZOrder | DesktopNative.SwpNoSize);
                StateChanged?.Invoke();
            }
            if (_edgeHidden && !_window.IsVisible && _dockEdge.Length != 0) ShowTab();
        }
        finally { _relocating = false; }
    }

    private void EnsureTab()
    {
        if (_tab is not null) return;
        _tab = new Window
        {
            Title = "栖点边缘入口",
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ShowInTaskbar = false,
            ShowActivated = false,
            Topmost = true,
            Width = 26,
            Height = 50,
            ToolTip = "栖点"
        };
        _tab.MouseEnter += (_, _) => { _revealTimer.Stop(); _revealTimer.Start(); };
        _tab.MouseLeave += (_, _) => _revealTimer.Stop();
        _tab.MouseLeftButtonDown += (_, e) => { e.Handled = true; RequestShow(); };
        _tabHandle = new WindowInteropHelper(_tab).EnsureHandle();
        long style = DesktopNative.GetWindowLongPtr(_tabHandle, -20).ToInt64();
        // Independent tool window: hiding the main window must not hide its edge entry. Never activate on hover.
        DesktopNative.SetWindowLongPtr(_tabHandle, -20, new IntPtr((style | 0x08000000 | 0x00000080) & ~0x00040000));
    }

    private void ShowTab()
    {
        if (_disposed || _dockEdge.Length == 0 || !TryGetGeometry(out var rectangle, out var work)) return;
        EnsureTab();
        bool horizontal = _dockEdge is "top" or "bottom";
        _tab!.Width = horizontal ? 50 : 26;
        _tab.Height = horizontal ? 26 : 50;
        var panel = new StackPanel
        {
            Orientation = horizontal ? Orientation.Horizontal : Orientation.Vertical,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        panel.Children.Add(new Image
        {
            Source = Application.Current.FindResource("BrandIcon") as ImageSource,
            Width = 20,
            Height = 20,
            Stretch = Stretch.Uniform,
            Margin = new Thickness(2)
        });
        panel.Children.Add(new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse(_dockEdge switch
            {
                "left" => "M 0 0 L 4 4 L 0 8",
                "right" => "M 4 0 L 0 4 L 4 8",
                "top" => "M 0 0 L 4 4 L 8 0",
                _ => "M 0 4 L 4 0 L 8 4"
            }),
            Stroke = (Brush)_window.FindResource("TextBrush"),
            StrokeThickness = 1.5,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4)
        });
        _tab.Content = new Border
        {
            Background = (Brush)_window.FindResource("SurfaceBrush"),
            BorderBrush = (Brush)_window.FindResource("BorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Child = panel
        };
        _tab.Show();
        // Move to the target monitor first so WPF receives the correct per-monitor DPI, then size in physical pixels.
        double scale = DpiScale(_handle);
        int width = (int)Math.Round(_tab.Width * scale);
        int height = (int)Math.Round(_tab.Height * scale);
        int x = Math.Clamp(rectangle.Left + (rectangle.Width - width) / 2, work.Left, Math.Max(work.Left, work.Right - width));
        int y = Math.Clamp(rectangle.Top + (rectangle.Height - height) / 2, work.Top, Math.Max(work.Top, work.Bottom - height));
        switch (_dockEdge)
        {
            case "left": x = work.Left; break;
            case "right": x = work.Right - width; break;
            case "top": y = work.Top; break;
            case "bottom": y = work.Bottom - height; break;
        }
        DesktopNative.SetWindowPos(_tabHandle, new IntPtr(-1), x, y, width, height, DesktopNative.SwpNoActivate);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _hideTimer.Stop();
        _revealTimer.Stop();
        _layoutTimer.Stop();
        _source?.RemoveHook(WindowMessage);
        _source = null;
        _window.MouseEnter -= WindowMouseEnter;
        _window.MouseLeave -= WindowMouseLeave;
        _window.Deactivated -= WindowDeactivated;
        _window.SizeChanged -= WindowSizeChanged;
        _window.StateChanged -= WindowStateChanged;
        EndDrag(null);
        _window.IsVisibleChanged -= WindowVisibilityChanged;
        _window.Closed -= WindowClosed;
        if (_tray is not null) { _tray.Visible = false; _tray.MouseClick -= TrayMouseClick; _tray.Dispose(); }
        _trayMenu?.Dispose();
        _trayIcon?.Dispose();
        _tab?.Close();
        _tab = null;
        _tabHandle = IntPtr.Zero;
    }
}
