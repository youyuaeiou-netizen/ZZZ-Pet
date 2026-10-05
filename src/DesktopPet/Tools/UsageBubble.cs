using System.Windows;
using System.Windows.Media;
using W = System.Windows.Controls;

namespace DesktopPet.Tools;

internal sealed class UsageBubble : Window
{
    private readonly W.StackPanel _body = new();
    private readonly W.TextBlock _status = ToolsWindow.Text("", 12);
    private readonly W.Button _refresh;
    private readonly PixelFrame _frame;
    internal UsageBubble(Action refresh, Action dismiss)
    {
        Title = "ChatGPT · Codex 额度"; Width = 270; SizeToContent = SizeToContent.Height;
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize; ShowActivated = false; ShowInTaskbar = false;
        AllowsTransparency = true; Background = System.Windows.Media.Brushes.Transparent; Topmost = true;
        FontFamily = new System.Windows.Media.FontFamily("Microsoft YaHei UI"); PixelFrame.Apply(this); NoticeWindow.NonActivating(this);
        var panel = new W.StackPanel { Margin = new Thickness(14, 10, 14, 10) };
        var title = ToolsWindow.Text("✦ Codex 额度", 15); title.FontWeight = FontWeights.SemiBold; title.Margin = new Thickness(0, 0, 0, 6); panel.Children.Add(title);
        panel.Children.Add(new W.ScrollViewer { Content = _body, MaxHeight = 240, VerticalScrollBarVisibility = W.ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = W.ScrollBarVisibility.Disabled });
        panel.Children.Add(_status);
        var actions = new W.WrapPanel(); _refresh = ToolsWindow.Button("刷新", refresh); actions.Children.Add(_refresh);
        actions.Children.Add(ToolsWindow.Button("收起", dismiss)); panel.Children.Add(actions);
        foreach (var button in actions.Children.OfType<W.Button>())
        { button.FontSize = 12; button.MinHeight = 26; button.Padding = new Thickness(8, 4, 8, 4); button.Margin = new Thickness(0, 6, 6, 0); }
        _frame = new PixelFrame { Child = panel, ShowTail = true, Margin = new Thickness(9, 0, 9, 0), Fill = Brush("#FFFAF4") };
        Content = _frame;
    }
    internal void Render(CodexUsage? usage, string? error, bool busy)
    {
        _body.Children.Clear();
        if (usage is null || usage.Buckets.Count == 0) _body.Children.Add(ToolsWindow.Text(busy ? "正在读取额度…" : "额度暂不可用", 14));
        else foreach (var bucket in usage.Buckets)
        {
            var content = new W.StackPanel();
            if (bucket.Name != "Codex")
            { var heading = ToolsWindow.Text(bucket.Name, 13); heading.FontWeight = FontWeights.SemiBold; heading.Margin = new Thickness(0, 0, 0, 4); content.Children.Add(heading); }
            AddWindow(content, bucket.Primary, "当前窗口", usage.RetrievedAt);
            AddWindow(content, bucket.Secondary, "长期窗口", usage.RetrievedAt);
            if (bucket.Primary is null && bucket.Secondary is null) content.Children.Add(ToolsWindow.Text("使用额度暂不可用", 12));
            if (bucket.Credits is not null)
            { var credits = ToolsWindow.Text(DateTimeOffset.UtcNow - usage.RetrievedAt > TimeSpan.FromMinutes(10) ? "积分：待更新" : bucket.Credits, 12); credits.Margin = new Thickness(0, 4, 0, 0); content.Children.Add(credits); }
            _body.Children.Add(new W.Border { Child = content, Padding = new Thickness(10, 6, 10, 8), Margin = new Thickness(0, 0, 0, 4),
                CornerRadius = new CornerRadius(12), Background = Brush("#FBE9F4") });
        }
        _status.Text = error ?? (busy && usage is not null ? "正在刷新…" : "");
        _status.Visibility = string.IsNullOrEmpty(_status.Text) ? Visibility.Collapsed : Visibility.Visible;
        _refresh.IsEnabled = !busy;
    }
    private static void AddWindow(W.Panel panel, UsageWindow? window, string fallback, DateTimeOffset retrieved)
    {
        if (window is null) return;
        var duration = window.Minutes switch { int m when m % 1440 == 0 => $"{m / 1440} 天", int m when m % 60 == 0 => $"{m / 60} 小时", int m => $"{m} 分钟", _ => fallback };
        var stale = DateTimeOffset.UtcNow - retrieved > TimeSpan.FromMinutes(10) || window.ResetsAt <= DateTimeOffset.UtcNow;
        var text = stale ? "待更新" : window.Remaining is double n ? $"剩余 {n:0.#}%" : "暂不可用";
        var heading = ToolsWindow.Text($"{duration} · {text}", 14); heading.FontWeight = FontWeights.SemiBold; heading.Margin = new Thickness(0, 4, 0, 4); panel.Children.Add(heading);
        if (!stale && window.Remaining is double remaining)
        {
            var track = new W.Grid { Height = 5, Margin = new Thickness(0, 0, 0, 4) };
            track.Children.Add(new W.Border { Background = Brush("#E9DDEF"), CornerRadius = new CornerRadius(4) });
            var fill = new W.Border { Background = Brush(remaining < 15 ? "#DDA0C8" : "#8DD9C0"), CornerRadius = new CornerRadius(4), HorizontalAlignment = System.Windows.HorizontalAlignment.Left };
            track.SizeChanged += (_, _) => fill.Width = track.ActualWidth * remaining / 100; track.Children.Add(fill); panel.Children.Add(track);
        }
        var reset = ToolsWindow.Text(window.ResetsAt is DateTimeOffset at ? $"{at.ToLocalTime():MM-dd HH:mm} 重置" : "重置时间暂不可用", 12);
        reset.Margin = new Thickness(0, 0, 0, 5); panel.Children.Add(reset);
    }
    internal void Position(Window pet) { _frame.TailLeft = WindowPlacement.Near(this, pet); _frame.InvalidateVisual(); }
    private static SolidColorBrush Brush(string value) => new((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(value));
}
