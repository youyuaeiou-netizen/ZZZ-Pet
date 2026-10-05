using System.Diagnostics;
using System.IO;
using DesktopPet.Tools;
using Microsoft.Win32;
using W = System.Windows.Controls;

namespace DesktopPet;

internal sealed class MonitorDriverPage : W.Expander
{
    public MonitorDriverPage()
    {
        Header = "硬件驱动状态"; var page = new W.StackPanel(); Content = page;
        var status = ToolsWindow.Text(""); page.Children.Add(status);
        void Refresh()
        {
            try
            {
                using var registry = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
                using var installed = registry.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO");
                using var session = registry.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager");
                var reboot = session?.GetValue("PendingFileRenameOperations") is string[] pending && pending.Any(p => p.Contains("PawnIO", StringComparison.OrdinalIgnoreCase));
                var message = installed?.GetValue("DisplayVersion") is string ? "PawnIO 已登记版本：{0}（不代表当前传感器可用）" : "未找到已登记的 PawnIO；部分温度／电压可能不可用。";
                if (reboot) message += "\nPawnIO 文件变更等待系统重启。";
                MonitorLocalizer.Formatted(status, message, installed?.GetValue("DisplayVersion") as string ?? "");
            }
            catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException or IOException)
            { status.Text = "无法读取已安装驱动状态。"; }
        }
        page.Children.Add(ToolsWindow.Button("刷新驱动状态", Refresh));
        page.Children.Add(ToolsWindow.Button("打开 PawnIO 官方下载页", () =>
        {
            try { Process.Start(new ProcessStartInfo("https://pawnio.eu/") { UseShellExecute = true })?.Dispose(); }
            catch (System.ComponentModel.Win32Exception) { status.Text = "无法打开默认浏览器。"; }
        }));
        Refresh();
    }
}
