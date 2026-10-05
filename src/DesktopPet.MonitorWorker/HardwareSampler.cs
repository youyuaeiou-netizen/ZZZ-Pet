using System.Diagnostics;
using System.Globalization;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using DesktopPet.Monitoring;
using LibreHardwareMonitor.Hardware;
using LiteMonitor.src.SystemServices;

namespace DesktopPet.MonitorWorker;

internal sealed class HardwareSampler : IDisposable
{
    private Computer? _computer;
    private readonly HttpClient _existing = new(new SocketsHttpHandler { UseProxy = false })
        { Timeout = TimeSpan.FromMilliseconds(700), MaxResponseContentBufferSize = 2 * 1024 * 1024 };
    private readonly Dictionary<string, (long Up, long Down, long Tick)> _network = [];
    private ulong _idle, _total;
    private bool _cpuInitialized;
    private long _lastExternalTry;
    private bool _externalAvailable;
    [StructLayout(LayoutKind.Sequential)] private struct MemoryStatus
    { public uint Length, Load; public ulong TotalPhys, AvailPhys, TotalPage, AvailPage, TotalVirtual, AvailVirtual, Extended; }
    [DllImport("kernel32.dll")] private static extern bool GlobalMemoryStatusEx(ref MemoryStatus memory);
    [DllImport("kernel32.dll")] private static extern bool GetSystemTimes(out ulong idle, out ulong kernel, out ulong user);

    public async Task<MonitorSnapshot> SampleAsync(CancellationToken token)
    {
        var now = DateTimeOffset.UtcNow;
        var devices = new List<MonitorDevice> { new("system/cpu", "CPU", "CPU"), new("system/memory", "系统内存", "MEM") };
        var metrics = new List<MonitorMetric>();
        string status = "基础采集运行中；未安装或修改驱动";
        if (GetSystemTimes(out var idle, out var kernel, out var user))
        {
            var total = kernel + user;
            double? load = _cpuInitialized && total > _total && idle >= _idle ? Math.Clamp(100d * (1 - (double)(idle - _idle) / (total - _total)), 0, 100) : null;
            _idle = idle; _total = total; _cpuInitialized = true;
            metrics.Add(new("system/cpu/load", "CPU 使用率", "system/cpu", "CPU.Load", "%", load, "Windows GetSystemTimes"));
        }
        var memory = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        if (GlobalMemoryStatusEx(ref memory))
        {
            metrics.Add(new("system/memory/load", "内存使用率", "system/memory", "MEM.Load", "%", memory.Load, "Windows GlobalMemoryStatusEx"));
            metrics.Add(new("system/memory/used", "已用内存", "system/memory", "Data", "GB", (memory.TotalPhys - memory.AvailPhys) / 1073741824d, "Windows"));
        }
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .OrderBy(n => n.NetworkInterfaceType == NetworkInterfaceType.Tunnel || new[] { "virtual", "vpn", "wintun", "wireguard", "tap", "vmware", "hyper-v" }
                .Any(s => n.Description.Contains(s, StringComparison.OrdinalIgnoreCase)) ? 1 : 0).ThenByDescending(n => n.Speed))
        {
            try
            {
                var counters = nic.GetIPStatistics(); var tick = Stopwatch.GetTimestamp();
                var id = "network/" + nic.Id; devices.Add(new(id, nic.Name, "NET"));
                var seen = _network.TryGetValue(id, out var previous);
                var seconds = seen ? (tick - previous.Tick) / (double)Stopwatch.Frequency : 0;
                metrics.Add(new(id + "/up", "上传", id, "NET.Up", "B/s", seconds > 0 && counters.BytesSent >= previous.Up ? (counters.BytesSent - previous.Up) / seconds : null, nic.Description));
                metrics.Add(new(id + "/down", "下载", id, "NET.Down", "B/s", seconds > 0 && counters.BytesReceived >= previous.Down ? (counters.BytesReceived - previous.Down) / seconds : null, nic.Description));
                _network[id] = (counters.BytesSent, counters.BytesReceived, tick);
            }
            catch (NetworkInformationException) { }
        }

        // Prefer the existing ObsUI read-only provider. Never start its scheduled task or own its lifetime.
        var external = false;
        if (_externalAvailable || Environment.TickCount64 - _lastExternalTry > 10000 || _lastExternalTry == 0)
        {
            _lastExternalTry = Environment.TickCount64;
            try
            {
                using var response = await _existing.GetAsync("http://127.0.0.1:8085/data.json", token);
                response.EnsureSuccessStatusCode();
                using var content = await response.Content.ReadAsStreamAsync(token);
                using var json = await JsonDocument.ParseAsync(content, cancellationToken: token);
                if (json.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException();
                var sampledAt = json.RootElement.TryGetProperty("SampledAt", out var stamp) && stamp.ValueKind == JsonValueKind.Number && stamp.TryGetInt64(out var ms)
                    ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : now;
                if (sampledAt <= now && now - sampledAt <= TimeSpan.FromSeconds(5))
                {
                    ReadExternal(json.RootElement, devices, metrics, sampledAt);
                    external = true; status = "运行中 · 只读复用现有 8085 采集服务";
                }
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or ArgumentException or IOException or InvalidOperationException) { }
            _externalAvailable = external;
        }
        if (external && _computer is not null) { _computer.Close(); _computer = null; }
        if (!external)
        {
            // Do not create a second hardware driver client alongside ObsUI's monitor when its endpoint is temporarily unavailable.
            var existingOwner = Process.GetProcessesByName("ObsUIHardwareMonitor").Concat(Process.GetProcessesByName("LibreHardwareMonitor")).ToArray();
            var ownedElsewhere = existingOwner.Length > 0;
            foreach (var p in existingOwner) p.Dispose();
            if (!ownedElsewhere)
            {
                try
                {
                    if (_computer is null)
                    {
                        _computer = new Computer { IsCpuEnabled = true, IsGpuEnabled = true, IsMemoryEnabled = false,
                            IsStorageEnabled = true, IsMotherboardEnabled = true, IsBatteryEnabled = true,
                            IsControllerEnabled = false, IsNetworkEnabled = false };
                        _computer.Open();
                    }
                    foreach (var hardware in _computer.Hardware.OrderBy(HardwareRules.GetHwPriority)) ReadHardware(hardware, devices, metrics);
                    status = "运行中 · LibreHardwareMonitor 0.9.6；缺失传感器显示不可用";
                }
                catch (Exception e) when (e is InvalidOperationException or UnauthorizedAccessException or IOException or DllNotFoundException)
                { status = "基础采集可用；硬件传感器暂不可用：" + e.GetType().Name; }
            }
            else
            {
                if (_computer is not null) { _computer.Close(); _computer = null; }
                status = "基础采集可用；等待现有硬件服务恢复，不重复打开驱动";
            }
        }
        using var identity = WindowsIdentity.GetCurrent();
        var elevated = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        return new(now, devices.DistinctBy(d => d.Id).ToList(), metrics.DistinctBy(m => m.Id).ToList(), status, elevated);
    }

