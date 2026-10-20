using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
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
        foreach (var arg in new[] { "--session_name", session, "--output_stdout", "--no_console_stats", "--v1_metrics", "--no_track_gpu", "--no_track_input", "--no_track_display", "--set_circular_buffer_size", "4096", "--exclude", "DesktopPet.exe", "--exclude", "DesktopPet.MonitorUi.exe", "--exclude", "explorer.exe" }) start.ArgumentList.Add(arg);
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
        CancellationTokenSource? captureLifetime = null;
        CaptureState? capture = null;
        void StopHelper()
        {
            captureLifetime?.Cancel();
            if (helper is not null)
            {
                try { if (!helper.HasExited) { helper.Kill(); helper.WaitForExit(3000); } }
                catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
                helper.Dispose(); helper = null;
            }
            captureLifetime?.Dispose(); captureLifetime = null;
            capture = null;
        }
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
            var lastMetricsAt = Environment.TickCount64;
            var restartAfterQuietSeconds = 20;
            var recoveries = 0;
            string? recoveryReason = null;
            FpsTraceStatistics? trace = null;
            var lastTraceQuery = 0L;
            var staleRecovery = new FpsStaleTraceRecovery();
            while (!lifetime.IsCancellationRequested)
            {
                if (helper is null)
                {
                    if (!StopOwnedTrace(session)) throw new IOException("FPS trace cleanup failed");
                    helper = Process.Start(CreateHelperStartInfo(executable, session)) ?? throw new IOException();
                    frames = new FpsFrames(); lastMetricsAt = Environment.TickCount64;
                    trace = null; lastTraceQuery = 0;
                    captureLifetime = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                    capture = new();
                    var currentCapture = capture; var captureToken = captureLifetime.Token;
                    var currentHelper = helper; var currentFrames = frames;
                    _ = Task.Run(async () =>
                    {
                        try { while (await currentHelper.StandardOutput.ReadLineAsync(captureToken) is string line) currentFrames.Read(line, DateTimeOffset.UtcNow); }
                        catch (Exception e) when (e is OperationCanceledException or IOException or InvalidOperationException) { }
                        finally { Volatile.Write(ref currentCapture.OutputEnded, 1); }
                    });
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            while (await currentHelper.StandardError.ReadLineAsync(captureToken) is string line)
                                if (line.Contains("ETW events were lost", StringComparison.OrdinalIgnoreCase))
                                    Volatile.Write(ref currentCapture.EventsLostWarning, 1);
                        }
                        catch (Exception e) when (e is OperationCanceledException or IOException or InvalidOperationException) { }
                    });
                }
                if (helper.HasExited || Volatile.Read(ref capture!.OutputEnded) != 0)
                {
                    recoveryReason = helper.HasExited ? $"采集器退出（{helper.ExitCode}）" : "采集输出中断";
                    recoveries++;
                    Console.WriteLine(JsonSerializer.Serialize(new FpsReading([], "recovering", recoveryReason, recoveries), MonitorJson.Options));
                    StopHelper(); StopOwnedTrace(session);
                    await Task.Delay(TimeSpan.FromSeconds(2), lifetime.Token);
                    continue;
                }
                var now = DateTimeOffset.UtcNow;
                var tick = Environment.TickCount64;
                if (tick - lastTraceQuery >= 1000) { trace = FpsTraceHealth.Read(session); lastTraceQuery = tick; }
                var metrics = frames.Metrics(now);
                if (metrics.Count > 0) { restartAfterQuietSeconds = 20; lastMetricsAt = tick; }
                var quietFor = tick - lastMetricsAt;
                var losing = trace is { EventsLost: > 0 } or { RealTimeBuffersLost: > 0 }
                    || Volatile.Read(ref capture!.EventsLostWarning) != 0;
                var stalled = metrics.Count == 0 && quietFor >= 10000;
                var reclaimed = staleRecovery.Observe(tick, stalled && losing,
                    stalled && losing ? FpsTraceHealth.Read("HWiNFO64") : null,
                    FpsTraceHealth.HwInfoRunning, FpsTraceHealth.StopAbandonedHwInfo);
                var detail = trace is null ? recoveryReason : $"ETW 丢失事件：{trace.EventsLost}；实时丢缓冲：{trace.RealTimeBuffersLost}"
                    + (recoveryReason is null ? "" : $"；最近恢复：{recoveryReason}");
                var status = stalled ? losing ? "etw-events-lost" : "no-frames" : "running";
                Console.WriteLine(JsonSerializer.Serialize(new FpsReading(metrics, status, detail, recoveries), MonitorJson.Options));
                if (reclaimed || metrics.Count == 0 && quietFor >= restartAfterQuietSeconds * 1000L)
                {
                    recoveryReason = reclaimed ? "已回收无运行进程且持续丢事件的 HWiNFO64 遗留会话"
                        : losing ? "ETW 丢事件且无有效帧" : "长时间没有有效帧";
                    recoveries++;
                    Console.WriteLine(JsonSerializer.Serialize(new FpsReading([], "recovering", recoveryReason, recoveries), MonitorJson.Options));
                    StopHelper();
                    StopOwnedTrace(session);
                    restartAfterQuietSeconds = Math.Min(120, restartAfterQuietSeconds * 2);
                    await Task.Delay(TimeSpan.FromSeconds(2), lifetime.Token);
                    continue;
                }
                await Task.Delay(250, lifetime.Token);
            }
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or System.ComponentModel.Win32Exception or OperationCanceledException)
        { if (!lifetime.IsCancellationRequested) Console.WriteLine(JsonSerializer.Serialize(new FpsReading([], "unavailable", "采集启动或会话清理失败：" + e.GetType().Name), MonitorJson.Options)); }
        finally
        {
            lifetime.Cancel(); StopHelper(); StopOwnedTrace(session);
        }
    }
    internal static bool StopOwnedTrace(string name) => FpsTraceHealth.StopOwned(name);
    private sealed class CaptureState { public int OutputEnded, EventsLostWarning; }
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
