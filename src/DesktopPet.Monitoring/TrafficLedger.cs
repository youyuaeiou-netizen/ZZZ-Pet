using System.Text.Json;

namespace DesktopPet.Monitoring;

public sealed record TrafficDay(double UploadBytes, double DownloadBytes);
public sealed class TrafficLedger(string directory)
{
    private DateTimeOffset? _last;
    private string? _device;
    private Dictionary<string, TrafficDay> _days = [];
    private long _savedAt;
    private bool _dirty;
    private bool _previousValid;
    public string? Error { get; private set; }
    public IReadOnlyDictionary<string, TrafficDay> Days => _days;
    public void Load()
    {
        try
        {
            var path = Path.Combine(directory, "traffic.json");
            if (File.Exists(path)) _days = JsonSerializer.Deserialize<Dictionary<string, TrafficDay>>(File.ReadAllText(path), MonitorJson.Options)
                ?? throw new InvalidDataException("流量数据为空");
            if (_days.Any(d => !DateOnly.TryParseExact(d.Key, "yyyy-MM-dd", out _) || d.Value is null ||
                !double.IsFinite(d.Value.UploadBytes) || !double.IsFinite(d.Value.DownloadBytes) || d.Value.UploadBytes < 0 || d.Value.DownloadBytes < 0))
                throw new InvalidDataException("流量数据无效");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
        { Error = "流量统计不可用，原文件保留。"; }
    }
    public void Observe(MonitorSnapshot snapshot, MonitorConfig config)
    {
        if (Error is not null) return;
        var catalog = MonitorSelection.Resolve(snapshot, config); var up = catalog.First(m => m.Id == "NET.Up"); var down = catalog.First(m => m.Id == "NET.Down");
        if (_last is DateTimeOffset last && snapshot.Timestamp > last && snapshot.Timestamp - last <= TimeSpan.FromSeconds(3) &&
            _previousValid && up.Valid && down.Valid && up.Value >= 0 && down.Value >= 0 && _device == up.DeviceId && up.DeviceId == down.DeviceId)
        {
            var day = snapshot.Timestamp.ToLocalTime().ToString("yyyy-MM-dd"); var seconds = (snapshot.Timestamp - last).TotalSeconds;
            var before = _days.GetValueOrDefault(day) ?? new(0, 0);
            _days[day] = new(before.UploadBytes + up.Value!.Value * seconds, before.DownloadBytes + down.Value!.Value * seconds);
            _dirty = true;
        }
        _last = snapshot.Timestamp; _device = up.DeviceId;
        _previousValid = up.Valid && down.Valid && up.Value >= 0 && down.Value >= 0;
        if (Environment.TickCount64 - _savedAt >= 60000) { Save(); _savedAt = Environment.TickCount64; }
    }
    public void ResetTiming() { _last = null; _device = null; _previousValid = false; }
    public void Save()
    {
        if (Error is not null || !_dirty) return;
        try
        {
            Directory.CreateDirectory(directory); var path = Path.Combine(directory, "traffic.json");
            File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(_days, MonitorJson.Options)); File.Move(path + ".tmp", path, true);
            _dirty = false;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Error = "流量统计无法保存。"; }
    }
}
