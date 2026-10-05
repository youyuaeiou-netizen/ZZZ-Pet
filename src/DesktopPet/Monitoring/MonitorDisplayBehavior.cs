using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using DesktopPet.Monitoring;
using Forms = System.Windows.Forms;

namespace DesktopPet;

// Changes only this Pet-owned HWND. No Explorer child, taskbar space, global hook or system setting is changed.
internal sealed class MonitorDisplayBehavior(Window window, MonitorService service) : IDisposable
{
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hwnd, out NativeRect rect);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] internal static extern int GetWindowLong(nint hwnd, int index);
    [DllImport("user32.dll")] private static extern int SetWindowLong(nint hwnd, int index, int value);
    private NativeRect? _expanded;
    private DateTimeOffset? _leftAt;
    private bool _through;
    public bool Collapsed => _expanded.HasValue;
    public void Tick(DateTimeOffset now)
    {
        var hwnd = new WindowInteropHelper(window).Handle; if (hwnd == 0 || !window.IsVisible) return;
        if (_through != service.Config.ClickThrough)
        {
            var style = GetWindowLong(hwnd, -20);
            SetWindowLong(hwnd, -20, service.Config.ClickThrough ? style | 0x20 : style & ~0x20); _through = service.Config.ClickThrough;
        }
        if (!service.Config.AutoHide) { Expand(hwnd); return; }
        if (!GetWindowRect(hwnd, out var rect) || !GetCursorPos(out var cursor)) return;
        var within = cursor.X >= rect.Left - 3 && cursor.X < rect.Right + 3 && cursor.Y >= rect.Top - 3 && cursor.Y < rect.Bottom + 3;
        if (within) { _leftAt = null; Expand(hwnd); return; }
        if (_expanded.HasValue) return;
        _leftAt ??= now; if (now - _leftAt < TimeSpan.FromSeconds(2)) return;
        var work = Forms.Screen.FromHandle(hwnd).WorkingArea;
        var width = rect.Right - rect.Left; var height = rect.Bottom - rect.Top;
        if (width >= work.Width || height >= work.Height) return;
        var collapsed = MonitorLayout.Collapse(new(rect.Left, rect.Top, width, height), new(work.Left, work.Top, work.Width, work.Height));
        if (collapsed is not MonitorRect target) return;
        _expanded = rect; SetWindowPos(hwnd, 0, target.X, target.Y, width, height, 0x14);
    }
    private void Expand(nint hwnd)
    {
        if (_expanded is not NativeRect rect) return;
        _expanded = null; _leftAt = null;
        SetWindowPos(hwnd, 0, rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top, 0x14);
    }
    public void Place()
    {
        var hwnd = new WindowInteropHelper(window).Handle; if (hwnd == 0) return;
        var screen = Forms.Screen.FromHandle(hwnd); var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(window);
        if (service.Config.DisplayLeft is double left && service.Config.DisplayTop is double top)
        {
            var proposed = new System.Drawing.Point((int)left, (int)top);
            screen = Forms.Screen.FromPoint(proposed);
        }
        var work = screen.WorkingArea; GetWindowRect(hwnd, out var rect);
        var width = Math.Min(rect.Right - rect.Left, work.Width); var height = Math.Min(rect.Bottom - rect.Top, work.Height);
        var x = Math.Clamp((int)(service.Config.DisplayLeft ?? work.Right - width), work.Left, work.Right - width);
        var y = Math.Clamp((int)(service.Config.DisplayTop ?? work.Top), work.Top, work.Bottom - height);
        SetWindowPos(hwnd, 0, x, y, width, height, 0x14);
    }
    public void SavePosition()
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (_expanded.HasValue || hwnd == 0 || !GetWindowRect(hwnd, out var rect)) return;
        Clamp(); GetWindowRect(hwnd, out rect);
        service.Config.DisplayLeft = rect.Left; service.Config.DisplayTop = rect.Top; service.Save();
    }
    public void Clamp()
    {
        if (!service.Config.ClampToScreen || _expanded.HasValue) return;
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == 0 || !GetWindowRect(hwnd, out var rect)) return;
        var work = Forms.Screen.FromHandle(hwnd).WorkingArea;
        var target = MonitorLayout.Clamp(new(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top), new(work.Left, work.Top, work.Width, work.Height));
        if (target.X != rect.Left || target.Y != rect.Top || target.Width != rect.Right - rect.Left || target.Height != rect.Bottom - rect.Top)
            SetWindowPos(hwnd, 0, target.X, target.Y, target.Width, target.Height, 0x14);
    }
    public void Dispose()
    {
        var hwnd = new WindowInteropHelper(window).Handle; if (hwnd != 0) { Expand(hwnd); if (_through) SetWindowLong(hwnd, -20, GetWindowLong(hwnd, -20) & ~0x20); }
    }
}
