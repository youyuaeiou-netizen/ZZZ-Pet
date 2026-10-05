using System.Windows;
using System.IO;
using Microsoft.Win32;
using DesktopPet.Monitoring;
using DesktopPet.Tools;
using W = System.Windows.Controls;
using Media = System.Windows.Media;

namespace DesktopPet;

internal sealed class MonitorAdvancedWindow : Window
{
    private readonly MonitorService _service;
    private readonly List<(W.ListBoxItem Item, string Title, Func<bool> Active, Func<UIElement> Build)> _groups = [];
    private readonly W.ListBox _navigation = new();
    private readonly W.ContentControl _body = new();
    private readonly Dictionary<int, UIElement> _pages = [];
    private readonly W.Grid _overview = new();
    private readonly List<W.Button> _overviewCards = [];
    private readonly W.TextBlock _heading = MonitorFluentStyle.Heading("进阶功能", 28);
    private readonly W.TextBlock _status = ToolsWindow.Text("");
    public MonitorAdvancedWindow(MonitorService service)
    {
        _service = service; Title = "系统监控 · 进阶功能"; Width = 860; Height = 760; MinWidth = 540; MinHeight = 420; ShowInTaskbar = false;
        Topmost = true; MonitorFluentStyle.Apply(this);
        var root = new W.Grid { Margin = new Thickness(20), Background = MonitorFluentStyle.Brush("#44F2EDF8") };
        var navigationColumn = new W.ColumnDefinition { Width = new GridLength(184) };
        root.ColumnDefinitions.Add(navigationColumn); root.ColumnDefinitions.Add(new W.ColumnDefinition());
        var sidebar = new W.DockPanel { Margin = new Thickness(0, 0, 18, 0) };
        var brand = new W.StackPanel();
        brand.Children.Add(MonitorFluentStyle.Heading("系统监控", 20));
        brand.Children.Add(ToolsWindow.Button("功能概览", () => { _navigation.SelectedIndex = -1; ShowPage(); }));
        W.DockPanel.SetDock(brand, W.Dock.Top); sidebar.Children.Add(brand);
        sidebar.Children.Add(_navigation); root.Children.Add(sidebar);
        var content = new W.DockPanel(); W.Grid.SetColumn(content, 1); root.Children.Add(content);
        var header = new W.StackPanel(); header.Children.Add(_heading);
        W.DockPanel.SetDock(header, W.Dock.Top); content.Children.Add(header);
        _status.Foreground = MonitorFluentStyle.Brush("#6B687A"); _status.FontSize = 12;
        W.DockPanel.SetDock(_status, W.Dock.Bottom); content.Children.Add(_status);
        content.Children.Add(MonitorSettings.Scroll(_body));
        Group("插件", () => service.Config.PluginsEnabled, () => MonitorFluentStyle.Card(new PluginPage(service)));
        Group("系统工具与内存", () => service.Config.AutoCleanOwnMemory || service.SystemTools.PreventSleep || service.SystemTools.Shutdown.Due is not null,
            () => new MonitorExtrasPage(service));
        Group("外观与布局", () => service.Config.ClickThrough || service.Config.AutoHide || service.Config.Visual.ShowGroups || service.Config.Visual.ShowBars || service.Config.Visual.SmoothValues,
            Appearance);
        Group("启动设置", StartupEnabled, () => MonitorFluentStyle.Card(Startup()));
        Group("维护与诊断", () => service.Config.FpsEnabled, Maintenance);
        _navigation.SelectionChanged += (_, _) => ShowPage();
        SizeChanged += (_, _) => { navigationColumn.Width = new GridLength(ActualWidth < 700 ? 136 : 184); ArrangeOverview(); };
        ShowPage(); Content = root; MonitorLocalizer.Attach(this, service);
        service.Changed += Refresh; service.SystemTools.Changed += Refresh;
        Closed += (_, _) => { service.Changed -= Refresh; service.SystemTools.Changed -= Refresh; };
        Refresh();
    }
    private void Group(string title, Func<bool> active, Func<UIElement> build)
    {
        var index = _groups.Count; var item = new W.ListBoxItem();
        _groups.Add((item, title, active, build)); _navigation.Items.Add(item);
        var summary = new W.StackPanel(); var heading = MonitorFluentStyle.Heading(title, 17); heading.Margin = new Thickness(0, 0, 0, 6); summary.Children.Add(heading);
        var grid = new W.Grid(); grid.ColumnDefinitions.Add(new W.ColumnDefinition { Width = new GridLength(42) }); grid.ColumnDefinitions.Add(new W.ColumnDefinition());
        var number = MonitorFluentStyle.Heading((index + 1).ToString("00"), 16); number.Foreground = MonitorFluentStyle.Brush("#7966BB"); grid.Children.Add(number);
        W.Grid.SetColumn(summary, 1); grid.Children.Add(summary);
        var button = new W.Button { Content = grid, HorizontalContentAlignment = System.Windows.HorizontalAlignment.Stretch, Padding = new Thickness(16),
            Background = MonitorFluentStyle.Brush("#CFFFFFFF"), Margin = new Thickness(0, 0, 0, 12), Tag = title };
        button.Click += (_, _) => _navigation.SelectedIndex = index; _overview.Children.Add(button); _overviewCards.Add(button); ArrangeOverview();
    }
    private void ArrangeOverview()
    {
        var columns = ActualWidth >= 760 ? 2 : 1;
        _overview.ColumnDefinitions.Clear(); _overview.RowDefinitions.Clear();
        for (var i = 0; i < columns; i++) _overview.ColumnDefinitions.Add(new W.ColumnDefinition());
        for (var i = 0; i < (_overviewCards.Count + columns - 1) / columns; i++) _overview.RowDefinitions.Add(new W.RowDefinition { Height = GridLength.Auto });
        for (var i = 0; i < _overviewCards.Count; i++)
        {
            var card = _overviewCards[i]; W.Grid.SetColumn(card, i % columns); W.Grid.SetRow(card, i / columns);
            W.Grid.SetColumnSpan(card, columns == 2 && i == 4 ? 2 : 1);
            card.Margin = new Thickness(0, 0, columns == 2 && i % 2 == 0 && i < 4 ? 12 : 0, 12);
        }
    }
    private void ShowPage()
    {
        var index = _navigation.SelectedIndex;
        if (index < 0) { _heading.Text = "进阶功能"; _body.Content = _overview; return; }
        var group = _groups[index];
        _heading.Text = group.Title;
        if (!_pages.TryGetValue(index, out var page)) _pages[index] = page = group.Build();
        _body.Content = page;
    }
    private void Refresh()
    {
        _status.Text = _service.Store.Available ? "" : _service.Status;
        _status.Visibility = string.IsNullOrEmpty(_status.Text) ? Visibility.Collapsed : Visibility.Visible;
        foreach (var (item, title, active, _) in _groups)
        {
            var label = new W.StackPanel(); label.Children.Add(MonitorFluentStyle.Heading(title, 13));
            if (active()) { var state = MonitorFluentStyle.Hint("● 已开启"); state.Margin = new Thickness(0); state.Foreground = MonitorFluentStyle.Brush("#7966BB"); label.Children.Add(state); }
            item.Content = label;
        }
    }
    private UIElement Appearance()
    {
        var page = new W.StackPanel(); var windows = new W.StackPanel(); windows.Children.Add(MonitorFluentStyle.Heading("独立监控窗", 16));
        windows.Children.Add(MonitorSettings.Toggle(_service, "独立监控窗鼠标穿透", () => _service.Config.ClickThrough, v => _service.Config.ClickThrough = v));
        windows.Children.Add(MonitorSettings.Toggle(_service, "独立监控窗靠边隐藏", () => _service.Config.AutoHide, v => _service.Config.AutoHide = v));
        windows.Children.Add(MonitorSettings.Toggle(_service, "独立监控窗置顶", () => _service.Config.DisplayTopmost, v => _service.Config.DisplayTopmost = v));
        windows.Children.Add(MonitorSettings.Toggle(_service, "限制独立监控窗拖出屏幕", () => _service.Config.ClampToScreen, v => _service.Config.ClampToScreen = v));
        page.Children.Add(MonitorFluentStyle.Card(windows));
        var taskbar = new W.StackPanel(); taskbar.Children.Add(ToolsWindow.Text("任务栏显示器"));
        taskbar.Children.Add(MonitorSettings.Choice(_service,
            new[] { "自动选择" }.Concat(System.Windows.Forms.Screen.AllScreens.Select(s => s.DeviceName)),
            () => _service.Config.TaskbarScreen ?? "自动选择", v => { _service.Config.TaskbarScreen = v == "自动选择" ? null : v; _service.Config.TaskbarLeft = _service.Config.TaskbarTop = null; },
            value => MonitorLocalizer.Language.Text(_service.Config.Language, value)));
        taskbar.Children.Add(ToolsWindow.Button("恢复任务栏自动定位", () => { _service.Config.TaskbarLeft = _service.Config.TaskbarTop = null; _service.Save(); }));
        var metrics = new W.StackPanel(); taskbar.Children.Add(new W.Expander { Header = "任务栏指标（最多 12 项）", Content = metrics });
        page.Children.Add(MonitorFluentStyle.Card(taskbar));
        var signature = "";
        MonitorSettings.Watch(metrics, _service, () =>
        {
            var catalog = _service.Catalog; var next = string.Join('|', catalog.Select(m => m.Id)) + _service.Config.Language + string.Join('|', _service.Config.TaskbarMetrics);
            if (next == signature) return; signature = next; metrics.Children.Clear();
            foreach (var metric in catalog)
            {
                var toggle = new W.CheckBox { Content = MonitorLocalizer.Language.Metric(_service.Config.Language, metric), Tag = metric.Id,
                    IsChecked = _service.Config.TaskbarMetrics.Contains(metric.Id), IsEnabled = _service.Store.Available };
                MonitorLocalizer.Preserve(toggle);
                toggle.Click += (_, _) =>
                {
                    if (toggle.IsChecked == true && !_service.Config.TaskbarMetrics.Contains(metric.Id))
                    {
                        if (_service.Config.TaskbarMetrics.Count >= 12) { toggle.IsChecked = false; _status.Text = "任务栏最多显示 12 项。"; _status.Visibility = Visibility.Visible; return; }
                        _service.Config.TaskbarMetrics.Add(metric.Id);
                    }
                    else if (toggle.IsChecked != true) _service.Config.TaskbarMetrics.Remove(metric.Id);
                    _service.Save();
                };
                metrics.Children.Add(toggle);
            }
        });
        page.Children.Add(new MonitorAppearancePage(_service)); return page;
    }
    private bool StartupEnabled()
    {
        try { using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            return PetStartupCommand.BelongsTo(key?.GetValue("DesktopPet") as string, Environment.ProcessPath); }
        catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException or IOException) { return false; }
    }
    private UIElement Startup()
    {
        var page = new W.StackPanel();
        var startup = new W.CheckBox { Content = "当前用户登录时启动 Pet（不创建管理员计划任务）", IsChecked = StartupEnabled() };
        var error = ToolsWindow.Text(""); page.Children.Add(startup); page.Children.Add(error);
        startup.Click += (_, _) =>
        {
            var previous = StartupEnabled();
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
                if (startup.IsChecked == true) key.SetValue("DesktopPet", PetStartupCommand.Create(Environment.ProcessPath!, Path.GetDirectoryName(_service.Store.DirectoryPath)!));
                else if (PetStartupCommand.BelongsTo(key.GetValue("DesktopPet") as string, Environment.ProcessPath)) key.DeleteValue("DesktopPet", false);
                error.Text = ""; Refresh();
            }
            catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException or IOException or ArgumentException)
            { startup.IsChecked = previous; error.Text = "自启动设置失败。"; }
        };
        return page;
    }
    private UIElement Maintenance()
    {
        var page = new W.StackPanel(); var health = new W.StackPanel(); health.Children.Add(MonitorFluentStyle.Heading("运行状态", 16));
        var diagnostic = ToolsWindow.Text(""); health.Children.Add(diagnostic);
        MonitorSettings.Watch(diagnostic, _service, () =>
        {
            var s = _service.Snapshot;
            var version = typeof(MonitorWindow).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
                .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion.Split('+')[0];
            diagnostic.Text = $"Pet {version}\n" + _service.Status + "\n"
                + $"后台 PID：{_service.WorkerProcessId?.ToString() ?? "—"} · {(s.Elevated ? "管理员权限" : "普通权限")}\n"
                + $"CPU 温度：{_service.Catalog.FirstOrDefault(m => m.Id == "CPU.Temp")?.Display ?? "不可用"} · GPU 温度：{_service.Catalog.FirstOrDefault(m => m.Id == "GPU.Temp")?.Display ?? "不可用"}\n"
                + $"FPS 组件：{s.Capabilities?.GetValueOrDefault("fps-helper") ?? "未启用"}";
        });
        var retry = ToolsWindow.Button("重试监控权限", () => _service.RestartElevated());
        MonitorSettings.Watch(retry, _service, () => retry.IsEnabled = _service.Config.Enabled && _service.Store.Available);
        health.Children.Add(retry);
        page.Children.Add(MonitorFluentStyle.Card(health));
        var devices = new W.StackPanel();
        foreach (var kind in new[] { "GPU", "NET", "DISK" }) AddDevice(devices, kind);
        devices.Children.Add(ToolsWindow.Text("刷新间隔（毫秒）"));
        devices.Children.Add(MonitorSettings.Choice(_service, new[] { 500, 1000, 2000, 5000 }, () => _service.Config.RefreshMs, v => _service.Config.RefreshMs = v));
        page.Children.Add(MonitorFluentStyle.Card(new W.Expander { Header = "采集设备与刷新", Content = devices }));
        var updates = new W.StackPanel(); updates.Children.Add(new MonitorDriverPage()); updates.Children.Add(new MonitorUpdatePage(_service));
        page.Children.Add(MonitorFluentStyle.Card(updates)); return page;
    }
    private void AddDevice(W.Panel page, string kind)
    {
        string? Get() => kind == "GPU" ? _service.Config.GpuDevice : kind == "NET" ? _service.Config.NetworkDevice : _service.Config.DiskDevice;
        var combo = new W.ComboBox { DisplayMemberPath = "Name" }; var updating = false;
        page.Children.Add(ToolsWindow.Text(kind == "GPU" ? "显卡" : kind == "NET" ? "网卡" : "磁盘")); page.Children.Add(combo);
        MonitorSettings.Watch(combo, _service, () =>
        {
            var selected = Get(); var devices = new List<MonitorDevice> { new("", MonitorLocalizer.Language.Text(_service.Config.Language, "自动选择"), kind) };
            devices.AddRange(_service.Snapshot.Devices.Where(d => d.Kind == kind));
            if (selected is not null && devices.All(d => d.Id != selected)) devices.Add(new(selected, MonitorLocalizer.Language.Format(_service.Config.Language, "设备已离线 · {0}", selected), kind));
            updating = true; combo.ItemsSource = devices; combo.SelectedItem = devices.FirstOrDefault(d => d.Id == selected) ?? devices[0];
            combo.IsEnabled = _service.Store.Available; updating = false;
        });
        combo.SelectionChanged += (_, _) =>
        {
            if (updating || combo.SelectedItem is not MonitorDevice d) return;
            var id = d.Id.Length == 0 ? null : d.Id;
            if (kind == "GPU") _service.Config.GpuDevice = id; else if (kind == "NET") _service.Config.NetworkDevice = id; else _service.Config.DiskDevice = id;
            _service.Save();
        };
    }
}
