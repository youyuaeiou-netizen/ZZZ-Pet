using System.Windows;
using System.Windows.Input;
using DesktopPet.Tools;
using W = System.Windows.Controls;

namespace DesktopPet.Search;

internal sealed class SearchAliasWindow : Window
{
    private readonly W.TextBox _input = new() { MaxLength = 200 };
    internal string Alias => _input.Text.Trim();
    internal SearchAliasWindow(string name)
    {
        Title = "添加检索别名"; Width = 380; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 240, 247));
        PixelFrame.Apply(this);
        var panel = new W.StackPanel { Margin = new Thickness(18) }; panel.Children.Add(ToolsWindow.Text(name, 15)); panel.Children.Add(_input);
        var row = new W.WrapPanel(); var save = ToolsWindow.Button("保存", () => DialogResult = true); save.IsDefault = true; save.IsEnabled = false;
        var cancel = ToolsWindow.Button("取消", () => DialogResult = false); cancel.IsCancel = true;
        _input.TextChanged += (_, _) => save.IsEnabled = Alias.Length > 0;
        row.Children.Add(save); row.Children.Add(cancel); panel.Children.Add(row); Content = panel;
        Loaded += (_, _) => _input.Focus();
        System.Windows.Automation.AutomationProperties.SetName(_input, "文件或应用的中英文别名");
    }
}
