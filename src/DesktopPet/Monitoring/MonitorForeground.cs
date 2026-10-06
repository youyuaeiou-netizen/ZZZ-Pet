using System.Runtime.InteropServices;
using System.Diagnostics;
using Microsoft.Win32.SafeHandles;

namespace DesktopPet;

// Read-only window/process discovery lives in the ordinary Pet process. No privilege or injection.
internal static class MonitorForeground
{
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint pid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeFileHandle CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "Process32FirstW")]
    private static extern bool Process32First(SafeFileHandle snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "Process32NextW")]
    private static extern bool Process32Next(SafeFileHandle snapshot, ref ProcessEntry entry);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        public uint Size, Usage, Id;
        public nuint Heap;
        public uint Module, Threads, Parent;
        public int Priority;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string File;
    }
    public static IReadOnlyList<int> ProcessIds()
    {
        var pid = ForegroundProcessId();
        return pid == 0 || pid == Environment.ProcessId ? [] : Family(pid, Processes());
    }
    internal static int ForegroundProcessId()
    {
        GetWindowThreadProcessId(GetForegroundWindow(), out var pid);
        return pid <= int.MaxValue ? (int)pid : 0;
    }
    internal static Dictionary<int, (int Parent, string File)> Processes()
    {
        var processes = new Dictionary<int, (int Parent, string File)>();
        using var snapshot = CreateToolhelp32Snapshot(2, 0);
        if (snapshot.IsInvalid) return processes;
        var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>(), File = "" };
        if (Process32First(snapshot, ref entry)) do
        {
            if (entry.Id <= int.MaxValue && entry.Parent <= int.MaxValue)
                processes[(int)entry.Id] = ((int)entry.Parent, entry.File);
        } while (Process32Next(snapshot, ref entry));
        return processes;
    }
    internal static IReadOnlyList<int> Family(int pid, IReadOnlyDictionary<int, (int Parent, string File)> processes)
    {
        var result = new List<int> { pid };
        if (!processes.TryGetValue(pid, out var root)) return result;
        // Chromium can own a foreground window in a child of its browser process.
        var ancestor = pid;
        var visited = new HashSet<int> { pid };
        while (processes.TryGetValue(ancestor, out var current) && processes.TryGetValue(current.Parent, out var parent)
            && parent.File.Equals(root.File, StringComparison.OrdinalIgnoreCase) && visited.Add(current.Parent)) ancestor = current.Parent;
        foreach (var (child, process) in processes)
        {
            if (child == pid || !process.File.Equals(root.File, StringComparison.OrdinalIgnoreCase)) continue;
            var cursor = child; visited.Clear();
            while (processes.TryGetValue(cursor, out var node) && visited.Add(cursor))
            {
                if (cursor == ancestor) { result.Add(child); break; }
                cursor = node.Parent;
            }
        }
        return result;
    }
}

// Inspecting Pet is a temporary overlay on the application being monitored.
// Keep its identity, never a frozen FPS number, while Pet owns the foreground.
internal sealed class MonitorForegroundTracker
{
    private (int Pid, long Started)? _last;
    private int _foreground = -1;
    private long _checkedAt;
    private IReadOnlyList<int> _targets = [];
    internal IReadOnlyList<int> ProcessIds()
    {
        var pid = MonitorForeground.ForegroundProcessId();
        if (pid == _foreground && Environment.TickCount64 - _checkedAt < 1000) return _targets;
        _foreground = pid; _checkedAt = Environment.TickCount64;
        return _targets = Select(pid, Environment.ProcessId, MonitorForeground.Processes(), Started);
    }
    internal IReadOnlyList<int> Select(int foreground, int pet, IReadOnlyDictionary<int, (int Parent, string File)> processes, Func<int, long?> started)
    {
        if (processes.TryGetValue(foreground, out var shell) &&
            (shell.File.Equals("explorer.exe", StringComparison.OrdinalIgnoreCase) || shell.File.Equals("dwm.exe", StringComparison.OrdinalIgnoreCase)))
        { _last = null; return []; }
        // Pet clicks and transient no-foreground states preserve the application being inspected.
        if (foreground == pet || foreground <= 0 || !processes.TryGetValue(foreground, out var candidate) ||
            candidate.File.StartsWith("DesktopPet", StringComparison.OrdinalIgnoreCase) ||
            candidate.File.Equals("PresentMon.exe", StringComparison.OrdinalIgnoreCase))
        {
            if (_last is not { } previous || !processes.ContainsKey(previous.Pid) || started(previous.Pid) != previous.Started)
            { _last = null; return []; }
            return MonitorForeground.Family(previous.Pid, processes);
        }
        _last = null;
        if (foreground <= 0 || !processes.ContainsKey(foreground)) return [];
        if (started(foreground) is long stamp) _last = (foreground, stamp);
        return MonitorForeground.Family(foreground, processes);
    }
    private static long? Started(int pid)
    {
        try { using var process = Process.GetProcessById(pid); return process.StartTime.ToUniversalTime().Ticks; }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return null; }
    }
}
