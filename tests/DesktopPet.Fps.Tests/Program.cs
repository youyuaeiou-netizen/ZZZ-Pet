using System.Diagnostics;
using DesktopPet.MonitorWorker;

if (args.FirstOrDefault() == "--fps-agent") { await FpsAgent.Run(args); return; }
if (args.FirstOrDefault() == "--live") { await LiveCapture.Run(args[1], int.Parse(args[2]), int.Parse(args[3])); return; }
if (args.FirstOrDefault() == "--owner")
{
    using var owner = new FpsSessionLease(args[1], args[2], _ => true);
    Console.WriteLine("ready"); Console.Out.Flush();
    await Task.Delay(Timeout.Infinite); return;
}
var directory = Path.Combine(Path.GetTempPath(), "DesktopPet.Fps.Tests", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
var stopped = new List<string>();
bool Stop(string session) { stopped.Add(session); return true; }
void Check(bool result, string message) { if (!result) throw new Exception(message); Console.WriteLine("PASS " + message); }
string Session() => "DesktopPet.FPS." + Guid.NewGuid().ToString("N");
var recovery = new FpsStaleTraceRecovery();
var candidate = new FpsTraceStatistics(10, 0, 256, 256, 42, 0x400100);
var cleanupCalls = 0;
bool Clean(FpsTraceStatistics _) { cleanupCalls++; return true; }
Check(!recovery.Observe(0, true, candidate, () => false, Clean), "one abandoned trace observation cannot trigger cleanup");
Check(!recovery.Observe(3000, true, candidate with { EventsLost = 20 }, () => true, Clean) && cleanupCalls == 0, "running HWiNFO is never stopped even when its trace loses events");
Check(!recovery.Observe(6000, true, candidate, () => null, Clean), "uncertain process detection prevents cleanup");
Check(!recovery.Observe(9000, false, candidate, () => false, Clean), "healthy Pet capture never cleans another trace");
Check(!recovery.Observe(12000, true, candidate with { FreeBuffers = 255 }, () => false, Clean), "a busy external trace is protected");
Check(!recovery.Observe(15000, true, candidate with { RealTimeBuffersLost = 1 }, () => false, Clean), "consumer backlog cannot be mistaken for the known abandoned trace");
Check(!recovery.Observe(16000, true, candidate with { LogFileMode = 1 }, () => false, Clean), "file traces are never treated as abandoned real-time capture");
Check(!recovery.Observe(18000, true, candidate, () => false, Clean)
    && !recovery.Observe(21000, true, candidate, () => false, Clean), "old loss counts without new losses do not trigger cleanup");
Check(recovery.Observe(24000, true, candidate with { EventsLost = 11 }, () => false, Clean) && cleanupCalls == 1,
    "confirmed idle trace with increasing loss and no HWiNFO process is reclaimed");
var changing = new FpsStaleTraceRecovery(); var probes = 0;
changing.Observe(0, true, candidate, () => false, Clean);
Check(!changing.Observe(3000, true, candidate with { EventsLost = 11 }, () => ++probes == 1 ? false : true, Clean)
    && cleanupCalls == 1, "HWiNFO starting before cleanup prevents stopping its session");
var replaced = new FpsStaleTraceRecovery(); replaced.Observe(0, true, candidate, () => false, Clean);
Check(!replaced.Observe(3000, true, candidate with { EventsLost = 11, LoggerThread = 43 }, () => false, Clean),
    "a replaced trace requires a new observation interval");
Check(!FpsTraceHealth.StopOwned("HWiNFO64") && !FpsTraceHealth.StopOwned("DesktopPet.FPS.invalid"),
    "normal trace cleanup rejects external and invalid session names");
try
{
    var active = Session();
    using (var owner = new FpsSessionLease(directory, active, Stop))
    {
        FpsSessionLease.Reclaim(directory, Stop);
        Check(stopped.Count == 0, "live capture is protected by its owner lock");
        var abandoned = Session(); var path = Path.Combine(directory, abandoned + ".lease");
        File.WriteAllText(path, ""); File.WriteAllText(Path.Combine(directory, "DesktopPet.FPS.invalid.lease"), "");
        FpsSessionLease.Reclaim(directory, _ => false);
        Check(File.Exists(path), "failed trace cleanup retains the recovery record");
        FpsSessionLease.Reclaim(directory, Stop);
        Check(stopped.SequenceEqual([abandoned]) && !File.Exists(path), "only abandoned valid sessions are reclaimed");
    }
    Check(stopped.Contains(active) && !File.Exists(Path.Combine(directory, active + ".lease")), "normal shutdown removes its own trace and record");
    var crashed = Session();
    var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, RedirectStandardOutput = true };
    if (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase)) start.ArgumentList.Add(typeof(FpsSessionLease).Assembly.Location);
    foreach (var arg in new[] { "--owner", directory, crashed }) start.ArgumentList.Add(arg);
    using var process = Process.Start(start)!;
    Check(await process.StandardOutput.ReadLineAsync() == "ready", "separate capture owner acquires its record");
    FpsSessionLease.Reclaim(directory, Stop);
    Check(!stopped.Contains(crashed), "another live process is not interrupted");
    process.Kill(); await process.WaitForExitAsync();
    FpsSessionLease.Reclaim(directory, Stop);
    Check(stopped.Contains(crashed) && !File.Exists(Path.Combine(directory, crashed + ".lease")), "forced owner termination leaves a reclaimable record");
}
finally { Directory.Delete(directory, true); }
