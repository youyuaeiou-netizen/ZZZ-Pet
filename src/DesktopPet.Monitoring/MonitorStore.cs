using System.Text.Json;

namespace DesktopPet.Monitoring;

public sealed class MonitorStore(string directory)
{
    public string DirectoryPath => directory;
    public string ConfigPath => Path.Combine(directory, "monitor.json");
    public string? Error { get; private set; }
    public bool Available => Error is null;
    public MonitorConfig Load()
    {
        try
        {
            var c = File.Exists(ConfigPath) ? JsonSerializer.Deserialize<MonitorConfig>(File.ReadAllText(ConfigPath), MonitorJson.Options)
                ?? throw new InvalidDataException("监控配置为空") : new MonitorConfig();
            if (c.SchemaVersion != 1 || c.SelectedMetrics is null || c.SelectedMetrics.Any(string.IsNullOrWhiteSpace) ||
                c.RefreshMs is < 500 or > 5000 ||
                c.FpsProcessId is <= 0 ||
                c.CleanMinutes is < 5 or > 1440 ||
                (c.UpdateManifestUrl is not null && !PetUpdateManifest.ValidUrl(c.UpdateManifestUrl)) ||
                !double.IsFinite(c.TaskbarBackgroundOpacity) || c.TaskbarBackgroundOpacity is < 0 or > 1 ||
                (c.TaskbarLeft is null) != (c.TaskbarTop is null) ||
                (c.TaskbarLeft is double taskbarLeft && !double.IsFinite(taskbarLeft)) || (c.TaskbarTop is double taskbarTop && !double.IsFinite(taskbarTop)) ||
                c.TaskbarMetrics is null || c.TaskbarMetrics.Count > 12 || c.TaskbarMetrics.Any(string.IsNullOrWhiteSpace) || c.TaskbarMetrics.Distinct().Count() != c.TaskbarMetrics.Count ||
                c.SelectedMetrics.Count > 256 || c.SelectedMetrics.Distinct().Count() != c.SelectedMetrics.Count ||
                c.Cpu is null || c.Gpu is null || !ValidRule(c.Cpu) || !ValidRule(c.Gpu) ||
                !double.IsFinite(c.UiScale) || c.UiScale is < .5 or > 2.5 || !double.IsFinite(c.Opacity) || c.Opacity is < .1 or > 1 ||
                c.WebPort is < 1024 or > 65535 || !MonitorLanguage.Codes.Contains(c.Language) || !ValidColor(c.Background) || !ValidColor(c.Foreground)
                || !MonitorTheme.Valid(c) || (c.DisplayLeft is double left && !double.IsFinite(left)) || (c.DisplayTop is double top && !double.IsFinite(top)))
                throw new InvalidDataException("监控配置损坏或版本不兼容，原文件已保留");
            return c;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or NotSupportedException)
        { Error = e.Message; return new(); }
    }
    private static bool ValidRule(TemperatureRule r) => double.IsFinite(r.Threshold) && r.Threshold is >= 30 and <= 130;
    public static bool ValidColor(string? text) => text is { Length: 7 } && text[0] == '#' && text.Skip(1).All(Uri.IsHexDigit);
    public bool Save(MonitorConfig config)
    {
        if (!Available) return false;
        try
        {
            Directory.CreateDirectory(directory);
            using (var stream = new FileStream(ConfigPath + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None))
            { JsonSerializer.Serialize(stream, config, MonitorJson.Options); stream.Flush(true); }
            File.Move(ConfigPath + ".tmp", ConfigPath, true);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Error = e.Message; return false; }
    }
}
