using System.Globalization;
using System.Text;

namespace DesktopPet.Monitoring;

public sealed record FpsReading(List<MonitorMetric> Metrics, string Status);
public sealed class FpsFrames
{
    private sealed class Samples { public string Name = ""; public DateTimeOffset At; public readonly Queue<double> Times = new(); }
    private readonly Dictionary<(int Pid, string Chain), Samples> _samples = [];
    private string[]? _header;
    private readonly object _lock = new();
    public void Read(string line, DateTimeOffset now)
    {
        if (line.Length > 32768) return; var cells = Csv(line);
        if (cells.Contains("ProcessID", StringComparer.OrdinalIgnoreCase) && cells.Contains("msBetweenPresents", StringComparer.OrdinalIgnoreCase)) { _header = cells; return; }
        if (_header is null || cells.Length != _header.Length) return;
        string Field(string name) { var i = Array.FindIndex(_header, column => column.Equals(name, StringComparison.OrdinalIgnoreCase)); return i >= 0 ? cells[i] : ""; }
        if (!int.TryParse(Field("ProcessID"), out var pid) || pid <= 0 ||
            !double.TryParse(Field("MsBetweenPresents"), NumberStyles.Float, CultureInfo.InvariantCulture, out var ms) || !double.IsFinite(ms) || ms is <= 0 or > 10000) return;
        var name = Field("Application");
        if (name.StartsWith("DesktopPet", StringComparison.OrdinalIgnoreCase)
            || name.Equals("explorer.exe", StringComparison.OrdinalIgnoreCase) || name.Equals("PresentMon.exe", StringComparison.OrdinalIgnoreCase)) return;
        lock (_lock)
        {
            var key = (pid, Field("SwapChainAddress"));
            foreach (var id in _samples.Where(p => now - p.Value.At > TimeSpan.FromSeconds(5)).Select(p => p.Key).ToArray()) _samples.Remove(id);
            if (!_samples.TryGetValue(key, out var sample))
            { if (_samples.Count >= 256) return; _samples[key] = sample = new(); }
            if (now - sample.At > TimeSpan.FromSeconds(2) || sample.Name != name) sample.Times.Clear();
            sample.At = now; sample.Name = name; sample.Times.Enqueue(ms); while (sample.Times.Count > 60) sample.Times.Dequeue();
        }
    }
    public List<MonitorMetric> Metrics(DateTimeOffset now)
    {
        lock (_lock) return _samples.Where(p => now - p.Value.At <= TimeSpan.FromSeconds(3) && p.Value.Times.Count >= 2)
            .GroupBy(p => p.Key.Pid).Select(g => g.OrderBy(p => p.Value.Times.Average()).First())
            .Select(p => new MonitorMetric("FPS." + p.Key.Pid, p.Value.Name.Equals("dwm.exe", StringComparison.OrdinalIgnoreCase) ? "桌面 FPS" : p.Value.Name + " · FPS",
                p.Value.Name.Equals("dwm.exe", StringComparison.OrdinalIgnoreCase) ? "desktop" : "process/" + p.Key.Pid, "FPS", "FPS",
                1000 / p.Value.Times.Average(), "PresentMon 1.10.0 · presented frames · fastest active swapchain" +
                    (p.Value.Name.Equals("dwm.exe", StringComparison.OrdinalIgnoreCase) ? " · 桌面合成呈现帧率，不是视频源帧率" : ""), SampledAt: p.Value.At)).ToList();
    }
    private static string[] Csv(string line)
    {
        var cells = new List<string>(); var text = new StringBuilder(); var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] == '"')
            { if (quoted && i + 1 < line.Length && line[i + 1] == '"') { text.Append('"'); i++; } else quoted = !quoted; }
            else if (line[i] == ',' && !quoted) { cells.Add(text.ToString()); text.Clear(); }
            else text.Append(line[i]);
        }
        cells.Add(text.ToString()); return cells.ToArray();
    }
}
