using System.Text.Json;
using DesktopPet.Monitoring;

var count = 0;
void Check(bool condition, string name) { if (!condition) throw new Exception(name); count++; Console.WriteLine("PASS " + name); }
var start = DateTimeOffset.UtcNow;
var liveMetric = new MonitorMetric("MEM.Load", "内存", "memory", "MEM.Load", "%", 50, "fixture");
foreach (var interval in new[] { 500, 1000, 2000, 5000 })
{
    var live = new MonitorSnapshot(start, [], [liveMetric], "running", Capabilities: new() { ["sampling-ms"] = interval.ToString() });
    Check(live.Fresh(start.AddMilliseconds(interval + 600)), "sampling interval includes sensor latency " + interval);
    Check(live.ForDisplay(liveMetric, start.AddMilliseconds(interval + 600)).Valid, "normal sampling does not flicker disconnected " + interval);
    Check(!live.Fresh(start.AddSeconds(20)) && live.ForDisplay(liveMetric, start.AddSeconds(20)).Display == "数据延迟", "stalled readings expire without claiming pipe loss " + interval);
}
Check(!MonitorSnapshot.Empty.Fresh(start) && MonitorSnapshot.Empty.ForDisplay(liveMetric, start).Display == "已断开", "empty disconnected snapshot never displays cached values");
var legacyLive = new MonitorSnapshot(start, [], [liveMetric], "running");
Check(legacyLive.Fresh(start.AddSeconds(4.9)) && !legacyLive.Fresh(start.AddSeconds(5.1)) && !legacyLive.Fresh(start.AddSeconds(-1)), "legacy and future timestamps retain bounded freshness");
var invalidInterval = legacyLive with { Capabilities = new() { ["sampling-ms"] = "999999999" } };
Check(!invalidInterval.Fresh(start.AddSeconds(6)), "invalid sampling metadata cannot prolong stale data");
var waitingFrames = legacyLive with { Capabilities = new() { ["fps-helper"] = "running" } };
Check(waitingFrames.ForDisplay(liveMetric with { Kind = "FPS", Value = null }, start).Display == "等待帧数据", "no foreground frames share waiting state across surfaces");
Check(waitingFrames.ForDisplay(liveMetric with { Value = null }, start).Display == "不可用", "missing sensors remain unavailable independently of FPS");
Check(waitingFrames.ForDisplay(liveMetric with { Kind = "FPS", SampledAt = start.AddSeconds(-4) }, start).Display == "等待帧数据", "stale FPS clears before otherwise fresh hardware expires");
foreach (var (helper, text) in new[] { ("starting", "FPS 正在启动"), ("requires-elevation", "FPS 需要管理员权限"), ("unavailable", "FPS 采集不可用") })
    Check((waitingFrames with { Capabilities = new() { ["fps-helper"] = helper } }).ForDisplay(liveMetric with { Kind = "FPS", Value = null }, start).Display == text, "FPS component state is distinct from no frames " + helper);
