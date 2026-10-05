using DesktopPet.Tools;
using System.Windows;
using W = System.Windows.Controls;

namespace DesktopPet;

internal sealed class MonitorExtrasPage : W.StackPanel
{
    public MonitorExtrasPage(MonitorService service)
    {
        Children.Add(MonitorFluentStyle.Card(new MonitorSystemToolsPage(service)));
        var memory = new W.StackPanel(); memory.Children.Add(MonitorFluentStyle.Heading("Pet 内存", 16));
        var status = ToolsWindow.Text(""); memory.Children.Add(status);
        memory.Children.Add(ToolsWindow.Button("整理 Pet 自身内存", () =>
        { status.Text = MonitorMemory.TrimOwn() ? "Pet 的工作集已整理；没有清理 ObsUI 或其他程序。" : "当前权限无法整理工作集。"; }));
        memory.Children.Add(MonitorSettings.Toggle(service, "定时整理 Pet 自身内存", () => service.Config.AutoCleanOwnMemory, v => service.Config.AutoCleanOwnMemory = v));
        memory.Children.Add(MonitorSettings.Number(service, "整理间隔（分钟，5–1440）", () => service.Config.CleanMinutes,
            v => service.Config.CleanMinutes = (int)v, 5, 1440, v => v == Math.Truncate(v)));
        Children.Add(MonitorFluentStyle.Card(memory));
        var traffic = ToolsWindow.Text(""); Children.Add(MonitorFluentStyle.Card(traffic));
        MonitorSettings.Watch(traffic, service, () =>
        {
            var day = service.Traffic.Days.GetValueOrDefault(DateTimeOffset.Now.ToString("yyyy-MM-dd"));
            MonitorLocalizer.Formatted(traffic, "今日流量：上传 {0} MB · 下载 {1} MB（本程序运行期间）",
                ((day?.UploadBytes ?? 0) / 1048576d).ToString("0.##"), ((day?.DownloadBytes ?? 0) / 1048576d).ToString("0.##"));
        });
    }
}
