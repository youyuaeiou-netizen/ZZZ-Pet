namespace DesktopPet.Monitoring;

public sealed class MonitorVisualStyle
{
    public double RowHeight { get; set; }
    public double Padding { get; set; } = 22;
    public double CornerRadius { get; set; } = 16;
    public double GroupRadius { get; set; }
    public double GroupPadding { get; set; } = 8;
    public double GroupSpacing { get; set; } = 8;
    public double GroupBottom { get; set; }
    public double GroupTitleOffset { get; set; } = 6;
    public string ValueFamily { get; set; } = "Microsoft YaHei UI";
    public double TitleSize { get; set; } = 16;
    public double GroupSize { get; set; } = 13;
    public double ValueSize { get; set; } = 13;
    public bool Bold { get; set; }
    public string TitleColor { get; set; } = "#55416B";
    public string GroupColor { get; set; } = "#55416B";
    public string GroupBackground { get; set; } = "#FFFBD8";
    public string BarBackground { get; set; } = "#E9DFF1";
    public string BarLow { get; set; } = "#26735A";
    public string BarMid { get; set; } = "#A46B00";
    public string BarHigh { get; set; } = "#B3263C";
    public bool ShowGroups { get; set; }
    public bool ShowBars { get; set; }
    public bool SmoothValues { get; set; }
    public int SmoothMs { get; set; } = 350;
    public Dictionary<string, MonitorColorThreshold> Thresholds { get; set; } = [];
    public bool Valid() => new[] { RowHeight, Padding, CornerRadius, GroupRadius, GroupPadding, GroupSpacing, GroupBottom, GroupTitleOffset }
        .All(v => double.IsFinite(v) && v is >= 0 and <= 120) && ValueFamily is { Length: > 0 and <= 100 } && !string.IsNullOrWhiteSpace(ValueFamily)
        && new[] { TitleSize, GroupSize, ValueSize }.All(v => double.IsFinite(v) && v is >= 8 and <= 32)
        && new[] { TitleColor, GroupColor, GroupBackground, BarBackground, BarLow, BarMid, BarHigh }.All(MonitorStore.ValidColor)
        && SmoothMs is >= 50 and <= 2000 && Thresholds is not null && Thresholds.Count <= 5
        && Thresholds.All(p => p.Key is "Load" or "Temp" or "Vram" or "Mem" or "NetKBps" && p.Value is not null && p.Value.Valid());
}
public sealed record MonitorColorThreshold(double Warn, double Crit)
{
    public bool Valid() => double.IsFinite(Warn) && double.IsFinite(Crit) && Warn >= 0 && Crit > Warn && Crit <= 1e12;
}