MonitorMetric Temp(double? value, string device = "cpu") => new("CPU.Temp", "CPU", device, "CPU.Temp", "℃", value, "CPU Package");
var episodes = new TemperatureEpisodes(); var rule = new TemperatureRule { Enabled = true };
bool Feed(int seconds, double? temp, bool quiet = false) => episodes.Observe("CPU", rule, Temp(temp), start.AddSeconds(seconds), start.AddSeconds(seconds), quiet);
for (var i = 0; i < 10; i++) Check(!Feed(i, 95), "short spike " + i);
Check(Feed(10, 95), "10s hot alerts once");
for (var i = 11; i <= 100; i++) Check(!Feed(i, 95), "continuous heat no repeat " + i);
var restored = JsonSerializer.Deserialize<TemperatureRule>(JsonSerializer.Serialize(rule))!;
var restarted = new TemperatureEpisodes();
Check(!restarted.Observe("CPU", restored, Temp(95), start.AddSeconds(101), start.AddSeconds(101), false), "restart preserves episode");
Check(!Feed(101, null) && rule.InEpisode, "missing sample does not recover");
for (var i = 102; i < 162; i++) Feed(i, 80);
Check(rule.InEpisode, "59s cool does not recover"); Feed(162, 80); Check(!rule.InEpisode, "60s cool recovers");
for (var i = 163; i < 173; i++) Check(!Feed(i, 96), "new hot episode waits " + i);
Check(Feed(173, 96), "new episode after recovery");
var quietRule = new TemperatureRule { Enabled = true }; var quietEpisodes = new TemperatureEpisodes();
for (var i = 0; i <= 10; i++) Check(!quietEpisodes.Observe("GPU", quietRule, Temp(95), start.AddSeconds(i), start.AddSeconds(i), true), "quiet suppress " + i);
Check(quietRule.InEpisode, "quiet consumes episode");
Check(!quietEpisodes.Observe("GPU", quietRule, Temp(95), start.AddSeconds(11), start.AddSeconds(11), false), "unquiet no backlog");
var gapRule = new TemperatureRule { Enabled = true }; var gaps = new TemperatureEpisodes();
for (var i = 0; i <= 9; i++) gaps.Observe("CPU", gapRule, Temp(95), start.AddSeconds(i), start.AddSeconds(i), false);
Check(!gaps.Observe("CPU", gapRule, Temp(95), start.AddSeconds(30), start.AddSeconds(30), false), "sampling gap resets hot timer");
Check(!gaps.Observe("CPU", gapRule, Temp(95), start.AddSeconds(31), start.AddSeconds(99), false), "stale sample no alert");
var duplicateRule = new TemperatureRule { Enabled = true }; var duplicates = new TemperatureEpisodes();
for (var i = 0; i < 100; i++) Check(!duplicates.Observe("CPU", duplicateRule, Temp(99), start, start, false), "duplicate stamp " + i);
var snoozeRule = new TemperatureRule { Enabled = true, SnoozeUntil = start.AddHours(1) }; var snoozes = new TemperatureEpisodes();
for (var i = 0; i <= 10; i++) Check(!snoozes.Observe("CPU", snoozeRule, Temp(95), start.AddSeconds(i), start.AddSeconds(i), false), "snoozed " + i);
Check(snoozeRule.InEpisode, "snoozed episode no replay after expiry");

var life = new BubbleLifetime(); life.Show();
Check(!life.Tick(start, true, false), "pet hover holds");
Check(!life.Tick(start.AddSeconds(10), false, true), "bubble hover holds");
Check(!life.Tick(start.AddSeconds(20), false, false), "leave begins timer");
Check(!life.Tick(start.AddSeconds(24.9), false, false), "before 5s stays");
Check(life.Tick(start.AddSeconds(25), false, false) && !life.Open, "5s closes");
life.Show(); life.Tick(start, false, false); life.Tick(start.AddSeconds(4), true, false);
Check(!life.Tick(start.AddSeconds(6), false, false), "return resets timer");
Check(!life.Tick(start.AddSeconds(50), false, false, true), "drag keeps existing bubble");

var snapshot = new MonitorSnapshot(start, [], [Temp(80) with { Id = "cpu/package" }, Temp(95) with { Id = "cpu/core", Source = "CPU Core #1" },
    Temp(70, "gpu1") with { Id = "gpu1/core", Kind = "GPU.Temp", Source = "GPU Core" },
    Temp(90, "gpu2") with { Id = "gpu2/core", Kind = "GPU.Temp", Source = "GPU Core" }], "test");
