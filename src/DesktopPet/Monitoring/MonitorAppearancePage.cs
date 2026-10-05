using System.IO;
using System.Windows;
using DesktopPet.Tools;
using DesktopPet.Monitoring;
using W = System.Windows.Controls;

namespace DesktopPet;

internal sealed class MonitorAppearancePage : W.StackPanel
{
    public MonitorAppearancePage(MonitorService service)
    {
        var general = new W.StackPanel(); general.Children.Add(ToolsWindow.Text("语言"));
        var languages = MonitorSettings.Choice(service, MonitorLanguage.Codes, () => service.Config.Language, v => service.Config.Language = v,
            code => MonitorLocalizer.Language.LanguageName(service.Config.Language, code));
        languages.Name = "MonitorLanguageChoice";
        general.Children.Add(languages); Children.Add(MonitorFluentStyle.Card(general));
        var colors = new W.StackPanel();
        foreach (var field in new[] { ("Background", "背景色"), ("Foreground", "指标名称颜色"), ("SafeColor", "正常数值色"),
            ("WarningColor", "警告数值色"), ("CriticalColor", "严重数值色") })
        {
            var property = typeof(MonitorConfig).GetProperty(field.Item1)!;
            colors.Children.Add(MonitorSettings.Text(service, field.Item2 + "（#RRGGBB）", () => (string)property.GetValue(service.Config)!,
                v => property.SetValue(service.Config, v), MonitorStore.ValidColor, "请输入 #RRGGBB 颜色。", 7));
        }
        Children.Add(MonitorFluentStyle.Card(new W.Expander { Header = "主题配色", Content = colors }));
        var typography = new W.StackPanel();
        typography.Children.Add(MonitorSettings.Text(service, "指标名称字体", () => service.Config.FontFamily, v => service.Config.FontFamily = v,
            s => !string.IsNullOrWhiteSpace(s), "请输入有效字体名称。"));
        typography.Children.Add(MonitorSettings.Number(service, "指标名称字号（8–32）", () => service.Config.FontSize, v => service.Config.FontSize = v, 8, 32));
        typography.Children.Add(MonitorSettings.Number(service, "行间距（0–30）", () => service.Config.RowSpacing, v => service.Config.RowSpacing = v, 0, 30));
        typography.Children.Add(MonitorSettings.Number(service, "面板宽度（240–900）", () => service.Config.PanelWidth, v => service.Config.PanelWidth = v, 240, 900));
        Children.Add(MonitorFluentStyle.Card(new W.Expander { Header = "字体与尺寸", Content = typography }));
        var thresholds = new W.StackPanel();
        thresholds.Children.Add(MonitorSettings.Number(service, "负载警告阈值（%）", () => service.Config.LoadWarning, v => service.Config.LoadWarning = v,
            0, 100, v => v < service.Config.LoadCritical));
        thresholds.Children.Add(MonitorSettings.Number(service, "负载严重阈值（%）", () => service.Config.LoadCritical, v => service.Config.LoadCritical = v,
            0, 100, v => v > service.Config.LoadWarning));
        thresholds.Children.Add(MonitorSettings.Number(service, "网络／磁盘速率警告阈值（MB/s）", () => service.Config.RateWarning, v => service.Config.RateWarning = v,
            0, 100000, v => v < service.Config.RateCritical));
        thresholds.Children.Add(MonitorSettings.Number(service, "网络／磁盘速率严重阈值（MB/s）", () => service.Config.RateCritical, v => service.Config.RateCritical = v,
            0, 100000, v => v > service.Config.RateWarning));
        Children.Add(MonitorFluentStyle.Card(new W.Expander { Header = "数值颜色阈值", Content = thresholds }));
        Children.Add(MonitorFluentStyle.Card(new W.Expander { Header = "分组、进度条与动画", Content = new MonitorVisualEditor(service) }));
        var themes = new W.StackPanel(); themes.Children.Add(MonitorFluentStyle.Heading("收藏主题", 16));
        var status = ToolsWindow.Text(""); themes.Children.Add(status);
        var actions = new W.WrapPanel(); themes.Children.Add(actions); Children.Add(MonitorFluentStyle.Card(themes));
        actions.Children.Add(ToolsWindow.Button("导入主题 JSON", () =>
        {
            var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "主题 JSON|*.json" };
            if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
            try
            {
                if (new FileInfo(dialog.FileName).Length > 128 * 1024) throw new InvalidDataException();
                MonitorTheme.Import(File.ReadAllText(dialog.FileName), service.Config);
                status.Text = service.Save() ? "主题已导入。" : service.Status;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException
                or InvalidOperationException or KeyNotFoundException or FormatException or ArgumentException)
            { status.Text = "主题不兼容或参数无效；原主题文件保留。"; }
        }));
        actions.Children.Add(ToolsWindow.Button("导出主题 JSON", () =>
        {
            var dialog = new Microsoft.Win32.SaveFileDialog { Filter = "主题 JSON|*.json", FileName = "Pet-theme.json" };
            if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
            try { File.WriteAllText(dialog.FileName, MonitorTheme.Export(service.Config)); status.Text = "主题已导出。"; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { status.Text = "主题导出失败。"; }
        }));
    }
}
