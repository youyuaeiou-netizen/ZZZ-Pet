using System.Globalization;
using System.Text.Json;

namespace DesktopPet.Monitoring;

public sealed record HistoryRead(List<MonitorSnapshot> Samples, int InvalidLines);
public sealed class MonitorHistory(string directory)
{
    public List<DateOnly> Dates() => Directory.Exists(directory) ? Directory.EnumerateFiles(directory, "*.jsonl")
        .Select(f => DateOnly.TryParseExact(Path.GetFileNameWithoutExtension(f), "yyyy-MM-dd", out var d) ? (DateOnly?)d : null)
        .Where(d => d.HasValue).Select(d => d!.Value).OrderDescending().ToList() : [];
    public HistoryRead Read(DateOnly date)
    {
        var file = Path.Combine(directory, date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".jsonl");
        if (!File.Exists(file)) return new([], 0);
        if (new FileInfo(file).Length > 64 * 1024 * 1024) throw new InvalidDataException("历史文件超过读取上限，原文件保留。");
        var samples = new List<MonitorSnapshot>(); var invalid = 0;
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } line)
        {
            try
            {
                if (line.Length > 2_000_000) throw new InvalidDataException();
                var s = JsonSerializer.Deserialize<MonitorSnapshot>(line, MonitorJson.Options);
                if (s is null || s.ProtocolVersion != 1 || s.Metrics is null || s.Devices is null || s.Metrics.Count > 4096 ||
                    s.Metrics.Any(m => m is null || string.IsNullOrWhiteSpace(m.Id) || m.Name is null || m.Source is null || m.Unit is null))
                    throw new InvalidDataException();
                samples.Add(s);
            }
            catch (Exception e) when (e is JsonException or InvalidDataException) { invalid++; }
        }
        return new(samples.OrderBy(s => s.Timestamp).DistinctBy(s => s.Timestamp).ToList(), invalid);
    }
    public static void Export(TextWriter writer, IEnumerable<MonitorSnapshot> samples)
    {
        writer.WriteLine("Timestamp,MetricId,Name,Value,Unit,Source");
        foreach (var s in samples)
            foreach (var m in s.Metrics)
                writer.WriteLine($"{s.Timestamp:O},{Quote(m.Id)},{Quote(m.Name)},{(m.Valid ? m.Value!.Value.ToString(CultureInfo.InvariantCulture) : "")},{Quote(m.Unit)},{Quote(m.Source)}");
    }
    private static string Quote(string text) => "\"" + (text.TrimStart().FirstOrDefault() is '=' or '+' or '-' or '@' ? "'" : "")
        + text.Replace("\"", "\"\"") + "\"";
}