var config = new MonitorConfig { GpuDevice = "gpu2" };
Check(MonitorSelection.Resolve(snapshot, config).Single(m => m.Id == "CPU.Temp").Value == 80, "package preferred over hotter core");
Check(MonitorSelection.Resolve(snapshot, config).Single(m => m.Id == "GPU.Temp").Value == 90, "GPU selection respected");
config.GpuDevice = "missing";
Check(!MonitorSelection.Resolve(snapshot, config).Single(m => m.Id == "GPU.Temp").Valid, "offline selected GPU not silently replaced");
var amd = snapshot with { Metrics = [Temp(80) with { Source = "CPU (Tctl/Tdie)" }, Temp(95) with { Source = "CPU Core #1" }] };
Check(MonitorSelection.Resolve(amd, config).First(m => m.Id == "CPU.Temp").Value == 80, "AMD package temperature preferred");
var sourceRule = new TemperatureRule { Enabled = true }; var sourceEpisodes = new TemperatureEpisodes();
for (var i = 0; i < 10; i++) sourceEpisodes.Observe("CPU", sourceRule, Temp(95), start.AddSeconds(i), start.AddSeconds(i), false);
Check(!sourceEpisodes.Observe("CPU", sourceRule, Temp(95, "different"), start.AddSeconds(10), start.AddSeconds(10), false), "device switch resets continuous hot timing");
sourceRule.Threshold = 92;
Check(!sourceEpisodes.Observe("CPU", sourceRule, Temp(95, "different"), start.AddSeconds(11), start.AddSeconds(11), false), "threshold edit resets continuous timing");

var output = args.Length > 0 ? Path.GetFullPath(args[0]) : throw new Exception("Pass isolated .harness test directory");
Directory.CreateDirectory(output); var store = new MonitorStore(Path.Combine(output, Guid.NewGuid().ToString("N")));
var c = store.Load(); Check(c.Enabled && c.HistoryEnabled && !c.FpsEnabled && c.Cpu.Enabled && c.Gpu.Enabled && c.Cpu.Threshold == 90 && c.Gpu.Threshold == 85, "monitor history and temperature alerts default on while FPS is opt in");
c.Cpu.InEpisode = true; c.SelectedMetrics.Reverse(); Check(store.Save(c), "store atomic save");
var reread = store.Load(); Check(reread.Cpu.InEpisode && reread.SelectedMetrics.SequenceEqual(c.SelectedMetrics), "episode and order persistence");
var legacy = new MonitorStore(Path.Combine(output, Guid.NewGuid().ToString("N"))); Directory.CreateDirectory(legacy.DirectoryPath);
File.WriteAllText(legacy.ConfigPath, "{\"Enabled\":false,\"HistoryEnabled\":false,\"Cpu\":{\"Enabled\":false,\"Threshold\":95},\"Gpu\":{\"Enabled\":true,\"Threshold\":80},\"WebEnabled\":true}");
var legacyConfig = legacy.Load();
Check(legacyConfig.TaskbarBackgroundOpacity == 1 && legacyConfig.TaskbarLeft is null && legacyConfig.TaskbarTop is null,
    "legacy taskbar config defaults to opaque automatic placement without changing old switches");
var taskbarStore = new MonitorStore(Path.Combine(output, Guid.NewGuid().ToString("N")));
var taskbarConfig = new MonitorConfig { TaskbarBackgroundOpacity = 0, TaskbarLeft = -1200, TaskbarTop = 850 };
Check(taskbarStore.Save(taskbarConfig) && taskbarStore.Load() is { TaskbarBackgroundOpacity: 0, TaskbarLeft: -1200, TaskbarTop: 850 }, "transparent taskbar and negative monitor coordinates round trip");
foreach (var bad in new[] { "{\"TaskbarBackgroundOpacity\":2}", "{\"TaskbarLeft\":10}" })
{
    var invalidTaskbar = new MonitorStore(Path.Combine(output, Guid.NewGuid().ToString("N"))); Directory.CreateDirectory(invalidTaskbar.DirectoryPath);
    File.WriteAllText(invalidTaskbar.ConfigPath, bad); invalidTaskbar.Load();
    Check(!invalidTaskbar.Available && File.ReadAllText(invalidTaskbar.ConfigPath) == bad, "invalid taskbar alpha or incomplete position preserves original file " + bad);
}
Check(!legacyConfig.Enabled && !legacyConfig.HistoryEnabled && !legacyConfig.Cpu.Enabled && legacyConfig.Gpu.Enabled
    && legacyConfig.Cpu.Threshold == 95 && legacyConfig.Gpu.Threshold == 80 && legacyConfig.WebEnabled,
    "old explicit off switches thresholds and retired web fields are preserved");
