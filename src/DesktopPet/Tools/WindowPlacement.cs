using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace DesktopPet.Tools;

internal static class WindowPlacement
{
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public Rect Monitor, Work; public uint Flags; }
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint window, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out Rect rect);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int cx, int cy, uint flags);

    public static bool Near(Window window, Window pet, bool lower = false, Window? avoid = null)
    {
        var handle = new WindowInteropHelper(window).Handle;
        var petHandle = new WindowInteropHelper(pet).Handle;
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(MonitorFromWindow(petHandle, 2), ref info) ||
            !GetWindowRect(petHandle, out var anchor) || !GetWindowRect(handle, out var rect)) return true;
        var width = Math.Min(rect.Right - rect.Left, info.Work.Right - info.Work.Left);
        var height = Math.Min(rect.Bottom - rect.Top, info.Work.Bottom - info.Work.Top);
        var x = anchor.Right + 12;
        if (x + width > info.Work.Right) x = anchor.Left - width - 12;
        x = Math.Clamp(x, info.Work.Left, info.Work.Right - width);
        var y = Math.Clamp(lower ? anchor.Bottom - height : anchor.Top, info.Work.Top, info.Work.Bottom - height);
        if (lower && avoid is not null && GetWindowRect(new WindowInteropHelper(avoid).Handle, out var other))
        {
            // Prefer the opposite side; when that side is unavailable, stack below the bubble.
            var opposite = other.Left >= anchor.Right ? anchor.Left-width-12 : anchor.Right+12;
            if (opposite >= info.Work.Left && opposite+width <= info.Work.Right) x=opposite;
            else
            {
                x=Math.Clamp(other.Left,info.Work.Left,info.Work.Right-width);
                y=other.Bottom+8;
                if(y+height>info.Work.Bottom) y=other.Top-height-8;
                y=Math.Clamp(y,info.Work.Top,info.Work.Bottom-height);
            }
        }
        // Physical monitor coordinates avoid mixing DIP coordinates between different-DPI monitors.
        SetWindowPos(handle, 0, x, y, width, height, 0x0014); // NOZORDER | NOACTIVATE
        return x >= anchor.Right;
    }
}
