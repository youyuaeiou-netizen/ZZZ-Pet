using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DesktopPet;

internal static class MonitorMemory
{
    [DllImport("psapi.dll", SetLastError = true)] private static extern bool EmptyWorkingSet(nint process);
    public static bool TrimOwn()
    {
        GC.Collect(); using var process = Process.GetCurrentProcess(); return EmptyWorkingSet(process.Handle);
    }
}
