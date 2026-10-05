using System.Text.Json;

namespace DesktopPet.Monitoring;

public static class MonitorTheme
{
    public static bool Valid(MonitorConfig c) => c.FontFamily is { Length: > 0 and <= 100 } && !string.IsNullOrWhiteSpace(c.FontFamily) && double.IsFinite(c.FontSize)
        && c.FontSize is >= 8 and <= 32 && double.IsFinite(c.RowSpacing) && c.RowSpacing is >= 0 and <= 30
        && double.IsFinite(c.PanelWidth) && c.PanelWidth is >= 240 and <= 900
        && MonitorStore.ValidColor(c.SafeColor) && MonitorStore.ValidColor(c.WarningColor) && MonitorStore.ValidColor(c.CriticalColor)
        && double.IsFinite(c.LoadWarning) && double.IsFinite(c.LoadCritical) && c.LoadWarning >= 0 && c.LoadCritical > c.LoadWarning && c.LoadCritical <= 100
        && double.IsFinite(c.RateWarning) && double.IsFinite(c.RateCritical) && c.RateWarning >= 0 && c.RateCritical > c.RateWarning && c.RateCritical <= 100000
        && c.Visual is not null && c.Visual.Valid();
    public static string Color(MonitorMetric metric, MonitorConfig c)
        => Level(metric, c) switch { -1 => c.Foreground, 2 => c.CriticalColor, 1 => c.WarningColor, _ => c.SafeColor };
    public static int Level(MonitorMetric metric, MonitorConfig c)
    {
        if (!metric.Valid) return -1;
        var value = metric.Value!.Value;
        var temperature = metric.Unit == "℃" || metric.Unit == "°C";
        var critical = temperature ? metric.Kind == "GPU.Temp" ? c.Gpu.Threshold : c.Cpu.Threshold
            : metric.Unit == "%" ? c.LoadCritical : metric.Unit == "B/s" ? c.RateCritical * 1048576 : double.PositiveInfinity;
        var warning = temperature ? critical - 5 : metric.Unit == "%" ? c.LoadWarning
            : metric.Unit == "B/s" ? c.RateWarning * 1048576 : double.PositiveInfinity;
        var category = temperature ? "Temp" : metric.Unit == "B/s" ? "NetKBps" : metric.Kind == "MEM.Load" ? "Mem"
            : metric.Unit == "%" && metric.Source.Contains("Memory", StringComparison.OrdinalIgnoreCase) && metric.DeviceId.Contains("gpu", StringComparison.OrdinalIgnoreCase) ? "Vram" : "Load";
        if ((temperature || metric.Unit is "%" or "B/s") && c.Visual.Thresholds.TryGetValue(category, out var threshold))
        { warning = threshold.Warn; critical = threshold.Crit; }
        return value >= critical ? 2 : value >= warning ? 1 : 0;
    }
    public static double? BarPercent(MonitorMetric metric, MonitorConfig c)
    {
        if (!metric.Valid) return null;
        var maximum = metric.Unit == "%" ? 100 : metric.Unit is "℃" or "°C" ? 130
            : metric.Unit == "B/s" ? c.Visual.Thresholds.GetValueOrDefault("NetKBps")?.Crit ?? c.RateCritical * 1048576 : 0;
        return maximum > 0 ? Math.Clamp(metric.Value!.Value / maximum * 100, 0, 100) : null;
    }
    public static void Import(string json, MonitorConfig destination)
    {
        using var document = JsonDocument.Parse(json); var root = document.RootElement;
        if (root.GetProperty("version").GetInt32() is not (1 or 2 or 3)) throw new InvalidDataException("不兼容的主题版本。");
        var candidate = JsonSerializer.Deserialize<MonitorConfig>(JsonSerializer.Serialize(destination, MonitorJson.Options), MonitorJson.Options)!;
        var color = root.GetProperty("color");
        string ColorValue(string key) { var text = color.GetProperty(key).GetString(); return MonitorStore.ValidColor(text) ? text! : throw new InvalidDataException("主题颜色无效。"); }
        candidate.Background = ColorValue("background"); candidate.Foreground = ColorValue("textPrimary");
        candidate.SafeColor = ColorValue("valueSafe"); candidate.WarningColor = ColorValue("valueWarn"); candidate.CriticalColor = ColorValue("valueCrit");
        if (root.TryGetProperty("font", out var font))
        {
            if (font.TryGetProperty("family", out var family)) candidate.FontFamily = family.GetString()!;
            if (font.TryGetProperty("item", out var item)) candidate.FontSize = item.GetDouble();
            if (font.TryGetProperty("valueFamily", out var vf)) candidate.Visual.ValueFamily = vf.GetString()!;
            if (font.TryGetProperty("title", out var title)) candidate.Visual.TitleSize = title.GetDouble();
            if (font.TryGetProperty("group", out var group)) candidate.Visual.GroupSize = group.GetDouble();
            if (font.TryGetProperty("value", out var value)) candidate.Visual.ValueSize = value.GetDouble();
            if (font.TryGetProperty("bold", out var bold)) candidate.Visual.Bold = bold.GetBoolean();
            if (font.TryGetProperty("scale", out var scale)) candidate.UiScale = scale.GetDouble();
        }
        if (root.TryGetProperty("layout", out var layout))
        {
            if (layout.TryGetProperty("itemGap", out var gap)) candidate.RowSpacing = gap.GetDouble();
            if (layout.TryGetProperty("panelWidth", out var width)) candidate.PanelWidth = width.GetDouble();
            foreach (var field in new[] { ("rowHeight", "RowHeight"), ("padding", "Padding"), ("cornerRadius", "CornerRadius"), ("groupRadius", "GroupRadius"),
                ("groupPadding", "GroupPadding"), ("groupSpacing", "GroupSpacing"), ("groupBottom", "GroupBottom"), ("groupTitleOffset", "GroupTitleOffset") })
                if (layout.TryGetProperty(field.Item1, out var v)) typeof(MonitorVisualStyle).GetProperty(field.Item2)!.SetValue(candidate.Visual, v.GetDouble());
        }
        foreach (var field in new[] { ("textTitle", "TitleColor"), ("textGroup", "GroupColor"), ("groupBackground", "GroupBackground"),
            ("barBackground", "BarBackground"), ("barLow", "BarLow"), ("barMid", "BarMid"), ("barHigh", "BarHigh") })
            if (color.TryGetProperty(field.Item1, out var v)) typeof(MonitorVisualStyle).GetProperty(field.Item2)!.SetValue(candidate.Visual, v.GetString());
        if (root.TryGetProperty("thresholds", out var thresholds))
        {
            candidate.Visual.Thresholds.Clear();
            foreach (var p in thresholds.EnumerateObject())
            {
                var key = new[] { "Load", "Temp", "Vram", "Mem", "NetKBps" }.FirstOrDefault(k => k.Equals(p.Name, StringComparison.OrdinalIgnoreCase)) ?? p.Name;
                candidate.Visual.Thresholds[key] = new((p.Value.TryGetProperty("Warn", out var warn) ? warn : p.Value.GetProperty("warn")).GetDouble(),
                    (p.Value.TryGetProperty("Crit", out var crit) ? crit : p.Value.GetProperty("crit")).GetDouble());
            }
        }
        if (root.TryGetProperty("pet", out var pet))
        {
            candidate.Visual.ShowGroups = pet.GetProperty("showGroups").GetBoolean(); candidate.Visual.ShowBars = pet.GetProperty("showBars").GetBoolean();
            candidate.Visual.SmoothValues = pet.GetProperty("smoothValues").GetBoolean(); candidate.Visual.SmoothMs = pet.GetProperty("smoothMs").GetInt32();
        }
        if (!Valid(candidate) || !double.IsFinite(candidate.UiScale) || candidate.UiScale is < .5 or > 2.5) throw new InvalidDataException("主题参数超出允许范围。");
        destination.Background = candidate.Background; destination.Foreground = candidate.Foreground;
        destination.SafeColor = candidate.SafeColor; destination.WarningColor = candidate.WarningColor; destination.CriticalColor = candidate.CriticalColor;
        destination.FontFamily = candidate.FontFamily; destination.FontSize = candidate.FontSize;
        destination.RowSpacing = candidate.RowSpacing; destination.PanelWidth = candidate.PanelWidth;
        destination.Visual = candidate.Visual; destination.UiScale = candidate.UiScale;
    }
    public static string Export(MonitorConfig c) => JsonSerializer.Serialize(new { version = 3, name = "Pet monitor theme",
        color = new { background = c.Background, textPrimary = c.Foreground, valueSafe = c.SafeColor, valueWarn = c.WarningColor, valueCrit = c.CriticalColor,
            textTitle = c.Visual.TitleColor, textGroup = c.Visual.GroupColor, groupBackground = c.Visual.GroupBackground, barBackground = c.Visual.BarBackground,
            barLow = c.Visual.BarLow, barMid = c.Visual.BarMid, barHigh = c.Visual.BarHigh },
        font = new { family = c.FontFamily, item = c.FontSize, valueFamily = c.Visual.ValueFamily, title = c.Visual.TitleSize,
            group = c.Visual.GroupSize, value = c.Visual.ValueSize, bold = c.Visual.Bold, scale = c.UiScale },
        layout = new { itemGap = c.RowSpacing, panelWidth = c.PanelWidth, rowHeight = c.Visual.RowHeight, padding = c.Visual.Padding,
            cornerRadius = c.Visual.CornerRadius, groupRadius = c.Visual.GroupRadius, groupPadding = c.Visual.GroupPadding, groupSpacing = c.Visual.GroupSpacing,
            groupBottom = c.Visual.GroupBottom, groupTitleOffset = c.Visual.GroupTitleOffset }, thresholds = c.Visual.Thresholds,
        pet = new { showGroups = c.Visual.ShowGroups, showBars = c.Visual.ShowBars, smoothValues = c.Visual.SmoothValues, smoothMs = c.Visual.SmoothMs } }, new JsonSerializerOptions { WriteIndented = true });
}