    private static string HardwareKind(string identifier) => identifier.Contains("gpu", StringComparison.OrdinalIgnoreCase) ? "GPU"
        : identifier.Contains("cpu", StringComparison.OrdinalIgnoreCase) ? "CPU" :
        new[] { "/storage/", "/hdd/", "/nvme/", "/ata/", "/scsi/" }.Any(p => identifier.StartsWith(p, StringComparison.OrdinalIgnoreCase)) ? "DISK" : "HOST";
    private static string MetricKind(string hardwareKind, string name, string type)
    {
        if (type == "Temperature")
        {
            if (hardwareKind == "CPU" && (HardwareRules.Has(name, "package") || HardwareRules.Has(name, "tctl/tdie") ||
                name.Equals("Tdie", StringComparison.OrdinalIgnoreCase) || HardwareRules.Has(name, "core #"))) return "CPU.Temp";
            if (hardwareKind == "GPU" && (name.Equals("GPU Core", StringComparison.OrdinalIgnoreCase) || name.Equals("Core", StringComparison.OrdinalIgnoreCase))) return "GPU.Temp";
        }
        if (type == "Load" && hardwareKind == "GPU" && (HardwareRules.Has(name, "core") || HardwareRules.Has(name, "d3d 3d"))) return "GPU.Load";
        if (type == "Throughput" && hardwareKind == "DISK")
        { if (HardwareRules.Has(name, "read")) return "DISK.Read"; if (HardwareRules.Has(name, "write")) return "DISK.Write"; }
        return type;
    }
    private static string Unit(string type) => type switch { "Temperature" => "℃", "Load" or "Level" => "%", "Clock" => "MHz",
        "Power" => "W", "Voltage" => "V", "Fan" => "RPM", "Flow" => "L/h", "Data" => "GB", "SmallData" => "MB", "Throughput" => "B/s", _ => "" };
    private static void ReadHardware(IHardware hardware, List<MonitorDevice> devices, List<MonitorMetric> metrics)
    {
        try { hardware.Update(); } catch (Exception e) when (e is IOException or InvalidOperationException) { return; }
        var id = hardware.Identifier.ToString(); var kind = HardwareKind(id);
        devices.Add(new(id, hardware.Name, kind));
        foreach (var sensor in hardware.Sensors)
        {
            var type = sensor.SensorType.ToString();
            metrics.Add(new(sensor.Identifier.ToString(), hardware.Name + " · " + sensor.Name, id,
                MetricKind(kind, sensor.Name, type), Unit(type), sensor.Value is float f && float.IsFinite(f) ? f : null,
                sensor.Name));
        }
        foreach (var child in hardware.SubHardware) ReadHardware(child, devices, metrics);
    }
    private static void ReadExternal(JsonElement node, List<MonitorDevice> devices, List<MonitorMetric> metrics, DateTimeOffset sampledAt)
    {
        if (node.ValueKind != JsonValueKind.Object) return;
        if (node.TryGetProperty("SensorId", out var sensor) || node.TryGetProperty("id", out sensor))
        {
            var id = sensor.ValueKind == JsonValueKind.String ? sensor.GetString() ?? "" : "";
            if (id.StartsWith('/'))
            {
                var parts = id.Split('/'); var device = parts.Length >= 3 ? "/" + parts[1] + "/" + parts[2] : id;
                var name = node.TryGetProperty("Text", out var text) ? text.GetString() ?? id : id;
                var hardwareName = node.TryGetProperty("HardwareName", out var hn) ? hn.GetString() ?? device : device;
                var type = node.TryGetProperty("Type", out var t) ? t.GetString() ?? "" : "";
                double? value = null;
                if (node.TryGetProperty("Value", out var v))
                {
                    if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var number) && double.IsFinite(number)) value = number;
                    else if (v.ValueKind == JsonValueKind.String && double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number) && double.IsFinite(number)) value = number;
                }
                var kind = HardwareKind(id); devices.Add(new(device, hardwareName, kind));
                metrics.Add(new(id, hardwareName + " · " + name, device, MetricKind(kind, name, type), Unit(type), value, name, SampledAt: sampledAt));
            }
        }
        if (node.TryGetProperty("Children", out var children) && children.ValueKind == JsonValueKind.Array)
            foreach (var child in children.EnumerateArray()) ReadExternal(child, devices, metrics, sampledAt);
    }
    public void Dispose() { _existing.Dispose(); _computer?.Close(); }
}
