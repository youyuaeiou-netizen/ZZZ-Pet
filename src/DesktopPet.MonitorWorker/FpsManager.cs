using System.Diagnostics;
using System.Text.Json;
using DesktopPet.Monitoring;

namespace DesktopPet.MonitorWorker;

internal sealed class FpsManager : IDisposable
{
    private Process? _agent;
    private string? _session;
    private FpsSessionLease? _lease;
    private long _restartAt, _lastReplyAt;
    private int _readerEnded;
    private int _recoveriesBeforeAgent;
    private string? _supervisorRecovery;
    private readonly CancellationTokenSource _lifetime = new();
    private FpsReading _reading = new([], "disabled");
    public FpsReading Reading => Volatile.Read(ref _reading);
    public void Start()
    {
        if (_lifetime.IsCancellationRequested) return;
        if (_agent is not null && !_agent.HasExited && Volatile.Read(ref _readerEnded) == 0 &&
            Environment.TickCount64 - Volatile.Read(ref _lastReplyAt) < 10000) return;
        if (_restartAt != 0 && Environment.TickCount64 - _restartAt < 5000) return;
        _restartAt = Environment.TickCount64;
        if (_agent is not null)
        {
            var completedRecoveries = Reading.Recoveries;
            var reason = _agent.HasExited ? "监督进程退出" : Volatile.Read(ref _readerEnded) != 0 ? "监督进程输出中断" : "监督进程回复超时";
            try { if (!_agent.HasExited) { _agent.Kill(); _agent.WaitForExit(3000); } }
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            _agent.Dispose(); _agent = null;
            _recoveriesBeforeAgent = Math.Max(_recoveriesBeforeAgent, completedRecoveries) + 1;
            _supervisorRecovery = reason;
            if (_session is string previousSession) FpsAgent.StopOwnedTrace(previousSession);
        }
        _lease?.Dispose(); _lease = null;
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        using var owner = Process.GetCurrentProcess();
        start.ArgumentList.Add("--fps-agent"); start.ArgumentList.Add(Environment.ProcessId.ToString());
        _session = "DesktopPet.FPS." + Guid.NewGuid().ToString("N");
        start.ArgumentList.Add(owner.StartTime.ToUniversalTime().Ticks.ToString()); start.ArgumentList.Add(_session);
        try
        {
            _lease = new FpsSessionLease(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DesktopPet", "monitor", "fps-sessions"), _session, FpsAgent.StopOwnedTrace);
            _agent = Process.Start(start);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        { _lease?.Dispose(); _lease = null; Volatile.Write(ref _reading, new([], "unavailable")); return; }
        if (_agent is null) { _lease?.Dispose(); _lease = null; Volatile.Write(ref _reading, new([], "unavailable")); return; }
        var agent = _agent;
        var session = _session;
        Volatile.Write(ref _readerEnded, 0); Volatile.Write(ref _lastReplyAt, Environment.TickCount64);
        Volatile.Write(ref _reading, new([], "starting", _supervisorRecovery, _recoveriesBeforeAgent));
        _ = Task.Run(async () =>
        {
            try
            {
                while (await agent.StandardOutput.ReadLineAsync(_lifetime.Token) is string line)
                {
                    if (line.Length > 1024 * 1024) break;
                    var reading = JsonSerializer.Deserialize<FpsReading>(line, MonitorJson.Options);
                    if (reading?.Metrics is not null && reading.Metrics.Count <= 256 && ReferenceEquals(_agent, agent))
                    {
                        reading = reading with { Recoveries = reading.Recoveries + _recoveriesBeforeAgent,
                            Diagnostic = _supervisorRecovery is null ? reading.Diagnostic
                                : (reading.Diagnostic is null ? "" : reading.Diagnostic + "；") + "最近监督恢复：" + _supervisorRecovery };
                        Volatile.Write(ref _reading, reading); Volatile.Write(ref _lastReplyAt, Environment.TickCount64);
                    }
                }
            }
            catch (Exception e) when (e is IOException or OperationCanceledException or JsonException or InvalidOperationException) { }
            finally
            {
                if (ReferenceEquals(_agent, agent))
                { Volatile.Write(ref _readerEnded, 1); if (Reading.Status is "running" or "starting" or "no-frames" or "etw-events-lost" or "recovering") Volatile.Write(ref _reading, new([], "unavailable", Reading.Diagnostic, Reading.Recoveries)); }
                // This elevated worker can also reclaim its own random ETW session
                // when the supervisor was killed and could not execute its finally.
                FpsAgent.StopOwnedTrace(session);
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
        _lease?.Dispose(); _lease = null;
        _lifetime.Dispose();
    }
}