File.WriteAllText(store.ConfigPath, "{ broken"); var damaged = new MonitorStore(store.DirectoryPath); damaged.Load();
Check(!damaged.Available && !damaged.Save(new()) && File.ReadAllText(store.ConfigPath) == "{ broken", "invalid file preserved");
var future = new MonitorStore(Path.Combine(output, Guid.NewGuid().ToString("N"))); Directory.CreateDirectory(future.DirectoryPath);
File.WriteAllText(future.ConfigPath, "{\"SchemaVersion\":99}"); future.Load(); Check(!future.Available && !future.Save(new()), "future schema preserved");
var theme = new MonitorConfig { Background = "#123456", FontSize = 19, RowSpacing = 7, PanelWidth = 500 };
var restoredTheme = new MonitorConfig(); MonitorTheme.Import(MonitorTheme.Export(theme), restoredTheme);
Check(restoredTheme.Background == theme.Background && restoredTheme.FontSize == 19 && restoredTheme.RowSpacing == 7 && restoredTheme.PanelWidth == 500, "theme export import preserves configurable fields");
var originalColor = restoredTheme.Background;
try { MonitorTheme.Import("{\"version\":1,\"color\":{\"background\":\"red\"}}", restoredTheme); } catch (Exception e) when (e is InvalidDataException or KeyNotFoundException) { }
Check(restoredTheme.Background == originalColor, "invalid theme does not partially replace current theme");
Check(MonitorTheme.Color(Temp(100), theme) == theme.CriticalColor && MonitorTheme.Color(Temp(87), theme) == theme.WarningColor, "three level temperature colors");
Check(MonitorTheme.Color(Temp(null), theme) == theme.Foreground, "missing value gets neutral color");
var trafficDir = Path.Combine(output, Guid.NewGuid().ToString("N")); var traffic = new TrafficLedger(trafficDir); traffic.Load(); traffic.Save();
Check(!Directory.Exists(trafficDir), "idle monitor does not create traffic data");
MonitorSnapshot Network(int second, double? value, string device = "nic") => new(start.AddSeconds(second), [],
    [new("up", "up", device, "NET.Up", "B/s", value, "test"), new("down", "down", device, "NET.Down", "B/s", value * 2, "test")], "test");
