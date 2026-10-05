using DesktopPet.Monitoring;
using DesktopPet.Tools;
using W = System.Windows.Controls;

namespace DesktopPet;

internal sealed class MonitorVisualEditor : W.StackPanel
{
    private static readonly (string Property, string Label)[] Fields = [
        ("RowHeight", "指标行最小高度（0 为自动）"), ("Padding", "内容边距"), ("CornerRadius", "边框圆角（0 保留像素外观）"),
        ("ShowGroups", "显示设备分组"), ("GroupRadius", "分组圆角"), ("GroupPadding", "分组内边距"), ("GroupSpacing", "分组间距"),
        ("GroupBottom", "分组标题下边距"), ("GroupTitleOffset", "分组标题上边距"), ("ValueFamily", "数值字体"), ("TitleSize", "标题字号"),
        ("GroupSize", "分组字号"), ("ValueSize", "数值字号"), ("Bold", "字体加粗"), ("TitleColor", "标题颜色"), ("GroupColor", "分组标题颜色"),
        ("GroupBackground", "分组背景颜色"), ("ShowBars", "显示可比较指标的进度条"), ("BarBackground", "进度条背景颜色"),
        ("BarLow", "进度条正常颜色"), ("BarMid", "进度条警告颜色"), ("BarHigh", "进度条严重颜色"),
        ("SmoothValues", "数值平滑过渡（提醒仍使用原始数据）"), ("SmoothMs", "过渡时长（50–2000 毫秒）")];
    public MonitorVisualEditor(MonitorService service)
    {
        foreach (var field in Fields)
        {
            var property = typeof(MonitorVisualStyle).GetProperty(field.Property)!;
            if (property.PropertyType == typeof(bool))
                Children.Add(MonitorSettings.Toggle(service, field.Label, () => (bool)property.GetValue(service.Config.Visual)!,
                    v => property.SetValue(service.Config.Visual, v)));
            else if (property.PropertyType == typeof(string))
                Children.Add(MonitorSettings.Text(service, field.Label, () => (string)property.GetValue(service.Config.Visual)!,
                    v => property.SetValue(service.Config.Visual, v), s => field.Property == "ValueFamily" ? !string.IsNullOrWhiteSpace(s) : MonitorStore.ValidColor(s),
                    field.Property == "ValueFamily" ? "请输入有效字体名称。" : "请输入 #RRGGBB 颜色。"));
            else
            {
                var size = field.Property.EndsWith("Size");
                Children.Add(MonitorSettings.Number(service, field.Label, () => Convert.ToDouble(property.GetValue(service.Config.Visual)),
                    v => property.SetValue(service.Config.Visual, property.PropertyType == typeof(int) ? (object)(int)v : v),
                    field.Property == "SmoothMs" ? 50 : size ? 8 : 0, field.Property == "SmoothMs" ? 2000 : size ? 32 : 120,
                    v => property.PropertyType != typeof(int) || v == Math.Truncate(v)));
            }
        }
        Children.Add(ToolsWindow.Text("分类颜色阈值（不改变提醒规则）"));
        var category = "Load"; var area = new W.StackPanel();
        void Category()
        {
            area.Children.Clear();
            var factor = category == "NetKBps" ? 1048576d : 1;
            MonitorColorThreshold Threshold() => service.Config.Visual.Thresholds.GetValueOrDefault(category)
                ?? (category == "Temp" ? new(service.Config.Cpu.Threshold - 5, service.Config.Cpu.Threshold)
                : category == "NetKBps" ? new(service.Config.RateWarning * factor, service.Config.RateCritical * factor)
                : new(service.Config.LoadWarning, service.Config.LoadCritical));
            var enabled = MonitorSettings.Toggle(service, "使用所选分类的独立颜色阈值",
                () => service.Config.Visual.Thresholds.ContainsKey(category), value =>
                { if (value) service.Config.Visual.Thresholds[category] = Threshold(); else service.Config.Visual.Thresholds.Remove(category); });
            area.Children.Add(enabled);
            var maximum = category == "Temp" ? 130 : category == "NetKBps" ? 100000 : 100;
            var warn = MonitorSettings.Number(service, "警告阈值", () => Threshold().Warn / factor,
                v => service.Config.Visual.Thresholds[category] = Threshold() with { Warn = v * factor }, 0, maximum,
                v => v * factor < Threshold().Crit);
            var critical = MonitorSettings.Number(service, "严重阈值", () => Threshold().Crit / factor,
                v => service.Config.Visual.Thresholds[category] = Threshold() with { Crit = v * factor }, 0, maximum,
                v => v * factor > Threshold().Warn);
            area.Children.Add(warn); area.Children.Add(critical);
            MonitorSettings.Watch(area, service, () =>
            { warn.IsEnabled = critical.IsEnabled = service.Store.Available && service.Config.Visual.Thresholds.ContainsKey(category); });
        }
        var choices = new W.ComboBox { ItemsSource = new[] { "Load", "Temp", "Vram", "Mem", "NetKBps" }, SelectedIndex = 0 };
        choices.SelectionChanged += (_, _) => { category = (string)choices.SelectedItem; Category(); };
        Children.Add(choices); Children.Add(area); Category();
    }
}