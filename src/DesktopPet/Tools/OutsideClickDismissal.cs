using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace DesktopPet.Tools;

// Nonactivating pet windows do not reliably receive Deactivated or WPF outside-capture events.
// Observe button-downs only while a transient surface is open; always pass the click onward.
internal sealed class OutsideClickDismissal : IDisposable
{
    [StructLayout(LayoutKind.Sequential)] internal struct ScreenPoint { public int X, Y; }
    private delegate nint MouseHook(int code, nint message, nint data);
    [DllImport("user32.dll", SetLastError = true)] private static extern nint SetWindowsHookEx(int id, MouseHook callback, nint module, uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")] private static extern nint CallNextHookEx(nint hook, int code, nint message, nint data);
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(ScreenPoint point);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint window, uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandle(string? module);
    private readonly Dispatcher _dispatcher;
    private readonly Func<IEnumerable<FrameworkElement>> _surfaces;
    private readonly Action _dismiss;
    private readonly MouseHook _callback;
    private nint _hook;
    private int _generation;
    private bool _disposed;
    internal bool Watching => _hook != 0;

    internal OutsideClickDismissal(Dispatcher dispatcher, Func<IEnumerable<FrameworkElement>> surfaces, Action dismiss)
    { _dispatcher = dispatcher; _surfaces = surfaces; _dismiss = dismiss; _callback = OnMouse; }

    internal void Watch(bool active)
    {
        if (_disposed || active == Watching) return;
        _generation++;
        if (active) _hook = SetWindowsHookEx(14, _callback, GetModuleHandle(null), 0);
        else { UnhookWindowsHookEx(_hook); _hook = 0; }
    }

    private nint OnMouse(int code, nint message, nint data)
    {
        if (code >= 0 && message is 0x0201 or 0x0204 or 0x0207 or 0x020B)
        {
            var point = Marshal.PtrToStructure<ScreenPoint>(data);
            PointerDown(point);
        }
        return CallNextHookEx(_hook, code, message, data);
    }

    internal void PointerDown(ScreenPoint point)
    {
        if (!Watching) return;
        var target = GetAncestor(WindowFromPoint(point), 2);
        foreach (var surface in _surfaces())
        {
            if (surface.IsVisible && PresentationSource.FromVisual(surface) is HwndSource source &&
                GetAncestor(source.Handle, 2) == target) return;
        }
        var generation = _generation;
        _dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            if (Watching && generation == _generation) _dismiss();
        }));
    }
    public void Dispose() { Watch(false); _disposed = true; GC.KeepAlive(_callback); }
}