traffic.Observe(Network(0, 100), config); traffic.Observe(Network(1, 100), config);
Check(traffic.Days.Values.Single().UploadBytes == 100, "traffic integrates valid interval");
traffic.Observe(Network(2, null), config); traffic.Observe(Network(3, 100), config);
Check(traffic.Days.Values.Single().UploadBytes == 100, "traffic missing interval not invented");
traffic.Observe(Network(4, 100, "other"), config); traffic.ResetTiming(); traffic.Observe(Network(5, 100, "other"), config);
Check(traffic.Days.Values.Single().UploadBytes == 100, "traffic device switch and session boundary skipped");
traffic.Save(); var reloadTraffic = new TrafficLedger(trafficDir); reloadTraffic.Load();
Check(reloadTraffic.Days.Values.Single().UploadBytes == 100, "traffic persists totals");
var historyDir = Path.Combine(output, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(historyDir);
var day = DateOnly.FromDateTime(start.LocalDateTime); var historyFile = Path.Combine(historyDir, day.ToString("yyyy-MM-dd") + ".jsonl");
File.WriteAllLines(historyFile, [JsonSerializer.Serialize(snapshot), "{broken", JsonSerializer.Serialize(snapshot), JsonSerializer.Serialize(snapshot with { Timestamp = start.AddSeconds(10), Metrics = [Temp(null)] })]);
var archive = new MonitorHistory(historyDir); var read = archive.Read(day);
Check(archive.Dates().SequenceEqual(new[] { day }) && read.Samples.Count == 2 && read.InvalidLines == 1, "history dates duplicates and corrupt lines handled");
Check(File.ReadAllLines(historyFile).Length == 4, "history read preserves original file");
using var csv = new StringWriter(); MonitorHistory.Export(csv, [snapshot with { Metrics = [Temp(null) with { Name = "=HYPERLINK(1)", Id = "test" }] }]);
Check(csv.ToString().Contains("'=" ) && csv.ToString().Contains(",,\"℃\""), "CSV preserves missing value and blocks formulas");
var diskSnapshot = snapshot with { Metrics = [new("disk1/r", "Read", "disk1", "DISK.Read", "B/s", 100, "Read Rate"), new("disk2/r", "Read", "disk2", "DISK.Read", "B/s", 200, "Read Rate")] };
Check(MonitorSelection.Resolve(diskSnapshot, new() { DiskDevice = "disk2" }).First(m => m.Id == "DISK.Read").Value == 200, "disk selection resolves correct throughput");
Check(MonitorLayout.Collapse(new(-1920, 0, 300, 200), new(-1920, 0, 1920, 1080))?.X == -2212, "left edge collapse uses physical negative monitor coordinates");
Check(MonitorLayout.Collapse(new(-300, 400, 300, 200), new(-1920, 0, 1920, 1080))?.X == -8, "right edge collapse leaves visible strip");
Check(MonitorLayout.Collapse(new(500, 300, 300, 200), new(0, 0, 1920, 1080)) is null, "central window does not edge hide");
Check(MonitorLayout.Collapse(new(100, 0, 300, 200), new(0, 0, 1920, 1080))?.Y == -192, "top edge collapse geometry");
Check(MonitorLayout.Clamp(new(-2000, 100, 300, 200), new(-1920, 0, 1920, 1080)) == new MonitorRect(-1920, 100, 300, 200), "clamp respects negative secondary screen coordinates");
Check(MonitorLayout.Clamp(new(1850, 1000, 300, 200), new(0, 0, 1920, 1040)) == new MonitorRect(1620, 840, 300, 200), "clamp respects work area reserved for taskbar");
Check(MonitorLayout.Clamp(new(-200, -200, 1500, 1500), new(0, 0, 800, 600)) == new MonitorRect(0, 0, 800, 600), "oversized window fits a smaller work area without invalid clamp range");
Check(MonitorLayout.FindGap(0, 1000, 200, [(0, 100), (400, 1000)]) == (100, 400), "taskbar uses existing blank gap");
Check(MonitorLayout.FindGap(0, 500, 200, [(0, 350), (100, 500)]) is null, "overlapping occupied intervals never counted as blank");
Check(MonitorLayout.FindGap(-1920, 0, 300, [(-1920, -1800), (-1200, 0)]) == (-1800, -1200), "secondary taskbar gap in negative screen coordinates");
var slowEpisodes = new TemperatureEpisodes(); var slowRule = new TemperatureRule { Enabled = true };
Check(!slowEpisodes.Observe("CPU", slowRule, Temp(99), start, start, false, 5000), "slow sampling starts hot timer");
Check(!slowEpisodes.Observe("CPU", slowRule, Temp(99), start.AddSeconds(5), start.AddSeconds(5), false, 5000), "slow sampling keeps valid continuity");
Check(slowEpisodes.Observe("CPU", slowRule, Temp(99), start.AddSeconds(10), start.AddSeconds(10), false, 5000), "slow sampling still alerts after ten seconds");
Check(PetUpdateManifest.IsNewer("0.3.0-beta.3", "0.3.0-beta.2") && !PetUpdateManifest.IsNewer("0.3.0-beta.1", "0.3.0-beta.2"), "Pet prerelease update comparison");
Check(PetUpdateManifest.IsNewer("0.3.0", "0.3.0-beta.2") && !PetUpdateManifest.IsNewer("0.3.0-beta.2", "0.3.0"), "release version takes precedence over prerelease");
Check(!PetUpdateManifest.ValidUrl("http://example.com/update") && !PetUpdateManifest.ValidUrl("https://user:password@example.com/update"), "update sources require HTTPS without user credentials");
var updateJson = JsonSerializer.Serialize(new PetUpdateManifest("DesktopPet", "win-x64", "0.3.0", "https://example.com/Pet.zip", new string('a',64), "test"));
Check(PetUpdateManifest.Parse(updateJson).Component == "DesktopPet", "whole Pet update manifest accepted");
var rejected = false; try { PetUpdateManifest.Parse(updateJson.Replace("DesktopPet", "LiteMonitor")); } catch (InvalidDataException) { rejected = true; }
Check(rejected, "independent LiteMonitor update rejected");
var frames = new FpsFrames(); frames.Read("Application,ProcessID,MsBetweenPresents", start);
// Header from pinned PresentMon 1.10.0 CsvOutput.cpp; values are controlled test data.
var officialFrames = new FpsFrames();
officialFrames.Read("Application,ProcessID,SwapChainAddress,Runtime,SyncInterval,PresentFlags,Dropped,TimeInSeconds,msInPresentAPI,msBetweenPresents,AllowsTearing,PresentMode,msUntilRenderComplete,msUntilDisplayed,msBetweenDisplayChange", start);
officialFrames.Read("probe.exe,42,0x1234,D3D9,1,0,0,1.0,0.1,16.6666667,0,Composed: Copy with GPU GDI,0.2,1,16.6666667", start);
officialFrames.Read("probe.exe,42,0x1234,D3D9,1,0,0,1.017,0.1,16.6666667,0,Composed: Copy with GPU GDI,0.2,1,16.6666667", start.AddMilliseconds(17));
Check(officialFrames.Metrics(start.AddMilliseconds(17)).Count == 1 && Math.Abs(officialFrames.Metrics(start.AddMilliseconds(17))[0].Value!.Value - 60) < .01, "pinned PresentMon 1.10.0 lowercase msBetweenPresents header produces FPS");
frames.Read("game.exe,42,16.6666667", start); frames.Read("game.exe,42,16.6666667", start.AddMilliseconds(17));
Check(Math.Abs(frames.Metrics(start.AddMilliseconds(17)).Single().Value!.Value - 60) < .01, "PresentMon frames converted to presented FPS");
frames.Read("DesktopPet.exe,100,1", start); frames.Read("dwm.exe,101,1", start); frames.Read("game.exe,42,NaN", start);
Check(frames.Metrics(start.AddSeconds(1)).Count == 1, "FPS excludes pet desktop compositor and invalid values");
Check(frames.Metrics(start.AddSeconds(5)).Count == 0, "FPS stale frames are unavailable rather than reused");
var foregroundFrames = new MonitorSnapshot(start, [], [new("FPS.1", "background", "process/1", "FPS", "FPS", 100, "test"),
    new("FPS.2", "foreground", "process/2", "FPS", "FPS", 60, "test"), new("FPS.3", "renderer", "process/3", "FPS", "FPS", 90, "test")],
    "test", Capabilities: new() { ["fps-foreground"] = "2,3" });
var foregroundConfig = new MonitorConfig { FpsEnabled = true, FpsProcessId = 1 };
Check(MonitorSelection.Resolve(foregroundFrames, foregroundConfig).First(m => m.Id == "FPS").Value == 60, "foreground window wins over legacy PID and faster background frames");
Check(MonitorSelection.Resolve(foregroundFrames with { Metrics = foregroundFrames.Metrics.Where(m => m.DeviceId != "process/2").ToList() }, foregroundConfig).First(m => m.Id == "FPS").Value == 90, "foreground renderer family provides frames when window process has none");
Check(!MonitorSelection.Resolve(foregroundFrames with { Capabilities = new() { ["fps-foreground"] = "4" } }, foregroundConfig).First(m => m.Id == "FPS").Valid, "foreground without frames never borrows background FPS");
Check(!MonitorSelection.Resolve(foregroundFrames with { Capabilities = null }, foregroundConfig).First(m => m.Id == "FPS").Valid, "missing foreground metadata never guesses a target");
Check(MonitorSelection.Resolve(foregroundFrames with { Capabilities = null }, foregroundConfig, history: true).First(m => m.Id == "FPS").Value == 100, "legacy history preserves its previous PID selection without changing live behavior");
foregroundConfig.FpsEnabled = false;
Check(MonitorSelection.Resolve(foregroundFrames, foregroundConfig, history: true).First(m => m.Id == "FPS").Value == 60, "disabling capture leaves recorded foreground FPS available in history");
var networkSnapshot = new MonitorSnapshot(start, [], [new("n1/up", "上传", "n1", "NET.Up", "B/s", 0, "test"), new("n1/down", "下载", "n1", "NET.Down", "B/s", 0, "test"), new("n2/up", "上传", "n2", "NET.Up", "B/s", 300, "test"), new("n2/down", "下载", "n2", "NET.Down", "B/s", 500, "test"), new("FPS.42", "FPS", "process/42", "FPS", "FPS", 60, "test")], "test");
var compact = MonitorSelection.Resolve(networkSnapshot, new());
Check(compact.Count(m => m.Kind is "NET.Up" or "NET.Down") == 2 && compact.First(m => m.Id == "NET.Up").Value == 300 && compact.First(m => m.Id == "NET.Down").Value == 500, "network catalog has only two rows from the same busiest adapter");
Check(compact.All(m => m.Kind != "FPS"), "FPS off removes both aggregate and raw process FPS rows");
var detailedSnapshot = networkSnapshot with { Metrics = [.. networkSnapshot.Metrics, new("system/memory/used", "已用内存", "system/memory", "Data", "GB", 12, "Windows"), .. Enumerable.Range(0, 300).Select(i => new MonitorMetric("gpu/detail/" + i, "GPU engine " + i, "gpu", "Load", "%", i % 100, "fixture"))] };
var important = MonitorSelection.Resolve(detailedSnapshot, new() { FpsEnabled = true });
Check(important.Count == 11 && important.All(m => !m.Id.StartsWith("gpu/detail/")) && detailedSnapshot.Metrics.Count > 300, "hundreds of raw sensors remain in snapshot but only eleven important metrics are selectable");
Check(important.Single(m => m.Id == "MEM.Used").Value == 12 && important.Single(m => m.Id == "MEM.Used").Unit == "GB", "curated used memory preserves GB units");
Check(MonitorSelection.Resolve(networkSnapshot, new() { NetworkDevice = "n1" }).First(m => m.Id == "NET.Up").Value == 0, "explicit network adapter selection retained");
var chains = new FpsFrames(); chains.Read("Application,ProcessID,SwapChainAddress,MsBetweenPresents", start);
foreach (var line in new[] { "game.exe,42,one,16.6667", "game.exe,42,one,16.6667", "game.exe,42,two,33.3333", "game.exe,42,two,33.3333" }) chains.Read(line, start);
Check(Math.Abs(chains.Metrics(start).Single().Value!.Value - 60) < .1, "multiple swapchains are not incorrectly averaged together");
var extended = new MonitorConfig { UiScale = 1.4, Visual = new() { CornerRadius = 12, ShowGroups = true, ShowBars = true, SmoothValues = true,
    ValueFamily = "Consolas", GroupSize = 11, ValueSize = 20, Bold = true, BarHigh = "#EE1234", Thresholds = new() { ["Temp"] = new(50, 70) } } };
var extendedCopy = new MonitorConfig(); MonitorTheme.Import(MonitorTheme.Export(extended), extendedCopy);
Check(extendedCopy.Visual.CornerRadius == 12 && extendedCopy.Visual.ValueFamily == "Consolas" && extendedCopy.Visual.ShowGroups && extendedCopy.Visual.ShowBars
    && extendedCopy.Visual.SmoothValues && extendedCopy.UiScale == 1.4 && extendedCopy.Visual.Bold && extendedCopy.Visual.BarHigh == "#EE1234", "complete theme visual fields round trip");
Check(MonitorTheme.Level(Temp(72), extendedCopy) == 2 && MonitorTheme.Level(Temp(55), extendedCopy) == 1, "theme temperature colors independent from alert threshold");
Check(extendedCopy.Cpu.Threshold == 90 && extendedCopy.Cpu.Enabled, "theme import does not alter temperature alert rules");
Check(MonitorTheme.BarPercent(Temp(null), extendedCopy) is null && MonitorTheme.BarPercent(Temp(200), extendedCopy) == 100, "bar missing data and clipping");
extendedCopy.Visual.ValueSize = double.NaN; Check(!MonitorTheme.Valid(extendedCopy), "invalid extended theme rejected");
var fallbackConfig = new MonitorConfig();
MonitorTheme.Import("{\"version\":3,\"font\":{\"valueFamily\":\"Consolas\",\"bold\":true},\"layout\":{\"cornerRadius\":10,\"groupPadding\":8},\"color\":{\"background\":\"#202020\",\"textPrimary\":\"#EEEEEE\",\"valueSafe\":\"#00CC00\",\"valueWarn\":\"#CCCC00\",\"valueCrit\":\"#CC0000\"},\"thresholds\":{\"temp\":{\"warn\":50,\"crit\":70}}}", fallbackConfig);
Check(fallbackConfig.Visual.Thresholds["Temp"].Warn == 50 && fallbackConfig.Visual.CornerRadius == 10 && fallbackConfig.Visual.ValueFamily == "Consolas", "upstream camel case theme options accepted");
var oldVisual = fallbackConfig.Visual;
try { MonitorTheme.Import(MonitorTheme.Export(fallbackConfig).Replace("Consolas", ""), fallbackConfig); } catch (InvalidDataException) { }
Check(ReferenceEquals(oldVisual, fallbackConfig.Visual), "invalid advanced theme cannot partially apply");
var shutdowns = 0; var shutdown = new MonitorShutdownTimer(() => shutdowns++);
shutdown.Schedule(1, 500); shutdown.Poll(60499);
Check(shutdowns == 0 && shutdown.Remaining(59999) == 1, "shutdown schedule waits for monotonic deadline");
shutdown.Poll(60500); shutdown.Poll(90000);
Check(shutdowns == 1 && shutdown.Due is null, "shutdown request fires once without forced repeated attempts");
shutdown.Schedule(20, 100000); shutdown.Cancel(); shutdown.Poll(2000000);
Check(shutdowns == 1 && shutdown.Due is null, "local shutdown cancel does not execute an OS command");
shutdown.Schedule(20, 100000); shutdown.Schedule(1, 100000); shutdown.Poll(160000);
Check(shutdowns == 2, "rescheduling replaces only the Pet countdown");
var invalidSchedule = false; try { shutdown.Schedule(0, 0); } catch (ArgumentOutOfRangeException) { invalidSchedule = true; }
Check(invalidSchedule && shutdown.Due is null, "invalid shutdown schedule rejected without mutation");
var startup = PetStartupCommand.Create(@"C:\测试 app\DesktopPet.exe", @"C:\独立 data\");
Check(startup.EndsWith("\"C:\\独立 data\""), "startup directory trailing separator does not escape closing quote");
Check(PetStartupCommand.Create(@"C:\Pet\DesktopPet.exe", @"C:\").EndsWith("\"C:\\\\\""), "root data directory backslash is escaped for Windows argument parsing");
Check(PetStartupCommand.BelongsTo(startup, @"c:\测试 app\desktopPet.exe"), "startup command recognizes exact owned executable regardless of case");
Check(!PetStartupCommand.BelongsTo(startup, @"C:\另一版本\DesktopPet.exe") && !PetStartupCommand.BelongsTo("\"C:\\测试 app\\DesktopPet.exe\"suffix", @"C:\测试 app\DesktopPet.exe"), "startup removal preserves another release and malformed prefix");
Console.WriteLine($"Monitoring tests passed: {count}");
