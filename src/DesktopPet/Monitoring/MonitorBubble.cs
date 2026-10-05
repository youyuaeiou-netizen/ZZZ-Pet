using System.Windows;
using DesktopPet.Monitoring;
using DesktopPet.Tools;
using W = System.Windows.Controls;
using Media = System.Windows.Media;

namespace DesktopPet;

internal sealed class MonitorBubble : Window
{
    private readonly W.StackPanel _rows = new() { Margin = new Thickness(22, 16, 22, 16) };
    private readonly PixelFrame _frame;
    private readonly bool _standalone;
    private readonly Dictionary<string, double> _previous = [];
    public MonitorBubble(Action showSettings, Action close, bool standalone = false, Action? dragged = null)
    {
        _standalone = standalone;
        Title = "电脑状态"; Width = 350; SizeToContent = SizeToContent.Height;
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize; ShowActivated = false; ShowInTaskbar = false;
        AllowsTransparency = true; Background = Media.Brushes.Transparent; Topmost = true;
        FontFamily = new Media.FontFamily("Microsoft YaHei UI"); PixelFrame.Apply(this); NoticeWindow.NonActivating(this);
        var root = new W.StackPanel();
        root.Children.Add(new W.ScrollViewer { Content = _rows, MaxHeight = 380, VerticalScrollBarVisibility = W.ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = W.ScrollBarVisibility.Disabled });
        var actions = new W.WrapPanel { Margin = new Thickness(22, 0, 22, 16) };
        actions.Children.Add(ToolsWindow.Button("监控设置", showSettings)); actions.Children.Add(ToolsWindow.Button("收起", close));
        root.Children.Add(actions); _frame = new PixelFrame { Child = root, ShowTail = !standalone, Margin = new Thickness(9, 0, 9, 0) }; Content = _frame;
        if (standalone) _rows.MouseLeftButtonDown += (_, e) =>
        { if (e.ClickCount == 2) showSettings(); else if (e.LeftButton == System.Windows.Input.MouseButtonState.Pressed) { DragMove(); dragged?.Invoke(); } };
    }
    public void Refresh(MonitorService service)
    {
        var c = service.Config; var visual = c.Visual;
        _rows.Children.Clear();
        _rows.Margin = new Thickness(visual.Padding, 16, visual.Padding, 16);
        var title = ToolsWindow.Text("电脑状态", visual.TitleSize);
        title.Foreground = new Media.SolidColorBrush((Media.Color)Media.ColorConverter.ConvertFromString(visual.TitleColor));
        title.FontWeight = FontWeights.SemiBold;
        var header = new W.DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        var accent = new W.TextBlock { Text = "✦", FontSize = 22, Foreground = new Media.SolidColorBrush(Media.Color.FromRgb(213, 148, 192)), Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
        header.Children.Add(accent); header.Children.Add(title); _rows.Children.Add(header);
        var now = DateTimeOffset.UtcNow;
        var catalog = service.Catalog;
        var values = new W.Primitives.UniformGrid { Columns = service.Config.Horizontal ? 2 : 1 };
        _rows.Children.Add(values);
        string? groupId = null;
        var currentValues = values;
        foreach (var id in service.Config.SelectedMetrics)
        {
            var metric = catalog.FirstOrDefault(m => m.Id == id);
            var label = metric is not null ? MonitorLocalizer.Language.Metric(service.Config.Language, metric) : id;
            metric ??= new(id, label, "", "", "", null, "无可用传感器");
            metric = service.Snapshot.ForDisplay(metric, now);
            if (visual.ShowGroups && groupId != metric.DeviceId)
            {
                groupId = metric.DeviceId;
                var content = new W.StackPanel();
                var heading = ToolsWindow.Text(service.Snapshot.Devices.FirstOrDefault(d => d.Id == groupId)?.Name ?? metric.Kind.Split('.')[0], visual.GroupSize);
                heading.Foreground = new Media.SolidColorBrush((Media.Color)Media.ColorConverter.ConvertFromString(visual.GroupColor));
                heading.FontWeight = visual.Bold ? FontWeights.Bold : FontWeights.Normal;
                heading.Margin = new Thickness(0, visual.GroupTitleOffset, 0, visual.GroupBottom); content.Children.Add(heading);
                currentValues = new() { Columns = c.Horizontal ? 2 : 1 }; content.Children.Add(currentValues);
                _rows.Children.Add(new W.Border { Child = content, Padding = new Thickness(visual.GroupPadding),
                    Margin = new Thickness(0, visual.GroupSpacing / 2, 0, visual.GroupSpacing / 2), CornerRadius = new CornerRadius(visual.GroupRadius),
                    Background = new Media.SolidColorBrush((Media.Color)Media.ColorConverter.ConvertFromString(visual.GroupBackground)) });
            }
            currentValues.Children.Add(new W.Border { Child = new MonitorMetricRow(metric, label, c, _previous.TryGetValue(id, out var prior) ? prior : null),
                CornerRadius = new CornerRadius(10), Background = new Media.SolidColorBrush(Media.Color.FromArgb(155, 255, 255, 255)),
                Padding = new Thickness(10, 5, 10, 5), Margin = new Thickness(0, 3, c.Horizontal ? 4 : 0, 3) });
            if (metric.Valid) _previous[id] = metric.Value!.Value; else _previous.Remove(id);
        }
        foreach (var id in _previous.Keys.Except(c.SelectedMetrics).ToArray()) _previous.Remove(id);
        if (service.Config.SelectedMetrics.Count == 0) _rows.Children.Add(ToolsWindow.Text("尚未选择展示指标。"));
        try { _frame.Fill = new Media.SolidColorBrush((Media.Color)Media.ColorConverter.ConvertFromString(service.Config.Background)); _frame.CornerRadius = visual.CornerRadius; _frame.InvalidateVisual(); }
        catch (FormatException) { }
        MonitorLocalizer.Translate(this, service.Config.Language);
        Opacity = service.Config.Opacity;
        W.TextBlock.SetFontFamily(_rows, new Media.FontFamily(service.Config.FontFamily));
        _rows.LayoutTransform = new Media.ScaleTransform(service.Config.UiScale, service.Config.UiScale);
        Width = Math.Clamp(service.Config.PanelWidth * service.Config.UiScale * (service.Config.Horizontal ? 1.5 : 1), 240, 900);
        try
        {
            var brush = new Media.SolidColorBrush((Media.Color)Media.ColorConverter.ConvertFromString(service.Config.Foreground));
            foreach (var block in _rows.Children.OfType<W.TextBlock>().Where(block => block != title)) block.Foreground = brush;
        }
        catch (FormatException) { }
    }
    public void Position(Window pet) { _frame.TailLeft = WindowPlacement.Near(this, pet); _frame.InvalidateVisual(); }
}
