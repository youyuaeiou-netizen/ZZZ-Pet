using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows;
using PetLocalModels;
using W = System.Windows.Controls;
using MessageBox = System.Windows.MessageBox;

namespace DesktopPet.Tools;

internal static class LocalModelSetup
{
    static readonly HttpClient Http = new() { BaseAddress = new Uri(ModelRuntime.GatewayEndpoint), Timeout = TimeSpan.FromSeconds(30) };
    static Window Dialog(FrameworkElement owner, string title, W.StackPanel panel)
    {
        var window = new Window { Owner = Window.GetWindow(owner), Title = title, Width = 700, Height = Math.Min(760, SystemParameters.WorkArea.Height - 60), ShowInTaskbar = false };
        MonitorFluentStyle.Apply(window); window.Content = MonitorSettings.Scroll(MonitorFluentStyle.Card(panel)); return window;
    }
    internal static async Task<JsonObject> Post(string route, object body)
    {
        using var response = await Http.PostAsJsonAsync("control/" + route, body);
        var result = await response.Content.ReadFromJsonAsync<JsonObject>() ?? new();
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException(result["error"]?["message"]?.GetValue<string>() ?? "操作失败。");
        return result;
    }
    public static async Task EnvironmentSettings(FrameworkElement owner)
    {
        var state = await Http.GetFromJsonAsync<JsonObject>("control/environment") ?? throw new IOException("环境发现失败。");
        var config = ModelRuntime.Read(); var panel = new W.StackPanel(); var window = Dialog(owner, "本地模型 · 环境设置", panel);
        panel.Children.Add(ToolsWindow.Text("Ollama 环境", 20));
        panel.Children.Add(ToolsWindow.Text("已检测本机地址和安装路径。多个候选请自行选择；也可以输入地址或选择程序。"));
        panel.Children.Add(ToolsWindow.Text("服务地址（仅本机 HTTP）"));
        var endpoint = new W.TextBox { Text = config.Endpoint };
        var endpointChoices = new W.ComboBox { ItemsSource = state["endpoints"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray(), SelectedItem = config.Endpoint };
        endpointChoices.SelectionChanged += (_, _) => { if (endpointChoices.SelectedItem is string selected) endpoint.Text = selected; };
        panel.Children.Add(endpointChoices); panel.Children.Add(endpoint);
        panel.Children.Add(ToolsWindow.Text("Ollama 程序"));
        var exes = state["executables"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray();
        var executable = new W.TextBox { Text = config.OllamaExecutable ?? (exes.Length == 1 ? exes[0] : "") };
        var executableChoices = new W.ComboBox { ItemsSource = exes, SelectedItem = executable.Text };
        executableChoices.SelectionChanged += (_, _) => { if (executableChoices.SelectedItem is string selected) executable.Text = selected; };
        panel.Children.Add(executableChoices); panel.Children.Add(executable);
        panel.Children.Add(ToolsWindow.Button("选择 ollama.exe…", () => { var picker = new Microsoft.Win32.OpenFileDialog { Filter = "Ollama 程序|ollama.exe" }; if (picker.ShowDialog(window) == true) executable.Text = picker.FileName; }));
        var startup = new W.CheckBox { Content = "登录后后台启动网关及 Ollama（退出 Pet 后继续运行）", IsChecked = !File.Exists(ModelRuntime.ConfigPath) || config.LoginStartup, Margin = new Thickness(0, 16, 0, 8) }; panel.Children.Add(startup);
        var feedback = ToolsWindow.Text(""); panel.Children.Add(feedback);
        panel.Children.Add(ToolsWindow.Button("保存并初始化", async () =>
        {
            try
            {
                var next = new ModelRuntime { Endpoint = ModelRuntime.ValidateEndpoint(endpoint.Text.Trim()), OllamaExecutable = string.IsNullOrWhiteSpace(executable.Text) ? null : executable.Text.Trim(), LoginStartup = startup.IsChecked == true };
                if (next.OllamaExecutable is not null && !File.Exists(next.OllamaExecutable)) throw new IOException("所选程序不存在。");
                await Post("environment", next);
                await RegisterStartup(next.LoginStartup);
                feedback.Text = "环境已保存。请点击启动 Ollama 服务；无需打开终端。";
            }
            catch (Exception ex) { feedback.Text = ex.Message; }
        }));
        panel.Children.Add(ToolsWindow.Button("Ollama 安装说明", () => Process.Start(new ProcessStartInfo("https://ollama.com/download/windows") { UseShellExecute = true })));
        window.ShowDialog();
    }
    internal static async Task RegisterStartup(bool enabled, bool existingOnly = false)
    {
        var exe = LocalModelExtension.Current.GatewayExecutable;
        string Quote(string text) => "'" + text.Replace("'", "''") + "'";
        var script = enabled ? $"$ErrorActionPreference='Stop'; $who=[Security.Principal.WindowsIdentity]::GetCurrent().Name; $exe={Quote(exe)}; $action=New-ScheduledTaskAction -Execute $exe -WorkingDirectory ([IO.Path]::GetDirectoryName($exe)); $trigger=New-ScheduledTaskTrigger -AtLogOn -User $who; $principal=New-ScheduledTaskPrincipal -UserId $who -LogonType Interactive -RunLevel Limited; $settings=New-ScheduledTaskSettingsSet -ExecutionTimeLimit ([TimeSpan]::Zero) -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1) -MultipleInstances IgnoreNew -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries; Register-ScheduledTask -TaskName 'DesktopPet Local Model Gateway' -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Force | Out-Null; if(Get-ScheduledTask -TaskName 'Ollama Local Model Server' -ErrorAction SilentlyContinue) {{ $oa=New-ScheduledTaskAction -Execute $exe -Argument '--ollama-service' -WorkingDirectory ([IO.Path]::GetDirectoryName($exe)); Set-ScheduledTask -TaskName 'Ollama Local Model Server' -Action $oa | Out-Null }}" : "$ErrorActionPreference='Stop'; if(Get-ScheduledTask -TaskName 'DesktopPet Local Model Gateway' -ErrorAction SilentlyContinue){ Unregister-ScheduledTask -TaskName 'DesktopPet Local Model Gateway' -Confirm:$false }";
        var legacy = Path.Combine(AppContext.BaseDirectory, "model-gateway", "DesktopPet.ModelGateway.exe");
        var packageRoot = Path.Combine(ModelRuntime.DataRoot, "plugins", "pet-local-model") + Path.DirectorySeparatorChar;
        var validation = $"$previous=Get-ScheduledTask -TaskName 'DesktopPet Local Model Gateway' -ErrorAction SilentlyContinue; if($previous) {{ $prior=[IO.Path]::GetFullPath($previous.Actions[0].Execute); if($prior -ne {Quote(legacy)} -and -not $prior.StartsWith({Quote(packageRoot)},[StringComparison]::OrdinalIgnoreCase)) {{ throw '已有同名启动任务不属于此插件，未修改。' }} }}; ";
        script = validation + (existingOnly ? "if(-not $previous){exit 0}; " : "") + script;
        var pwsh = (System.Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).Select(p => Path.Combine(p.Trim('"'), "pwsh.exe")).FirstOrDefault(File.Exists) ?? Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.ProgramFiles), "PowerShell", "7", "pwsh.exe");
        // ScheduledTasks is a Windows module; allow the inbox shell on computers without PowerShell 7.
        if (!File.Exists(pwsh)) pwsh = Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        using var process = Process.Start(new ProcessStartInfo(pwsh, "-NoProfile -NonInteractive -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(script))) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true }) ?? throw new IOException("启动任务配置失败。");
        var error = await process.StandardError.ReadToEndAsync(); await process.WaitForExitAsync();
        if (process.ExitCode != 0) throw new IOException("环境已保存，但登录启动任务注册失败：" + error);
    }
    public static async Task Clients(FrameworkElement owner, string selectedModel)
    {
        var state = await Http.GetFromJsonAsync<JsonObject>("control/clients") ?? throw new IOException("客户端发现失败。");
        var panel = new W.StackPanel(); var window = Dialog(owner, "本地模型 · 接入客户端", panel);
        panel.Children.Add(ToolsWindow.Text("DSH / OpenCode", 20));
        var kind = new W.ComboBox { ItemsSource = new[] { "opencode", "dsh" }, SelectedIndex = 0 }; panel.Children.Add(kind);
        var candidates = state["candidates"]!.AsArray();
        var initial = candidates.FirstOrDefault(c => c!["kind"]!.GetValue<string>() == "opencode")?["path"]?.GetValue<string>() ?? state["suggestedOpenCode"]!.GetValue<string>();
        var paths = new W.TextBox { Text = initial };
        var pathChoices = new W.ComboBox { ItemsSource = candidates.Select(c => c!["path"]!.GetValue<string>()).Append(initial).Distinct().ToArray(), SelectedItem = initial };
        pathChoices.SelectionChanged += (_, _) => { if (pathChoices.SelectedItem is string selected) paths.Text = selected; };
        panel.Children.Add(ToolsWindow.Text("目标配置文件（可选择或输入路径）")); panel.Children.Add(pathChoices); panel.Children.Add(paths);
        kind.SelectionChanged += (_, _) => { paths.Text = candidates.FirstOrDefault(c => c!["kind"]!.GetValue<string>() == (string)kind.SelectedItem)?["path"]?.GetValue<string>() ?? (kind.SelectedIndex == 0 ? state["suggestedOpenCode"]!.GetValue<string>() : ""); pathChoices.SelectedItem = paths.Text; };
        panel.Children.Add(ToolsWindow.Button("选择配置文件（含项目配置）…", () => { var picker = new Microsoft.Win32.OpenFileDialog { Filter = "客户端配置|*.json;*.jsonc;*.yml;*.yaml|所有文件|*.*" }; if (picker.ShowDialog(window) == true) paths.Text = picker.FileName; }));
        var setDefault = new W.CheckBox { Content = "将当前所选模型设为默认", IsChecked = false }; panel.Children.Add(setDefault);
        var autoSync = new W.CheckBox { Content = "授权自动同步新增模型、能力和上下文预算", IsChecked = true }; panel.Children.Add(autoSync);
        var preview = new W.TextBox { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 200, VerticalScrollBarVisibility = W.ScrollBarVisibility.Auto }; panel.Children.Add(preview);
        var feedback = ToolsWindow.Text(""); panel.Children.Add(feedback);
        string? token = null;
        var apply = ToolsWindow.Button("确认接入（先备份再写入）", async () =>
        {
            try { if (token is null) return; var result = await Post("clients/apply", new { token }); token = null; feedback.Text = $"{result["message"]}\n备份：{result["backup"]}"; }
            catch (Exception ex) { feedback.Text = ex.Message; token = null; }
        }); apply.IsEnabled = false;
        panel.Children.Add(ToolsWindow.Button("预览配置变更", async () =>
        {
            apply.IsEnabled = false; token = null;
            try { var result = await Post("clients/preview", new { kind = (string)kind.SelectedItem, path = paths.Text, model = selectedModel, setDefault = setDefault.IsChecked == true, autoSync = autoSync.IsChecked == true }); token = result["token"]!.GetValue<string>(); preview.Text = $"目标：{result["path"]}\n原插件配置：\n{result["before"]}\n新插件配置：\n{result["after"]}\n默认：{result["defaultAfter"]}"; feedback.Text = result["note"]!.GetValue<string>(); apply.IsEnabled = true; }
            catch (Exception ex) { feedback.Text = ex.Message; }
        })); panel.Children.Add(apply);
        void Invalidate() { token = null; apply.IsEnabled = false; }
        paths.TextChanged += (_, _) => Invalidate(); kind.SelectionChanged += (_, _) => Invalidate(); setDefault.Click += (_, _) => Invalidate(); autoSync.Click += (_, _) => Invalidate();
        foreach (var target in state["targets"]!.AsArray())
        {
            var path = target!["path"]!.GetValue<string>();
            panel.Children.Add(ToolsWindow.Text($"{path}\n{target["message"]}"));
            panel.Children.Add(ToolsWindow.Button("解除此客户端接入", async () =>
            {
                if (MessageBox.Show(window, "将备份配置并移除插件管理的提供方，恢复此前默认值。人工修改会阻止解除。", "解除接入", MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;
                try { var result = await Post("clients/detach", new { path }); feedback.Text = "已解除接入；备份：" + result["backup"]; }
                catch (Exception ex) { feedback.Text = ex.Message; }
            }));
        }
        window.ShowDialog();
    }
}
