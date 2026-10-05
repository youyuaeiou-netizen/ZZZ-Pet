using DesktopPet.Tools;
using System.Windows;
using W = System.Windows.Controls;

namespace DesktopPet;

internal sealed class MonitorDisplayPage : W.StackPanel
{
    public MonitorDisplayPage(MonitorService service)
    {
        Margin = new Thickness(12);
        Children.Add(ToolsWindow.Text("宠物气泡", 20));
        Children.Add(MonitorSettings.Toggle(service, "显示独立监控窗", () => service.Config.DisplayEnabled, v => service.Config.DisplayEnabled = v));
        var taskbar = new W.StackPanel();
        taskbar.Children.Add(MonitorSettings.Toggle(service, "任务栏显示", () => service.Config.TaskbarEnabled, v => service.Config.TaskbarEnabled = v));
        var heading = new W.DockPanel { Margin = new Thickness(0, 8, 0, 0) };
        var percent = ToolsWindow.Text(""); W.DockPanel.SetDock(percent, W.Dock.Right); heading.Children.Add(percent);
        heading.Children.Add(ToolsWindow.Text("任务栏背景不透明度")); taskbar.Children.Add(heading);
        var slider = new W.Slider { Name = "TaskbarOpacitySlider", Minimum = 0, Maximum = 100, TickFrequency = 1,
            IsSnapToTickEnabled = true, SmallChange = 1, LargeChange = 10, IsMoveToPointEnabled = true };
        slider.SetResourceReference(StyleProperty, "MonitorHistorySlider"); taskbar.Children.Add(slider);
        var error = ToolsWindow.Text(""); error.Foreground = System.Windows.Media.Brushes.Firebrick;
        error.Visibility = Visibility.Collapsed; taskbar.Children.Add(error);
        var updating = false;
        MonitorSettings.Watch(slider, service, () =>
        {
            updating = true; slider.Value = service.Config.TaskbarBackgroundOpacity * 100; percent.Text = $"{slider.Value:0}%";
            slider.IsEnabled = service.Store.Available;
            if (!service.Store.Available) { error.Text = service.Status; error.Visibility = Visibility.Visible; }
            updating = false;
        });
        slider.ValueChanged += (_, _) =>
        {
            if (updating) return;
            service.Config.TaskbarBackgroundOpacity = slider.Value / 100;
            var saved = service.Save();
            error.Text = saved ? "" : service.Status; error.Visibility = saved ? Visibility.Collapsed : Visibility.Visible;
        };
        Children.Add(MonitorFluentStyle.Card(taskbar));
        Children.Add(MonitorSettings.Number(service, "界面缩放（0.5–2.5）", () => service.Config.UiScale, v => service.Config.UiScale = v, .5, 2.5));
        Children.Add(MonitorSettings.Number(service, "透明度（0.1–1）", () => service.Config.Opacity, v => service.Config.Opacity = v, .1, 1));
        Children.Add(MonitorSettings.Toggle(service, "双列布局", () => service.Config.Horizontal, v => service.Config.Horizontal = v));
    }
}
