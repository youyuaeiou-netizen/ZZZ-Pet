using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using DesktopPet.Monitoring;
using DesktopPet.MonitorWorker;

// This worker never installs drivers, starts other applications, writes ObsUI state,
// creates web listeners, or stops processes by name. Only its owning Pet can issue commands.
if (args.FirstOrDefault() == "--fps-agent") { await FpsAgent.Run(args); return; }
if (args.Length != 3 || !args[0].StartsWith("DesktopPet.Monitor.", StringComparison.Ordinal) ||
    !int.TryParse(args[1], out var parentId) || !long.TryParse(args[2], out var parentStart)) return;
using var parent = Process.GetProcessById(parentId);
if (parent.StartTime.ToUniversalTime().Ticks != parentStart) return;
using var lifetime = new CancellationTokenSource();
_ = Task.Run(async () =>
{
    while (!lifetime.IsCancellationRequested)
    {
        if (parent.HasExited) { lifetime.Cancel(); return; }
        try { await Task.Delay(500, lifetime.Token); } catch (OperationCanceledException) { return; }
    }
});
using var pipe = new NamedPipeClientStream(".", args[0], PipeDirection.InOut, PipeOptions.Asynchronous);
FpsManager? fps = null;
try
{
    await pipe.ConnectAsync(10000, lifetime.Token);
    using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, true);
    using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true) { AutoFlush = true };
    using var sampler = new HardwareSampler();
    var refreshMs = 1000;
    MonitorSnapshot? hardwareSnapshot = null;
    var hardwareClock = Stopwatch.StartNew();
    while (!lifetime.IsCancellationRequested)
    {
        var line = await reader.ReadLineAsync(lifetime.Token);
        if (line is null || line.Length > 4096) break;
        var command = JsonSerializer.Deserialize<MonitorCommand>(line, MonitorJson.Options);
        if (command?.ProtocolVersion != 1) break;
        if (command.Operation == "stop") break;
        if (command.Operation is not ("sample" or "configure" or "catalog" or "status")) break;
        if (command.Operation == "configure")
        {
            if (command.RefreshMs is not (>= 500 and <= 5000)) break; refreshMs = command.RefreshMs.Value;
            if (command.FpsEnabled) { fps ??= new(); fps.Start(); }
            else { fps?.Dispose(); fps = null; }
        }
        // Frequent FPS replies reuse hardware at its configured cadence; timestamps stay truthful.
        if (fps is null || hardwareSnapshot is null || command.Operation != "sample" || hardwareClock.ElapsedMilliseconds >= refreshMs)
        { hardwareClock.Restart(); hardwareSnapshot = await sampler.SampleAsync(lifetime.Token); }
        var snapshot = hardwareSnapshot with { Metrics = [.. hardwareSnapshot.Metrics], Devices = [.. hardwareSnapshot.Devices] };
        var reading = fps?.Reading ?? new([], "disabled");
        var fpsMetrics = reading.Metrics.Where(m => m.SampledAt is DateTimeOffset t && DateTimeOffset.UtcNow - t <= TimeSpan.FromSeconds(3)).ToList();
        snapshot.Metrics.AddRange(fpsMetrics); snapshot.Devices.AddRange(fpsMetrics.Select(m => new MonitorDevice(m.DeviceId, m.Name, "FPS")).DistinctBy(d => d.Id));
        snapshot = snapshot with { Capabilities = new()
        {
            ["collection"] = "running", ["sampling-ms"] = refreshMs.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["cpu-temperature"] = snapshot.Metrics.Any(m => m.Kind == "CPU.Temp" && m.Valid) ? "ready" : "unavailable",
            ["gpu-temperature"] = snapshot.Metrics.Any(m => m.Kind == "GPU.Temp" && m.Valid) ? "ready" : "unavailable",
            ["driver-installation"] = "disabled", ["fps-helper"] = reading.Status, ["network-plugins"] = "normal-process-only",
            ["obsui-shared"] = snapshot.Status.Contains("8085") ? "ready" : "not-connected"
        } };
        await writer.WriteLineAsync(JsonSerializer.Serialize(snapshot, MonitorJson.Options));
    }
}
catch (Exception e) when (e is IOException or OperationCanceledException or TimeoutException or JsonException)
{ /* Pipe loss or owner exit terminates the worker; no reconnecting orphan. */ }
finally { lifetime.Cancel(); fps?.Dispose(); }
