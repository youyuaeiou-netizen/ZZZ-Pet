using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Threading;
using W = System.Windows.Controls;

namespace DesktopPet.Tools;

public sealed class LocalModelPage : W.StackPanel
{
    static readonly int[] ContextTiers = [49152, 65536, 98304, 131072];
    readonly HttpClient http = new() { BaseAddress = new Uri(PetLocalModels.ModelRuntime.GatewayEndpoint), Timeout = TimeSpan.FromMinutes(15) };
    readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(3) };
    readonly W.TextBlock status = ToolsWindow.Text("正在连接本地网关…");
    readonly W.TextBlock resident = ToolsWindow.Text("");
    readonly W.TextBlock capacity = ToolsWindow.Text("");
    readonly W.TextBlock value = ToolsWindow.Text("");
    readonly W.TextBlock feedback = ToolsWindow.Text("");
    readonly List<(int Target, W.Button Button)> presets = [];
    readonly W.ComboBox models = new() { MinHeight = 28 };
    readonly W.Slider slider = new() { Minimum = 49152, Maximum = 131072, TickFrequency = 0, SmallChange = 16384, LargeChange = 32768, IsSnapToTickEnabled = true, Ticks = new System.Windows.Media.DoubleCollection { 49152, 65536, 98304, 131072 }, IsEnabled = false };
    readonly W.Button applyContext;
    bool refreshing, busy, dirty;
    int stateVersion;
    string current = "";
    public LocalModelPage()
    {
        Children.Add(ToolsWindow.Text("本地模型", 20));
        Children.Add(status); Children.Add(resident);
        Children.Add(ToolsWindow.Text("模型（已有上下文标签共用同一模型族设置）")); Children.Add(models);
        Children.Add(capacity); Children.Add(value); Children.Add(slider);
        Children.Add(ToolsWindow.Text("固定上下文档位：48K / 64K / 96K / 128K。首次按显存预选；显存未知时默认 64K。建议不保证性能，Qwen3.5 9B 的插件上限为 128K。"));
        var presetPanel = new W.WrapPanel();
        foreach (var n in ContextTiers)
        {
            var target = n;
            var button = ToolsWindow.Button($"{n / 1024}K", () => { if (slider.IsEnabled && target <= slider.Maximum) slider.Value = target; });
            presets.Add((target, button)); presetPanel.Children.Add(button);
        }
        Children.Add(presetPanel);
        applyContext = ToolsWindow.Button("应用上下文", async () => await Run(async () => { if (!slider.IsEnabled) return; await Post("settings", new { model = current, context = (int)slider.Value }); dirty = false; feedback.Text = "已保存；授权接入的 DSH / OpenCode 将同步上下文预算。"; })); Children.Add(applyContext);
        Add("启动 Ollama 服务", async () => { await Post("service/start", new { }); feedback.Text = "启动请求已发送，状态将在数秒内刷新。"; });
        Add("加载模型", async () => { await Post("load", new { model = current }); feedback.Text = "模型已加载。"; });
        Add("卸载模型", async () => { feedback.Text = "等待当前请求结束后卸载…"; await Post("unload", new { model = current }); feedback.Text = "模型已卸载，服务保持在线；下次请求可自动加载。"; });
        Add("启动网关／重试连接", async () => { StartGateway(); await Task.Delay(1000); await Refresh(); });
        Add("环境设置／首次初始化", async () => await LocalModelSetup.EnvironmentSettings(this));
        Add("接入 DSH / OpenCode／解除接入", async () => await LocalModelSetup.Clients(this, current));
        Children.Add(feedback);
        Children.Add(ToolsWindow.Text("网关：127.0.0.1:11435 · Ollama 地址可在环境设置中修改\n滑块只切换固定档位，不增加模型文件；网关运行容量与授权客户端预算同步。"));
        models.SelectionChanged += async (_, _) =>
        {
            if (refreshing || models.SelectedItem is not string model) return;
            current = model; dirty = false;
            await Run(async () => { await Post("settings", new { model }); await Refresh(); });
        };
        slider.ValueChanged += (_, _) => { value.Text = $"上下文：{slider.Value / 1024:0}K ({slider.Value:0} tokens)"; if (!refreshing) dirty = true; };
        timer.Tick += async (_, _) => await Refresh();
        Loaded += async (_, _) => { await Refresh(); timer.Start(); };
        Unloaded += (_, _) => timer.Stop();
    }
    void Add(string text, Func<Task> action) => Children.Add(ToolsWindow.Button(text, async () => await Run(action)));
    async Task Run(Func<Task> action)
    {
        if (busy) return;
        busy = true; stateVersion++;
        try { await action(); }
        catch (Exception ex) { feedback.Text = ex.Message; }
        finally { busy = false; stateVersion++; }
    }
    async Task Post(string route, object body)
    {
        using var response = await http.PostAsJsonAsync("control/" + route, body);
        if (!response.IsSuccessStatusCode)
        {
            var error = JsonNode.Parse(await response.Content.ReadAsStringAsync());
            throw new InvalidOperationException(error?["error"]?["message"]?.GetValue<string>() ?? $"操作失败：{response.StatusCode}");
        }
    }
    async Task Refresh()
    {
        if (refreshing) return;
        refreshing = true;
        var version = stateVersion;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var state = await http.GetFromJsonAsync<JsonObject>("control/status", timeout.Token);
            if (version != stateVersion) return;
            var online = state?["online"]?.GetValue<bool>() == true;
            status.Text = online ? $"Ollama 服务：在线 · 排队：{state!["queued"]} · 当前：{state["active"]?.GetValue<string>() ?? "空闲"}" : "Ollama 服务：离线，请点击启动 Ollama 服务。";
            if (!online) { models.ItemsSource = Array.Empty<string>(); current = ""; slider.IsEnabled = false; applyContext.IsEnabled = false; foreach (var preset in presets) preset.Button.IsEnabled = false; resident.Text = "模型驻留：暂不可用"; capacity.Text = "容量：暂不可用"; return; }
            var tags = state!["models"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray();
            var catalog = state["catalog"]?.AsArray();
            var choices = catalog is null ? tags.Where(tag => !System.Text.RegularExpressions.Regex.IsMatch(tag, @"-\d+k$") || !tags.Contains(System.Text.RegularExpressions.Regex.Replace(tag, @"-\d+k$", ""))).ToArray() : catalog.GroupBy(n => n!["family"]?.GetValue<string>() ?? n["id"]!.GetValue<string>()).Select(g => g.FirstOrDefault(n => n!["id"]!.GetValue<string>() == g.Key)?["id"]!.GetValue<string>() ?? g.First()!["id"]!.GetValue<string>()).ToArray();
            var selected = state["model"]!.GetValue<string>();
            if (catalog is not null && !choices.Contains(selected))
            {
                var family = catalog.FirstOrDefault(n => n!["id"]!.GetValue<string>() == selected)?["family"]?.GetValue<string>();
                selected = catalog.FirstOrDefault(n => n!["family"]?.GetValue<string>() == family && choices.Contains(n["id"]!.GetValue<string>()))?["id"]?.GetValue<string>() ?? selected;
            }
            if (!choices.Contains(selected)) selected = state["model"]!.GetValue<string>();
            if (models.ItemsSource is not string[] previous || !previous.SequenceEqual(choices)) models.ItemsSource = choices;
            models.SelectedItem = selected; current = selected;
            var max = state["capacity"]?.GetValue<int>();
            var allowed = state["contextTiers"]?.AsArray().Select(n => n!.GetValue<int>()).ToArray() ?? (state["maximumContext"]?.GetValue<int>() is int maximum ? ContextTiers.Where(t => t <= maximum).ToArray() : []);
            slider.IsEnabled = allowed.Length > 0;
            applyContext.IsEnabled = allowed.Length > 0;
            foreach (var preset in presets) preset.Button.IsEnabled = allowed.Contains(preset.Target);
            if (allowed.Length > 0)
            {
                slider.Minimum = allowed[0]; slider.Maximum = allowed[^1];
                slider.Ticks = new System.Windows.Media.DoubleCollection(allowed.Select(t => (double)t));
            }
            else { slider.Minimum = 49152; slider.Maximum = 49152; slider.Ticks = new System.Windows.Media.DoubleCollection { 49152 }; }
            var effectiveMax = state["maximumContext"]?.GetValue<int>() ?? max;
            if (tags.Length == 0) capacity.Text = "尚未安装可用的对话模型，请先在 Ollama 中安装。";
            else if (max is null) capacity.Text = "模型容量元数据不可用，无法调整上下文。";
            else if (effectiveMax is int em && em < 49152) capacity.Text = $"模型容量上限：{em / 1024}K，低于固定档位最小值 48K；保持模型可用上限运行。";
            else if (effectiveMax is int capped && capped < max.Value) capacity.Text = $"模型声明容量：{max.Value / 1024}K · 插件限制上限：{capped / 1024}K" + (DisplayModelName(current).Equals("qwen3.5:9b", StringComparison.OrdinalIgnoreCase) ? "（Qwen3.5 9B）" : "");
            else capacity.Text = $"模型容量上限：{effectiveMax.GetValueOrDefault() / 1024}K" + (state["recommendedContext"] is { } recommendation ? $" · 首次建议：{recommendation.GetValue<int>() / 1024}K" : "");
            if (state["missingModel"]?.GetValue<bool>() == true) capacity.Text = "之前选择的模型已删除，请选择替代模型。客户端默认模型不会自动切换。";
            var savedContext = state["context"]!.GetValue<int>();
            if (!dirty && allowed.Length > 0) slider.Value = allowed.Contains(savedContext) ? savedContext : allowed[0];
            value.Text = allowed.Length == 0 ? $"上下文：{savedContext / 1024:0}K（无可用固定档位）" : $"上下文：{slider.Value / 1024:0}K ({slider.Value:0} tokens)";
            var running = state["running"]!.AsArray();
            resident.Text = running.Count == 0 ? "模型驻留：未加载" : "模型驻留：" + string.Join("、", running.Select(n => $"{n!["name"]} ({n["context_length"]} tokens)"));
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or System.Text.Json.JsonException)
        { status.Text = "网关离线，请点击启动网关／重试连接。"; slider.IsEnabled = false; applyContext.IsEnabled = false; foreach (var preset in presets) preset.Button.IsEnabled = false; resident.Text = "模型驻留：暂不可用"; }
        finally { refreshing = false; }
    }
    static string DisplayModelName(string model) => System.Text.RegularExpressions.Regex.Replace(model, @"-\d+k$", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    internal static void StartGateway()
    {
        var executable = LocalModelExtension.Current.GatewayExecutable;
        if (!File.Exists(executable)) throw new FileNotFoundException("网关组件未安装，请使用包含 model-gateway 的完整发布包。");
        using var probe = new System.Net.Sockets.TcpClient();
        try
        {
            if (probe.ConnectAsync("127.0.0.1", new Uri(PetLocalModels.ModelRuntime.GatewayEndpoint).Port).Wait(500) && probe.Connected)
            {
                using var client = new HttpClient { BaseAddress = new Uri(PetLocalModels.ModelRuntime.GatewayEndpoint), Timeout = TimeSpan.FromSeconds(3) };
                try
                {
                    var state = JsonNode.Parse(client.GetStringAsync("control/status").GetAwaiter().GetResult());
                    if (state?["gateway"]?.GetValue<string>() == "pet-local-model" || state?["online"] is not null && state["model"] is not null && state["queued"] is not null) return;
                }
                catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or System.Text.Json.JsonException) { }
                throw new InvalidOperationException("网关端口被占用，未识别到 Pet 网关。请检查占用程序后重试。");
            }
        }
        catch (AggregateException ex) when (ex.InnerException is System.Net.Sockets.SocketException) { }
        Process.Start(new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(executable)! });
    }
    internal static async Task RestartGateway()
    {
        using var client = new HttpClient { BaseAddress = new Uri(PetLocalModels.ModelRuntime.GatewayEndpoint), Timeout = TimeSpan.FromSeconds(5) };
        try
        {
            using var state = await client.GetAsync("/control/status");
            var json = JsonNode.Parse(await state.Content.ReadAsStringAsync());
            // Do not terminate an unrelated service occupying this port.
            if (json?["gateway"]?.GetValue<string>() != "pet-local-model") throw new InvalidOperationException("插件包已安装，但旧网关没有安全重启接口。已有插件启动任务已更新时，重启电脑后生效；其他程序占用端口时请检查占用情况。");
            using var restart = await client.PostAsJsonAsync("/control/restart", new { });
            if (!restart.IsSuccessStatusCode) throw new InvalidOperationException("插件已安装，但网关正忙；请求结束后再重试。");
            for (var i = 0; i < 20; i++)
            {
                await Task.Delay(200);
                using var probe = new System.Net.Sockets.TcpClient();
                try { await probe.ConnectAsync("127.0.0.1", new Uri(PetLocalModels.ModelRuntime.GatewayEndpoint).Port); }
                catch (System.Net.Sockets.SocketException) { StartGateway(); return; }
            }
            throw new InvalidOperationException("旧网关尚未退出，请稍后重试。");
        }
        catch (HttpRequestException) { StartGateway(); }
    }
}
