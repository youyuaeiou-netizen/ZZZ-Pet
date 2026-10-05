using System.Windows;
using System.Windows.Threading;
using DesktopPet.Monitoring;
using DesktopPet.Tools;
using W = System.Windows.Controls;

namespace DesktopPet;

internal sealed class MonitorController : IDisposable
{
    private readonly Window _pet;
    private readonly ToolsController _tools;
    private readonly MonitorService _service;
    private readonly BubbleLifetime _lifetime = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private MonitorBubble? _bubble;
    private MonitorWindow? _panel;
    private MonitorBubble? _display;
    private MonitorDisplayBehavior? _displayBehavior;
    private MonitorTaskbarWindow? _taskbar;
    private Window? _alert;
    private readonly Dictionary<string, MonitorMetric> _pendingAlerts = [];
    private bool _dragging;
    private bool _disposed;
    private bool _lastFresh;
    private bool _petHidden;
    public MonitorController(Window pet, ToolsController tools, string directory)
    {
        _pet = pet; _tools = tools; _service = new(directory, pet.Dispatcher, () => tools.Quiet);
        _service.Changed += Refresh; _service.HighTemperature += Alert;
        _timer.Tick += Tick;
        _pet.Loaded += Loaded; _pet.LocationChanged += Moved; _pet.SizeChanged += Sized;
        var entry = new W.MenuItem { Header = "系统监控" }; entry.Click += (_, _) => ShowPanel();
        pet.ContextMenu.Items.Add(entry);
    }
    private void Loaded(object sender, RoutedEventArgs e) { _service.Restore(); _timer.Start(); }
    public void Click()
    {
        if (!_service.Config.Enabled || !_service.Store.Available || _disposed || _petHidden) return;
        _lifetime.Show();
        if (_bubble is null)
        {
            _bubble = new MonitorBubble(ShowPanel, DismissBubble) { Owner = _pet };
            _bubble.IsVisibleChanged += (_, _) => BubbleVisibilityChanged?.Invoke();
        }
        Refresh();
    }
    public void Drag(bool active) => _dragging = active;
    internal bool HasBubble => _bubble?.IsVisible == true;
    internal Window? BubbleWindow => _bubble;
    internal event Action? BubbleVisibilityChanged;
    internal void DismissBubble() { _lifetime.Close(); _bubble?.Hide(); }
    public void ShowPanel()
    {
        if (_disposed) return;
        if (_panel is null) { _panel = new MonitorWindow(_service) { Owner = _petHidden ? null : _pet }; _panel.Closed += (_, _) => _panel = null; _panel.Show(); }
        WindowPlacement.Near(_panel, _pet); _panel.Activate();
    }
    public void SetPetHidden(bool hidden)
    {
        _petHidden = hidden;
        if (hidden) { _lifetime.Close(); _bubble?.Hide(); }
        if (_panel is not null) _panel.Owner = hidden ? null : _pet;
        if (_taskbar is not null) _taskbar.Owner = hidden ? null : _pet;
        if (_display is not null) _display.Owner = !hidden && _service.Config.DisplayTopmost ? _pet : null;
        if (_alert is not null) _alert.Owner = hidden ? null : _pet;
    }
    private void Tick(object? sender, EventArgs e)
    {
        if (_disposed) return;
        _service.ObserveFpsTarget();
        var fresh = _service.Snapshot.Fresh(DateTimeOffset.UtcNow);
        if (fresh != _lastFresh) { _lastFresh = fresh; Refresh(); }
        _lifetime.Tick(DateTimeOffset.UtcNow, _pet.IsMouseOver, _bubble?.IsVisible == true && _bubble.IsMouseOver, _dragging);
        if (_tools.Quiet || !_service.Config.Enabled)
        { _pendingAlerts.Clear(); _alert?.Close(); _alert = null; }
        foreach (var key in _pendingAlerts.Keys.ToArray())
        {
            var rule = key == "CPU.Temp" ? _service.Config.Cpu : _service.Config.Gpu;
            if (!rule.Enabled || !rule.InEpisode || rule.SnoozeUntil > DateTimeOffset.UtcNow) _pendingAlerts.Remove(key);
        }
        if (_alert is not null) { if (_tools.HasBubble) _alert.Hide(); else if (!_alert.IsVisible) _alert.Show(); }
        if (_alert is null && !_tools.HasBubble && _pendingAlerts.Count > 0)
        {
            var next = _pendingAlerts.First(); _pendingAlerts.Remove(next.Key);
            ShowAlert(next.Key, next.Value);
        }
        UpdateVisibility();
        _displayBehavior?.Tick(DateTimeOffset.UtcNow);
    }
    private void Refresh()
    {
        if (_disposed) return;
        _bubble?.Refresh(_service);
        if (_service.Config.Enabled && _service.Config.TaskbarEnabled)
        { _taskbar ??= new MonitorTaskbarWindow(_service, ShowPanel) { Owner = _petHidden ? null : _pet }; _taskbar.Refresh(); }
        else { _taskbar?.Close(); _taskbar = null; }
        UpdateDisplay();
        UpdateVisibility();
    }
    private void UpdateDisplay()
    {
        if (!_service.Config.Enabled || !_service.Config.DisplayEnabled)
        { _displayBehavior?.Dispose(); _displayBehavior = null; _display?.Close(); _display = null; return; }
        if (_display is null)
        {
            _display = new MonitorBubble(ShowPanel, () => { _service.Config.DisplayEnabled = false; _service.Save(); }, true, () => _displayBehavior?.SavePosition()) { Owner = _petHidden ? null : _pet };
            _display.Refresh(_service); _display.Show(); _displayBehavior = new(_display, _service); _displayBehavior.Place();
        }
        else _display.Refresh(_service);
        // An owned window stays above its topmost owner. Detach only this optional
        // display when its own topmost option is off; lifetime remains controller-owned.
        _display.Owner = !_petHidden && _service.Config.DisplayTopmost ? _pet : null;
        _display.Topmost = _service.Config.DisplayTopmost;
        _displayBehavior?.Clamp();
    }
    private void UpdateVisibility()
    {
        if (_disposed) return;
        if (!_service.Config.Enabled || _petHidden) _lifetime.Close();
        if (_bubble is null) return;
        if (_lifetime.Open && !_tools.HasBubble && _alert is null)
        { if (!_bubble.IsVisible) _bubble.Show(); _bubble.Position(_pet); }
        else _bubble.Hide();
    }
    private void Alert(string key, MonitorMetric metric)
    {
        if (_disposed || _tools.Quiet) return;
        if (_tools.HasBubble || _alert is not null) { _pendingAlerts[key] = metric; return; }
        ShowAlert(key, metric);
    }
    private void ShowAlert(string key, MonitorMetric metric)
    {
        var window = new Window { Title = "艾莲布 · 高温提醒", Width = 350, SizeToContent = SizeToContent.Height,
            WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, ShowActivated = false, ShowInTaskbar = false,
            AllowsTransparency = true, Background = System.Windows.Media.Brushes.Transparent, Topmost = true, Owner = _petHidden ? null : _pet };
        PixelFrame.Apply(window); NoticeWindow.NonActivating(window);
        var panel = new W.StackPanel { Margin = new Thickness(22) };
        var heading = ToolsWindow.Text("", 16);
        MonitorLocalizer.Formatted(heading, "{0} 持续高温：{1}", () => [MonitorLocalizer.Language.Metric(_service.Config.Language, metric), metric.Display]);
        panel.Children.Add(heading);
        panel.Children.Add(ToolsWindow.Button("暂停提醒 1 小时", () => { _service.Snooze(key); window.Close(); }));
        panel.Children.Add(ToolsWindow.Button("本次高温不再提示", () => { _service.SilenceEpisode(key); window.Close(); }));
        panel.Children.Add(ToolsWindow.Button("打开监控", () => { ShowPanel(); window.Close(); }));
        panel.Children.Add(ToolsWindow.Button("收起", window.Close));
        window.Content = new PixelFrame { Child = panel, ShowTail = true };
        MonitorLocalizer.Attach(window, _service);
        _alert = window; window.Closed += (_, _) => { if (ReferenceEquals(_alert, window)) _alert = null; Refresh(); };
        window.Show(); WindowPlacement.Near(window, _pet); Refresh();
    }
    private void Moved(object? sender, EventArgs e) { _bubble?.Position(_pet); if (_alert is not null) WindowPlacement.Near(_alert, _pet); }
    private void Sized(object sender, SizeChangedEventArgs e) => Moved(sender, e);
    public void Dispose()
    {
        if (_disposed) return; _disposed = true; _timer.Stop(); _service.Dispose();
        _pet.Loaded -= Loaded; _pet.LocationChanged -= Moved; _pet.SizeChanged -= Sized;
        _service.Changed -= Refresh; _service.HighTemperature -= Alert;
        _taskbar?.Close(); _displayBehavior?.Dispose(); _display?.Close(); _bubble?.Close(); _panel?.Close(); _alert?.Close();
    }
}
