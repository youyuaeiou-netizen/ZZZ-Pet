using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using DesktopPet.Monitoring;

namespace DesktopPet.MonitorWorker;

internal static class FpsAgent
{
    internal const string HelperSha256 = "b2a706bc6ad475749e3b7e3409263aa1e6906d45bdcf993f6dbc0f660188f1af";
    internal static ProcessStartInfo CreateHelperStartInfo(string executable, string session)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "--session_name", session, "--output_stdout", "--no_console_stats", "--v1_metrics", "--no_track_gpu", "--no_track_input", "--exclude", "DesktopPet.exe", "--exclude", "DesktopPet.MonitorUi.exe", "--exclude", "explorer.exe" }) start.ArgumentList.Add(arg);
        return start;
    }
    public static async Task Run(string[] args)
    {
        if (args.Length != 4 || !int.TryParse(args[1], out var ownerId) || !long.TryParse(args[2], out var ownerStart) ||
            !args[3].StartsWith("DesktopPet.FPS.", StringComparison.Ordinal) || !Guid.TryParseExact(args[3][15..], "N", out _)) return;
        using var owner = Process.GetProcessById(ownerId);
        if (owner.StartTime.ToUniversalTime().Ticks != ownerStart) return;
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
        { Console.WriteLine(JsonSerializer.Serialize(new FpsReading([], "requires-elevation"), MonitorJson.Options)); return; }
        var executable = Path.Combine(AppContext.BaseDirectory, "fps", "PresentMon.exe");
        if (!File.Exists(executable) || Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(executable))).ToLowerInvariant() != HelperSha256)
        { Console.WriteLine(JsonSerializer.Serialize(new FpsReading([], "helper-integrity-error"), MonitorJson.Options)); return; }
        using var lifetime = new CancellationTokenSource(); using var job = new OwnedJob();
        Process? helper = null;
        var session = args[3];
        try
        {
            // No -stop_existing_session: this helper owns a fresh, random session name.
            // Join before launch: children inherit this non-breakaway job, avoiding
            // a helper launch/assignment gap if this supervisor is terminated.
            using (var current = Process.GetCurrentProcess()) job.Assign(current);
            _ = Task.Run(async () => { try { while (!lifetime.IsCancellationRequested) { if (owner.HasExited) { lifetime.Cancel(); return; } await Task.Delay(300, lifetime.Token); } } catch (OperationCanceledException) { } });
            _ = Task.Run(async () => { await Console.In.ReadLineAsync(); lifetime.Cancel(); });
            var frames = new FpsFrames();
            var lastMetricsAt = DateTimeOffset.UtcNow;
            var receivedMetrics = false;
            var emptyRecoveryUsed = false;
            while (!lifetime.IsCancellationRequested)
            {
                if (helper is null)
                {
                    StopOwnedTrace(session);
                    helper = Process.Start(CreateHelperStartInfo(executable, session)) ?? throw new IOException();
                    frames = new FpsFrames(); lastMetricsAt = DateTimeOffset.UtcNow; receivedMetrics = false;
                    var currentHelper = helper; var currentFrames = frames;
                    _ = Task.Run(async () =>
                    { try { while (await currentHelper.StandardOutput.ReadLineAsync(lifetime.Token) is string line) currentFrames.Read(line, DateTimeOffset.UtcNow); } catch (OperationCanceledException) { } });
                    _ = Task.Run(async () => { try { while (await currentHelper.StandardError.ReadLineAsync(lifetime.Token) is not null) { } } catch (OperationCanceledException) { } });
                }
                if (helper.HasExited) throw new IOException("PresentMon stopped unexpectedly");
                var now = DateTimeOffset.UtcNow;
                var metrics = frames.Metrics(now);
                if (metrics.Count > 0) { receivedMetrics = true; emptyRecoveryUsed = false; lastMetricsAt = now; }
                var quietFor = now - lastMetricsAt;
                var status = metrics.Count == 0 && quietFor >= TimeSpan.FromSeconds(10) ? "no-frames" : "running";
                Console.WriteLine(JsonSerializer.Serialize(new FpsReading(metrics, status), MonitorJson.Options));
                if (metrics.Count == 0 && quietFor >= TimeSpan.FromSeconds(20) && (receivedMetrics || !emptyRecoveryUsed))
                {
                    Console.WriteLine(JsonSerializer.Serialize(new FpsReading([], "recovering"), MonitorJson.Options));
                    try { helper.Kill(); helper.WaitForExit(3000); }
                    catch (InvalidOperationException) { }
                    helper.Dispose(); helper = null;
                    StopOwnedTrace(session);
                    emptyRecoveryUsed = true;
                    await Task.Delay(TimeSpan.FromSeconds(2), lifetime.Token);
                    continue;
                }
                await Task.Delay(250, lifetime.Token);
            }
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or System.ComponentModel.Win32Exception or OperationCanceledException)
        { if (!lifetime.IsCancellationRequested) Console.WriteLine(JsonSerializer.Serialize(new FpsReading([], "unavailable"), MonitorJson.Options)); }
        finally
        {
            lifetime.Cancel(); StopOwnedTrace(session);
            if (helper is not null) { try { if (!helper.HasExited) helper.Kill(); } catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { } helper.Dispose(); }
        }
    }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)] private static extern uint ControlTrace(ulong handle, string name, nint properties, uint command);
    internal static void StopOwnedTrace(string name)
    {
        if (!name.StartsWith("DesktopPet.FPS.", StringComparison.Ordinal) || !Guid.TryParseExact(name[15..], "N", out _)) return;
        // EVENT_TRACE_PROPERTIES is 120 bytes on Windows x64. Only this agent's generated session name is accepted.
        var label = Encoding.Unicode.GetBytes(name + "\0"); var memory = Marshal.AllocHGlobal(120 + label.Length);
        try
        {
            Marshal.Copy(new byte[120 + label.Length], 0, memory, 120 + label.Length);
            Marshal.WriteInt32(memory, 120 + label.Length); Marshal.WriteInt32(memory, 116, 120); Marshal.Copy(label, 0, memory + 120, label.Length);
            ControlTrace(0, name, memory, 1);
        }
        finally { Marshal.FreeHGlobal(memory); }
    }
    private sealed class OwnedJob : IDisposable
    {
        [StructLayout(LayoutKind.Sequential)] private struct BasicLimit { public long ProcessTime, JobTime; public uint Flags; public nuint Minimum, Maximum; public uint ActiveProcesses; public nuint Affinity; public uint Priority, Scheduling; }
        [StructLayout(LayoutKind.Sequential)] private struct IoCounters { public ulong ReadOps, WriteOps, OtherOps, ReadBytes, WriteBytes, OtherBytes; }
        [StructLayout(LayoutKind.Sequential)] private struct Limit { public BasicLimit Basic; public IoCounters Io; public nuint ProcessMemory, JobMemory, PeakProcess, PeakJob; }
        [DllImport("kernel32.dll")] private static extern SafeFileHandle CreateJobObject(nint attributes, string? name);
        [DllImport("kernel32.dll")] private static extern bool SetInformationJobObject(SafeFileHandle job, int type, ref Limit limit, uint size);
        [DllImport("kernel32.dll")] private static extern bool AssignProcessToJobObject(SafeFileHandle job, nint process);
        private readonly SafeFileHandle _job = CreateJobObject(0, null);
        public OwnedJob()
        { var limits = new Limit { Basic = new() { Flags = 0x2000 } }; if (_job.IsInvalid || !SetInformationJobObject(_job, 9, ref limits, (uint)Marshal.SizeOf<Limit>())) throw new IOException("FPS job unavailable"); }
        public void Assign(Process process) { if (!AssignProcessToJobObject(_job, process.Handle)) throw new IOException("FPS job assignment failed"); }
        public void Dispose() => _job.Dispose();
    }
}
