using System.Text.Json;
using System.Text.Json.Serialization;

namespace DesktopPet.Monitoring;

public sealed record MonitorDevice(string Id, string Name, string Kind);
public sealed record MonitorMetric(string Id, string Name, string DeviceId, string Kind,
    string Unit, double? Value, string Source, string? Text = null, DateTimeOffset? SampledAt = null)
{
    public bool Valid => Value is double v && double.IsFinite(v);
    public string Display => Text ?? (Valid ? Unit == "B/s" ? FormatRate(Value!.Value) : $"{Value:0.##} {Unit}" : "不可用");
    private static string FormatRate(double n) => n >= 1048576 ? $"{n / 1048576:0.##} MB/s" : n >= 1024 ? $"{n / 1024:0.##} KB/s" : $"{n:0} B/s";
}
public sealed record MonitorSnapshot(DateTimeOffset Timestamp, List<MonitorDevice> Devices,
    List<MonitorMetric> Metrics, string Status, bool Elevated = false, int ProtocolVersion = 1, Dictionary<string, string>? Capabilities = null)
{
    public bool Fresh(DateTimeOffset now)
    {
        // A supported five-second interval must not expire while the next sensor read completes.
        var interval = int.TryParse(Capabilities?.GetValueOrDefault("sampling-ms"), out var ms) && ms is >= 500 and <= 5000 ? ms : 1000;
        return Timestamp != DateTimeOffset.MinValue && now >= Timestamp &&
            now - Timestamp <= TimeSpan.FromMilliseconds(Math.Max(5000, interval + 3000));
    }
    public MonitorMetric ForDisplay(MonitorMetric metric, DateTimeOffset now)
    {
        if (!Fresh(now)) return metric with
        {
            Value = null,
            Text = metric.Kind == "FPS" ? "—" : Timestamp == DateTimeOffset.MinValue ? "已断开" : "数据延迟",
            Source = metric.Kind == "FPS" ? Timestamp == DateTimeOffset.MinValue ? "FPS 监控尚未连接。" : "FPS 监控数据延迟。" : metric.Source
        };
        if (metric.Kind == "FPS" && metric.SampledAt is DateTimeOffset frameAt && (frameAt > now || now - frameAt > TimeSpan.FromSeconds(3)))
            metric = metric with { Value = null, Text = null };
        if (metric.Kind == "FPS" && !metric.Valid)
        {
            var helper = Capabilities?.GetValueOrDefault("fps-helper");
            var source = helper switch
            {
                "recovering" => "长时间未收到帧，正在重新连接系统帧事件。",
                "running" => "当前没有可读取的连续呈现帧；静止画面没有可报告的 FPS。",
                "no-frames" => "采集器运行超过十秒仍无有效帧。可能是应用没有连续呈现或系统帧事件采集异常；不代表 FPS 为零。",
                "starting" => "正在启动帧率采集组件。",
                "requires-elevation" => "FPS 采集需要管理员权限。",
                "unavailable" or "helper-integrity-error" => "FPS 采集组件不可用。",
                _ => metric.Source
            };
            return metric with { Text = "—", Source = source };
        }
        return metric;
    }
    public static MonitorSnapshot Empty => new(DateTimeOffset.MinValue, [], [], "未连接");
}
public sealed record MonitorCommand(string Operation, int ProtocolVersion = 1, int? RefreshMs = null, bool FpsEnabled = false);
public static class MonitorJson
{
    public static readonly JsonSerializerOptions Options = new() { WriteIndented = false,
        NumberHandling = JsonNumberHandling.Strict };
}
public sealed class TemperatureRule
{
    public bool Enabled { get; set; }
    public double Threshold { get; set; } = 90;
    public bool InEpisode { get; set; }
    public DateTimeOffset? SnoozeUntil { get; set; }
    public string? EpisodeDeviceId { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}
public sealed class MonitorConfig
{
    public int SchemaVersion { get; set; } = 1;
    public bool Enabled { get; set; } = true;
    public List<string> SelectedMetrics { get; set; } = ["CPU.Load", "GPU.Load", "MEM.Load", "CPU.Temp", "GPU.Temp", "NET.Up", "NET.Down"];
    public string? GpuDevice { get; set; }
    public string? NetworkDevice { get; set; }
    public string? DiskDevice { get; set; }
    public TemperatureRule Cpu { get; set; } = new() { Enabled = true };
    public TemperatureRule Gpu { get; set; } = new() { Enabled = true, Threshold = 85 };
    public bool HistoryEnabled { get; set; } = true;
    public int RefreshMs { get; set; } = 1000;
    public bool FpsEnabled { get; set; }
    public int? FpsProcessId { get; set; }
    public bool AutoCleanOwnMemory { get; set; }
    public int CleanMinutes { get; set; } = 30;
    public bool PluginsEnabled { get; set; }
    public bool WebEnabled { get; set; }
    public bool WebLan { get; set; }
    public bool WebIpv6 { get; set; }
    public int WebPort { get; set; } = 5057;
    public bool TaskbarEnabled { get; set; }
    public string? TaskbarScreen { get; set; }
    public double TaskbarBackgroundOpacity { get; set; } = 1;
    public double? TaskbarLeft { get; set; }
    public double? TaskbarTop { get; set; }
    public List<string> TaskbarMetrics { get; set; } = ["CPU.Load", "GPU.Load", "MEM.Load"];
    public string Language { get; set; } = "zh";
    public string Background { get; set; } = "#FFFBD8";
    public string Foreground { get; set; } = "#55416B";
    public double UiScale { get; set; } = 1;
    public double Opacity { get; set; } = 1;
    public bool Horizontal { get; set; }
    public bool ClickThrough { get; set; }
    public bool AutoHide { get; set; }
    public bool DisplayTopmost { get; set; } = true;
    public bool ClampToScreen { get; set; } = true;
    public string? UpdateManifestUrl { get; set; }
    public bool DisplayEnabled { get; set; }
    public double? DisplayLeft { get; set; }
    public double? DisplayTop { get; set; }
    public string FontFamily { get; set; } = "Microsoft YaHei UI";
    public double FontSize { get; set; } = 13;
    public double RowSpacing { get; set; } = 4;
    public double PanelWidth { get; set; } = 350;
    public string SafeColor { get; set; } = "#26735A";
    public string WarningColor { get; set; } = "#A46B00";
    public string CriticalColor { get; set; } = "#B3263C";
    public double LoadWarning { get; set; } = 70;
    public double LoadCritical { get; set; } = 90;
    public double RateWarning { get; set; } = 10;
    public double RateCritical { get; set; } = 50;
    public MonitorVisualStyle Visual { get; set; } = new();
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public static class MonitorSelection
{
    public static readonly (string Id, string Name)[] Defaults = [("CPU.Load", "CPU 使用率"), ("GPU.Load", "GPU 使用率"),
        ("MEM.Load", "内存使用率"), ("MEM.Used", "已用内存"), ("CPU.Temp", "CPU 温度"), ("GPU.Temp", "GPU 核心温度"),
        ("NET.Up", "上传速度"), ("NET.Down", "下载速度"), ("DISK.Read", "磁盘读取"), ("DISK.Write", "磁盘写入"), ("FPS", "呈现帧率")];

    public static List<MonitorMetric> Resolve(MonitorSnapshot snapshot, MonitorConfig config, bool history = false)
    {
        var all = new List<MonitorMetric>();
        foreach (var (key, name) in Defaults)
        {
            if (key == "FPS" && !config.FpsEnabled && !history) continue;
            var kind = key.StartsWith("GPU.") ? "GPU" : key.StartsWith("NET.") ? "NET" : key.StartsWith("DISK.") ? "DISK" : key == "FPS" ? "FPS" : "CPU";
            var preferred = kind == "GPU" ? config.GpuDevice : kind == "NET" ? config.NetworkDevice : kind == "DISK" ? config.DiskDevice
                : null;
            if (kind == "NET" && preferred is null)
                preferred = snapshot.Metrics.Where(m => (m.Kind is "NET.Up" or "NET.Down") && m.Valid)
                    .GroupBy(m => m.DeviceId).OrderByDescending(g => g.Sum(m => m.Value ?? 0)).FirstOrDefault()?.Key;
            var candidates = snapshot.Metrics.Where(m => (m.Kind == key || key == "MEM.Used" && m.Id == "system/memory/used") && (preferred is null || m.DeviceId == preferred)).ToList();
            // Foreground targets are stamped by the ordinary UI process, not persisted as a manual PID.
            if (key == "FPS")
            {
                var frameNow = history ? snapshot.Timestamp : DateTimeOffset.UtcNow;
                bool HasRecentFrame(MonitorMetric metric) => metric.Valid &&
                    (metric.SampledAt is not DateTimeOffset sampledAt || sampledAt <= frameNow.AddSeconds(history ? 3 : 0) &&
                     frameNow - sampledAt <= TimeSpan.FromSeconds(3));
                var targets = (snapshot.Capabilities?.GetValueOrDefault("fps-foreground") ?? "").Split(',')
                    .Where(s => int.TryParse(s, out var pid) && pid > 0).Select(s => "process/" + s).ToArray();
                candidates = candidates.Where(m => targets.Contains(m.DeviceId) && HasRecentFrame(m)).ToList();
                preferred = targets.FirstOrDefault();
                // The window process wins; shared renderers from the same process tree are a fallback.
                candidates = candidates.OrderBy(m => m.DeviceId == preferred ? 0 : 1).ThenByDescending(m => m.Value).ToList();
                // If the foreground app has no presents, keep tracking another active app
                // instead of dropping its fresh sample. DWM is the final desktop fallback.
                if (candidates.Count == 0)
                    candidates = snapshot.Metrics.Where(m => m.Kind == "FPS" && m.DeviceId.StartsWith("process/", StringComparison.Ordinal) && HasRecentFrame(m))
                        .OrderByDescending(m => m.SampledAt).ThenByDescending(m => m.Value).ToList();
                if (candidates.Count == 0 && snapshot.Metrics.FirstOrDefault(m => m.Kind == "FPS" && m.DeviceId == "desktop" && HasRecentFrame(m)) is { } desktop)
                    candidates.Add(desktop);
                if (history && snapshot.Capabilities?.ContainsKey("fps-foreground") != true)
                {
                    // Old history predates foreground selection; preserve its former target semantics only here.
                    preferred = config.FpsProcessId is int legacyPid ? "process/" + legacyPid : null;
                    candidates = snapshot.Metrics.Where(m => m.Kind == "FPS" && m.Valid && (preferred is null || m.DeviceId == preferred))
                        .OrderByDescending(m => m.Value).ToList();
                }
            }
            var chosen = key == "CPU.Temp" ? candidates.OrderBy(m => IsPackageTemperature(m.Source) ? 0 : 1)
                .ThenByDescending(m => m.Value).FirstOrDefault(m => m.Valid) : candidates.FirstOrDefault();
            all.Add(chosen is null ? new(key, name, preferred ?? "", key, key == "MEM.Used" ? "GB" : key.EndsWith("Temp") ? "℃" : kind is "NET" or "DISK" ? "B/s" : kind == "FPS" ? "FPS" : "%", null, "无可用传感器")
                : chosen with { Id = key, Name = key == "FPS" && chosen.DeviceId == "desktop" ? "桌面 FPS" : name, Source = chosen.Source + (snapshot.Devices.FirstOrDefault(d => d.Id == chosen.DeviceId) is MonitorDevice device ? " · " + device.Name : "") });
        }
        return all;
    }
    private static bool IsPackageTemperature(string source) => source.Contains("Package", StringComparison.OrdinalIgnoreCase)
        || source.Contains("Tctl/Tdie", StringComparison.OrdinalIgnoreCase) || source.Equals("Tdie", StringComparison.OrdinalIgnoreCase);
}
