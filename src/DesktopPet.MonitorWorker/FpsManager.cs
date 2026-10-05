using System.Diagnostics;
using System.Text.Json;
using DesktopPet.Monitoring;

namespace DesktopPet.MonitorWorker;

internal sealed class FpsManager : IDisposable
{
    private Process? _agent;
    private string? _session;
    private readonly CancellationTokenSource _lifetime = new();
    private FpsReading _reading = new([], "disabled");
    public FpsReading Reading => Volatile.Read(ref _reading);
    public void Start()
    {
        if (_agent is not null) return;
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        using var owner = Process.GetCurrentProcess();
        start.ArgumentList.Add("--fps-agent"); start.ArgumentList.Add(Environment.ProcessId.ToString());
        _session = "DesktopPet.FPS." + Guid.NewGuid().ToString("N");
        start.ArgumentList.Add(owner.StartTime.ToUniversalTime().Ticks.ToString()); start.ArgumentList.Add(_session);
        try { _agent = Process.Start(start); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        { Volatile.Write(ref _reading, new([], "unavailable")); return; }
        if (_agent is null) { Volatile.Write(ref _reading, new([], "unavailable")); return; }
        var agent = _agent;
        Volatile.Write(ref _reading, new([], "starting"));
        _ = Task.Run(async () =>
        {
            try
            {
                while (await agent.StandardOutput.ReadLineAsync(_lifetime.Token) is string line)
                {
                    if (line.Length > 1024 * 1024) break;
                    var reading = JsonSerializer.Deserialize<FpsReading>(line, MonitorJson.Options);
                    if (reading?.Metrics is not null && reading.Metrics.Count <= 256) Volatile.Write(ref _reading, reading);
                }
            }
            catch (Exception e) when (e is IOException or OperationCanceledException or JsonException or InvalidOperationException) { }
            finally
            {
                if (Reading.Status is "running" or "starting") Volatile.Write(ref _reading, new([], "unavailable"));
                // This elevated worker can also reclaim its own random ETW session
                // when the supervisor was killed and could not execute its finally.
                if (_session is string session) FpsAgent.StopOwnedTrace(session);
            }
        });
        _ = Task.Run(async () => { try { while (await agent.StandardError.ReadLineAsync(_lifetime.Token) is not null) { } } catch (Exception e) when (e is IOException or OperationCanceledException or InvalidOperationException) { } });
    }
    public void Dispose()
    {
        _lifetime.Cancel();
        if (_agent is not null)
        {
            try { if (!_agent.HasExited) { _agent.StandardInput.WriteLine("stop"); if (!_agent.WaitForExit(3000)) _agent.Kill(); } }
            catch (Exception e) when (e is IOException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
            _agent.Dispose(); _agent = null;
        }
        if (_session is string session) FpsAgent.StopOwnedTrace(session);
        _lifetime.Dispose();
    }
}
