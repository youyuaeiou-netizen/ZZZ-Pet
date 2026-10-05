using System.Windows;
using DesktopPet.Monitoring;
using DesktopPet.Tools;
using W = System.Windows.Controls;
using Media = System.Windows.Media;

namespace DesktopPet;

internal sealed class MonitorWindow : Window
{
    private readonly MonitorService _service;
    private readonly W.TextBlock _status = ToolsWindow.Text("");
    private readonly W.CheckBox _enabled;
    private readonly W.CheckBox _temperatureSwitch = new() { Content = "高温提醒", Margin = new Thickness(0, 6, 0, 6) };
    private readonly W.StackPanel _metrics = new();
    private readonly Dictionary<string, W.TextBlock> _values = [];
    private string _catalogSignature = "";
    private MonitorAdvancedWindow? _advanced;

    public MonitorWindow(MonitorService service)
    {
        _service = service; Title = "系统监控"; Width = 660; Height = 740; MinWidth = 430; MinHeight = 400;
        ShowInTaskbar = false; Topmost = true; MonitorFluentStyle.Apply(this);
        var root = new W.DockPanel();
        var advanced = ToolsWindow.Button("进阶功能", () =>
        {
            if (_advanced is null)
            {
                _advanced = new MonitorAdvancedWindow(service) { Owner = this };
                _advanced.Closed += (_, _) => _advanced = null; _advanced.Show();
            }
            _advanced.Activate();
        });
        advanced.Margin = new Thickness(0, 14, 0, 0);
        W.DockPanel.SetDock(advanced, W.Dock.Bottom); root.Children.Add(advanced);
        var heading = MonitorFluentStyle.Heading("系统监控", 26); heading.Margin = new Thickness(0, 0, 0, 12);
        W.DockPanel.SetDock(heading, W.Dock.Top); root.Children.Add(heading);
        var tabs = new W.TabControl(); root.Children.Add(tabs);
        var monitor = Page();
        _enabled = new W.CheckBox { Content = "启用系统监控", Margin = new Thickness(0, 5, 0, 5) };
        _enabled.Click += (_, _) => service.SetEnabled(_enabled.IsChecked == true);
        monitor.Children.Add(_enabled); monitor.Children.Add(_status);
        _temperatureSwitch.Click += (_, _) =>
        { service.Config.Cpu.Enabled = service.Config.Gpu.Enabled = _temperatureSwitch.IsChecked == true; service.Save(); };
        monitor.Children.Add(_temperatureSwitch);
        var temperatures = Page();
        AddTemperature(temperatures, service, "CPU"); AddTemperature(temperatures, service, "GPU");
        monitor.Children.Add(new W.Expander { Header = "提醒设置", Content = temperatures, Margin = new Thickness(0, 4, 0, 4), Padding = new Thickness(0) });
        monitor.Children.Add(_metrics); AddTab(tabs, "监控", monitor);
        AddTab(tabs, "显示", new MonitorDisplayPage(service));
        var history = new MonitorHistoryPage(service); AddTab(tabs, "历史", history);
        Content = new W.Border { Name = "MonitorSurface", Child = root, Margin = new Thickness(18), Padding = new Thickness(18),
            CornerRadius = new CornerRadius(18), Background = MonitorFluentStyle.Brush("#CCFFFFFF"),
            BorderBrush = MonitorFluentStyle.Brush("#DFFFFFFF"), BorderThickness = new Thickness(1) };
        MonitorLocalizer.Attach(this, service);
        service.Changed += Refresh;
        Closed += (_, _) => { service.Changed -= Refresh; history.Dispose(); _advanced?.Close(); };
        Refresh();
    }
    private static W.StackPanel Page() => new() { Margin = new Thickness(12) };
    private static void AddTab(W.TabControl tabs, string name, W.StackPanel page) => tabs.Items.Add(new W.TabItem
        { Header = name, Content = MonitorSettings.Scroll(page) });
    private static void AddTemperature(W.Panel page, MonitorService service, string kind)
    {
        TemperatureRule Rule() => kind == "CPU" ? service.Config.Cpu : service.Config.Gpu;
        page.Children.Add(MonitorSettings.Toggle(service, kind + " 高温提醒", () => Rule().Enabled, v => Rule().Enabled = v));
        page.Children.Add(MonitorSettings.Number(service, kind + " 阈值（℃，30–130）", () => Rule().Threshold, v => Rule().Threshold = v, 30, 130));
    }
    private void Refresh()
    {
        var c = _service.Config;
        _enabled.IsChecked = c.Enabled; _enabled.IsEnabled = _service.Store.Available;
        _status.Text = _service.Store.Available ? "" : _service.Status;
        _status.Visibility = string.IsNullOrEmpty(_status.Text) ? Visibility.Collapsed : Visibility.Visible;
        _temperatureSwitch.IsEnabled = _service.Store.Available;
        _temperatureSwitch.IsChecked = c.Cpu.Enabled == c.Gpu.Enabled ? c.Cpu.Enabled : null;
        _temperatureSwitch.Content = c.Cpu.Enabled == c.Gpu.Enabled ? "高温提醒" : "高温提醒（部分开启）";
        var catalog = _service.Catalog;
        var signature = string.Join('|', catalog.Select(m => m.Id)) + "/" + string.Join('|', c.SelectedMetrics) + "/" + c.FpsEnabled + "/" + c.Language + "/" + _service.Store.Available;
        if (signature != _catalogSignature)
        {
            _catalogSignature = signature; _metrics.Children.Clear(); _values.Clear();
            var ordered = c.SelectedMetrics.Select(id => catalog.FirstOrDefault(m => m.Id == id)).OfType<MonitorMetric>()
                .Concat(catalog.Where(m => !c.SelectedMetrics.Contains(m.Id))).Take(c.FpsEnabled ? 20 : 19).ToList();
            if (!c.FpsEnabled) ordered.Add(new("FPS", "呈现帧率", "", "FPS", "FPS", null, ""));
            foreach (var metric in ordered) AddRow(metric);
        }
        foreach (var (id, value) in _values)
        {
            var metric = catalog.FirstOrDefault(m => m.Id == id);
            var now = DateTimeOffset.UtcNow;
            var fresh = _service.Snapshot.Fresh(now);
            var display = _service.Snapshot.ForDisplay(metric ?? new(id, id, "", id, "", null, "无可用传感器"), now);
            value.Text = MonitorLocalizer.Language.Text(c.Language, id == "FPS" && !c.FpsEnabled ? "—" : !c.Enabled ? "未启用" : display.Display);
            value.ToolTip = display.Source;
            value.Foreground = new Media.SolidColorBrush((Media.Color)Media.ColorConverter.ConvertFromString(fresh && metric is not null ? MonitorTheme.Color(metric, c) : c.Foreground));
        }
    }
    private void AddRow(MonitorMetric metric)
    {
        var c = _service.Config;
        var row = new W.Grid { Margin = new Thickness(0, 3, 0, 3) };
        row.ColumnDefinitions.Add(new W.ColumnDefinition());
        foreach (var width in new[] { 100, 28, 28 }) row.ColumnDefinitions.Add(new W.ColumnDefinition { Width = new GridLength(width) });
        var caption = ToolsWindow.Text(MonitorLocalizer.Language.Metric(c.Language, metric), 12);
        caption.Margin = new Thickness(0); MonitorLocalizer.Preserve(caption);
        var selected = metric.Id == "FPS" ? c.FpsEnabled : c.SelectedMetrics.Contains(metric.Id);
        var toggle = new W.CheckBox { Content = caption, IsChecked = selected, IsEnabled = _service.Store.Available,
            Margin = new Thickness(0), VerticalAlignment = VerticalAlignment.Center, ToolTip = metric.Source, Tag = metric.Id };
        toggle.Click += (_, _) =>
        {
            if (metric.Id == "FPS") _service.SetFpsEnabled(toggle.IsChecked == true);
            else
            {
                if (toggle.IsChecked == true && !c.SelectedMetrics.Contains(metric.Id)) c.SelectedMetrics.Add(metric.Id);
                else if (toggle.IsChecked != true) c.SelectedMetrics.Remove(metric.Id);
                _service.Save();
            }
        };
        row.Children.Add(toggle);
        var value = ToolsWindow.Text("", 12); value.Margin = new Thickness(0); value.VerticalAlignment = VerticalAlignment.Center;
        MonitorLocalizer.Preserve(value); W.Grid.SetColumn(value, 1); row.Children.Add(value); _values[metric.Id] = value;
        var visible = c.SelectedMetrics.Where(id => _service.Catalog.Any(m => m.Id == id)).ToList();
        for (var i = 0; i < 2; i++)
        {
            var offset = i == 0 ? -1 : 1; var index = visible.IndexOf(metric.Id);
            var move = new W.Button { Content = i == 0 ? "↑" : "↓", ToolTip = i == 0 ? "上移" : "下移", Padding = new Thickness(0),
                Height = 24, MinHeight = 24, Width = 24, MinWidth = 24,
                Margin = new Thickness(2, 0, 0, 0), IsEnabled = _service.Store.Available && index >= 0 && index + offset >= 0 && index + offset < visible.Count };
            move.Click += (_, _) => Move(metric.Id, offset); W.Grid.SetColumn(move, i + 2); row.Children.Add(move);
        }
        _metrics.Children.Add(row);
    }
    private void Move(string id, int offset)
    {
        var selected = _service.Config.SelectedMetrics;
        var visible = selected.Where(key => _service.Catalog.Any(m => m.Id == key)).ToList();
        var index = visible.IndexOf(id); var other = index + offset;
        if (index < 0 || other < 0 || other >= visible.Count) return;
        var from = selected.IndexOf(id); var to = selected.IndexOf(visible[other]);
        (selected[from], selected[to]) = (selected[to], selected[from]); _service.Save();
    }
}
