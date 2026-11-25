using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using DesktopPet.MonitorWorker;

internal static class LiveCapture
{
    [StructLayout(LayoutKind.Sequential)] private struct BasicInfo
    { public nint Reserved, Peb, Reserved2, Reserved3, ProcessId, ParentId; }
    [DllImport("ntdll.dll")] private static extern int NtQueryInformationProcess(nint process, int type, out BasicInfo info, int size, out int length);
    [DllImport("ntdll.dll")] private static extern int NtSuspendProcess(nint process);

    internal static async Task Run(string output, int targetPid, int seconds)
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)) throw new Exception("Live FPS checks require elevation");
        Directory.CreateDirectory(output);
        using var manager = new FpsManager();
        using var samples = new StreamWriter(Path.Combine(output, "samples.jsonl"));
        var clock = Stopwatch.StartNew(); var count = 0; var valid = 0; var lastValid = 0d; var longestGap = 0d;
        var faults = new List<object>(); double? pendingAt = null; string? pendingFault = null;
        var nextFault = 0;
        Process Agent() => (Process)typeof(FpsManager).GetField("_agent", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(manager)!;
        while (clock.Elapsed.TotalSeconds < seconds)
        {
            manager.Start(); var now = clock.Elapsed.TotalSeconds;
            var reading = manager.Reading;
            var metric = reading.Metrics.FirstOrDefault(m => m.Id == "FPS." + targetPid && m.Valid
                && m.SampledAt is DateTimeOffset at && DateTimeOffset.UtcNow - at <= TimeSpan.FromSeconds(3));
            count++;
            if (metric is not null)
            {
                valid++; longestGap = Math.Max(longestGap, now - lastValid); lastValid = now;
                if (pendingAt is double start)
                { faults.Add(new { Fault = pendingFault, RecoverySeconds = now - start }); pendingAt = null; }
            }
            await samples.WriteLineAsync(JsonSerializer.Serialize(new { Seconds = now, reading.Status, Value = metric?.Value, reading.Diagnostic, reading.Recoveries }));
            await samples.FlushAsync();
            var schedule = new[] { 25, 65, 105 };
            if (nextFault < schedule.Length && now >= schedule[nextFault] && metric is not null)
            {
                var agent = Agent();
                if (nextFault == 0)
                {
                    using var helper = Process.GetProcessesByName("PresentMon").FirstOrDefault(p =>
                    {
                        try { return NtQueryInformationProcess(p.Handle, 0, out var info, Marshal.SizeOf<BasicInfo>(), out _) == 0 && info.ParentId == agent.Id; }
                        catch (System.ComponentModel.Win32Exception) { return false; }
                    }) ?? throw new Exception("Owned helper not found");
                    helper.Kill(); pendingFault = "helper-exit";
                }
                else if (nextFault == 1) { agent.Kill(); pendingFault = "agent-exit"; }
                else { if (NtSuspendProcess(agent.Handle) != 0) throw new Exception("Cannot suspend owned agent"); pendingFault = "agent-hang"; }
                // Existing metrics remain fresh briefly; measure recovery from an actual missing-frame sample.
                pendingAt = now; nextFault++;
                await Task.Delay(3500);
                manager.Start();
            }
            await Task.Delay(250);
        }
        longestGap = Math.Max(longestGap, clock.Elapsed.TotalSeconds - lastValid);
        var recoveryTimes = faults.Select(f => JsonSerializer.SerializeToElement(f).GetProperty("RecoverySeconds").GetDouble()).ToArray();
        var passed = valid > count * .85 && nextFault == 3 && faults.Count == 3 && recoveryTimes.All(t => t < 18) && longestGap < 18;
        // Diagnostic counters must survive replacement of the supervisor too.
        var finalRecoveries = manager.Reading.Recoveries;
        passed &= finalRecoveries >= 3;
        File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new
        { Seconds = clock.Elapsed.TotalSeconds, Samples = count, ValidSamples = valid, LongestGapSeconds = longestGap, Faults = faults, FinalRecoveries = finalRecoveries, Passed = passed }, new JsonSerializerOptions { WriteIndented = true }));
        if (!passed) throw new Exception("Live capture did not satisfy reliability criteria");
        Console.WriteLine("PASS real frames, helper exit, agent exit, agent hang and sustained capture");
    }
}
