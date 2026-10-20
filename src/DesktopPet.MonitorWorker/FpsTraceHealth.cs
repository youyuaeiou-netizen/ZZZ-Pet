using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace DesktopPet.MonitorWorker;

internal sealed record FpsTraceStatistics(uint EventsLost, uint RealTimeBuffersLost,
    uint Buffers, uint FreeBuffers, nint LoggerThread, uint LogFileMode)
{
    public bool IdleAndLosing => EventsLost > 0 && RealTimeBuffersLost == 0 && Buffers > 0 && FreeBuffers == Buffers
        && (LogFileMode & 0x100) != 0 && (LogFileMode & 0xF) == 0;
}

internal static class FpsTraceHealth
{
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ControlTrace(ulong handle, string name, nint properties, uint command);

    internal static FpsTraceStatistics? Read(string name)
    {
        if (!Owned(name) && name != "HWiNFO64") return null;
        var memory = Allocate(name);
        try
        {
            if (ControlTrace(0, name, memory, 0) != 0) return null;
            return new((uint)Marshal.ReadInt32(memory, 88), (uint)Marshal.ReadInt32(memory, 100),
                (uint)Marshal.ReadInt32(memory, 80), (uint)Marshal.ReadInt32(memory, 84), Marshal.ReadIntPtr(memory, 104),
                (uint)Marshal.ReadInt32(memory, 64));
        }
        finally { Marshal.FreeHGlobal(memory); }
    }

    internal static bool StopOwned(string name) => Owned(name) && Stop(name);
    internal static bool Owned(string name) => name.StartsWith("DesktopPet.FPS.", StringComparison.Ordinal)
        && Guid.TryParseExact(name[15..], "N", out _);

    internal static bool? HwInfoRunning()
    {
        Process[]? processes = null;
        try
        {
            processes = Process.GetProcesses();
            return processes.Any(p => p.ProcessName.StartsWith("HWiNFO", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        { return null; }
        finally { if (processes is not null) foreach (var p in processes) p.Dispose(); }
    }

    internal static bool StopAbandonedHwInfo(FpsTraceStatistics expected)
    {
        // A known failure from this installation: an exited HWiNFO leaves its
        // real-time trace losing events. Never stop it when ownership is uncertain.
        var current = Read("HWiNFO64");
        return current is not null && current.LoggerThread == expected.LoggerThread && current.IdleAndLosing
            && current.EventsLost >= expected.EventsLost && HwInfoRunning() == false && Stop("HWiNFO64");
    }

    private static bool Stop(string name)
    {
        var memory = Allocate(name);
        try { return ControlTrace(0, name, memory, 1) is 0 or 4201; }
        finally { Marshal.FreeHGlobal(memory); }
    }

    private static nint Allocate(string name)
    {
        // EVENT_TRACE_PROPERTIES is 120 bytes on x64; allow returned log-file names.
        var memory = Marshal.AllocHGlobal(8192);
        Marshal.Copy(new byte[8192], 0, memory, 8192);
        Marshal.WriteInt32(memory, 8192);
        Marshal.WriteInt32(memory, 116, 120);
        Marshal.WriteInt32(memory, 112, 4096);
        var label = Encoding.Unicode.GetBytes(name + "\0");
        Marshal.Copy(label, 0, memory + 120, label.Length);
        return memory;
    }
}

internal sealed class FpsStaleTraceRecovery
{
    private FpsTraceStatistics? _previous;
    private long _observedAt, _retryAt;

    internal bool Observe(long now, bool ownTraceLosingWithoutFrames, FpsTraceStatistics? candidate,
        Func<bool?> hwInfoRunning, Func<FpsTraceStatistics, bool> stop)
    {
        if (!ownTraceLosingWithoutFrames || candidate is null || !candidate.IdleAndLosing || hwInfoRunning() != false)
        { _previous = null; return false; }
        if (_previous is null || _previous.LoggerThread != candidate.LoggerThread || candidate.EventsLost < _previous.EventsLost)
        { _previous = candidate; _observedAt = now; return false; }
        if (now - _observedAt < 2000 || candidate.EventsLost <= _previous.EventsLost || now < _retryAt) return false;
        _previous = null; _retryAt = now + 30000;
        // Recheck the process after the observation interval, immediately before cleanup.
        return hwInfoRunning() == false && stop(candidate);
    }
}
