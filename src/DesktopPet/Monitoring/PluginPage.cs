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
    private readonly W.TextBlock _status = ToolsWindow.Text("");
    private readonly W.StackPanel _body = new();
    private bool _modelOpen;
    private readonly System.Windows.Threading.DispatcherTimer _refreshTimer = new() { Interval = TimeSpan.FromMilliseconds(180) };
    public PluginPage(MonitorService service)
    {
        _service = service;
        Children.Add(_status); Children.Add(_body);
        _refreshTimer.Tick += (_, _) => { _refreshTimer.Stop(); Refresh(); };
        Loaded += (_, _) => { LocalModelExtension.Current.Changed += ScheduleRefresh; Refresh(); };
        Unloaded += (_, _) => { LocalModelExtension.Current.Changed -= ScheduleRefresh; _refreshTimer.Stop(); };
        Refresh();
    }
    private void ScheduleRefresh() { _refreshTimer.Stop(); _refreshTimer.Start(); }
    private void Run(Action action, bool animate = false)
    {
        try { action(); _status.Text = ""; }
        catch (Exception ex) { _status.Text = ex.Message; }
        if (animate) ScheduleRefresh(); else Refresh();
    }
    private void Refresh()
    {
        _body.Children.Clear();
        if (_service.Plugins.Error is not null) _status.Text = _service.Plugins.Error;
        var plugin = LocalModelExtension.Current;
        if (_modelOpen && plugin.Installed)
        {
            _body.Children.Add(ToolsWindow.Button("‹ 已安装的插件", () => { _modelOpen = false; Refresh(); }));
            _body.Children.Add(ToolsWindow.Text("本地模型控制 · " + plugin.Version, 20));
            _body.Children.Add(ToolsWindow.Button("检查更新／导入插件包", ShowInstall));
            var toggle = Switch("启用本地模型控制", plugin.Enabled, SetModelEnabled, plugin.Error is null);
            var heading = new W.WrapPanel(); heading.Children.Add(toggle); heading.Children.Add(ToolsWindow.Text(plugin.Enabled ? "已打开" : "已关闭")); _body.Children.Add(heading);
            _body.Children.Add(ToolsWindow.Text("关闭或卸载停用 Pet 管理入口，保留模型与设置；DSH/OpenCode 的独立服务继续运行。"));
            if (plugin.Enabled) _body.Children.Add(new LocalModelPage());
            return;
        }
        _modelOpen = false;
        var toolbar = new W.DockPanel { Margin = new Thickness(0, 0, 0, 12) };
        var install = ToolsWindow.Button("安装插件", ShowInstall);
        W.DockPanel.SetDock(install, W.Dock.Right); toolbar.Children.Add(install);
        toolbar.Children.Add(MonitorFluentStyle.Hint("已安装")); _body.Children.Add(toolbar);
        if (plugin.Installed)
            AddRow("◈", "本地模型控制", plugin.Enabled, () => { _modelOpen = true; Refresh(); }, SetModelEnabled,
                () => { plugin.Uninstall(); _modelOpen = false; }, plugin.Error is null,
                "卸载本地模型控制？模型、设置和独立服务将保留，可重新安装。");
        foreach (var instance in _service.Plugins.Instances)
        {
            var template = _service.Plugins.Templates.FirstOrDefault(t => t.Id == instance.TemplateId);
            AddRow("▦", template?.Meta.Name ?? instance.TemplateId, instance.Enabled && _service.Config.PluginsEnabled,
                () => { if (template is not null) Edit(instance, template); }, next =>
            {
                var previous = instance.Enabled;
                var previousNetwork = _service.Config.PluginsEnabled;
                instance.Enabled = next;
                if (!_service.Plugins.Save())
                { instance.Enabled = previous; throw new InvalidOperationException(_service.Plugins.Error ?? _service.Plugins.SaveError ?? "插件保存失败。"); }
                if (next) _service.Config.PluginsEnabled = true;
                if (!_service.Save())
                { instance.Enabled = previous; _service.Config.PluginsEnabled = previousNetwork; _service.Plugins.Save(); throw new InvalidOperationException(_service.Status); }
            }, () =>
            {
                var index = _service.Plugins.Instances.IndexOf(instance);
                _service.Plugins.Instances.Remove(instance);
                if (!_service.Plugins.Save())
                { _service.Plugins.Instances.Insert(index, instance); throw new InvalidOperationException(_service.Plugins.SaveError ?? _service.Plugins.Error ?? "卸载失败。"); }
                _service.Save();
            }, _service.Plugins.Error is null && _service.Store.Available);
        }
        if (!plugin.Installed && _service.Plugins.Instances.Count == 0) _body.Children.Add(MonitorFluentStyle.Hint("尚未安装插件。点击“安装插件”选择扩展功能。"));
    }
    private void SetModelEnabled(bool enabled)
    {
        LocalModelExtension.Current.SetEnabled(enabled);
        if (enabled) LocalModelPage.StartGateway();
    }
    private W.CheckBox Switch(string name, bool enabled, Action<bool> change, bool available = true)
    {
        var toggle = new W.CheckBox { IsChecked = enabled, IsEnabled = available, ToolTip = name, VerticalAlignment = VerticalAlignment.Center };
        toggle.SetResourceReference(StyleProperty, "PluginSwitch");
        System.Windows.Automation.AutomationProperties.SetName(toggle, name);
        toggle.Click += (_, _) => Run(() => change(toggle.IsChecked == true), animate: true);
        return toggle;
    }
    private void AddRow(string icon, string name, bool enabled, Action open, Action<bool> change, Action remove, bool available, string? removeMessage = null)
    {
        var row = new W.Grid { Margin = new Thickness(0, 3, 0, 3) };
        foreach (var width in new[] { new GridLength(32), new GridLength(1, GridUnitType.Star), new GridLength(44), new GridLength(38) }) row.ColumnDefinitions.Add(new W.ColumnDefinition { Width = width });
        var symbol = ToolsWindow.Text(icon, 19); symbol.VerticalAlignment = VerticalAlignment.Center; row.Children.Add(symbol);
        var title = ToolsWindow.Button(name, open); title.HorizontalContentAlignment = System.Windows.HorizontalAlignment.Left;
        title.Background = System.Windows.Media.Brushes.Transparent; title.BorderThickness = new Thickness(0); title.Margin = new Thickness(0); title.Padding = new Thickness(4, 10, 4, 10);
        W.Grid.SetColumn(title, 1); row.Children.Add(title);
        var toggle = Switch("启用 " + name, enabled, change, available); W.Grid.SetColumn(toggle, 2); row.Children.Add(toggle);
        var more = ToolsWindow.Button("⋯", () => { }); more.Margin = new Thickness(0); more.Padding = new Thickness(4); more.Background = System.Windows.Media.Brushes.Transparent; more.ToolTip = "插件操作";
        var menu = new W.ContextMenu();
        var details = new W.MenuItem { Header = "打开详情" }; details.Click += (_, _) => open(); menu.Items.Add(details);
        var uninstall = new W.MenuItem { Header = "卸载插件", IsEnabled = available };
        uninstall.Click += (_, _) =>
        {
            if (MessageBox.Show(Window.GetWindow(this), removeMessage ?? $"卸载“{name}”及其保存的输入配置？插件模板将保留。", "卸载插件", MessageBoxButton.OKCancel) == MessageBoxResult.OK) Run(remove);
        };
        menu.Items.Add(uninstall); more.ContextMenu = menu;
        more.Click += (_, _) => { menu.PlacementTarget = more; menu.IsOpen = true; };
        W.Grid.SetColumn(more, 3); row.Children.Add(more); _body.Children.Add(row);
    }
    private void ShowInstall()
    {
        var window = new Window { Owner = Window.GetWindow(this), Title = "安装插件", Width = 440, Height = 440, ShowInTaskbar = false };
        MonitorFluentStyle.Apply(window);
        var options = new W.StackPanel { Margin = new Thickness(20) };
        options.Children.Add(ToolsWindow.Text("可安装的插件", 20));
        options.Children.Add(ToolsWindow.Text("本地模型控制 · 官方 GitHub 插件市场"));
        var progress = ToolsWindow.Text(""); options.Children.Add(progress);
        using var cancellation = new CancellationTokenSource();
        var installing = false;
        async Task Install(Func<Task<(string Directory, string Version)>> action)
        {
            if (installing) return; installing = true;
            try
            {
                progress.Text = "正在检查插件包…";
                var package = await action();
                LocalModelExtension.Current.InstallPackage(package.Directory, package.Version);
                if (PetLocalModels.ModelRuntime.Read().LoginStartup) await LocalModelSetup.RegisterStartup(true);
                else if (!System.IO.File.Exists(PetLocalModels.ModelRuntime.ConfigPath)) await LocalModelSetup.RegisterStartup(true, existingOnly: true);
                await LocalModelPage.RestartGateway();
                progress.Text = "插件已安装。打开详情进行环境初始化和客户端接入。"; Refresh();
            }
            catch (OperationCanceledException) { progress.Text = "安装已取消，原版本保留。"; }
            catch (Exception ex) { progress.Text = ex.Message; }
            finally { installing = false; }
        }
        options.Children.Add(ToolsWindow.Button(LocalModelExtension.Current.Installed ? "检查更新并安装" : "从市场下载安装", async () => await Install(async () =>
        {
            var catalog = await LocalModelPackage.FetchCatalog(cancellation.Token);
            progress.Text = "正在下载 " + catalog.Version;
            return await LocalModelPackage.Download(catalog, new Progress<string>(text => progress.Text = text), cancellation.Token);
        })));
        options.Children.Add(ToolsWindow.Button("导入本地模型插件包…", async () =>
        {
            var picker = new Microsoft.Win32.OpenFileDialog { Filter = "Pet 本地模型插件包|*.zip" };
            if (picker.ShowDialog(window) == true) await Install(() => LocalModelPackage.Install(picker.FileName, cancellation.Token));
        }));
        options.Children.Add(ToolsWindow.Button("取消下载／安装", () => { if (installing) cancellation.Cancel(); }));
        window.Closing += (_, _) => cancellation.Cancel();
        foreach (var template in _service.Plugins.Templates)
        {
            var selected = template;
            options.Children.Add(ToolsWindow.Button(selected.Meta.Name + " · 安装", () => { window.Close(); Edit(null, selected); }));
        }
        options.Children.Add(ToolsWindow.Button("导入插件模板…", () =>
        {
            var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "JSON 插件模板|*.json" };
            if (dialog.ShowDialog(window) != true) return;
            Run(() => { if (!_service.Plugins.Import(dialog.FileName)) throw new InvalidOperationException("插件模板导入失败。请检查格式或重复标识。"); });
            window.Close(); ShowInstall();
        }));
        window.Content = MonitorSettings.Scroll(options); window.ShowDialog();
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
        var enabled = new W.CheckBox { Content = "启用插件", IsChecked = original?.Enabled == true };
        enabled.SetResourceReference(StyleProperty, "PluginSwitch"); page.Children.Add(enabled);
        if (original is not null) enabled.Click += (_, _) =>
        {
            var previous = original.Enabled; original.Enabled = enabled.IsChecked == true;
            if (!_service.Plugins.Save())
            {
                original.Enabled = previous; enabled.IsChecked = previous;
                MessageBox.Show(window, _service.Plugins.Error ?? _service.Plugins.SaveError ?? "插件保存失败。");
            }
            else { if (original.Enabled) _service.Config.PluginsEnabled = true; _service.Save(); }
        };
        page.Children.Add(ToolsWindow.Button("保存", () =>
        {
            var replacement = new PluginInstanceConfig { Id = original?.Id ?? Guid.NewGuid().ToString("N"), TemplateId = template.Id,
                Enabled = enabled.IsChecked == true, InputValues = global.ToDictionary(p => p.Key, p => p.Value()),
                Targets = targets.Select(t => t.ToDictionary(p => p.Key, p => p.Value())).ToList() };
            if (original is not null) _service.Plugins.Instances.Remove(original);
            _service.Plugins.Instances.Add(replacement);
            if (_service.Plugins.Save()) { if (replacement.Enabled) _service.Config.PluginsEnabled = true; _service.Save(); window.Close(); Refresh(); }
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
