using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;
using DesktopPet.Tools;
using W = System.Windows.Controls;
using MessageBox = System.Windows.MessageBox;

namespace DesktopPet;

internal sealed class MonitorSystemToolsPage : W.Expander
{
    private readonly MonitorService _service;
    private readonly W.TextBlock _status = ToolsWindow.Text("");
    private readonly W.CheckBox _awake;
    public MonitorSystemToolsPage(MonitorService service)
    {
        _service = service; Header = "系统工具"; var panel = new W.StackPanel(); Content = panel;
        _awake = new() { Content = "本次运行期间阻止自动睡眠和熄屏", IsChecked = service.SystemTools.PreventSleep };
        _awake.Click += (_, _) => { if (!service.SystemTools.SetPreventSleep(_awake.IsChecked == true)) { _awake.IsChecked = service.SystemTools.PreventSleep; _status.Text = "无法更改本次睡眠请求。"; } };
        panel.Children.Add(_awake);
        var actions = new W.WrapPanel(); panel.Children.Add(actions);
        void Action(W.Button button) { button.Width = 200; actions.Children.Add(button); }
        Action(ToolsWindow.Button("关闭显示器", async () =>
        {
            var window = Window.GetWindow(this); if (window is null) return;
            await MonitorSystemTools.TurnOffDisplay(new WindowInteropHelper(window).Handle);
        }));
        Action(ToolsWindow.Button("打开任务管理器", () => Run(() => MonitorSystemTools.StartSystem("Taskmgr.exe"))));
        Action(ToolsWindow.Button("刷新桌面图标", () => Run(() => MonitorSystemTools.StartSystem("ie4uinit.exe", ["-show"]))));
        Action(ToolsWindow.Button("清理系统临时文件", () => Run(() =>
            Process.Start(new ProcessStartInfo("ms-settings:storagesense") { UseShellExecute = true })?.Dispose())));
        Action(ToolsWindow.Button("重启资源管理器", async () =>
        {
            if (!Confirm("将关闭当前资源管理器窗口并短暂刷新任务栏。正在进行的文件操作可能中断，请先保存。继续？")) return;
            try { await MonitorSystemTools.RestartExplorer(); }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or OperationCanceledException) { _status.Text = "资源管理器重启失败。"; }
        }));
        var minutes = new W.TextBox { Text = "60", MaxLength = 4 }; panel.Children.Add(ToolsWindow.Text("关机延时（1–1440 分钟）")); panel.Children.Add(minutes);
        panel.Children.Add(ToolsWindow.Button("设置定时关机", () =>
        {
            if (!int.TryParse(minutes.Text, out var value) || value is < 1 or > 1440) { _status.Text = "请输入 1–1440 分钟。"; return; }
            if (!Confirm(MonitorLocalizer.Language.Format(service.Config.Language, "将在 {0} 分钟后请求关机。请保存所有程序的工作；可在这里取消，退出 Pet 会取消本次计划。继续？", value))) return;
            service.SystemTools.Shutdown.Schedule(value, Environment.TickCount64); Refresh();
        }));
        panel.Children.Add(ToolsWindow.Button("取消本程序的定时关机", () => { service.SystemTools.Shutdown.Cancel(); Refresh(); }));
        panel.Children.Add(_status);
        service.SystemTools.Changed += Refresh;
        Unloaded += (_, _) => service.SystemTools.Changed -= Refresh;
        Loaded += (_, _) => { service.SystemTools.Changed -= Refresh; service.SystemTools.Changed += Refresh; Refresh(); };
    }
    private bool Confirm(string text) => MessageBox.Show(Window.GetWindow(this), MonitorLocalizer.Language.Text(_service.Config.Language, text), MonitorLocalizer.Language.Text(_service.Config.Language, "系统工具"), MessageBoxButton.OKCancel, MessageBoxImage.Warning) == MessageBoxResult.OK;
    private void Run(Action action)
    {
        try { action(); }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { _status.Text = "系统工具无法启动。"; }
    }
    private void Refresh()
    {
        var tools = _service.SystemTools; _awake.IsChecked = tools.PreventSleep;
        _status.Text = tools.Error ?? (tools.Shutdown.Due is null ? "未设置定时关机" : MonitorLocalizer.Language.Format(_service.Config.Language, "定时关机剩余 {0} 秒", tools.Shutdown.Remaining(Environment.TickCount64)));
    }
}
