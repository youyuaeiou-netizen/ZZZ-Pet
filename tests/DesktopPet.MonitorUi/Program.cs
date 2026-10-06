using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DesktopPet;
using DesktopPet.Monitoring;
using DesktopPet.Character;
using DesktopPet.Tools;
using W = System.Windows.Controls;

internal static class Program
{
    private static readonly List<string> Passed = [];
    private static string _output = "";
    [STAThread] private static int Main(string[] args)
    {
        _output = Path.Combine(Path.GetFullPath(args[0]), Guid.NewGuid().ToString("N")); Directory.CreateDirectory(_output);
        var app = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        try
        {
            if (args.Skip(1).Contains("--desktop-fps-only")) { FpsCli(); DesktopFpsDisplay(); PetInteraction(); }
            else if (args.Skip(1).Contains("--usage-live")) UsageLive();
            else if (args.Skip(1).Contains("--quota-only")) QuotaAndDismissal();
            else if (args.Skip(1).Contains("--interaction-only")) PetInteraction();
            else if (args.Skip(1).Contains("--pet-only")) PetVisibility();
            else if (args.Skip(1).Contains("--taskbar-only")) TaskbarInteraction();
            else if (args.Skip(1).Contains("--ui-only")) { Ui(); SimplifiedControls(); Dropdowns(); TaskbarInteraction(); }
            else { Localization(); FpsCli(); Lifecycle(); Protocol(); Ui(); SimplifiedControls(); Dropdowns(); TaskbarInteraction(); PetVisibility(); PetInteraction(); PluginCancellation(); Plugins(); HistoryBoundary(); }
            File.WriteAllText(Path.Combine(_output, "result.json"), JsonSerializer.Serialize(new { passed = Passed, note = "isolated data, ordinary worker, current desktop DPI; no driver installation or global configuration" }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"PASS {Passed.Count} monitor integration checks; {_output}"); return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
        finally { foreach (Window w in app.Windows.Cast<Window>().ToArray()) w.Close(); app.Shutdown(); }
    }
    private static void Check(bool value, string name) { if (!value) throw new Exception(name); Passed.Add(name); }
    private static void DesktopFpsDisplay()
    {
        using var service = new MonitorService(Path.Combine(_output, "desktop-fps"), Dispatcher.CurrentDispatcher, () => true);
        service.Config.FpsEnabled = true; service.Config.HistoryEnabled = false;
        service.Config.TaskbarMetrics = ["FPS"]; service.Config.TaskbarLeft = 30; service.Config.TaskbarTop = 30;
        service.FpsTargets = () => [];
        var now = DateTimeOffset.UtcNow;
        var desktop = new MonitorMetric("FPS.101", "桌面 FPS", "desktop", "FPS", "FPS", 60, "PresentMon · DWM", SampledAt: now);
        typeof(MonitorService).GetMethod("Apply", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(service,
            [new MonitorSnapshot(now, [], [desktop], "fixture", Capabilities: new() { ["fps-helper"] = "running" })]);
        var taskbar = new MonitorTaskbarWindow(service); taskbar.Show(); taskbar.Refresh(); Pump(100);
        try
        {
            var text = string.Join(' ', Find<W.TextBlock>(taskbar).Select(t => t.Text));
            Check(text.Contains("桌面 FPS") && text.Contains("60") && !text.Contains("等待"), "taskbar shows explicitly labelled desktop FPS number");
            Capture(taskbar, "desktop-fps-taskbar.png");
        }
        finally { taskbar.Close(); }
    }
    private static CodexUsage UsageFixture()
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(new { rateLimits = new { primary = new { usedPercent = 99 } },
            rateLimitsByLimitId = new { codex = new { primary = new { usedPercent = 25, windowDurationMins = 300, resetsAt = DateTimeOffset.UtcNow.AddHours(2).ToUnixTimeSeconds() },
                secondary = new { usedPercent = 42, windowDurationMins = 10080, resetsAt = DateTimeOffset.UtcNow.AddDays(3).ToUnixTimeSeconds() },
                credits = new { balance = "11.27", unlimited = false } } } }));
        return CodexUsage.Parse(document.RootElement);
    }
    private static void UsageLive()
    {
        var data = Task.Run(() => CodexUsageClient.ReadAsync(CancellationToken.None)).GetAwaiter().GetResult();
        Check(data.Buckets.Count > 0 && data.Buckets.Any(b => b.Primary?.Remaining is not null || b.Secondary?.Remaining is not null), "installed Codex returns live quota through read-only account RPC");
        var window = new UsageBubble(() => { }, () => { }); window.Render(data, null, false); window.Show(); Pump(100);
        Capture(window, "usage-live.png"); window.Close();
    }
    private static void QuotaAndDismissal()
    {
        var data = UsageFixture();
        Check(data.Buckets.Single().Primary!.Remaining == 75 && data.Buckets.Single().Secondary!.Remaining == 58, "multi-bucket remaining percentage takes precedence over legacy view");
        Check(data.Buckets.Single().Credits == "积分：11.27", "credits retain their own unit without inventing a currency");
        using var missing = JsonDocument.Parse("""{"rateLimits":{"primary":{"usedPercent":null,"windowDurationMins":null,"resetsAt":null},"secondary":null}}""");
        var missingData = CodexUsage.Parse(missing.RootElement);
        Check(missingData.Buckets.Single().Primary!.Remaining is null && missingData.Buckets.Single().Secondary is null, "missing quota and reset values remain unavailable instead of zero");
        using var over = JsonDocument.Parse("""{"rateLimits":{"primary":{"usedPercent":130},"secondary":{"usedPercent":-1}}}""");
        var clamped = CodexUsage.Parse(over.RootElement);
        Check(clamped.Buckets.Single().Primary!.Remaining == 0 && clamped.Buckets.Single().Secondary!.Remaining is null, "over-limit percentages clamp and invalid negative usage remains unavailable");
        var paths = new RuntimePaths(new StartupOptions(null, Path.Combine(_output, "quota-data"), null));
        new MonitorStore(paths.MonitorDirectory).Save(new() { Enabled = false });
        var store = new SettingsStore(paths.SettingsPath); var catalog = CharacterCatalog.Load(paths.CharacterRoot, paths.UserCharacterRoot);
        var pet = new PetWindow(catalog.Characters, paths.CharacterRoot, "ellen-flat2d", store.Load(), store, paths) { ShowActivated = false };
        pet.Show(); Pump(100);
        var monitor = Field<MonitorController>(pet, "_monitor"); var service = Field<MonitorService>(monitor, "_service"); service.Config.Enabled = true;
        var dismissal = Field<OutsideClickDismissal>(pet, "_outsideClick");
        Check(!dismissal.Watching, "native outside-click hook is idle when no transient surface is open");
        monitor.Click(); Pump(80);
        Check(dismissal.Watching && monitor.HasBubble, "showing computer bubble installs a real native outside-click hook");
        static OutsideClickDismissal.ScreenPoint At(FrameworkElement view, int x, int y)
        { var point = view.PointToScreen(new System.Windows.Point(x, y)); return new() { X = (int)point.X, Y = (int)point.Y }; }
        dismissal.PointerDown(At(monitor.BubbleWindow!, 70, 50)); Pump(30);
        Check(monitor.HasBubble, "clicking within native computer bubble preserves its controls");
        var other = new Window { Width = 180, Height = 110, Left = SystemParameters.WorkArea.Left + 20, Top = SystemParameters.WorkArea.Top + 20,
            Topmost = true, ShowActivated = false, Content = new W.Border { Background = System.Windows.Media.Brushes.White } };
        other.Show(); Pump(60);
        var outside = At(other, 60, 50);
        Check(WindowFromPoint(new NativePoint { X = outside.X, Y = outside.Y }) == new System.Windows.Interop.WindowInteropHelper(other).Handle, "outside test point resolves to a separate native window");
        var menu = pet.ContextMenu; menu.PlacementTarget = pet; menu.IsOpen = true; Pump(60);
        Check(menu.IsOpen, "nonactivating character menu opens alongside computer bubble");
        dismissal.PointerDown(At(menu, 50, 25)); Pump(30);
        Check(menu.IsOpen && monitor.HasBubble, "clicking inside menu does not dismiss the surface group");
        dismissal.PointerDown(outside); Pump(80);
        Check(!menu.IsOpen && !monitor.HasBubble && !dismissal.Watching, "one external native hit closes both menu and bubble and releases hook");
        Pump(200); Check(!monitor.HasBubble && service.WorkerProcessId is null, "dismissed computer bubble stays closed without restarting sampling");
        menu.IsOpen = true; Pump(50); dismissal.PointerDown(outside); Pump(50);
        Check(!menu.IsOpen && !dismissal.Watching, "external click also dismisses menu when it is the only open surface");
        monitor.Click(); Pump(40); dismissal.PointerDown(outside); monitor.DismissBubble(); monitor.Click(); Pump(40);
        Check(monitor.HasBubble, "queued click from old surface cannot dismiss a newly opened bubble"); monitor.DismissBubble();
        var reads = 0;
        using var usage = new UsageController(pet, monitor.DismissBubble, _ => { reads++; return Task.FromResult(data); });
        usage.Show(); Pump(80); var bubble = (UsageBubble)usage.BubbleWindow!;
        Check(reads == 1 && Find<W.TextBlock>(bubble).Any(t => t.Text.Contains("剩余 75%")) && Find<W.TextBlock>(bubble).Any(t => t.Text.Contains("7 天")), "quota bubble displays both live-shaped windows after one request");
        Check(bubble.ActualWidth <= 280 && bubble.ActualHeight <= 290 && Find<W.Button>(bubble).Count(b => b.IsVisible) == 2,
            "compact quota bubble keeps both actions within a small measured window");
        Capture(bubble, "usage-controlled.png");
        usage.Dismiss(); usage.Show(); Click(bubble, "刷新"); Pump(50);
        Check(reads == 1, "reopen uses cached quota and rapid manual refresh is throttled");
        typeof(UsageController).GetField("_lastAttempt", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(usage, DateTimeOffset.UtcNow.AddMinutes(-6));
        Click(bubble, "刷新"); Pump(50); Check(reads == 2, "explicit refresh reads again after throttle interval");
        bubble.Render(missingData, null, false); Check(Find<W.TextBlock>(bubble).Any(t => t.Text.Contains("暂不可用")) && !Find<W.TextBlock>(bubble).Any(t => t.Text.Contains("剩余 0%")), "unavailable quota is not rendered as exhausted");
        bubble.Render(data with { RetrievedAt = DateTimeOffset.UtcNow.AddMinutes(-11) }, "读取失败", false);
        Check(Find<W.TextBlock>(bubble).Any(t => t.Text.Contains("待更新")) && !Find<W.TextBlock>(bubble).Any(t => t.Text.Contains("剩余 75%")), "stale quota is visibly unavailable rather than current"); Capture(bubble, "usage-stale.png");
        var expired = data with { Buckets = [new("Codex", new(75, 300, DateTimeOffset.UtcNow.AddSeconds(-1)), null, null)] };
        bubble.Render(expired, null, false); Check(Find<W.TextBlock>(bubble).Any(t => t.Text.Contains("待更新")), "reset boundary invalidates old quota without assuming a replenishment");
        usage.SetPetHidden(true); Check(!usage.HasBubble, "hiding pet also dismisses attached quota bubble");
        usage.Show(); Check(usage.HasBubble && bubble.Owner is null, "tray can show quota while pet is hidden"); usage.Dismiss();
        using var failed = new UsageController(pet, () => { }, _ => Task.FromException<CodexUsage>(new IOException("test-only failure")));
        failed.Show(); Pump(40); Check(Find<W.TextBlock>(failed.BubbleWindow!).Any(t => t.Text.Contains("读取失败")), "quota failure is shown without throwing or fake numbers"); failed.Dismiss();
        var pending = new TaskCompletionSource<CodexUsage>(); CancellationToken queryToken = default;
        var pendingUsage = new UsageController(pet, () => { }, token => { queryToken = token; return pending.Task; });
        pendingUsage.Show(); Pump(40); pendingUsage.Dispose(); Check(queryToken.IsCancellationRequested, "exit cancels only the owned quota query");
        pending.SetCanceled(queryToken); Pump(50);
        var quotaEntry = menu.Items.OfType<W.MenuItem>().Single(i => i.Header as string == "ChatGPT 额度");
        Check(quotaEntry.IsEnabled, "character context menu exposes quota entry");
        var liveController = Field<UsageController>(pet, "_usage");
        typeof(UsageController).GetField("_usage", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(liveController, data);
        typeof(UsageController).GetField("_lastAttempt", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(liveController, DateTimeOffset.UtcNow);
        quotaEntry.RaiseEvent(new RoutedEventArgs(W.MenuItem.ClickEvent)); Pump(60);
        Check(liveController.HasBubble && dismissal.Watching, "quota menu opens attached bubble using common outside-click detection");
        dismissal.PointerDown(outside); Pump(50);
        Check(!liveController.HasBubble && !dismissal.Watching, "quota also dismisses on outside click");
        other.Close(); pet.Close(); Check(!dismissal.Watching, "closing pet leaves no mouse hook installed");
    }
    private static void FpsCli()
    {
        var workerAssembly = Assembly.LoadFrom(Path.Combine(AppContext.BaseDirectory, "monitor", "DesktopPet.MonitorWorker.dll"));
        var create = workerAssembly.GetType("DesktopPet.MonitorWorker.FpsAgent")!.GetMethod("CreateHelperStartInfo", BindingFlags.NonPublic | BindingFlags.Static)!;
        var start = (ProcessStartInfo)create.Invoke(null, [Path.Combine(AppContext.BaseDirectory, "monitor", "fps", "PresentMon.exe"), "DesktopPet.FPS." + Guid.NewGuid().ToString("N")])!;
        Check(!start.ArgumentList.Contains("dwm.exe") && start.ArgumentList.Contains("DesktopPet.exe"), "desktop compositor is captured while pet itself remains excluded");
        start.ArgumentList.Add("--help"); // Usage only: validate the actual pinned CLI without starting an ETW session.
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(10000)) { process.Kill(); throw new Exception("PresentMon CLI validation timed out"); }
        var text = stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult();
        Check(text.Contains("PresentMon 2.6.0") && !text.Contains("unrecognized argument", StringComparison.OrdinalIgnoreCase) && process.ExitCode == 1, "production FPS arguments accepted by pinned PresentMon CLI without starting capture");
    }
    private static void Localization()
    {
        var count = 7;
        var root = new W.StackPanel(); var text = new W.TextBlock(); root.Children.Add(text);
        MonitorLocalizer.Formatted(text, "{0} 条记录；跳过 {1} 行。", () => [count, 2]);
        foreach (var code in MonitorLanguage.Codes)
        {
            MonitorLocalizer.Translate(root, code);
            Check(text.Text == MonitorLocalizer.Language.Format(code, "{0} 条记录；跳过 {1} 行。", count, 2), "existing dynamic text switches language " + code);
        }
        count = 12; MonitorLocalizer.Translate(root, "de");
        Check(text.Text.Contains("12") && !text.Text.Contains("7"), "dynamic values recomputed after language change");
        text.Text = "不可用"; MonitorLocalizer.Translate(root, "fr");
        Check(text.Text == "Indisponible", "plain status replaces formatted binding without resurrecting old values");
        MonitorLocalizer.Formatted(text, "下载：{0} Mbps · 读取 {1} MB", "12.5", "1.25");
        MonitorLocalizer.Translate(root, "ru"); MonitorLocalizer.Translate(root, "zh");
        Check(text.Text == "下载：12.5 Mbps · 读取 1.25 MB", "return to Chinese preserves units and numeric values");
        var author = new W.TextBlock { Text = "不可用" }; var input = new W.TextBox { Text = "不可用" }; var password = new W.PasswordBox { Password = "不可用" };
        MonitorLocalizer.Preserve(author); root.Children.Add(author); root.Children.Add(input); root.Children.Add(password);
        MonitorLocalizer.Translate(root, "en");
        Check(author.Text == "不可用" && input.Text == "不可用" && password.Password == "不可用", "author labels and editable inputs are never translated");
        var resource = JsonSerializer.Deserialize<Dictionary<string, string[]>>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "monitor-languages", "pet.json")))!;
        string[] order = ["en", "zh-tw", "ja", "ko", "fr", "de", "es", "ru"];
        for (var i = 0; i < order.Length; i++)
            Check(resource.All(p => MonitorLocalizer.Language.Text(order[i], p.Key) == p.Value[i]), "all Pet resource translations loaded without fallback " + order[i]);
        var malformed = Path.Combine(_output, "malformed-language"); Directory.CreateDirectory(malformed);
        File.WriteAllText(Path.Combine(malformed, "pet.json"), JsonSerializer.Serialize(new Dictionary<string, string[]> {
            ["{0} 条记录；跳过 {1} 行。"] = ["Bad {0", "Lost {0}", "Wrong {2}", "", "Records {1}, {0}", "Records {0}, {1}", "Records {0}, {1}", "Records {0}, {1}"] }));
        var language = new MonitorLanguage(malformed);
        Check(language.Format("en", "{0} 条记录；跳过 {1} 行。", 7, 2).Contains("7") && language.Text("en", "{0} 条记录；跳过 {1} 行。") != "Bad {0", "malformed translated format rejected without UI crash");
        Check(language.Text("ja", "{0} 条记录；跳过 {1} 行。") != "Wrong {2}" && language.Text("zh-tw", "{0} 条记录；跳过 {1} 行。") != "Lost {0}", "missing or substituted arguments rejected");
        Check(language.Format("fr", "{0} 条记录；跳过 {1} 行。", 7, 2) == "Records 2, 7", "translation may reorder arguments without changing their values");
    }
    private static void Pump(int ms)
    {
        var frame = new DispatcherFrame(); var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; }; timer.Start(); Dispatcher.PushFrame(frame);
    }
    private static void Until(Func<bool> predicate, int timeout, string name)
    { var clock = Stopwatch.StartNew(); while (!predicate() && clock.ElapsedMilliseconds < timeout) Pump(50); Check(predicate(), name); }
    private static void Lifecycle()
    {
        using var service = new MonitorService(Path.Combine(_output, "lifecycle"), Dispatcher.CurrentDispatcher, () => false);
        Check(service.Config.Enabled && !service.Running && !service.Web.Running, "monitor defaults on but constructor does not launch worker or web listener");
        using var unusedPort = new TcpListener(System.Net.IPAddress.Loopback, 0); unusedPort.Start();
        service.Config.WebPort = ((System.Net.IPEndPoint)unusedPort.LocalEndpoint).Port; unusedPort.Stop();
        service.Config.WebEnabled = service.Config.WebLan = service.Config.WebIpv6 = true; service.Save();
        service.SetEnabled(true);
        Until(() => service.Snapshot.Metrics.Count > 0, 25000, "real worker connects and returns hardware catalog");
        Check(service.Snapshot.Metrics.Any(m => m.Kind == "MEM.Load" && m.Valid), "real memory sample available");
        Check(!service.Snapshot.Elevated, "worker uses ordinary permissions");
        unusedPort.Start(); Check(!service.Web.Running, "legacy enabled web configuration does not bind even while sampling"); unusedPort.Stop();
        var hardware = new List<MonitorSnapshot>(); var lastHardware = DateTimeOffset.MinValue;
        var hardwareClock = Stopwatch.StartNew();
        while (hardwareClock.Elapsed < TimeSpan.FromSeconds(20))
        {
            if (service.Snapshot.Timestamp > lastHardware && service.Snapshot.Fresh(DateTimeOffset.UtcNow))
            { hardware.Add(service.Snapshot); lastHardware = service.Snapshot.Timestamp; }
            Pump(50);
        }
        var hardwareMetrics = hardware.SelectMany(s => s.Metrics).Where(m => m.Value is double v && double.IsFinite(v))
            .GroupBy(m => m.Id).Select(g => new { kind = g.Last().Kind, unit = g.Last().Unit, source = g.Last().Source,
                validSamples = g.Count(), minimum = g.Min(m => m.Value!.Value), maximum = g.Max(m => m.Value!.Value), latest = g.Last().Value }).ToArray();
        var availability = hardware.SelectMany(s => s.Metrics).GroupBy(m => m.Kind).Select(g => new
            { kind = g.Key, samples = g.Count(), valid = g.Count(m => m.Valid), available = g.Any(m => m.Valid) }).ToArray();
        var hardwareReport = Path.Combine(_output, "real-hardware-readings.json");
        File.WriteAllText(hardwareReport, JsonSerializer.Serialize(new { capturedAt = DateTimeOffset.Now, sampleCount = hardware.Count,
            elevated = service.Snapshot.Elevated, capabilities = service.Snapshot.Capabilities, devices = service.Snapshot.Devices.Select(d => new { d.Kind }).Distinct(),
            availability, metrics = hardwareMetrics }, new JsonSerializerOptions { WriteIndented = true }));
        Check(hardware.Count >= 10 && hardwareMetrics.Any(m => m.kind == "MEM.Load"), "real hardware report records fresh samples over 20 seconds");
        Check(service.Snapshot.Capabilities?.GetValueOrDefault("sampling-ms") == "1000", "pipe configuration acknowledgement uses stable capability identifiers");
        service.Config.RefreshMs = 5000; service.Save();
        Until(() => service.Snapshot.Capabilities?.GetValueOrDefault("sampling-ms") == "5000", 8000, "five-second sampling acknowledged without restarting worker");
        var slowClock = Stopwatch.StartNew(); var slowStamps = new List<DateTimeOffset>(); var expired = false; var maxAge = 0d;
        while (slowClock.Elapsed < TimeSpan.FromSeconds(11))
        {
            var s = service.Snapshot; var observed = DateTimeOffset.UtcNow;
            expired |= !s.Fresh(observed); maxAge = Math.Max(maxAge, (observed - s.Timestamp).TotalSeconds);
            if (slowStamps.Count == 0 || slowStamps[^1] != s.Timestamp) slowStamps.Add(s.Timestamp);
            Pump(50);
        }
        File.WriteAllText(Path.Combine(_output, "five-second-sampling.json"), JsonSerializer.Serialize(new { sampleCount = slowStamps.Count, maxAgeSeconds = maxAge,
            gapsSeconds = slowStamps.Zip(slowStamps.Skip(1), (a, b) => (b - a).TotalSeconds), expired, elevated = service.Snapshot.Elevated }));
        Check(slowStamps.Count >= 3 && !expired, "five-second real sampling remains fresh through sensor reads across consecutive cycles");
        service.Config.FpsEnabled = true; service.Save();
        Until(() => service.Snapshot.Capabilities?.GetValueOrDefault("fps-helper") == "requires-elevation", 8000, "FPS configuration reaches ordinary worker at five-second hardware cadence");
        var replies = new List<(DateTimeOffset Received, DateTimeOffset Hardware)>();
        void Replied() => replies.Add((DateTimeOffset.UtcNow, service.Snapshot.Timestamp));
        service.Changed += Replied; Pump(4200); service.Changed -= Replied;
        Check(replies.Count >= 6 && replies.Select(r => r.Hardware).Distinct().Count() < replies.Count && service.Snapshot.Capabilities?.GetValueOrDefault("sampling-ms") == "5000", "FPS replies update twice each second while hardware retains its configured five-second timestamp");
        var replyGaps = replies.Zip(replies.Skip(1), (a, b) => (b.Received - a.Received).TotalSeconds).Order().ToArray();
        Check(replyGaps.Length >= 5 && replyGaps[replyGaps.Length / 2] < .85, "ordinary FPS reply median is below former one-second delivery interval");
        Check(replies.Zip(replies.Skip(1), (a, b) => (b.Received - a.Received).TotalSeconds).All(gap => gap < 2.5), "FPS reply gaps are independent of five-second hardware refresh");
        File.WriteAllText(Path.Combine(_output, "fps-reply-cadence.json"), JsonSerializer.Serialize(new { replyCount = replies.Count, hardwareStamps = replies.Select(r => r.Hardware).Distinct().Count(),
            gapsSeconds = replies.Zip(replies.Skip(1), (a, b) => (b.Received - a.Received).TotalSeconds), note = "ordinary permissions; IPC cadence only, not real FPS capture" }));
        service.Config.FpsEnabled = false; service.Save();
        service.Config.RefreshMs = 500; service.Save();
        Until(() => service.Snapshot.Capabilities?.GetValueOrDefault("sampling-ms") == "500", 8000, "sampling configuration changes without restarting Pet");
        service.Config.FpsEnabled = true; service.Save();
        Until(() => service.Snapshot.Capabilities?.GetValueOrDefault("fps-helper") == "requires-elevation", 8000, "ordinary FPS request reports missing elevation without prompting or breaking monitoring");
        Check(service.Snapshot.Metrics.Any(m => m.Kind == "MEM.Load" && m.Valid), "FPS permission failure preserves basic monitoring");
        service.Config.FpsEnabled = false; service.Save();
        Until(() => service.Snapshot.Capabilities?.GetValueOrDefault("fps-helper") == "disabled", 8000, "disabling FPS shuts down its owned supervisor");
        var owned = service.WorkerProcessId!.Value;
        service.SetEnabled(false);
        Until(() => !service.Running, 8000, "disable shuts down worker");
        Check(!Alive(owned), "owned worker exited without orphans");
        service.SetEnabled(true); service.SetEnabled(false); service.SetEnabled(true);
        Until(() => service.Snapshot.Metrics.Count > 0, 25000, "rapid disable-enable restarts correctly");
        owned = service.WorkerProcessId!.Value; using (var process = Process.GetProcessById(owned)) process.Kill();
        Until(() => service.Status.Contains("不可用") || service.Status.Contains("中断"), 12000, "worker crash reported without ending Pet");
        Until(() => service.WorkerProcessId is int next && next != owned && service.Snapshot.Fresh(DateTimeOffset.UtcNow), 18000, "worker crash reconnects automatically with ordinary permissions");
        Check(!Alive(owned) && !service.Snapshot.Elevated, "recovery does not keep old worker or request automatic elevation");
        service.SetEnabled(false); Until(() => !service.Running, 8000, "crashed session cleans up");
        service.SetEnabled(true); Until(() => service.Snapshot.Metrics.Count > 0, 25000, "restart after crash recovers");
        owned = service.WorkerProcessId!.Value; service.Dispose(); Until(() => !Alive(owned), 8000, "dispose terminates only owned worker");
        var brokenDir = Path.Combine(_output, "broken"); Directory.CreateDirectory(brokenDir); var broken = Path.Combine(brokenDir, "monitor.json");
        File.WriteAllText(broken, "{broken"); using var invalid = new MonitorService(brokenDir, Dispatcher.CurrentDispatcher, () => false);
        invalid.SetEnabled(true); Check(!invalid.Running && File.ReadAllText(broken) == "{broken", "invalid monitor data preserved and disabled");
    }
    private static bool Alive(int pid) { try { using var p = Process.GetProcessById(pid); return !p.HasExited; } catch (ArgumentException) { return false; } }
    private static void Protocol()
    {
        Task.Run(async () =>
        {
            var name = "DesktopPet.Monitor." + Guid.NewGuid().ToString("N");
            using var pipe = new System.IO.Pipes.NamedPipeServerStream(name, System.IO.Pipes.PipeDirection.InOut, 1,
                System.IO.Pipes.PipeTransmissionMode.Byte, System.IO.Pipes.PipeOptions.Asynchronous | System.IO.Pipes.PipeOptions.CurrentUserOnly);
            using var parent = Process.GetCurrentProcess();
            var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "monitor", "DesktopPet.MonitorWorker.exe")) { UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add(name); start.ArgumentList.Add(Environment.ProcessId.ToString()); start.ArgumentList.Add(parent.StartTime.ToUniversalTime().Ticks.ToString());
            using var worker = Process.Start(start)!;
            try
            {
                using var deadline = new CancellationTokenSource(15000); await pipe.WaitForConnectionAsync(deadline.Token);
                using var reader = new StreamReader(pipe, System.Text.Encoding.UTF8, false, 4096, true);
                using var writer = new StreamWriter(pipe, new System.Text.UTF8Encoding(false), 4096, true) { AutoFlush = true };
                foreach (var command in new[] { new MonitorCommand("configure", RefreshMs: 2000), new MonitorCommand("catalog"), new MonitorCommand("status") })
                {
                    await writer.WriteLineAsync(JsonSerializer.Serialize(command, MonitorJson.Options));
                    var line = await reader.ReadLineAsync(deadline.Token); var s = JsonSerializer.Deserialize<MonitorSnapshot>(line!, MonitorJson.Options)!;
                    Check(s.ProtocolVersion == 1 && s.Capabilities?.GetValueOrDefault("sampling-ms") == "2000", "bounded pipe command " + command.Operation);
                    Check(s.Devices.Count > 0 && s.Metrics.Count > 0 && s.Capabilities?.GetValueOrDefault("driver-installation") == "disabled", "pipe catalog and status remain read-only " + command.Operation);
                }
                await writer.WriteLineAsync(JsonSerializer.Serialize(new MonitorCommand("stop"), MonitorJson.Options)); await worker.WaitForExitAsync(deadline.Token);
                Check(worker.HasExited, "pipe stop command terminates owned worker");
            }
            finally { pipe.Dispose(); if (!worker.HasExited) { worker.Kill(); await worker.WaitForExitAsync(); } }
        }).GetAwaiter().GetResult();
    }
    private static T Field<T>(object value, string name) => (T)value.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(value)!;
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern nint SendMessage(nint window, uint message, nint wParam, nint lParam);
    private static bool NativeVisible(Window window) => IsWindowVisible(new System.Windows.Interop.WindowInteropHelper(window).Handle);
    private static void PetVisibility()
    {
        var paths = new RuntimePaths(new StartupOptions(null, Path.Combine(_output, "pet-visibility"), null));
        new MonitorStore(paths.MonitorDirectory).Save(new() { Enabled = false });
        var store = new SettingsStore(paths.SettingsPath);
        Directory.CreateDirectory(paths.DataDirectory);
        File.WriteAllText(paths.SettingsPath, "{\"SchemaVersion\":1,\"Scale\":1.2,\"futureSetting\":17}");
        Check(!store.Load().PetHidden, "old settings without visibility field default to visible");
        var catalog = CharacterCatalog.Load(paths.CharacterRoot, paths.UserCharacterRoot);
        var pet = new PetWindow(catalog.Characters, paths.CharacterRoot, "ellen-flat2d", store.Load(), store, paths) { ShowActivated = false };
        pet.Show(); Pump(100);
        var monitor = Field<MonitorController>(pet, "_monitor"); var service = Field<MonitorService>(monitor, "_service");
        var tools = Field<DesktopPet.Tools.ToolsController>(pet, "_tools"); var toolService = Field<DesktopPet.Tools.ToolsService>(tools, "_service");
        var tray = Field<TrayService>(pet, "_tray");
        System.Windows.Forms.ToolStripMenuItem TrayItem(string label)
        {
            typeof(TrayService).GetMethod("RebuildMenu", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(tray, null);
            return Field<System.Windows.Forms.ContextMenuStrip>(tray, "_menu").Items.OfType<System.Windows.Forms.ToolStripMenuItem>().Single(i => i.Text == label);
        }
        service.Config.Enabled = true; service.Config.DisplayEnabled = true; service.Config.TaskbarEnabled = true;
        service.Config.TaskbarLeft = 30; service.Config.TaskbarTop = 60; service.Save();
        typeof(MonitorService).GetMethod("Apply", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(service,
            [new MonitorSnapshot(DateTimeOffset.UtcNow, [], [new("mem/load", "内存", "memory", "MEM.Load", "%", 52, "fixture")], "controlled fixture")]);
        monitor.ShowPanel(); tools.ShowPanel(); monitor.Click(); Pump(100);
        var panel = Field<MonitorWindow>(monitor, "_panel"); var toolPanel = Field<DesktopPet.Tools.ToolsWindow>(tools, "_panel");
        var display = Field<MonitorBubble>(monitor, "_display"); var taskbar = Field<MonitorTaskbarWindow>(monitor, "_taskbar");
        var bubble = Field<MonitorBubble>(monitor, "_bubble");
        var position = (pet.Left, pet.Top); var scale = store.Load().Scale;
        var hide = pet.ContextMenu.Items.OfType<W.MenuItem>().Single(i => i.Header as string == "隐藏 Q 宠");
        hide.RaiseEvent(new RoutedEventArgs(W.MenuItem.ClickEvent)); Pump(200);
        Check(!pet.IsVisible && !NativeVisible(pet) && store.Load().PetHidden, "pet context menu hides native window and persists choice");
        Check(!bubble.IsVisible && !Field<DispatcherTimer>(pet, "_animationTimer").IsEnabled, "hiding closes attached computer bubble and pauses sprite timer");
        Check(new Window[] { panel, toolPanel, display, taskbar }.All(w => w.IsVisible && NativeVisible(w) && w.Owner is null), "existing tools settings standalone display and taskbar remain natively visible");
        Check(Field<System.Windows.Forms.NotifyIcon>(tray, "_icon").Visible, "tray remains available while pet is hidden");
        monitor.Click(); Pump(100); Check(!bubble.IsVisible, "hidden pet cannot reopen its attached bubble");
        TrayItem("系统监控").PerformClick(); Pump(50);
        Check(!pet.IsVisible && panel.IsVisible, "tray opens monitor without restoring hidden pet");
        toolService.StartFocus(); var remaining = toolService.State.Focus.RemainingSeconds; Pump(1200);
        Check(toolService.State.Focus.Running && toolService.State.Focus.RemainingSeconds < remaining, "focus countdown continues while pet is hidden");
        toolService.Confirm("hidden pet feedback"); Pump(80);
        var notice = Field<DesktopPet.Tools.NoticeWindow>(tools, "_popup");
        Check(notice.IsVisible && NativeVisible(notice) && notice.Owner is null && !pet.IsVisible, "new reminder feedback stays visible without restoring pet");
        notice.Close();
        typeof(MonitorController).GetMethod("ShowAlert", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(monitor,
            ["CPU.Temp", new MonitorMetric("cpu/temp", "CPU", "cpu", "CPU.Temp", "℃", 95, "fixture")]); Pump(80);
        var alert = Field<Window>(monitor, "_alert");
        Check(alert.IsVisible && NativeVisible(alert) && alert.Owner is null && !pet.IsVisible, "new temperature alert stays visible without restoring pet"); alert.Close();
        panel.Close(); toolPanel.Close();
        TrayItem("系统监控").PerformClick(); tools.ShowPanel(); Pump(80);
        panel = Field<MonitorWindow>(monitor, "_panel"); toolPanel = Field<DesktopPet.Tools.ToolsWindow>(tools, "_panel");
        Check(panel.IsVisible && toolPanel.IsVisible && panel.Owner is null && toolPanel.Owner is null, "new settings windows open normally while pet is hidden");
        service.Config.DisplayEnabled = false; service.Config.TaskbarEnabled = false; service.Save();
        service.Config.DisplayEnabled = true; service.Config.TaskbarEnabled = true; service.Save(); Pump(100);
        display = Field<MonitorBubble>(monitor, "_display"); taskbar = Field<MonitorTaskbarWindow>(monitor, "_taskbar");
        Check(display.IsVisible && taskbar.IsVisible && NativeVisible(display) && NativeVisible(taskbar), "display switches still create visible windows while pet is hidden");
        service.SetEnabled(true);
        Until(() => service.WorkerProcessId is not null && service.Snapshot.Fresh(DateTimeOffset.UtcNow) &&
            service.Snapshot.Metrics.Any(m => m.Kind == "MEM.Load" && m.Valid && m.Source != "fixture"), 25000, "real ordinary monitor worker connects while pet is hidden");
        var pid = service.WorkerProcessId; var timestamp = service.Snapshot.Timestamp;
        TrayItem("显示 Q 宠").PerformClick(); Pump(100);
        Check(pet.IsVisible && NativeVisible(pet) && !store.Load().PetHidden && Field<DispatcherTimer>(pet, "_animationTimer").IsEnabled, "tray show restores native pet and sprite timer");
        Check(new Window[] { panel, toolPanel, display, taskbar }.All(w => w.Owner == pet && w.IsVisible), "restoring pet preserves windows and their original ownership");
        Check(Math.Abs(pet.Left - position.Left) < 1 && Math.Abs(pet.Top - position.Top) < 1 && store.Load().Scale == scale, "hide show preserves pet position and scale");
        TrayItem("隐藏 Q 宠").PerformClick();
        Until(() => service.Snapshot.Timestamp > timestamp, 8000, "fresh hardware sampling continues while hidden");
        Check(service.Running && service.WorkerProcessId == pid, "hide show does not restart monitoring worker");
        typeof(System.Windows.Forms.NotifyIcon).GetMethod("OnDoubleClick", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(Field<System.Windows.Forms.NotifyIcon>(tray, "_icon"), [EventArgs.Empty]); Pump(50);
        Check(pet.IsVisible && !store.Load().PetHidden, "tray double click restores hidden pet");
        pet.SetPetHidden(true); pet.NotifyAlreadyRunning(); Pump(50);
        Check(pet.IsVisible && !store.Load().PetHidden, "duplicate launch restores hidden pet and saves visible state");
        pet.SetPetHidden(true); pet.Close();
        Until(() => pid is null || !ProcessExists(pid.Value), 10000, "closing hidden pet terminates its ordinary worker");
        Check(store.Load().PetHidden && !panel.IsVisible && !toolPanel.IsVisible && !display.IsVisible && !taskbar.IsVisible, "exit closes detached windows and preserves hidden preference");
        Check(store.Load().AdditionalFields?.GetValueOrDefault("futureSetting").GetInt32() == 17, "visibility persistence preserves unknown user fields");
        new MonitorStore(paths.MonitorDirectory).Save(new() { Enabled = false });
        var restored = new PetWindow(catalog.Characters, paths.CharacterRoot, "ellen-flat2d", store.Load(), store, paths);
        restored.Show(); Pump(200);
        Check(!restored.IsVisible && !NativeVisible(restored) && !restored.ShowActivated, "restart with hidden preference stays hidden without activation");
        Check(Field<TrayService>(restored, "_tray") is not null && !Field<DispatcherTimer>(restored, "_animationTimer").IsEnabled, "hidden startup still initializes tray and pauses animation");
        restored.SetPetHidden(false); Pump(80); Check(restored.IsVisible && !store.Load().PetHidden, "hidden startup remains recoverable through show action");
        restored.Close();
    }
    private static bool ProcessExists(int pid)
    { try { using var process = Process.GetProcessById(pid); return !process.HasExited; } catch (ArgumentException) { return false; } }
    private static void PetInteraction()
    {
        var tracker = new MonitorForegroundTracker();
        var processes = new Dictionary<int, (int, string)> { [10] = (0, "video.exe"), [11] = (10, "video.exe"),
            [20] = (0, "game.exe"), [30] = (0, "explorer.exe"), [31] = (0, "dwm.exe"), [32] = (0, "PresentMon.exe"), [99] = (0, "DesktopPet.exe") };
        tracker.Select(10, 99, processes, _ => 100);
        foreach (var foreground in new[] { 0, 32, 99 })
            Check(tracker.Select(foreground, 99, processes, _ => 100).SequenceEqual(new[] { 10, 11 }), "Pet shell and transient foreground preserve valid FPS target " + foreground);
        foreach (var foreground in new[] { 30, 31 })
        {
            tracker.Select(10, 99, processes, _ => 100);
            Check(tracker.Select(foreground, 99, processes, _ => 100).Count == 0 && tracker.Select(99, 99, processes, _ => 100).Count == 0,
                "desktop foreground clears previous application so desktop FPS is selected " + foreground);
        }
        Check(tracker.Select(20, 99, processes, _ => 100).SequenceEqual(new[] { 20 }), "real external application switch still replaces FPS target");
        Check(tracker.Select(0, 99, processes, _ => 101).Count == 0, "retained FPS target clears on PID reuse during transient foreground");
        tracker.Select(10, 99, processes, _ => 100); processes.Remove(10);
        Check(tracker.Select(30, 99, processes, _ => 100).Count == 0, "closed application clears target even while desktop is foreground");
        Check(new MonitorForegroundTracker().Select(30, 99, processes, _ => 100).Count == 0, "desktop startup never chooses an unrelated background FPS target");
        var paths = new RuntimePaths(new StartupOptions(null, Path.Combine(_output, "pet-interaction"), null));
        new MonitorStore(paths.MonitorDirectory).Save(new() { Enabled = false });
        var store = new SettingsStore(paths.SettingsPath); var catalog = CharacterCatalog.Load(paths.CharacterRoot, paths.UserCharacterRoot);
        var pet = new PetWindow(catalog.Characters, paths.CharacterRoot, "ellen-flat2d", store.Load(), store, paths) { ShowActivated = false };
        pet.Show(); Pump(80);
        Check((MonitorDisplayBehavior.GetWindowLong(new System.Windows.Interop.WindowInteropHelper(pet).Handle, -20) & 0x08000000) != 0,
            "pet uses native NOACTIVATE to keep video foreground during clicks");
        Check(SendMessage(new System.Windows.Interop.WindowInteropHelper(pet).Handle, 0x0021, 0, (0x0201 << 16) | 1) == 3,
            "pet native left mouse activation returns MA_NOACTIVATE without discarding click");
        var controller = Field<MonitorController>(pet, "_monitor"); var service = Field<MonitorService>(controller, "_service");
        service.Config.Enabled = service.Config.FpsEnabled = true; service.Config.SelectedMetrics = ["FPS"]; service.FpsTargets = () => [11200];
        var frameAt = DateTimeOffset.UtcNow;
        var snapshot = new MonitorSnapshot(frameAt, [], [new("FPS.11200", "video", "process/11200", "FPS", "FPS", 60, "controlled FPS fixture", SampledAt: frameAt)],
            "fixture", Capabilities: new() { ["fps-helper"] = "running" });
        typeof(MonitorService).GetMethod("Apply", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(service, [snapshot]);
        var original = service.Snapshot;
        for (var i = 0; i < 3; i++)
        {
            controller.Click(); Pump(50);
            var bubble = Field<MonitorBubble>(controller, "_bubble");
            Check(Find<W.TextBlock>(bubble).Any(t => t.Text.Contains("60")) && !service.Running && service.WorkerProcessId is null &&
                service.Snapshot.Timestamp == original.Timestamp && service.Snapshot.Capabilities!["fps-foreground"] == "11200",
                "reopening computer bubble renders cached FPS without launching capture or changing target " + i);
            bubble.Hide();
        }
        var menu = pet.ContextMenu; menu.PlacementTarget = pet; menu.IsOpen = true; Pump(80);
        var hide = menu.Items.OfType<W.MenuItem>().Single(i => i.Header as string == "隐藏 Q 宠");
        // Render the hover state without injecting global mouse/keyboard input into the user's desktop.
        typeof(W.MenuItem).GetProperty("IsHighlighted")!.GetSetMethod(true)!.Invoke(hide, [true]); Pump(80);
        var selection = (W.Border)hide.Template.FindName("Selection", hide);
        Check(selection.CornerRadius.TopLeft == 6 && selection.Background is SolidColorBrush { Color: var hover } && hover == (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#EADFF6"),
            "menu highlight uses unified lavender rounded selection");
        Check(Find<W.TextBlock>(hide).Count(t => t.Text == "✓") == 1 && ((FrameworkElement)hide.Template.FindName("CheckMark", hide)).Visibility == Visibility.Collapsed,
            "unchecked menu action has no persistent selection mark");
        CaptureElement(menu, "pet-context-menu-highlight.png");
        var quiet = menu.Items.OfType<W.MenuItem>().Single(i => i.Header as string == "安静模式");
        typeof(W.MenuItem).GetProperty("IsHighlighted")!.GetSetMethod(true)!.Invoke(hide, [false]);
        typeof(W.MenuItem).GetProperty("IsHighlighted")!.GetSetMethod(true)!.Invoke(quiet, [true]); quiet.IsChecked = true; Pump(50);
        Check(((FrameworkElement)quiet.Template.FindName("CheckMark", quiet)).Visibility == Visibility.Visible, "quiet mode keeps explicit checkmark separate from hover state");
        CaptureElement(menu, "pet-context-menu-checked.png"); quiet.IsChecked = false;
        Check(menu.Template.FindName("MenuFrame", menu) is W.Border { CornerRadius.TopLeft: 10 } && menu.ActualWidth < 260,
            "menu has a compact rounded surface without default system gutter");
        hide.IsEnabled = false; hide.Focus(); Pump(30);
        Check(hide.Opacity == .45, "disabled menu action remains distinct"); hide.IsEnabled = true;
        menu.IsOpen = false;
        service.Start(false); // Ordinary isolated worker only; no UAC or administrator FPS request.
        Until(() => service.WorkerProcessId is not null && service.Snapshot.Metrics.Any(m => m.Kind == "MEM.Load" && m.Valid),
            25000, "ordinary worker provides hardware sample before repeated bubble clicks");
        var pid = service.WorkerProcessId;
        for (var i = 0; i < 3; i++) { controller.Click(); Pump(100); }
        Check(service.WorkerProcessId == pid && service.Running, "repeated bubble clicks keep the existing real worker process");
        pet.Close(); Until(() => pid is null || !ProcessExists(pid.Value), 10000, "interaction test leaves no ordinary worker running");
    }
    private static void Ui()
    {
        var paths = new RuntimePaths(new StartupOptions(null, Path.Combine(_output, "ui-data"), null));
        new MonitorStore(paths.MonitorDirectory).Save(new() { Enabled = false });
        var store = new SettingsStore(paths.SettingsPath); var catalog = CharacterCatalog.Load(paths.CharacterRoot, paths.UserCharacterRoot);
        var pet = new PetWindow(catalog.Characters, paths.CharacterRoot, "ellen-flat2d", store.Load(), store, paths) { ShowActivated = false };
        pet.Show(); Pump(100); var controller = Field<MonitorController>(pet, "_monitor"); var service = Field<MonitorService>(controller, "_service");
        service.Config.Enabled = true; // Inject controlled data without starting a second real hardware worker.
        var now = DateTimeOffset.UtcNow;
        var snapshot = new MonitorSnapshot(now, [new("gpu", "测试 GPU", "GPU")], [
            new("cpu/load", "CPU", "cpu", "CPU.Load", "%", 28, "Windows"), new("cpu/temp", "CPU", "cpu", "CPU.Temp", "℃", 64, "CPU Package"),
            new("gpu/load", "GPU", "gpu", "GPU.Load", "%", 45, "GPU Core"), new("gpu/temp", "GPU", "gpu", "GPU.Temp", "℃", 61, "GPU Core"),
            new("mem/load", "内存", "memory", "MEM.Load", "%", 52, "Windows")], "测试数据 · 非实时硬件值");
        typeof(MonitorService).GetMethod("Apply", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(service, [snapshot]);
        controller.Click(); Pump(150); var bubble = Field<MonitorBubble>(controller, "_bubble");
        Check(bubble.IsVisible && bubble.Owner == pet && bubble.Title == "电脑状态", "click opens pet-attached data bubble with plain computer status title");
        Check(Find<W.Border>(bubble).Count(b => b.Child is MonitorMetricRow && b.CornerRadius.TopLeft == 10 && b.Padding.Left == 10) == service.Config.SelectedMetrics.Count,
            "computer status gives every chosen metric a padded rounded card");
        Check(Find<W.ScrollViewer>(bubble).Any(s => s.MaxHeight == 380 && s.HorizontalScrollBarVisibility == W.ScrollBarVisibility.Disabled), "computer status keeps bounded scrolling and fixed visible actions");
        var before = (pet.Left, pet.Top); pet.Left += 20; Pump(100); Check(pet.Left != before.Left && bubble.IsVisible, "bubble follows pet movement");
        Capture(bubble, "data-bubble.png");
        CaptureDesktop(bubble, "desktop-data-bubble.png");
        var bubbleMetrics = service.Config.SelectedMetrics.ToList(); var bubbleWidth = service.Config.PanelWidth;
        service.Config.PanelWidth = 240;
        service.Config.SelectedMetrics = Enumerable.Range(0, 18).Select(i => "preview/" + i).ToList(); bubble.Refresh(service); Pump(60);
        var bubbleScroll = Find<W.ScrollViewer>(bubble).First(s => s.MaxHeight == 380);
        Check(bubbleScroll.ScrollableHeight > 0 && Find<W.Button>(bubble).Where(b => b.Content as string is "监控设置" or "收起").All(b => b.IsVisible), "narrow long computer status keeps scrollable metrics and visible footer actions");
        Capture(bubble, "data-bubble-long-narrow.png");
        service.Config.SelectedMetrics = bubbleMetrics; service.Config.PanelWidth = bubbleWidth;
        service.Config.Visual.CornerRadius = 0; bubble.Refresh(service);
        Check(Field<DesktopPet.Tools.PixelFrame>(bubble, "_frame").CornerRadius == 0, "explicit saved square-frame preference remains valid");
        service.Config.Visual.CornerRadius = 16; bubble.Refresh(service);
        service.Config.Visual.ShowGroups = true; service.Config.Visual.ShowBars = true; service.Config.Visual.SmoothValues = true;
        service.Config.Visual.CornerRadius = 12; service.Config.Visual.GroupRadius = 8; service.Config.Visual.ValueFamily = "Consolas"; service.Save(); Pump(500);
        Check(Find<W.ProgressBar>(bubble).Any(p => p.IsVisible && p.Value > 0), "advanced theme draws hardware progress bars");
        Check(Find<W.Border>(bubble).Any(b => b.CornerRadius.TopLeft == service.Config.Visual.GroupRadius), "advanced theme draws device groups");
        Capture(bubble, "advanced-theme.png"); service.Config.Visual = new(); service.Save(); Pump(100);
        var panel = new MonitorWindow(service) { Owner = pet, ShowActivated = false }; panel.Show(); Pump(120); Capture(panel, "monitor-panel.png");
        var surface = (W.Border)panel.Content;
        Check(surface.Name == "MonitorSurface" && surface.Padding.Left >= 18 && surface.Margin.Left >= 18, "monitor surface keeps content away from both card and window borders");
        var slowSnapshot = snapshot with { Timestamp = DateTimeOffset.UtcNow.AddMilliseconds(-5800), Capabilities = new() { ["sampling-ms"] = "5000" } };
        typeof(MonitorService).GetMethod("Apply", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(service, [slowSnapshot]); Pump(100);
        Check(Field<Dictionary<string, W.TextBlock>>(panel, "_values")["MEM.Load"].Text.Contains("52") && !Find<W.TextBlock>(bubble).Any(t => t.Text == "已断开"), "five-second readings remain visible in panel and bubble after former expiry boundary");
        var delayedSnapshot = slowSnapshot with { Timestamp = DateTimeOffset.UtcNow.AddSeconds(-20) };
        typeof(MonitorService).GetMethod("Apply", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(service, [delayedSnapshot]); Pump(100);
        Check(Field<Dictionary<string, W.TextBlock>>(panel, "_values")["MEM.Load"].Text == "数据延迟" && Find<W.TextBlock>(bubble).Any(t => t.Text == "数据延迟"), "stalled sampling clears old numbers and displays delayed state on both surfaces");
        typeof(MonitorService).GetMethod("Apply", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(service, [MonitorSnapshot.Empty]); Pump(100);
        Check(Field<Dictionary<string, W.TextBlock>>(panel, "_values")["MEM.Load"].Text == "已断开" && Find<W.TextBlock>(bubble).Any(t => t.Text == "已断开"), "pipe-loss empty snapshot displays disconnected on both surfaces");
        typeof(MonitorService).GetMethod("Apply", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(service, [snapshot]); Pump(100);
        var networkSnapshot = snapshot with { Metrics = [.. snapshot.Metrics,
            new("network/a/up", "上传", "a", "NET.Up", "B/s", 0, "fixture"), new("network/a/down", "下载", "a", "NET.Down", "B/s", 0, "fixture"),
            new("network/b/up", "上传", "b", "NET.Up", "B/s", 2400, "fixture"), new("network/b/down", "下载", "b", "NET.Down", "B/s", 52000, "fixture")] };
        service.Config.SelectedMetrics.AddRange(["network/b/up", "network/b/down"]);
        typeof(MonitorService).GetMethod("Apply", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(service, [networkSnapshot]); Pump(100);
        Check(service.Catalog.Count(m => m.Kind is "NET.Up" or "NET.Down") == 2 && service.Config.SelectedMetrics.Count(id => id == "NET.Up" || id == "NET.Down") == 2 && !service.Config.SelectedMetrics.Contains("network/b/up"), "repeated network rows and existing bubble selections collapse into upload download pair");
        Capture(panel, "network-two-rows-controlled-data.png");
        var detailSnapshot = networkSnapshot with { Metrics = [.. networkSnapshot.Metrics, .. Enumerable.Range(0, 300).Select(i => new MonitorMetric("gpu/engine/" + i, "GPU engine " + i, "gpu", "Load", "%", i % 100, "fixture"))] };
        service.Config.SelectedMetrics.Add("gpu/engine/1");
        typeof(MonitorService).GetMethod("Apply", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(service, [detailSnapshot]); Pump(100);
        Check(service.Catalog.Count <= 20 && service.Catalog.All(m => !m.Id.StartsWith("gpu/engine/")) && Field<Dictionary<string, W.TextBlock>>(panel, "_values").Count <= 20, "important metrics and bubble order hide hundreds of raw sensors and legacy choices");
        Capture(panel, "important-metrics-controlled-data.png");
        service.Config.FpsEnabled = true; service.Config.FpsProcessId = 22222; service.FpsTargets = () => [11200];
        var frameAt = DateTimeOffset.UtcNow;
        var fpsSnapshot = snapshot with { Timestamp = frameAt, Metrics = [.. snapshot.Metrics,
            new("FPS.11200", "GpuFrameProbe.exe · FPS", "process/11200", "FPS", "FPS", 142, "PresentMon", SampledAt: frameAt),
            new("FPS.22222", "Other.exe · FPS", "process/22222", "FPS", "FPS", 240, "PresentMon", SampledAt: frameAt)],
            Capabilities = new() { ["fps-helper"] = "running" } };
        typeof(MonitorService).GetMethod("Apply", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(service, [fpsSnapshot]); Pump(100);
        var fpsValue = Field<Dictionary<string, W.TextBlock>>(panel, "_values")["FPS"];
        Check(fpsValue.Text.Contains("142") && !fpsValue.Text.Contains("240"), "FPS follows foreground application despite legacy PID and faster background process");
        Capture(panel, "fps-panel-controlled-data.png");
        var historyFile = Path.Combine(service.Store.DirectoryPath, "history", DateTimeOffset.Now.ToString("yyyy-MM-dd") + ".jsonl");
        var historySize = new FileInfo(historyFile).Length;
        typeof(MonitorService).GetField("_historyAt", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(service, 0L);
        var newFramesSameHardware = fpsSnapshot with { Metrics = fpsSnapshot.Metrics.Select(m => m.Id == "FPS.11200" ? m with { Value = 128, SampledAt = DateTimeOffset.UtcNow } : m).ToList() };
        typeof(MonitorService).GetMethod("Apply", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(service, [newFramesSameHardware]); Pump(50);
        Check(fpsValue.Text.Contains("128") && new FileInfo(historyFile).Length == historySize, "new FPS with unchanged hardware timestamp updates display without duplicate history even after write interval expires");
        service.FpsTargets = () => [22222];
        typeof(MonitorService).GetMethod("Apply", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(service, [fpsSnapshot]); Pump(50);
        Check(fpsValue.Text.Contains("240"), "foreground change updates FPS on the next sample");
        service.FpsTargets = () => [33333];
        typeof(MonitorService).GetMethod("Apply", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(service, [fpsSnapshot]); Pump(50);
        Check(fpsValue.Text == "等待帧数据", "foreground without frames displays waiting instead of background FPS");
        Check(Find<W.TextBlock>(bubble).Any(t => t.Text == "等待帧数据"), "bubble shares foreground FPS waiting state with monitoring panel");
        Check(fpsValue.ToolTip is string hint && !hint.Contains("传感器"), "FPS waiting tooltip describes frames rather than a missing hardware sensor");
        service.FpsTargets = () => [11200];
        var expiringFrames = fpsSnapshot with { Timestamp = DateTimeOffset.UtcNow, Metrics = fpsSnapshot.Metrics.Select(m => m.Kind == "FPS" ? m with { SampledAt = DateTimeOffset.UtcNow.AddSeconds(-2.7) } : m).ToList() };
        typeof(MonitorService).GetMethod("Apply", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(service, [expiringFrames]); Pump(600);
        Check(fpsValue.Text == "等待帧数据" && service.Snapshot.Fresh(DateTimeOffset.UtcNow), "FPS expiry refreshes settings while hardware remains fresh and no new pipe reply arrives");
        typeof(MonitorService).GetMethod("Apply", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(service, [snapshot]); Pump(100);
        Check(!Field<Dictionary<string, W.TextBlock>>(panel, "_values")["FPS"].Text.Contains("142"), "FPS display clears the old value when capture no longer supplies frames");
        service.Config.FpsEnabled = false; service.Config.FpsProcessId = null;
        service.SetFpsEnabled(false); Pump(100);
        Check(Field<Dictionary<string, W.TextBlock>>(panel, "_values")["FPS"].Text == "—" && service.Catalog.All(m => m.Kind != "FPS") && !service.Config.SelectedMetrics.Contains("FPS"), "one FPS switch hides display and removes bubble metric immediately");
        typeof(MonitorService).GetMethod("Apply", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(service, [snapshot]); Pump(100);
        CaptureDesktop(panel, "desktop-monitor-panel.png");
        Check(Find<W.TabControl>(panel).Single().Items.Count == 3, "daily monitor has exactly three pages");
        Click(panel, "进阶功能"); var advanced = Field<MonitorAdvancedWindow>(panel, "_advanced"); Pump(100);
        var navigation = Field<W.ListBox>(advanced, "_navigation");
        Check(navigation.Items.Count == 5 && navigation.SelectedIndex == -1, "advanced opens with five overview cards and unloaded setting groups");
        Check(!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621) || MonitorFluentStyle.AcrylicEnabled(advanced), "supported desktop uses native Acrylic backdrop");
        CaptureDesktop(advanced, "desktop-advanced-overview.png"); Capture(advanced, "advanced-overview.png");
        navigation.SelectedIndex = 1; Pump(100);
        var tools = Find<MonitorSystemToolsPage>(advanced).Single();
        Check(!service.SystemTools.PreventSleep && service.SystemTools.Shutdown.Due is null, "system tools do not enable sleep prevention or shutdown by default");
        tools.IsExpanded = true; Pump(100);
        service.SystemTools.Shutdown.Schedule(60, Environment.TickCount64); Click(advanced, "取消本程序的定时关机");
        Check(service.SystemTools.Shutdown.Due is null, "system tool cancel button clears only Pet schedule without OS shutdown commands");
        tools.BringIntoView(); Pump(100); Capture(advanced, "system-tools.png"); CaptureDesktop(advanced, "desktop-system-tools.png");
        navigation.SelectedIndex = 2; Pump(100);
        Capture(advanced, "appearance-overview.png");
        Find<W.Expander>(advanced).First(e => e.Header as string == "分组、进度条与动画").IsExpanded = true; Pump(100);
        var themeEditor = Find<MonitorVisualEditor>(advanced).Single();
        Find<W.ComboBox>(themeEditor).First().SelectedItem = "Temp"; Pump(50);
        Toggle(themeEditor, "使用所选分类的独立颜色阈值", true);
        Commit(Setting(themeEditor, "警告阈值"), "60"); Commit(Setting(themeEditor, "严重阈值"), "80");
        Check(service.Config.Visual.Thresholds["Temp"].Warn == 60 && service.Config.Cpu.Threshold == 90 && service.Config.Cpu.Enabled,
            "category color editor immediately saves independent thresholds without changing alerts");
        Commit(Setting(themeEditor, "警告阈值"), "90");
        Check(service.Config.Visual.Thresholds["Temp"].Warn == 60, "invalid category color edit preserves saved thresholds");
        service.Config.Visual = new(); service.Save(); advanced.Close();
        Find<W.TabControl>(panel).Single().SelectedIndex = 2; Pump(100); Capture(panel, "history-panel.png");
        var history = ((W.ScrollViewer)((W.TabItem)Find<W.TabControl>(panel).Single().Items[2]).Content).Content!;
        var dates = Field<W.ComboBox>(history, "_dates");
        Check(dates.Items.Count > 1, "history page discovers recorded dates");
        dates.SelectedIndex = 1; Until(() => Field<List<MonitorSnapshot>?>(history, "_loaded") is not null, 5000, "history date loads without changing monitor state");
        Check(Field<HistoryGraph>(history, "_graph").Series.Length > 0, "history chart displays recorded data");
        dates.SelectedIndex = 0; Pump(100);
        Check(Field<W.TextBlock>(history, "_status").Visibility == Visibility.Collapsed, "returning to live history clears previous date status");
        dates.IsDropDownOpen = true; Pump(50);
        var popup = (W.Primitives.Popup)dates.Template.FindName("PART_Popup", dates);
        Check(popup.IsOpen && popup.Child is W.Border { CornerRadius.TopLeft: 8 }, "history date dropdown keeps selection functionality with rounded Fluent popup");
        Check(Find<W.TextBlock>(dates).Any(t => t.Text == "当前会话") && !Find<W.TextBlock>(popup.Child).Any(t => t.Text.Contains("DayChoice")), "history selected date and popup display labels instead of data objects");
        dates.IsDropDownOpen = false; Pump(30);
        Check(Find<W.TextBlock>(Field<W.ComboBox>(history, "_metric")).Any(t => t.Text.Contains("CPU") && !t.Text.Contains("Choice")), "history metric selector displays human readable metric name");
        var zoom = Field<W.Slider>(history, "_zoom"); var graph = Field<HistoryGraph>(history, "_graph");
        var series = graph.Series;
        zoom.Value = 10; Pump(20); Check(graph.Series.Length <= Math.Max(2, (int)Math.Ceiling(series.Length / 10d)), "styled zoom slider still controls recent history window");
        zoom.Value = 1; Pump(20);
        graph.Series = Enumerable.Range(0, 12).Select(i => new HistoryPoint(now.AddSeconds(i * 10), i == 6 ? null : 24 + Math.Sin(i * .6) * 12, "%")).ToArray();
        graph.InvalidateVisual(); Pump(50); Capture(panel, "history-trend-controlled.png");
        panel.Width = panel.MinWidth; panel.Height = panel.MinHeight; Pump(50); Capture(panel, "minimum-history.png");
        var historyScroll = Find<W.ScrollViewer>(panel).First(s => ReferenceEquals(s.Content, history));
        Check(historyScroll.ExtentWidth <= historyScroll.ViewportWidth + 1 && historyScroll.ScrollableHeight > 0, "minimum history window stacks filters and scrolls without horizontal overflow");
        panel.Width = 660; panel.Height = 740; Pump(30);
        graph.Series = []; graph.InvalidateVisual(); Pump(30); Capture(panel, "history-empty.png");
        graph.Series = [new(now, 24, "%")]; graph.InvalidateVisual(); Pump(30); Capture(panel, "history-single-sample.png");
        graph.Series = series; graph.InvalidateVisual(); Pump(30);
        service.Config.DisplayEnabled = true; service.Save(); Pump(100);
        var display = Field<MonitorBubble>(controller, "_display");
        Check(display.IsVisible && display.Owner == pet, "standalone monitor window belongs to Pet");
        service.Config.DisplayTopmost = false; service.Save(); Pump(100);
        Check(!display.Topmost && display.Owner is null && pet.Topmost &&
            (MonitorDisplayBehavior.GetWindowLong(new System.Windows.Interop.WindowInteropHelper(display).Handle, -20) & 8) == 0,
            "monitor topmost can be disabled without changing the topmost pet");
        service.Config.DisplayTopmost = true; service.Save(); Pump(100);
        Check(display.Topmost && display.Owner == pet &&
            (MonitorDisplayBehavior.GetWindowLong(new System.Windows.Interop.WindowInteropHelper(display).Handle, -20) & 8) != 0,
            "monitor topmost restores attachment to pet");
        var work = System.Windows.Forms.Screen.PrimaryScreen!.WorkingArea;
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(display);
        service.Config.ClampToScreen = true;
        display.Left = (work.Right - 20) / dpi.DpiScaleX;
        Field<MonitorDisplayBehavior>(controller, "_displayBehavior").SavePosition(); Pump(100);
        Check(service.Config.DisplayLeft <= work.Right - display.ActualWidth * dpi.DpiScaleX + 2, "drag completion clamps only owned monitor into work area");
        service.Config.ClampToScreen = false; display.Left = (work.Right - 20) / dpi.DpiScaleX;
        Field<MonitorDisplayBehavior>(controller, "_displayBehavior").SavePosition(); Pump(100);
        Check(Math.Abs(service.Config.DisplayLeft!.Value - (work.Right - 20)) < 2, "monitor can opt out of drag clamping and retain partial offscreen position");
        service.Config.ClampToScreen = true; service.Save(); Pump(100);
        service.Config.ClickThrough = true; service.Save(); Pump(300);
        var displayHandle = new System.Windows.Interop.WindowInteropHelper(display).Handle;
        Check((MonitorDisplayBehavior.GetWindowLong(displayHandle, -20) & 0x20) != 0, "click-through applies to owned monitor window");
        var petHandle = new System.Windows.Interop.WindowInteropHelper(pet).Handle;
        Check((MonitorDisplayBehavior.GetWindowLong(petHandle, -20) & 0x20) == 0, "pet remains interactive when monitor is click-through");
        service.Config.ClickThrough = false; service.Save(); Pump(300);
        Check((MonitorDisplayBehavior.GetWindowLong(displayHandle, -20) & 0x20) == 0, "click-through can be turned off");
        service.Config.Language = "en"; service.Save(); Pump(600);
        Check(Find<W.TabControl>(panel).Single().Items.Cast<W.TabItem>().Any(i => i.Header as string == "Display"), "language switches in existing WPF panel");
        Capture(panel, "english-panel.png");
        service.Config.Language = "zh"; service.Config.DisplayEnabled = false; service.Save(); Pump(600);
        Check(!display.IsVisible, "disabled display closes independently of pet");
        service.Config.TaskbarEnabled = true; service.Save(); Pump(100);
        var taskbar = Field<MonitorTaskbarWindow>(controller, "_taskbar");
        Until(() => !taskbar.PlacementStatus.StartsWith("等待"), 5000, "taskbar placement reports availability or verified safe fallback");
        var taskbarHandle = new System.Windows.Interop.WindowInteropHelper(taskbar).EnsureHandle();
        Check((MonitorDisplayBehavior.GetWindowLong(taskbarHandle, -20) & 0x08000020) == 0x08000000, "taskbar display stays nonactivating while receiving clicks and drags");
        if (taskbar.IsVisible) Capture(taskbar, "taskbar-monitor.png");
        File.WriteAllText(Path.Combine(_output, "taskbar-status.txt"), taskbar.PlacementStatus);
        service.Config.TaskbarEnabled = false; service.Save(); Pump(100);
        Check(!taskbar.IsVisible, "taskbar display closes on disable");
        Find<W.TabControl>(panel).Single().SelectedIndex = 0; Pump(50);
        var ids = service.Config.SelectedMetrics.ToArray(); MoveMetric(panel, ids[0], "↓");
        Check(service.Config.SelectedMetrics[1] == ids[0], "unified metric reorder persists");
        service.Config.Cpu.Enabled = true;
        typeof(MonitorController).GetMethod("Alert", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(controller, ["CPU.Temp", new MonitorMetric("CPU.Temp", "CPU 温度", "cpu", "CPU.Temp", "℃", 95, "CPU Package")]);
        Pump(120); var alert = Field<Window>(controller, "_alert"); Capture(alert, "temperature-alert.png");
        Click(alert, "暂停提醒 1 小时"); Check(service.Config.Cpu.SnoozeUntil > DateTimeOffset.UtcNow.AddMinutes(59), "alert snooze action saves state");
        panel.Close(); controller.Drag(false);
        var lifetime = Field<BubbleLifetime>(controller, "_lifetime"); lifetime.Show(); lifetime.Tick(now, false, false); lifetime.Tick(now.AddSeconds(5), false, false); Pump(200);
        Check(!bubble.IsVisible, "expired data bubble hides");
        controller.Click(); Pump(100); Click(bubble, "收起"); Pump(200); Check(!bubble.IsVisible, "manual close does not reopen next tick");
        pet.Close();
    }
    private static W.TextBox Setting(DependencyObject root, string label) => Find<W.StackPanel>(root)
        .First(p => p.Children.OfType<W.TextBlock>().Any(t => t.Text == label)).Children.OfType<W.TextBox>().Single();
    private static void Commit(W.TextBox box, string value)
    {
        box.Text = value;
        box.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice,
            PresentationSource.FromVisual(box), 0, System.Windows.Input.Key.Enter) { RoutedEvent = System.Windows.Input.Keyboard.KeyDownEvent }); Pump(20);
    }
    private static void Toggle(DependencyObject root, string label, bool value)
    {
        var box = Find<W.CheckBox>(root).First(b => b.Content as string == label);
        box.IsChecked = value; box.RaiseEvent(new RoutedEventArgs(W.Primitives.ButtonBase.ClickEvent)); Pump(20);
    }
    private static void MoveMetric(Window panel, string id, string arrow)
    {
        var box = Find<W.CheckBox>(panel).First(b => b.Tag as string == id);
        var row = (W.Grid)box.Parent;
        row.Children.OfType<W.Button>().First(b => b.Content as string == arrow).RaiseEvent(new RoutedEventArgs(W.Button.ClickEvent)); Pump(20);
    }
    private static void SimplifiedControls()
    {
        var dir = Path.Combine(_output, "simplified");
        using var service = new MonitorService(dir, Dispatcher.CurrentDispatcher, () => false);
        service.Config.Enabled = false; service.Config.WebEnabled = true; service.Config.WebLan = true; service.Config.WebIpv6 = true;
        service.Config.Cpu.Enabled = false; service.Config.Gpu.Enabled = true; service.Config.DisplayEnabled = false;
        Check(service.Save() && !service.Web.Running, "legacy web flags never create a listener on save");
        var panel = new MonitorWindow(service) { ShowActivated = false }; panel.Show(); Pump(100);
        Check(panel.Title == "系统监控", "monitor title uses unified name");
        var tabs = Find<W.TabControl>(panel).Single();
        Check(tabs.Items.Cast<W.TabItem>().Select(t => t.Header as string).SequenceEqual(new[] { "监控", "显示", "历史" }), "three daily pages have stable names");
        var reminder = Find<W.CheckBox>(panel).First(b => b.Content as string == "高温提醒（部分开启）");
        Check(reminder.IsChecked is null, "old mixed alert configuration displays partial state");
        reminder.IsChecked = false; reminder.RaiseEvent(new RoutedEventArgs(W.Primitives.ButtonBase.ClickEvent)); Pump(20);
        Check(!service.Config.Cpu.Enabled && !service.Config.Gpu.Enabled, "alert master switch disables both channels immediately");
        Toggle(panel, "高温提醒", true);
        Check(service.Config.Cpu.Enabled && service.Config.Gpu.Enabled, "alert master switch enables both channels immediately");
        tabs.SelectedIndex = 1; Pump(100);
        Toggle(panel, "显示独立监控窗", true); Toggle(panel, "任务栏显示", true); Toggle(panel, "双列布局", true);
        Check(service.Config.DisplayEnabled && service.Config.TaskbarEnabled && service.Config.Horizontal, "display switches save immediately without an apply button");
        Commit(Setting(panel, "界面缩放（0.5–2.5）"), "1.5");
        Check(service.Config.UiScale == 1.5, "completed numeric entry persists immediately");
        Commit(Setting(panel, "界面缩放（0.5–2.5）"), "NaN");
        Check(service.Config.UiScale == 1.5, "invalid numeric edit preserves saved value");
        tabs.SelectedIndex = 2; Pump(100); Toggle(panel, "记录历史（每 10 秒，单日日志最多 32 MB）", false);
        Check(!service.Config.HistoryEnabled, "history switch persists without reopening panel");
        var old = new MonitorStore(dir).Load();
        Check(!old.Enabled && old.Cpu.Enabled && old.Gpu.Enabled && !old.HistoryEnabled && old.DisplayEnabled && old.WebEnabled, "old explicit switches and unused web fields round trip");
        service.Config.FpsEnabled = true; service.Config.SelectedMetrics.Add("FPS"); service.Save();
        typeof(MonitorService).GetMethod("CancelElevation", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(service, null);
        Check(!service.Config.FpsEnabled && !service.Config.SelectedMetrics.Contains("FPS") && !service.Running, "elevation cancellation clears FPS state without launching a prompt");
        panel.Width = panel.MinWidth; panel.Height = panel.MinHeight; Pump(100); Capture(panel, "minimum-monitor.png");
        tabs.SelectedIndex = 0; Pump(100); Capture(panel, "minimum-metrics.png");
        var scroller = Find<W.ScrollViewer>(panel).First(s => s.Content is W.StackPanel);
        Check(scroller.ViewportWidth > 0 && scroller.ExtentWidth <= scroller.ViewportWidth + 1, "minimum monitor width has no horizontal overflow");
        panel.Width = 660; panel.Height = 740; Pump(100); CaptureDesktop(panel, "simplified-desktop-monitor.png");
        Click(panel, "进阶功能"); var advanced = Field<MonitorAdvancedWindow>(panel, "_advanced"); Pump(100);
        var navigation = Field<W.ListBox>(advanced, "_navigation"); navigation.SelectedIndex = 4; Pump(100);
        Check(Find<MonitorDriverPage>(advanced).Any() && Find<MonitorUpdatePage>(advanced).Any(), "driver and update share maintenance group");
        Check(!Find<W.Button>(advanced).Any(b => (b.Content as string)?.Contains("测速") == true || (b.Content as string)?.Contains("网页") == true), "removed web and speed-test actions absent from maintenance");
        Check(!Find<W.TextBlock>(advanced).Any(t => t.Text.Contains("FPS 进程")) && !Find<W.TextBox>(advanced).Any(b => b.MaxLength == 10), "manual FPS PID editor is removed");
        Capture(advanced, "maintenance.png"); CaptureDesktop(advanced, "desktop-maintenance.png");
        advanced.Width = advanced.MinWidth; advanced.Height = advanced.MinHeight; Pump(100); Capture(advanced, "minimum-advanced.png");
        var advancedScroll = Find<W.ScrollViewer>(advanced).First(s => s.Content is W.ContentControl);
        Check(advancedScroll.ExtentWidth <= advancedScroll.ViewportWidth + 1 && advancedScroll.ScrollableHeight > 0, "minimum advanced window scrolls vertically without horizontal overflow");
        navigation.SelectedIndex = 0; Pump(100);
        Toggle(advanced, "启用联网插件", true); Check(service.Config.PluginsEnabled, "plugin switch remains immediately accessible through navigation");
        Toggle(advanced, "启用联网插件", false); navigation.SelectedIndex = 3; Pump(100);
        Check(Find<W.CheckBox>(advanced).Any(b => (b.Content as string)?.Contains("登录时启动") == true), "startup settings remain accessible without changing OS settings");
        var family = MonitorForeground.Family(2, new Dictionary<int, (int, string)> { [1] = (0, "chrome.exe"), [2] = (1, "chrome.exe"), [3] = (1, "chrome.exe"), [4] = (1, "other.exe"), [5] = (0, "chrome.exe"), [6] = (7, "chrome.exe"), [7] = (6, "chrome.exe") });
        Check(family.SequenceEqual(new[] { 2, 1, 3 }), "foreground process family excludes unrelated applications and cycles");
        Check(MonitorForeground.ProcessIds().All(id => id > 0 && id != Environment.ProcessId), "native foreground discovery returns valid external targets");
        var targetMemory = new MonitorForegroundTracker();
        var targetProcesses = new Dictionary<int, (int, string)> { [10] = (0, "video.exe"), [11] = (10, "video.exe"), [20] = (0, "game.exe"), [99] = (0, "DesktopPet.exe") };
        Check(targetMemory.Select(10, 99, targetProcesses, _ => 100).SequenceEqual(new[] { 10, 11 }), "animation application becomes automatic FPS target");
        Check(targetMemory.Select(99, 99, targetProcesses, _ => 100).SequenceEqual(new[] { 10, 11 }), "opening Pet and settings retains previous external FPS target and renderer family");
        Check(targetMemory.Select(20, 99, targetProcesses, _ => 200).SequenceEqual(new[] { 20 }) && targetMemory.Select(99, 99, targetProcesses, _ => 200).SequenceEqual(new[] { 20 }), "switching external application replaces retained FPS target");
        Check(targetMemory.Select(99, 99, targetProcesses, _ => 201).Count == 0, "PID reuse cannot retain an unrelated FPS target");
        Check(new MonitorForegroundTracker().Select(99, 99, targetProcesses, _ => 100).Count == 0, "Pet foreground without previous application never guesses background target");
        targetMemory.Select(10, 99, targetProcesses, _ => 100); targetProcesses.Remove(10);
        Check(targetMemory.Select(99, 99, targetProcesses, _ => 100).Count == 0, "closed previous application clears FPS target instead of freezing frames");
        advanced.Close(); panel.Close();
        using var broken = new MonitorService(Path.Combine(_output, "save-failure"), Dispatcher.CurrentDispatcher, () => false);
        var failedPanel = new MonitorWindow(broken) { ShowActivated = false }; failedPanel.Show(); Pump(100);
        Find<W.TabControl>(failedPanel).Single().SelectedIndex = 1; Pump(100);
        Directory.CreateDirectory(broken.Store.ConfigPath);
        Toggle(failedPanel, "显示独立监控窗", true);
        Check(!broken.Config.DisplayEnabled && !broken.Store.Available
            && Find<W.CheckBox>(failedPanel).First(b => b.Content as string == "显示独立监控窗").IsChecked == false,
            "failed save restores configuration and checkbox while reporting store error");
        Check(!string.IsNullOrEmpty(broken.Status), "failed save leaves visible diagnostic status"); failedPanel.Close();
    }
    private static void PluginCancellation()
    {
        using var listener = new TcpListener(System.Net.IPAddress.Loopback, 0); listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        using var service = new MonitorService(Path.Combine(_output, "plugin-cancel"), Dispatcher.CurrentDispatcher, () => false);
        service.Plugins.Templates.Add(new LiteMonitor.src.Plugins.PluginTemplate { Id = "pending", Meta = new() { Name = "本地取消测试" },
            Execution = new() { Type = "api_json", Url = $"http://127.0.0.1:{port}/", Interval = 0, MinInterval = 0, Extract = new() { ["value"] = "temperature" } },
            Outputs = [new() { Key = "temperature", Label = "测试温度", Format = "{{value}}", Unit = "℃" }] });
        service.Plugins.Instances.Add(new() { Id = "pending-test", TemplateId = "pending", Enabled = true });
        Check(service.Plugins.Save(), "pending local plugin fixture saves");
        for (var attempt = 0; attempt < 2; attempt++)
        {
            service.Config.Enabled = true; service.Config.PluginsEnabled = true; service.Save();
            var accept = listener.AcceptTcpClientAsync();
            var poll = (Task)typeof(MonitorService).GetMethod("PollPluginsAsync", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(service, [CancellationToken.None])!;
            Until(() => accept.IsCompleted, 5000, "local plugin request reaches pending fixture");
            using var client = accept.GetAwaiter().GetResult();
            if (attempt == 0) { service.Config.PluginsEnabled = false; service.Save(); }
            else service.SetEnabled(false);
            Until(() => poll.IsCompleted, 5000, attempt == 0 ? "plugin switch cancels pending request" : "monitor switch cancels pending plugin request");
            Check(service.Plugins.Metrics.Count == 0, "canceled request does not publish plugin values");
            service.Plugins.Save(); // Reset the fixture's polling interval before the second request.
        }
    }
    private static IEnumerable<T> Find<T>(DependencyObject root) where T : DependencyObject
    { if (root is T t) yield return t; for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) foreach (var child in Find<T>(VisualTreeHelper.GetChild(root, i))) yield return child; }
    private static void Click(Window window, string text)
    { var button = Find<W.Button>(window).First(b => b.Content as string == text); button.RaiseEvent(new RoutedEventArgs(W.Button.ClickEvent)); Pump(20); }
    private static void Capture(Window window, string file) => CaptureElement(window, file);
    private static void CaptureElement(FrameworkElement window, string file)
    {
        window.UpdateLayout(); var dpi = VisualTreeHelper.GetDpi(window);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(window.ActualHeight * dpi.DpiScaleY),
            96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
        // RenderTargetBitmap cannot render DWM's desktop material. Flatten a neutral backing for layout evidence.
        var backing = new DrawingVisual(); using (var dc = backing.RenderOpen()) dc.DrawRectangle(MonitorFluentStyle.Brush("#F0EDF5"), null, new Rect(0, 0, window.ActualWidth, window.ActualHeight));
        bitmap.Render(backing); bitmap.Render(window);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var output = File.Create(Path.Combine(_output, file)); encoder.Save(output);
    }
    private static void TaskbarInteraction()
    {
        using var service = new MonitorService(Path.Combine(_output, "taskbar-interaction"), Dispatcher.CurrentDispatcher, () => false);
        service.Config.Enabled = false; service.Config.TaskbarEnabled = true;
        var bounds = System.Windows.Forms.Screen.PrimaryScreen!.Bounds;
        service.Config.TaskbarLeft = bounds.Left + 40; service.Config.TaskbarTop = bounds.Top + 80;
        service.Config.TaskbarBackgroundOpacity = .35; service.Config.Opacity = .2; service.Save();
        service.Config.Enabled = true; // Enable only the controlled snapshot; do not call Restore/start a worker.
        var snapshot = new MonitorSnapshot(DateTimeOffset.UtcNow, [], [
            new("cpu", "CPU", "cpu", "CPU.Load", "%", 8.8, "fixture"), new("gpu", "GPU", "gpu", "GPU.Load", "%", 1, "fixture"),
            new("mem", "内存", "memory", "MEM.Load", "%", 78, "fixture")], "controlled fixture");
        typeof(MonitorService).GetMethod("Apply", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(service, [snapshot]);
        var settingsOpened = 0;
        var taskbar = new MonitorTaskbarWindow(service, () => settingsOpened++); taskbar.Refresh(); Pump(100);
        var frame = Field<W.Border>(taskbar, "_frame"); var rows = Field<W.StackPanel>(taskbar, "_rows");
        Check(frame.Background.Opacity == .35 && frame.BorderBrush.Opacity == .35 && taskbar.Opacity == 1 &&
            Find<W.TextBlock>(taskbar).All(t => t.Foreground.Opacity == 1), "taskbar background and border fade together while text remains fully opaque");
        Check(rows.Children.Cast<MonitorMetricRow>().All(row => row.ColumnDefinitions[0].Width.IsAuto &&
            row.MinWidth == 0 && ((W.TextBlock)row.Children[0]).Margin.Right == 4), "taskbar names sit four DIP from values without artificial minimum width");
        Check(Find<W.TextBlock>(taskbar).Any(t => t.Text == "8.8 %"), "compact taskbar preview contains controlled current data rather than an empty snapshot");
        Capture(taskbar, "taskbar-compact-translucent.png");
        service.Config.TaskbarBackgroundOpacity = 0; service.Save(); taskbar.Refresh(); Pump(200);
        var taskbarHandle = new System.Windows.Interop.WindowInteropHelper(taskbar).Handle;
        Check(GetWindowRect(taskbarHandle, out var hitBounds), "transparent taskbar native bounds available");
        var blank = new NativePoint { X = hitBounds.Left + 2, Y = (hitBounds.Top + hitBounds.Bottom) / 2 };
        var hit = WindowFromPoint(blank);
        File.WriteAllText(Path.Combine(_output, "zero-opacity-native-hit.json"), JsonSerializer.Serialize(new {
            expected = taskbarHandle.ToInt64(), actual = hit.ToInt64(), blank.X, blank.Y,
            note = "native hit test on blank padding; no global cursor movement or injected input" }));
        Check(hit == taskbarHandle, "zero-opacity blank padding receives native mouse input instead of passing through");
        Check(WindowFromPoint(new NativePoint { X = hitBounds.Right - 2, Y = hitBounds.Bottom - 2 }) == taskbarHandle,
            "zero-opacity outer corner also retains native mouse hit surface");
        Check(taskbar.Opacity == 1 && Find<W.TextBlock>(taskbar).All(t => t.Foreground.Opacity == 1), "zero-opacity hit surface never fades labels or numeric text");
        var down = new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount,
            System.Windows.Input.MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent };
        typeof(System.Windows.Input.MouseButtonEventArgs).GetProperty("ClickCount")!.SetValue(down, 1);
        taskbar.RaiseEvent(down);
        var up = new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount,
            System.Windows.Input.MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseLeftButtonUpEvent }; taskbar.RaiseEvent(up);
        Check(down.Handled && up.Handled && taskbar.ManualRefreshCount == 0, "single click is consumed without manual refresh or leaking to Explorer");
        var twice = new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount,
            System.Windows.Input.MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent };
        typeof(System.Windows.Input.MouseButtonEventArgs).GetProperty("ClickCount")!.SetValue(twice, 2); taskbar.RaiseEvent(twice); Pump(50);
        Check(twice.Handled && taskbar.ManualRefreshCount == 1, "double click refreshes once without restarting collection or changing manual position");
        taskbar.Left += 30; taskbar.Top += 20; taskbar.SavePosition(); Pump(50);
        var saved = new MonitorStore(service.Store.DirectoryPath).Load(); var left = saved.TaskbarLeft; var top = saved.TaskbarTop;
        Check(left > bounds.Left + 40 && top > bounds.Top + 80, "drag completion persists owned taskbar coordinates");
        taskbar.Refresh(); Pump(1600); taskbar.Refresh(); Pump(50);
        Check(service.Config.TaskbarLeft == left && service.Config.TaskbarTop == top && taskbar.PlacementStatus.Contains("自由位置"), "periodic rendering never returns a manually placed monitor to an automatic gap");
        taskbar.ContextMenu.Items.Cast<W.MenuItem>().Single().RaiseEvent(new RoutedEventArgs(W.MenuItem.ClickEvent));
        Check(settingsOpened == 1, "taskbar context menu opens monitoring settings");
        var panel = new MonitorWindow(service) { ShowActivated = false }; panel.Show();
        var advanced = new MonitorAdvancedWindow(service) { Owner = panel, ShowActivated = false }; advanced.Show(); Pump(100);
        Check(panel.Topmost && advanced.Topmost && (MonitorDisplayBehavior.GetWindowLong(new System.Windows.Interop.WindowInteropHelper(panel).Handle, -20) & 8) != 0 &&
            (MonitorDisplayBehavior.GetWindowLong(new System.Windows.Interop.WindowInteropHelper(advanced).Handle, -20) & 8) != 0, "monitor and advanced settings use native topmost windows");
        Field<W.ListBox>(advanced, "_navigation").SelectedIndex = 2; Pump(100);
        Find<W.TabControl>(panel).Single().SelectedIndex = 1; Pump(100);
        var alpha = Find<W.Slider>(panel).Single(s => s.Name == "TaskbarOpacitySlider");
        var taskbarGroup = (W.StackPanel)alpha.Parent;
        Check(Find<W.CheckBox>(taskbarGroup).Any(b => b.Content as string == "任务栏显示") && !Find<W.Slider>(advanced).Any(s => s.Name == "TaskbarOpacitySlider") &&
            !Find<W.TextBlock>(advanced).Any(t => t.Text.Contains("任务栏背景不透明度")), "taskbar slider shares a card with its enable switch and has no advanced duplicate");
        alpha.Value = 0; taskbar.Refresh();
        Check(frame.Background.Opacity == 0 && frame.BorderBrush.Opacity == 0 && new MonitorStore(service.Store.DirectoryPath).Load().TaskbarBackgroundOpacity == 0, "slider immediately saves fully transparent fill and border");
        alpha.Value = 100; taskbar.Refresh();
        Check(frame.Background.Opacity == 1 && frame.BorderBrush.Opacity == 1 && taskbar.Opacity == 1, "slider reaches opaque endpoint without fading text");
        alpha.Value = 42; taskbar.Refresh();
        Check(new MonitorStore(service.Store.DirectoryPath).Load().TaskbarBackgroundOpacity == .42 && frame.Background.Opacity == .42 &&
            Find<W.TextBlock>(taskbar).All(t => t.Foreground.Opacity == 1), "slider intermediate position saves immediately with opaque metric text");
        Capture(panel, "taskbar-opacity-display-page.png");
        panel.Width = panel.MinWidth; panel.Height = panel.MinHeight; Pump(100); Capture(panel, "taskbar-opacity-display-minimum.png");
        var displayScroll = Find<W.ScrollViewer>(panel).First(s => s.Content is MonitorDisplayPage);
        Check(displayScroll.ExtentWidth <= displayScroll.ViewportWidth + 1 && alpha.ActualWidth > 0, "taskbar switch and slider fit minimum display page with vertical scrolling");
        advanced.Width = advanced.MinWidth; advanced.Height = advanced.MinHeight; Pump(100); Capture(advanced, "taskbar-settings-minimum.png");
        var scroll = Find<W.ScrollViewer>(advanced).First(s => ReferenceEquals(s.Content, Field<W.ContentControl>(advanced, "_body")));
        Check(scroll.ExtentWidth <= scroll.ViewportWidth + 1, "taskbar settings remain scrollable without horizontal overflow at minimum size");
        scroll.ScrollToVerticalOffset(270); Pump(100); Capture(advanced, "taskbar-settings-minimum-scrolled.png");
        Click(advanced, "恢复任务栏自动定位");
        Check(service.Config.TaskbarLeft is null && service.Config.TaskbarTop is null, "reset button restores automatic taskbar placement");
        advanced.Close(); panel.Close(); taskbar.Close();
        var restored = new MonitorTaskbarWindow(service); service.Config.TaskbarLeft = left; service.Config.TaskbarTop = top; restored.Refresh(); Pump(50);
        Check(restored.IsVisible && restored.PlacementStatus.Contains("自由位置"), "new taskbar window restores saved manual placement"); restored.Close();
        using var failedService = new MonitorService(Path.Combine(_output, "slider-save-failure"), Dispatcher.CurrentDispatcher, () => false);
        var failedWindow = new MonitorWindow(failedService) { ShowActivated = false }; failedWindow.Show();
        Find<W.TabControl>(failedWindow).Single().SelectedIndex = 1; Pump(100);
        var failedSlider = Find<W.Slider>(failedWindow).Single(s => s.Name == "TaskbarOpacitySlider");
        Directory.CreateDirectory(failedService.Store.ConfigPath); failedSlider.Value = 25; Pump(100);
        Check(failedSlider.Value == 100 && failedService.Config.TaskbarBackgroundOpacity == 1 && !failedSlider.IsEnabled &&
            Find<W.TextBlock>((DependencyObject)failedSlider.Parent).Any(t => t.Visibility == Visibility.Visible && t.Text == failedService.Status), "failed slider save restores position and shows an error beside the disabled control");
        failedWindow.Close();
    }
    private static void Dropdowns()
    {
        using var service = new MonitorService(Path.Combine(_output, "dropdowns"), Dispatcher.CurrentDispatcher, () => false);
        service.Config.Enabled = false; service.Save();
        var advanced = new MonitorAdvancedWindow(service) { ShowActivated = false }; advanced.Show(); Pump(100);
        var navigation = Field<W.ListBox>(advanced, "_navigation"); navigation.SelectedIndex = 2; Pump(100);
        var combo = Find<W.ComboBox>(advanced).Single(c => c.Name == "MonitorLanguageChoice");
        string[] Labels() => combo.Items.Cast<object>().Select(item => (string)item.GetType().GetProperty("Name")!.GetValue(item)!).ToArray();
        Check(Labels().SequenceEqual(new[] { "简体中文", "繁体中文", "英语", "日语", "韩语", "法语", "德语", "西班牙语", "俄语" }), "Chinese language chooser shows all nine complete Chinese language names");
        Check(combo.SelectedValue as string == "zh" && Find<W.TextBlock>(combo).Any(t => t.Text == "简体中文"), "selected language shows its complete name while retaining configuration code");
        var peer = new System.Windows.Automation.Peers.ComboBoxAutomationPeer(combo);
        var expand = (System.Windows.Automation.Provider.IExpandCollapseProvider)peer.GetPattern(System.Windows.Automation.Peers.PatternInterface.ExpandCollapse)!;
        expand.Expand(); Pump(100); var popup = (W.Primitives.Popup)combo.Template.FindName("PART_Popup", combo);
        Check(popup.IsOpen && popup.Child is W.Border { CornerRadius.TopLeft: 8 } && Math.Abs(((FrameworkElement)popup.Child).ActualWidth - combo.ActualWidth) < 2, "shared dropdown opens through automation with rounded menu matching control width");
        combo.MaxDropDownHeight = 180; Pump(50);
        var menuScroll = Find<W.ScrollViewer>(popup.Child).Single(); menuScroll.ScrollToEnd(); Pump(50);
        Check(menuScroll.VerticalOffset > 0 && Find<W.Primitives.ScrollBar>(popup.Child).Any(b => b.IsVisible && b.ActualWidth <= 8.5), "shared dropdown keeps all options reachable through slim styled scrollbar");
        combo.MaxDropDownHeight = 280; menuScroll.ScrollToTop(); Pump(50);
        Capture(advanced, "dropdowns-chinese-settings.png"); CaptureElement((FrameworkElement)popup.Child, "languages-chinese-popup.png");
        combo.SelectedValue = "en"; Pump(600);
        Check(service.Config.Language == "en" && new MonitorStore(service.Store.DirectoryPath).Load().Language == "en", "language dropdown selection saves code immediately without extra apply action");
        Check(Labels().SequenceEqual(new[] { "Simplified Chinese", "Traditional Chinese", "English", "Japanese", "Korean", "French", "German", "Spanish", "Russian" }) && combo.SelectedValue as string == "en" && Find<W.TextBlock>(combo).Any(t => t.Text == "English"), "English selection relabels every option and retains visible selected identity");
        expand.Expand(); Pump(100); Capture(advanced, "dropdowns-english-settings.png"); CaptureElement((FrameworkElement)popup.Child, "languages-english-popup.png"); expand.Collapse();
        foreach (var code in MonitorLanguage.Codes)
        {
            combo.SelectedValue = code; Pump(30);
            var names = Labels();
            Check(names.Length == 9 && names.Distinct().Count() == 9 && !names.Any(MonitorLanguage.Codes.Contains) && combo.SelectedValue as string == code && service.Config.Language == code && Find<W.TextBlock>(combo).Any(t => t.Text == MonitorLocalizer.Language.LanguageName(code, code)),
                "all language names refresh without exposing codes or resetting selection " + code);
        }
        combo.SelectedValue = "zh"; Pump(600);
        var section = Find<W.Expander>(advanced).First(e => e.Header as string == "任务栏指标（最多 12 项）");
        var header = (W.Primitives.ToggleButton)section.Template.FindName("header", section);
        header.IsChecked = true; Pump(50);
        Check(section.IsExpanded && ((FrameworkElement)section.Template.FindName("body", section)).Visibility == Visibility.Visible, "styled expand header opens its settings body");
        header.IsChecked = false; Pump(30); Check(!section.IsExpanded, "styled expand header collapses again");
        Check(Find<W.ComboBox>(advanced).All(c => c.Template == ((Style)advanced.FindResource("MonitorCombo")).Setters.OfType<Setter>().First(s => s.Property == W.Control.TemplateProperty).Value), "appearance dropdowns share common template instead of system default");
        advanced.Width = advanced.MinWidth; advanced.Height = advanced.MinHeight; combo.SelectedValue = "ru"; Pump(100);
        var scroller = Find<W.ScrollViewer>(advanced).First(s => s.Content is W.ContentControl);
        Capture(advanced, "dropdowns-minimum-russian.png");
        Check(scroller.ExtentWidth <= scroller.ViewportWidth + 1 && Find<W.TextBlock>(combo).Any(t => t.Text == "Русский"), $"minimum settings window retains full selected language without horizontal overflow ({scroller.ExtentWidth:0.##}/{scroller.ViewportWidth:0.##}; {string.Join('|', Find<W.TextBlock>(combo).Select(t => t.Text))})");
        expand.Expand(); Pump(100); CaptureElement((FrameworkElement)popup.Child, "languages-russian-popup.png"); expand.Collapse();
        navigation.SelectedIndex = 4; Pump(600); var deviceSection = Find<W.Expander>(advanced).First(e => (string?)e.Header == MonitorLocalizer.Language.Text("ru", "采集设备与刷新"));
        deviceSection.IsExpanded = true; Pump(50);
        Check(Find<W.ComboBox>(deviceSection).Count() == 4 && Find<W.ComboBox>(deviceSection).All(c => c.Template.FindName("PART_Popup", c) is W.Primitives.Popup), "device selectors and refresh interval receive shared dropdown template");
        advanced.Close();
        using var failing = new MonitorService(Path.Combine(_output, "language-save-failure"), Dispatcher.CurrentDispatcher, () => false);
        Directory.CreateDirectory(failing.Store.ConfigPath);
        var failedWindow = new MonitorAdvancedWindow(failing) { ShowActivated = false }; failedWindow.Show(); Pump(50);
        Field<W.ListBox>(failedWindow, "_navigation").SelectedIndex = 2; Pump(50);
        var failedChoice = Find<W.ComboBox>(failedWindow).Single(c => c.Name == "MonitorLanguageChoice"); failedChoice.SelectedValue = "en"; Pump(100);
        Check(failing.Config.Language == "zh" && failedChoice.SelectedValue as string == "zh" && !failedChoice.IsEnabled && !string.IsNullOrWhiteSpace(failing.Status), "failed language save restores language selection and reports unavailable storage");
        failedWindow.Close();
    }
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool GetWindowRect(nint hwnd, out NativeRect rect);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern nint WindowFromPoint(NativePoint point);
    private static void CaptureDesktop(Window window, string file)
    {
        window.Activate(); Pump(250);
        var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
        if (GetForegroundWindow() != handle)
        {
            File.WriteAllText(Path.Combine(_output, file + ".skipped.txt"), "Desktop capture skipped because the test window was not foreground. Layout bitmap is separate from DWM material evidence."); return;
        }
        if (!GetWindowRect(handle, out var rect)) return;
        var screen = System.Windows.Forms.Screen.FromHandle(new System.Windows.Interop.WindowInteropHelper(window).Handle).Bounds;
        var area = System.Drawing.Rectangle.Intersect(screen, System.Drawing.Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom));
        if (area.Width <= 0 || area.Height <= 0) return;
        using var bitmap = new System.Drawing.Bitmap(area.Width, area.Height);
        using var graphics = System.Drawing.Graphics.FromImage(bitmap);
        graphics.CopyFromScreen(area.Location, System.Drawing.Point.Empty, area.Size); bitmap.Save(Path.Combine(_output, file), System.Drawing.Imaging.ImageFormat.Png);
    }
    private static void Web()
    {
        using var web = new MonitorWebService(); web.Configure(true, 8085, []); Check(!web.Running && web.Error is not null, "ObsUI hardware port is reserved");
        web.Configure(true, 5173, []); Check(!web.Running, "ObsUI UI port is reserved");
        var probe = new TcpListener(System.Net.IPAddress.Loopback, 0); probe.Start(); var port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
        web.Configure(true, port, [new("test", "测试", "test", "Load", "%", 42, "fixture")]); Check(web.Running, "optional loopback web starts");
        using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false });
        var unauthorized = http.GetAsync($"http://127.0.0.1:{port}/api/snapshot").GetAwaiter().GetResult();
        Check(unauthorized.StatusCode == System.Net.HttpStatusCode.Forbidden, "web requires per-session token");
        var url = web.Url.Replace("/?", "/api/snapshot?"); var json = http.GetStringAsync(url).GetAwaiter().GetResult();
        Check(json.Contains("42"), "authenticated snapshot includes selected values"); web.Stop(); Check(!web.Running, "web stops on disable");
        var sharing = new MonitorConfig { WebLan = true, WebIpv6 = Socket.OSSupportsIPv6 };
        sharing.Visual = new() { ShowBars = true, ShowGroups = true, CornerRadius = 10, ValueFamily = "Consolas" };
        web.Configure(true, port, [new("test", "测试", "test", "Load", "%", 42, "fixture")], sharing);
        Check(web.Running, "optional LAN socket starts without URL reservations");
        var local = http.GetStringAsync(web.Url.Replace("/?", "/api/snapshot?")).GetAwaiter().GetResult();
        Check(local.Contains("42"), "LAN mode still authenticates local data");
        using (var data = JsonDocument.Parse(local)) Check(data.RootElement.GetProperty("Visual").GetProperty("ShowBars").GetBoolean()
            && data.RootElement.GetProperty("Items")[0].GetProperty("Bar").GetDouble() == 42, "web snapshot shares advanced theme and bar data with WPF");
        var html = http.GetStringAsync(web.Url).GetAwaiter().GetResult();
        File.WriteAllText(Path.Combine(_output, "web-script.js"), html.Split("<script>")[1].Split("</script>")[0]);
        if (Socket.OSSupportsIPv6)
        {
            var v6 = http.GetStringAsync(web.UrlForHost("[::1]").Replace("/?", "/api/snapshot?")).GetAwaiter().GetResult();
            Check(v6.Contains("42"), "IPv6 endpoint returns authenticated metrics");
        }
        web.Stop();
        var language = new MonitorLanguage(Path.Combine(AppContext.BaseDirectory, "monitor-languages"));
        foreach (var code in MonitorLanguage.Codes) Check(language.Key(code, "Items.CPU.Load", "missing") != "missing", "pinned language resource available " + code);
        foreach (var code in MonitorLanguage.Codes.Where(c => c != "zh"))
        {
            Check(language.Text(code, "高级主题与布局") != "高级主题与布局", "owned advanced theme translation available " + code);
            Check(!language.Format(code, "定时关机剩余 {0} 秒", 123).Contains("{0}"), "dynamic system status formats without losing placeholder " + code);
        }
    }
    private static void Plugins()
    {
        using var listener = new TcpListener(System.Net.IPAddress.Loopback, 0); listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        var fixture = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(); using var stream = client.GetStream();
            using var reader = new StreamReader(stream, System.Text.Encoding.ASCII, false, 1024, true);
            using var deadline = new CancellationTokenSource(10000);
            while (await reader.ReadLineAsync(deadline.Token) is { Length: > 0 }) { }
            var body = System.Text.Encoding.UTF8.GetBytes("{\"temperature\":23}");
            var header = System.Text.Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(header); await stream.WriteAsync(body);
        });
        var directory = Path.Combine(_output, "plugin-data"); using var host = new PluginHost(directory);
        host.Templates.Add(new LiteMonitor.src.Plugins.PluginTemplate { Id = "fixture", Meta = new() { Name = "测试插件" },
            Execution = new() { Type = "api_json", Url = $"http://127.0.0.1:{port}/", Extract = new() { ["value"] = "temperature" } },
            Outputs = [new() { Key = "temperature", Label = "测试温度", Format = "{{value}}", Unit = "℃" }] });
        host.Instances.Add(new LiteMonitor.src.Core.PluginInstanceConfig { Id = "test", TemplateId = "fixture", Enabled = true,
            InputValues = new() { ["testOnlySecret"] = "fixture-not-a-real-credential" } });
        Check(host.Save(), "plugin inputs saved through DPAPI");
        var bytes = File.ReadAllBytes(Path.Combine(directory, "plugins.bin"));
        Check(!System.Text.Encoding.UTF8.GetString(bytes).Contains("fixture-not-a-real-credential"), "no plaintext input in plugin config");
        Task.Run(() => host.PollAsync(false, default)).GetAwaiter().GetResult(); Check(host.Metrics.Count == 0, "disabled plugins issue no requests");
        Task.Run(() => host.PollAsync(true, default)).GetAwaiter().GetResult(); fixture.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
        Check(host.Metrics.Single().Value == 23, "upstream extraction and template processing work");
        using var restored = new PluginHost(directory); Check(restored.Instances.Single().InputValues["testOnlySecret"] == "fixture-not-a-real-credential", "plugin encrypted inputs restore");
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reply = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pendingFixture = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(); using var stream = client.GetStream();
            using var reader = new StreamReader(stream, System.Text.Encoding.ASCII, false, 1024, true);
            using var deadline = new CancellationTokenSource(10000);
            while (await reader.ReadLineAsync(deadline.Token) is { Length: > 0 }) { }
            received.SetResult(); await reply.Task.WaitAsync(deadline.Token);
            var body = System.Text.Encoding.UTF8.GetBytes("{\"temperature\":99}");
            await stream.WriteAsync(System.Text.Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n"));
            await stream.WriteAsync(body);
        });
        host.Save(); var pending = Task.Run(() => host.PollAsync(true, default));
        received.Task.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult(); host.Instances.Single().InputValues["testOnlySecret"] = "changed-test-fixture";
        Check(host.Save(), "plugin configuration can change during an in-flight request");
        reply.SetResult(); pending.GetAwaiter().GetResult(); pendingFixture.GetAwaiter().GetResult();
        Check(host.Metrics.Count == 0, "old plugin response cannot repopulate values after configuration changes");
        File.WriteAllBytes(Path.Combine(directory, "plugins.bin"), [1, 2, 3]); using var broken = new PluginHost(directory);
        Check(broken.Error is not null && !broken.Save() && File.ReadAllBytes(Path.Combine(directory, "plugins.bin")).SequenceEqual(new byte[] { 1, 2, 3 }), "invalid plugin data preserved");
    }
    private static void HistoryBoundary()
    {
        using var service = new MonitorService(Path.Combine(_output, "history-boundary"), Dispatcher.CurrentDispatcher, () => false);
        service.Config.Enabled = true;
        var directory = Path.Combine(service.Store.DirectoryPath, "history"); Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, DateTimeOffset.Now.ToString("yyyy-MM-dd") + ".jsonl");
        using (var stream = File.Create(file)) stream.SetLength(32 * 1024 * 1024 - 1);
        typeof(MonitorService).GetField("_historyAt", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(service, Environment.TickCount64 - 11000);
        typeof(MonitorService).GetMethod("Apply", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(service,
            [new MonitorSnapshot(DateTimeOffset.UtcNow, [], [new("test", "容量边界", "test", "Load", "%", 42, "fixture")], "fixture")]);
        Check(new FileInfo(file).Length == 32 * 1024 * 1024 - 1, "history refuses a record that would cross daily byte capacity without changing existing data");
        Check(service.Snapshot.Metrics.Single().Value == 42, "history capacity does not stop sampling or alter current values");
    }
}
