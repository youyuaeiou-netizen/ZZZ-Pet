namespace DesktopPet.Monitoring;

public readonly record struct MonitorRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
}
public static class MonitorLayout
{
    public static MonitorRect Clamp(MonitorRect window, MonitorRect work)
    {
        var width = Math.Min(window.Width, work.Width); var height = Math.Min(window.Height, work.Height);
        return new(Math.Clamp(window.X, work.X, work.Right - width), Math.Clamp(window.Y, work.Y, work.Bottom - height), width, height);
    }
    public static MonitorRect? Collapse(MonitorRect window, MonitorRect work)
    {
        if (window.Width >= work.Width || window.Height >= work.Height) return null;
        if (Math.Abs(window.X - work.X) <= 16) return window with { X = work.X - window.Width + 8 };
        if (Math.Abs(window.Right - work.Right) <= 16) return window with { X = work.Right - 8 };
        if (Math.Abs(window.Y - work.Y) <= 16) return window with { Y = work.Y - window.Height + 8 };
        if (Math.Abs(window.Bottom - work.Bottom) <= 16) return window with { Y = work.Bottom - 8 };
        return null;
    }
    public static (int Start, int End)? FindGap(int start, int end, int required, IEnumerable<(int Start, int End)> occupied)
    {
        var cursor = start;
        foreach (var rect in occupied.Where(r => r.End > start && r.Start < end).OrderBy(r => r.Start))
        {
            if (rect.Start - cursor >= required) return (cursor, Math.Min(rect.Start, end));
            cursor = Math.Max(cursor, rect.End);
        }
        return end - cursor >= required ? (cursor, end) : null;
    }
}
