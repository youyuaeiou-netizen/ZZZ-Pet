using System.Windows;
using System.Windows.Interop;
using System.Runtime.InteropServices;
using W = System.Windows.Controls;
using Media = System.Windows.Media;
namespace DesktopPet.Tools;
internal sealed class NoticeWindow : Window
{
    [DllImport("user32.dll")] private static extern int GetWindowLong(nint window, int index);
    [DllImport("user32.dll")] private static extern int SetWindowLong(nint window, int index, int value);
    private readonly ToolsService _service;
    private readonly Action _showTools;
    private readonly W.StackPanel _panel = new() { Margin = new Thickness(22,14,22,14) };
    private readonly W.TextBlock _status = ToolsWindow.Text("");
    private readonly PixelFrame _frame;
    private List<ToolNotice> _items = [];
    private int _index;
    public ToolNotice Notice { get; private set; }
    public bool Important { get; private set; }
    public NoticeWindow(ToolNotice notice, ToolsService service, Action showTools)
    {
        Notice = notice; _service = service; _showTools = showTools;
        Title = "艾莲布 · 对话"; Width = 340; SizeToContent = SizeToContent.Height;
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
        ShowActivated = false; ShowInTaskbar = false; Topmost = true;
        AllowsTransparency = true; Background = Media.Brushes.Transparent;
        FontFamily = new Media.FontFamily("Microsoft YaHei UI"); PixelFrame.Apply(this);
        _frame = new PixelFrame { Child = _panel, ShowTail = true, Margin = new Thickness(9,0,9,0) };
        Content = _frame; NonActivating(this); Refresh();
    }
    public void SetSide(bool right) { _frame.TailLeft = right; _frame.InvalidateVisual(); }
    public void Status(string text) { _status.Text = text; _status.Visibility = Visibility.Visible; }
    public void ClearStatus() { _status.Text = ""; _status.Visibility = Visibility.Collapsed; }
    public void Refresh()
    {
        var selected = Notice.ReminderId;
        _items = _service.State.Reminders.Where(r => r.PendingOccurrence is not null && r.SnoozeUntil is null)
            .OrderBy(r => r.PendingOccurrence).Select(r => new ToolNotice(r.Title,r.Id)).ToList();
        if (_service.State.Focus.AwaitingNext) _items.Add(new ToolNotice(_service.State.Focus.Stage == FocusStage.Focus
            ? "专注结束，休息一下吧。" : "休息结束，可以开始下一轮专注。"));
        Important = _items.Count > 0 && Notice.Kind == NoticeKind.Important;
        if (Important)
        {
            var found = _items.FindIndex(n => n.ReminderId == selected);
            _index = found >= 0 ? found : Math.Min(_index,_items.Count-1); Notice = _items[_index];
        }
        _panel.Children.Clear();
        var heading = new W.StackPanel { Orientation = W.Orientation.Horizontal };
        heading.Children.Add(new PixelHeart()); heading.Children.Add(ToolsWindow.Text(Important ? $"艾莲布 · 待处理 {_index+1}/{_items.Count}" : "艾莲布",12));
        _panel.Children.Add(heading);
        _panel.Children.Add(new W.Border { CornerRadius = new CornerRadius(12), Background = new Media.SolidColorBrush(Media.Color.FromRgb(255, 232, 242)),
            Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(0, 8, 0, 6), Child = new W.ScrollViewer { Content = ToolsWindow.Text(Notice.Text,16), MaxHeight = 210,
            VerticalScrollBarVisibility = W.ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = W.ScrollBarVisibility.Disabled } });
        _status.Visibility = string.IsNullOrEmpty(_status.Text) ? Visibility.Collapsed : Visibility.Visible; _panel.Children.Add(_status);
        var actions = new W.WrapPanel();
        if (Important && Notice.ReminderId is Guid id)
        {
            actions.Children.Add(ToolsWindow.Button("完成", () => { _service.Complete(id); _service.Confirm("本次提醒已完成。"); }));
            actions.Children.Add(ToolsWindow.Button("10 分钟后提醒", () => { _service.Snooze(id,DateTime.Now); _service.Confirm("10 分钟后再提醒。"); }));
        }
        else if (Important) actions.Children.Add(ToolsWindow.Button(_service.State.Focus.Stage == FocusStage.Focus ? "开始休息" : "开始下一轮", () => { _service.StartFocus(); _service.Confirm("下一阶段已开始。"); }));
        if (Important && _items.Count > 1) actions.Children.Add(ToolsWindow.Button("下一件", () => { _index = (_index+1)%_items.Count; Notice = _items[_index]; Refresh(); }));
        actions.Children.Add(ToolsWindow.Button("查看", _showTools)); actions.Children.Add(ToolsWindow.Button("收起", Close));
        _panel.Children.Add(actions);
    }
    internal static void NonActivating(Window window)
    {
        window.SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(window).Handle;
            SetWindowLong(handle,-20,GetWindowLong(handle,-20)|0x08000000|0x00000080);
            HwndSource.FromHwnd(handle)?.AddHook(NoActivate);
        };
    }
    private static nint NoActivate(nint hwnd,int message,nint wParam,nint lParam,ref bool handled)
    { if(message == 0x0021) { handled=true; return 3; } return 0; }
}
