using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Interop;
using System.Windows.Input;
using DesktopPet.Monitoring;
using W = System.Windows.Controls;
using Media = System.Windows.Media;
using Forms = System.Windows.Forms;

namespace DesktopPet;

// An interactive Pet-owned display. Automatic placement uses verified blank taskbar space;
// a user drag overrides placement without modifying Explorer windows or layout.
internal sealed class MonitorTaskbarWindow : Window
{
    private readonly W.StackPanel _rows = new() { Orientation = W.Orientation.Horizontal };
    private readonly W.Border _frame;
    private bool _locating;
    private long _locatedAt;
    private bool _closed;
    private readonly Dictionary<string, double> _previous = [];
    private Task<List<NativeRect>>? _controlRead;
    private bool _pressed, _dragging;
    private NativePoint _pressPoint;
    private NativeRect _pressBounds;
    internal int ManualRefreshCount { get; private set; }
    public string PlacementStatus { get; private set; } = "等待定位任务栏";
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint FindWindow(string? cls, string? text);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint FindWindowEx(nint parent, nint after, string? cls, string? text);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hwnd, out NativeRect rect);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern int SetWindowLong(nint hwnd, int index, int value);
    private readonly MonitorService _service;
    public MonitorTaskbarWindow(MonitorService service, Action? openSettings = null)
    {
        _service = service; Title = "Pet · taskbar monitor"; Width = 300; Height = 30;
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize; ShowActivated = false; ShowInTaskbar = false;
        // Native layered-window hit testing ignores alpha-zero pixels before WPF sees mouse events.
        // A separate 1/255-alpha surface keeps the entire strip interactive even when its theme fill is zero.
        AllowsTransparency = true; Topmost = true; Background = new Media.SolidColorBrush(Media.Color.FromArgb(1, 0, 0, 0));
        _frame = new W.Border { Child = _rows, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), Padding = new Thickness(6, 2, 6, 2) };
        Content = _frame;
        var menu = new W.ContextMenu(); var settings = new W.MenuItem { Header = "监控设置" };
        settings.Click += (_, _) => openSettings?.Invoke(); menu.Items.Add(settings); ContextMenu = menu;
        ToolTip = MonitorLocalizer.Language.Text(service.Config.Language, "拖动调整位置 · 双击刷新 · 右键设置");
        PreviewMouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            if (e.ClickCount == 2) { EndDrag(); ManualRefresh(); return; }
            if (!GetCursorPos(out _pressPoint) || !GetWindowRect(new WindowInteropHelper(this).Handle, out _pressBounds)) return;
            _pressed = CaptureMouse(); _dragging = false;
        };
        PreviewMouseMove += (_, e) =>
        {
            if (!_pressed || e.LeftButton != MouseButtonState.Pressed || !GetCursorPos(out var point)) return;
            var x = point.X - _pressPoint.X; var y = point.Y - _pressPoint.Y;
            var dpi = Media.VisualTreeHelper.GetDpi(this);
            if (!_dragging && Math.Abs(x) < SystemParameters.MinimumHorizontalDragDistance * dpi.DpiScaleX &&
                Math.Abs(y) < SystemParameters.MinimumVerticalDragDistance * dpi.DpiScaleY) return;
            _dragging = true;
            SetWindowPos(new WindowInteropHelper(this).Handle, 0, _pressBounds.Left + x, _pressBounds.Top + y, 0, 0, 0x0015);
            e.Handled = true;
        };
        PreviewMouseLeftButtonUp += (_, e) => { e.Handled = true; EndDrag(); };
        LostMouseCapture += (_, _) => EndDrag();
        Closed += (_, _) => _closed = true;
        SourceInitialized += (_, _) => { var hwnd = new WindowInteropHelper(this).Handle; SetWindowLong(hwnd, -20, (MonitorDisplayBehavior.GetWindowLong(hwnd, -20) | 0x08000000) & ~0x20); };
    }
    internal void ManualRefresh()
    { ManualRefreshCount++; _locatedAt = 0; Refresh(); }
    private void EndDrag()
    {
        if (!_pressed) return;
        var moved = _dragging; _pressed = _dragging = false; if (IsMouseCaptured) ReleaseMouseCapture();
        if (moved) SavePosition();
    }
    internal void SavePosition()
    {
        if (!GetWindowRect(new WindowInteropHelper(this).Handle, out var rect)) return;
        var screen = Forms.Screen.FromRectangle(System.Drawing.Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom));
        var work = screen.Bounds; // Allow positioning on the taskbar as well as anywhere in the desktop.
        var width = rect.Right - rect.Left; var height = rect.Bottom - rect.Top;
        var x = Math.Clamp(rect.Left, work.Left, Math.Max(work.Left, work.Right - width));
        var y = Math.Clamp(rect.Top, work.Top, Math.Max(work.Top, work.Bottom - height));
        _service.Config.TaskbarLeft = x; _service.Config.TaskbarTop = y;
        if (!_service.Save()) ToolTip = _service.Status;
        Refresh();
    }
    public void Refresh()
    {
        var c = _service.Config; var now = DateTimeOffset.UtcNow; var catalog = _service.Catalog;
        ((W.MenuItem)ContextMenu.Items[0]).Header = MonitorLocalizer.Language.Text(c.Language, "监控设置");
        ToolTip = _service.Store.Available ? MonitorLocalizer.Language.Text(c.Language, "拖动调整位置 · 双击刷新 · 右键设置") : _service.Status;
        _frame.Background = Brush(c.Background, c.TaskbarBackgroundOpacity); _frame.BorderBrush = Brush(c.Foreground, c.TaskbarBackgroundOpacity); Opacity = 1;
        _rows.Children.Clear();
        foreach (var id in c.TaskbarMetrics)
        {
            var metric = catalog.FirstOrDefault(m => m.Id == id);
            var name = MonitorLocalizer.Language.Key(c.Language, "Short." + id, metric?.Name ?? id);
            if (id == "FPS" && metric?.DeviceId == "desktop") name = MonitorLocalizer.Language.Text(c.Language, "桌面 FPS");
            metric ??= new(id, name, "", "", "", null, "不可用");
            metric = _service.Snapshot.ForDisplay(metric, now);
            var row = new MonitorMetricRow(metric, name, c, _previous.TryGetValue(id, out var previous) ? previous : null, compact: true)
                { Margin = new Thickness(4, 0, 8, 0), LayoutTransform = new Media.ScaleTransform(c.UiScale, c.UiScale) };
            _rows.Children.Add(row);
            if (metric.Valid) _previous[id] = metric.Value!.Value; else _previous.Remove(id);
        }
        foreach (var key in _previous.Keys.Where(id => !c.TaskbarMetrics.Contains(id)).ToArray()) _previous.Remove(key);
        _rows.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
        if (!_pressed) { Width = Math.Clamp(_rows.DesiredSize.Width + 20, 80, 1200); Height = Math.Clamp(_rows.DesiredSize.Height + 8, 24, 100); }
        if (!_closed && !_pressed && c.TaskbarLeft is double left && c.TaskbarTop is double top)
        { PlaceManual(left, top); return; }
        if (!_closed && !_pressed && !_locating && _controlRead is not { IsCompleted: false } && Environment.TickCount64 - _locatedAt >= 1500) _ = LocateAsync();
    }
    private void PlaceManual(double left, double top)
    {
        var screen = Forms.Screen.FromPoint(new System.Drawing.Point((int)Math.Clamp(left, int.MinValue, int.MaxValue), (int)Math.Clamp(top, int.MinValue, int.MaxValue)));
        var dpi = Media.VisualTreeHelper.GetDpi(this); var width = (int)Math.Ceiling(Width * dpi.DpiScaleX); var height = (int)Math.Ceiling(Height * dpi.DpiScaleY);
        var x = (int)Math.Clamp(left, screen.Bounds.Left, Math.Max(screen.Bounds.Left, screen.Bounds.Right - width));
        var y = (int)Math.Clamp(top, screen.Bounds.Top, Math.Max(screen.Bounds.Top, screen.Bounds.Bottom - height));
        if (!IsVisible) Show();
        SetWindowPos(new WindowInteropHelper(this).Handle, 0, x, y, width, height, 0x0014);
        PlacementStatus = "任务栏监控运行中 · 自由位置";
    }
    private static Media.SolidColorBrush Brush(string color, double opacity) => new((Media.Color)Media.ColorConverter.ConvertFromString(color)) { Opacity = opacity };
    private async Task LocateAsync()
    {
        _locating = true; _locatedAt = Environment.TickCount64;
        var screenName = _service.Config.TaskbarScreen;
        try
        {
            var screen = Forms.Screen.AllScreens.FirstOrDefault(s => s.DeviceName == screenName) ?? Forms.Screen.PrimaryScreen!;
            var bar = screen.Primary ? FindWindow("Shell_TrayWnd", null) : 0;
            if (bar == 0)
            {
                while ((bar = FindWindowEx(0, bar, "Shell_SecondaryTrayWnd", null)) != 0)
                { if (GetWindowRect(bar, out var r) && screen.Bounds.IntersectsWith(System.Drawing.Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom))) break; }
            }
            if (bar == 0 || !IsWindowVisible(bar) || !GetWindowRect(bar, out var bounds)) { Hide(); PlacementStatus = "任务栏不可用"; return; }
            var vertical = bounds.Bottom - bounds.Top > bounds.Right - bounds.Left;
            var hwnd = new WindowInteropHelper(this).EnsureHandle();
            var dpi = Media.VisualTreeHelper.GetDpi(this); var width = (int)Math.Ceiling(Width * dpi.DpiScaleX); var height = (int)Math.Ceiling(Height * dpi.DpiScaleY);
            // UI Automation reads only taskbar controls. Unknown/timeout results hide our display instead of covering buttons.
            _controlRead = Task.Run(() => ReadControls(bar, bounds));
            var occupied = await _controlRead.WaitAsync(TimeSpan.FromSeconds(2));
            if (_closed || _pressed || _service.Config.TaskbarLeft is not null || screenName != _service.Config.TaskbarScreen || !_service.Config.Enabled || !_service.Config.TaskbarEnabled) return;
            var intervals = occupied.Select(r => vertical ? (r.Top - 4, r.Bottom + 4) : (r.Left - 4, r.Right + 4));
            var gap = MonitorLayout.FindGap(vertical ? bounds.Top + 8 : bounds.Left + 8, vertical ? bounds.Bottom - 8 : bounds.Right - 8, vertical ? height : width, intervals);
            if (gap is null || (vertical ? width > bounds.Right - bounds.Left : height > bounds.Bottom - bounds.Top))
            { Hide(); PlacementStatus = "任务栏空白空间不足"; return; }
            var x = vertical ? bounds.Left + (bounds.Right - bounds.Left - width) / 2 : gap.Value.Start;
            var y = vertical ? gap.Value.Start : bounds.Top + (bounds.Bottom - bounds.Top - height) / 2;
            if (!IsVisible) Show(); SetWindowPos(hwnd, 0, x, y, width, height, 0x0014);
            PlacementStatus = "任务栏监控运行中 · 自动定位";
        }
        catch (Exception e) when (e is TimeoutException or ElementNotAvailableException or InvalidOperationException or UnauthorizedAccessException or COMException)
        { if (!_closed && !_pressed && _service.Config.TaskbarLeft is null) { Hide(); PlacementStatus = "任务栏定位暂不可用"; } }
        finally { _locating = false; }
    }
    private static List<NativeRect> ReadControls(nint bar, NativeRect bounds)
    {
        var root = AutomationElement.FromHandle(bar) ?? throw new InvalidOperationException();
        var controls = root.FindAll(TreeScope.Descendants, System.Windows.Automation.Condition.TrueCondition); var result = new List<NativeRect>();
        for (var i = 0; i < Math.Min(controls.Count, 500); i++)
        {
            var element = controls[i]; var current = element.Current;
            if (current.IsOffscreen || current.BoundingRectangle.IsEmpty) continue;
            var type = current.ControlType;
            if (type == ControlType.Window || type == ControlType.Pane || type == ControlType.Group || type == ControlType.ToolBar || type == ControlType.List) continue;
            var r = current.BoundingRectangle;
            if (r.Width > 0 && r.Height > 0 && r.Left < bounds.Right && r.Right > bounds.Left && r.Top < bounds.Bottom && r.Bottom > bounds.Top)
                result.Add(new() { Left = (int)r.Left, Top = (int)r.Top, Right = (int)Math.Ceiling(r.Right), Bottom = (int)Math.Ceiling(r.Bottom) });
        }
        // A provider returning no controls does not prove the taskbar is empty.
        if (result.Count == 0 || controls.Count > 500) throw new InvalidOperationException();
        return result;
    }
}
