using System.Windows;
using DesktopPet.Tools;
using LiteMonitor.src.Core;
using LiteMonitor.src.Plugins;
using W = System.Windows.Controls;
using MessageBox = System.Windows.MessageBox;

namespace DesktopPet;

internal sealed class PluginPage : W.StackPanel
{
    private readonly MonitorService _service;
    private readonly W.StackPanel _instances = new();
    private readonly W.ComboBox _templates = new();
    private readonly W.TextBlock _status = ToolsWindow.Text("");
    public PluginPage(MonitorService service)
    {
        _service = service;
        Children.Add(ToolsWindow.Text("自定义插件", 20));
        var enabled = MonitorSettings.Toggle(service, "启用联网插件", () => service.Config.PluginsEnabled, v => service.Config.PluginsEnabled = v, () => service.Plugins.Error is null);
        enabled.IsEnabled = service.Plugins.Error is null && service.Store.Available; Children.Add(enabled);
        Children.Add(_status); Children.Add(_templates);
        Children.Add(ToolsWindow.Button("添加插件实例", () => { if (_templates.SelectedItem is PluginTemplate t) Edit(null, t); }));
        Children.Add(ToolsWindow.Button("导入 JSON 模板", () =>
        {
            var dialog = new Microsoft.Win32.OpenFileDialog { Filter = MonitorLocalizer.Language.Text(service.Config.Language, "JSON 模板") + "|*.json" };
            if (dialog.ShowDialog(Window.GetWindow(this)) == true)
            { _status.Text = service.Plugins.Import(dialog.FileName) ? "模板已导入，尚未启用。" : "导入失败：模板损坏、过大或标识已存在。"; Refresh(); }
        })); Children.Add(_instances); Refresh();
    }
    private void Refresh()
    {
        _templates.ItemsSource = _service.Plugins.Templates.ToArray(); _templates.DisplayMemberPath = "Meta.Name";
        if (_templates.Items.Count > 0) _templates.SelectedIndex = 0;
        if (_service.Plugins.Error is not null) _status.Text = _service.Plugins.Error;
        _instances.Children.Clear();
        foreach (var instance in _service.Plugins.Instances)
        {
            var template = _service.Plugins.Templates.FirstOrDefault(t => t.Id == instance.TemplateId);
            var row = new W.WrapPanel(); var enabled = new W.CheckBox { Content = template?.Meta.Name ?? instance.TemplateId, IsChecked = instance.Enabled };
            MonitorLocalizer.Preserve(enabled);
            enabled.Click += (_, _) =>
            {
                var previous = instance.Enabled; instance.Enabled = enabled.IsChecked == true;
                if (!_service.Plugins.Save())
                { instance.Enabled = previous; enabled.IsChecked = previous; _status.Text = _service.Plugins.Error ?? _service.Plugins.SaveError ?? "插件保存失败。"; }
                else _service.Save();
            };
            row.Children.Add(enabled);
            if (template is not null) row.Children.Add(ToolsWindow.Button("编辑", () => Edit(instance, template)));
            row.Children.Add(ToolsWindow.Button("移除实例", () =>
            {
                if (MessageBox.Show(Window.GetWindow(this), MonitorLocalizer.Language.Text(_service.Config.Language, "移除此插件实例及其保存的输入配置？模板保留。"), MonitorLocalizer.Language.Text(_service.Config.Language, "移除插件"), MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;
                _service.Plugins.Instances.Remove(instance); _service.Plugins.Save(); _service.Save(); Refresh();
            })); _instances.Children.Add(row);
        }
    }
    private void Edit(PluginInstanceConfig? original, PluginTemplate template)
    {
        var owner = Window.GetWindow(this);
        var window = new Window { Owner = owner, Title = "艾莲布 · " + template.Meta.Name, Width = 480, Height = 650,
            Background = System.Windows.Media.Brushes.Cornsilk, ShowInTaskbar = false };
        MonitorFluentStyle.Apply(window);
        var page = new W.StackPanel { Margin = new Thickness(20) };
        var global = new Dictionary<string, Func<string>>(); var targets = new List<Dictionary<string, Func<string>>>();
        void AddInput(W.Panel panel, PluginInput input, Dictionary<string, string>? values, Dictionary<string, Func<string>> getters)
        {
            var label = ToolsWindow.Text(input.Label); MonitorLocalizer.Preserve(label); panel.Children.Add(label);
            var value = values?.GetValueOrDefault(input.Key) ?? input.DefaultValue;
            if (input.Type == "password")
            {
                var control = new W.PasswordBox { Password = value, MaxLength = 1000 }; panel.Children.Add(control); getters[input.Key] = () => control.Password;
            }
            else if (input.Type == "select" && input.Options is { Count: > 0 } options)
            {
                var control = new W.ComboBox { ItemsSource = options, DisplayMemberPath = "Label", SelectedItem = options.FirstOrDefault(o => o.Value == value) ?? options[0] };
                MonitorLocalizer.Preserve(control);
                panel.Children.Add(control); getters[input.Key] = () => (control.SelectedItem as PluginInputOption)?.Value ?? "";
            }
            else
            {
                var control = new W.TextBox { Text = value, MaxLength = 1000, ToolTip = input.Placeholder };
                panel.Children.Add(control); getters[input.Key] = () => control.Text;
            }
        }
        foreach (var input in template.Inputs.Where(i => i.Scope != "target")) AddInput(page, input, original?.InputValues, global);
        var targetArea = new W.StackPanel(); page.Children.Add(targetArea);
        void AddTarget(Dictionary<string, string>? values)
        {
            if (targets.Count >= 100) return;
            var group = new W.StackPanel { Margin = new Thickness(0, 12, 0, 12) }; var getters = new Dictionary<string, Func<string>>();
            group.Children.Add(ToolsWindow.Text("监控目标", 15));
            foreach (var input in template.Inputs.Where(i => i.Scope == "target")) AddInput(group, input, values, getters);
            group.Children.Add(ToolsWindow.Button("移除此目标", () => { targetArea.Children.Remove(group); targets.Remove(getters); }));
            targets.Add(getters); targetArea.Children.Add(group);
        }
        if (template.Inputs.Any(i => i.Scope == "target"))
        {
            if (original?.Targets.Count > 0) foreach (var target in original.Targets) AddTarget(target);
            else AddTarget(null);
            page.Children.Add(ToolsWindow.Button("添加目标", () => AddTarget(null)));
        }
        var enabled = new W.CheckBox { Content = "启用此实例", IsChecked = original?.Enabled == true }; page.Children.Add(enabled);
        if (original is not null) enabled.Click += (_, _) =>
        {
            var previous = original.Enabled; original.Enabled = enabled.IsChecked == true;
            if (!_service.Plugins.Save())
            {
                original.Enabled = previous; enabled.IsChecked = previous;
                MessageBox.Show(window, _service.Plugins.Error ?? _service.Plugins.SaveError ?? "插件保存失败。");
            }
            else _service.Save();
        };
        page.Children.Add(ToolsWindow.Button("保存", () =>
        {
            var replacement = new PluginInstanceConfig { Id = original?.Id ?? Guid.NewGuid().ToString("N"), TemplateId = template.Id,
                Enabled = enabled.IsChecked == true, InputValues = global.ToDictionary(p => p.Key, p => p.Value()),
                Targets = targets.Select(t => t.ToDictionary(p => p.Key, p => p.Value())).ToList() };
            if (original is not null) _service.Plugins.Instances.Remove(original);
            _service.Plugins.Instances.Add(replacement);
            if (_service.Plugins.Save()) { _service.Save(); window.Close(); Refresh(); }
            else
            {
                _service.Plugins.Instances.Remove(replacement); if (original is not null) _service.Plugins.Instances.Add(original);
                MessageBox.Show(window, MonitorLocalizer.Language.Text(_service.Config.Language, _service.Plugins.Error ?? _service.Plugins.SaveError ?? "插件保存失败。"));
            }
        }));
        window.Content = new W.ScrollViewer { Content = page, VerticalScrollBarVisibility = W.ScrollBarVisibility.Auto };
        MonitorLocalizer.Attach(window, _service);
        window.ShowDialog();
    }
}
