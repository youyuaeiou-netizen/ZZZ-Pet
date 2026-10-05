using System.IO;
using System.Windows;
using DesktopPet.Monitoring;
using DesktopPet.Tools;
using W = System.Windows.Controls;
using Media = System.Windows.Media;
using Point = System.Windows.Point;

namespace DesktopPet;

internal sealed class MonitorHistoryPage : W.StackPanel, IDisposable
{
    private readonly MonitorService _service;
    private readonly MonitorHistory _archive;
    private readonly W.ComboBox _dates = new(), _metric = new();
    private readonly W.Slider _zoom = new() { Minimum = 1, Maximum = 10, Value = 1, TickFrequency = 1, IsSnapToTickEnabled = true };
    private readonly HistoryGraph _graph = new() { Height = 190 };
    private readonly W.TextBlock _status = ToolsWindow.Text("");
    private readonly Queue<MonitorSnapshot> _live = new();
    private List<MonitorSnapshot>? _loaded;
    private bool _loading;
    private bool _disposed;
    private string _language = "";
    public MonitorHistoryPage(MonitorService service)
    {
        _service = service; _archive = new(Path.Combine(service.Store.DirectoryPath, "history"));
        Margin = new Thickness(12); System.Windows.Documents.TextElement.SetFontWeight(this, FontWeights.Normal);
        Loaded += (_, _) => { if (Parent is W.ScrollViewer scroll) scroll.SetResourceReference(FocusVisualStyleProperty, "MonitorHistoryFocus"); };
        _graph.LanguageCode = () => service.Config.Language;
        _dates.SetResourceReference(StyleProperty, "MonitorHistoryCombo"); _metric.SetResourceReference(StyleProperty, "MonitorHistoryCombo");
        _zoom.SetResourceReference(StyleProperty, "MonitorHistorySlider");
        _dates.ToolTip = "日期（当前会话实时更新）";
        var filters = new W.StackPanel(); var fields = new W.Grid();
        fields.ColumnDefinitions.Add(new W.ColumnDefinition()); fields.ColumnDefinitions.Add(new W.ColumnDefinition());
        fields.RowDefinitions.Add(new W.RowDefinition { Height = GridLength.Auto }); fields.RowDefinitions.Add(new W.RowDefinition { Height = GridLength.Auto });
        W.StackPanel Field(string label, W.ComboBox control)
        {
            var field = new W.StackPanel(); var caption = MonitorFluentStyle.Hint(label); caption.Margin = new Thickness(0, 0, 0, 6);
            field.Children.Add(caption); field.Children.Add(control); return field;
        }
        var dateField = Field("日期", _dates); var metricField = Field("指标", _metric);
        fields.Children.Add(dateField); fields.Children.Add(metricField); filters.Children.Add(fields);
        SizeChanged += (_, _) =>
        {
            var narrow = ActualWidth < 460;
            W.Grid.SetColumn(metricField, narrow ? 0 : 1); W.Grid.SetRow(metricField, narrow ? 1 : 0);
            W.Grid.SetColumnSpan(dateField, narrow ? 2 : 1); W.Grid.SetColumnSpan(metricField, narrow ? 2 : 1);
            dateField.Margin = new Thickness(0, 0, narrow ? 0 : 12, narrow ? 10 : 0);
        };
        var actions = new W.WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        actions.Children.Add(ToolsWindow.Button("刷新日期", RefreshDates));
        actions.Children.Add(ToolsWindow.Button("导出所选日期 CSV", Export)); filters.Children.Add(actions);
        foreach (var button in actions.Children.OfType<W.Button>())
        {
            button.Background = MonitorFluentStyle.Brush("#EEE8F7"); button.Foreground = MonitorFluentStyle.Brush("#655297");
        }
        W.Border Card(UIElement child) { var card = MonitorFluentStyle.Card(child); card.Padding = new Thickness(14); return card; }
        Children.Add(Card(filters));
        var trend = new W.StackPanel(); var heading = MonitorFluentStyle.Heading("趋势与历史", 16); heading.Margin = new Thickness(0, 0, 0, 10);
        trend.Children.Add(heading); trend.Children.Add(_graph);
        var range = new W.Grid { Margin = new Thickness(0, 12, 0, 0) };
        range.ColumnDefinitions.Add(new W.ColumnDefinition { Width = GridLength.Auto }); range.ColumnDefinitions.Add(new W.ColumnDefinition());
        range.ColumnDefinitions.Add(new W.ColumnDefinition { Width = new GridLength(34) });
        var rangeLabel = MonitorFluentStyle.Hint("时间缩放"); rangeLabel.Margin = new Thickness(0, 0, 12, 0); rangeLabel.VerticalAlignment = VerticalAlignment.Center; range.Children.Add(rangeLabel);
        W.Grid.SetColumn(_zoom, 1); range.Children.Add(_zoom);
        var zoomValue = MonitorFluentStyle.Hint("1×"); zoomValue.Margin = new Thickness(8, 0, 0, 0); zoomValue.VerticalAlignment = VerticalAlignment.Center;
        MonitorLocalizer.Preserve(zoomValue); W.Grid.SetColumn(zoomValue, 2); range.Children.Add(zoomValue);
        _zoom.ValueChanged += (_, _) => zoomValue.Text = $"{_zoom.Value:0}×";
        _zoom.ToolTip = "放大最近时间段（1–10 倍）；悬停图表查看时间和值"; trend.Children.Add(range); Children.Add(Card(trend));
        var recording = new W.StackPanel();
        recording.Children.Add(MonitorSettings.Toggle(service, "记录历史（每 10 秒，单日日志最多 32 MB）", () => service.Config.HistoryEnabled, v => service.Config.HistoryEnabled = v));
        _status.Foreground = MonitorFluentStyle.Brush("#6B687A"); _status.Margin = new Thickness(0, 5, 0, 0);
        recording.Children.Add(_status); _status.Visibility = Visibility.Collapsed;
        Children.Add(Card(recording));
        _dates.SelectionChanged += async (_, _) => await LoadDate();
        _metric.SelectionChanged += (_, _) => UpdateGraph(); _zoom.ValueChanged += (_, _) => UpdateGraph();
        service.Changed += Refresh; RefreshDates(); Refresh();
    }
    private sealed record Choice(string Id, string Name);
    private sealed record DayChoice(DateOnly? Date, string Name);
    private void RefreshDates()
    {
        var previous = (_dates.SelectedItem as DayChoice)?.Date;
        var choices = new List<DayChoice> { new(null, MonitorLocalizer.Language.Text(_service.Config.Language, "当前会话")) };
        try { choices.AddRange(_archive.Dates().Select(d => new DayChoice(d, d.ToString("yyyy-MM-dd")))); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { ShowStatus("历史目录暂不可读。"); }
        _dates.ItemsSource = choices; _dates.DisplayMemberPath = "Name";
        _dates.SelectedItem = choices.FirstOrDefault(c => c.Date == previous) ?? choices[0];
    }
    private async Task LoadDate()
    {
        if (_loading || _disposed) return;
        if ((_dates.SelectedItem as DayChoice)?.Date is not DateOnly day) { _loaded = null; ShowStatus(""); RebuildMetrics(); return; }
        _loading = true; _dates.IsEnabled = false;
        try
        {
            var result = await Task.Run(() => _archive.Read(day)); if (_disposed) return;
            _loaded = result.Samples; MonitorLocalizer.Formatted(_status, "{0} 条记录；跳过 {1} 行。", result.Samples.Count, result.InvalidLines);
            _status.Visibility = Visibility.Visible; RebuildMetrics();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException) { _loaded = []; ShowStatus("历史读取失败，原文件保留。"); }
        finally { _loading = false; _dates.IsEnabled = true; }
    }
    private void Refresh()
    {
        if (_language != _service.Config.Language) { _language = _service.Config.Language; RefreshDates(); RebuildMetrics(); }
        var s = _service.Snapshot;
        if (s.Fresh(DateTimeOffset.UtcNow) && (_live.Count == 0 || _live.Last().Timestamp != s.Timestamp))
        { _live.Enqueue(s with { Metrics = s.Metrics.Concat(_service.Plugins.Metrics).ToList() }); while (_live.Count > 3600) _live.Dequeue(); }
        if (_loaded is null) RebuildMetrics();
    }
    private IEnumerable<MonitorSnapshot> Samples => _loaded is null ? _live : _loaded;
    private void RebuildMetrics()
    {
        var selected = (_metric.SelectedItem as Choice)?.Id;
        var metrics = Samples.SelectMany(s => MonitorSelection.Resolve(s, _service.Config, history: true)).DistinctBy(m => m.Id)
            .Select(m => new Choice(m.Id, MonitorLocalizer.Language.Metric(_service.Config.Language, m))).ToList();
        if (metrics.Count == 0) metrics = MonitorSelection.Defaults.Select(m => new Choice(m.Id, MonitorLocalizer.Language.Key(_service.Config.Language, "Items." + m.Id, m.Name))).ToList();
        _metric.ItemsSource = metrics; _metric.DisplayMemberPath = "Name";
        _metric.SelectedItem = metrics.FirstOrDefault(m => m.Id == selected) ?? metrics[0]; UpdateGraph();
    }
    private void UpdateGraph()
    {
        var key = (_metric.SelectedItem as Choice)?.Id ?? "CPU.Load";
        var samples = Samples.ToArray(); var take = Math.Max(2, (int)Math.Ceiling(samples.Length / _zoom.Value));
        _graph.Series = samples.TakeLast(take).Select(s =>
        {
            var metric = MonitorSelection.Resolve(s, _service.Config, history: true).FirstOrDefault(m => m.Id == key);
            var valid = metric?.Valid == true && (metric.SampledAt is null || s.Timestamp - metric.SampledAt <= TimeSpan.FromSeconds(5));
            return new HistoryPoint(s.Timestamp, valid ? metric!.Value : null, metric?.Unit ?? "");
        }).ToArray(); _graph.InvalidateVisual();
    }
    private void Export()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog { Filter = MonitorLocalizer.Language.Text(_service.Config.Language, "CSV 文件") + "|*.csv", FileName = "Pet-monitor.csv" };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        try { using var writer = new StreamWriter(dialog.FileName, false, new System.Text.UTF8Encoding(true)); MonitorHistory.Export(writer, Samples); ShowStatus("历史已导出。"); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { ShowStatus("导出失败，原始历史未改变。"); }
    }
    private void ShowStatus(string text) { _status.Text = text; _status.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible; }
    public void Dispose() { _disposed = true; _service.Changed -= Refresh; }
}

internal sealed record HistoryPoint(DateTimeOffset Time, double? Value, string Unit);
internal sealed class HistoryGraph : FrameworkElement
{
    private readonly W.ToolTip _tooltip = new();
    public Func<string> LanguageCode { get; set; } = () => "zh";
    public HistoryPoint[] Series { get; set; } = [];
    private Rect Plot => new(48, 18, Math.Max(1, ActualWidth - 66), Math.Max(1, ActualHeight - 52));
    public HistoryGraph()
    {
        _tooltip.SetResourceReference(StyleProperty, "MonitorHistoryTooltip");
        MouseMove += (_, e) =>
        {
            if (Series.Length == 0) { ToolTip = null; return; }
            var position = Math.Clamp((e.GetPosition(this).X - Plot.Left) / Plot.Width, 0, 1);
            var seconds = Math.Max(0, (Series[^1].Time - Series[0].Time).TotalSeconds) * position;
            var p = Series.MinBy(p => Math.Abs((p.Time - Series[0].Time).TotalSeconds - seconds))!;
            _tooltip.Content = $"{p.Time.ToLocalTime():yyyy-MM-dd HH:mm:ss} · {(p.Value is double v && double.IsFinite(v) ? v.ToString("0.##") + " " + p.Unit : MonitorLocalizer.Language.Text(LanguageCode(), "不可用"))}";
            ToolTip = _tooltip;
        };
    }
    protected override void OnRender(Media.DrawingContext dc)
    {
        if (ActualWidth < 1 || ActualHeight < 1) return;
        var ink = MonitorFluentStyle.Brush("#8A829B"); var accent = MonitorFluentStyle.Brush("#7966BB");
        dc.DrawRoundedRectangle(MonitorFluentStyle.Brush("#FAF9FD"), new Media.Pen(MonitorFluentStyle.Brush("#E7E1F1"), 1), new Rect(0, 0, ActualWidth, ActualHeight), 9, 9);
        Media.FormattedText Text(string text) => new(text, System.Globalization.CultureInfo.CurrentCulture, System.Windows.FlowDirection.LeftToRight,
            new Media.Typeface("Segoe UI, Microsoft YaHei UI"), 10, ink, Media.VisualTreeHelper.GetDpi(this).PixelsPerDip);
        var values = Series.Where(p => p.Value is double v && double.IsFinite(v)).Select(p => p.Value!.Value).ToArray();
        if (values.Length == 0)
        {
            var empty = Text(MonitorLocalizer.Language.Text(LanguageCode(), "暂无可用趋势数据"));
            dc.DrawText(empty, new Point(Math.Max(8, (ActualWidth - empty.Width) / 2), (ActualHeight - empty.Height) / 2)); return;
        }
        var min = Math.Min(0, values.Min()); var max = Math.Max(min + 1, values.Max() * 1.1);
        var plot = Plot;
        var span = Math.Max(1, (Series[^1].Time - Series[0].Time).TotalSeconds);
        for (var i = 0; i <= 4; i++)
        {
            var y = plot.Top + plot.Height * i / 4;
            dc.DrawLine(new Media.Pen(MonitorFluentStyle.Brush("#EBE6F3"), 1), new Point(plot.Left, y), new Point(plot.Right, y));
            if (i % 2 == 0)
            {
                var label = Text($"{max - (max - min) * i / 4:0.##}"); label.MaxTextWidth = 40; label.Trimming = TextTrimming.CharacterEllipsis;
                dc.DrawText(label, new Point(5, y - label.Height / 2));
            }
        }
        var first = Text(Series[0].Time.ToLocalTime().ToString("HH:mm:ss")); dc.DrawText(first, new Point(plot.Left, plot.Bottom + 10));
        if (Series.Length > 1)
        {
            var last = Text(Series[^1].Time.ToLocalTime().ToString("HH:mm:ss"));
            dc.DrawText(last, new Point(Math.Max(plot.Left + first.Width + 8, plot.Right - last.Width), plot.Bottom + 10));
        }
        Point? previous = null; DateTimeOffset? beforeTime = null;
        var latest = Series.LastOrDefault(p => p.Value is double n && double.IsFinite(n));
        var curve = new Media.Pen(accent, 2.4);
        foreach (var p in Series)
        {
            if (p.Value is not double v || !double.IsFinite(v)) { previous = null; continue; }
            var point = new Point(plot.Left + (p.Time - Series[0].Time).TotalSeconds / span * plot.Width, plot.Bottom - (v - min) / (max - min) * plot.Height);
            if (previous is Point before && beforeTime is DateTimeOffset t && p.Time - t <= TimeSpan.FromSeconds(20))
                dc.DrawLine(curve, before, point);
            if (values.Length == 1 || ReferenceEquals(p, latest))
                dc.DrawEllipse(accent, new Media.Pen(Media.Brushes.White, 1.5), point, 3.5, 3.5);
            previous = point; beforeTime = p.Time;
        }
    }
}
