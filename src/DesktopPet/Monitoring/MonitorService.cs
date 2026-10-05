using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows.Threading;
using DesktopPet.Monitoring;

namespace DesktopPet;

public sealed class MonitorService : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly Func<bool> _quiet;
    private readonly TemperatureEpisodes _episodes = new();
    private CancellationTokenSource? _run;
    private Task? _running;
    private bool _disposed;
    private bool _elevationRequested;
    private long _historyAt;
    private int _restartCount;
    private DateTimeOffset _restartWindow;
    private long _trimAt = Environment.TickCount64;
    private string _savedConfig;
    private CancellationTokenSource? _pluginRun;
    private bool _fpsDisplayValid;
    public MonitorStore Store { get; }
    public MonitorConfig Config { get; }
    public PluginHost Plugins { get; }
    public TrafficLedger Traffic { get; }
    public MonitorWebService Web { get; } = new();
    internal MonitorSystemTools SystemTools { get; } = new();
    public MonitorSnapshot Snapshot { get; private set; } = MonitorSnapshot.Empty;
    public string Status { get; private set; } = "未启用";
    public event Action? Changed;
    public event Action<string, MonitorMetric>? HighTemperature;
    public List<MonitorMetric> Catalog => MonitorSelection.Resolve(Snapshot, Config).Concat(Config.Enabled && Config.PluginsEnabled ? Plugins.Metrics : []).DistinctBy(m => m.Id)
        .Take(Config.FpsEnabled ? 20 : 19).ToList(); // Reserve a row for the FPS switch while capture is off.
    public bool Running => _running is { IsCompleted: false };
    public int? WorkerProcessId { get; private set; }
    internal Func<IReadOnlyList<int>> FpsTargets { get; set; } = new MonitorForegroundTracker().ProcessIds;
    internal void ObserveFpsTarget()
    {
        if (!Config.Enabled || !Config.FpsEnabled) return;
        var targets = string.Join(',', FpsTargets());
        if (Snapshot.Timestamp == DateTimeOffset.MinValue) return;
        var changed = Snapshot.Capabilities?.GetValueOrDefault("fps-foreground") != targets;
        if (changed)
        {
            var capabilities = new Dictionary<string, string>(Snapshot.Capabilities ?? []);
            capabilities["fps-foreground"] = targets; Snapshot = Snapshot with { Capabilities = capabilities };
        }
        var metric = MonitorSelection.Resolve(Snapshot, Config).First(m => m.Id == "FPS");
        var valid = Snapshot.ForDisplay(metric, DateTimeOffset.UtcNow).Valid;
        if (changed || valid != _fpsDisplayValid) { _fpsDisplayValid = valid; Changed?.Invoke(); }
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetNamedPipeClientProcessId(nint pipe, out uint pid);

    public MonitorService(string directory, Dispatcher dispatcher, Func<bool> quiet)
    {
        Store = new(directory); Config = Store.Load(); _dispatcher = dispatcher; _quiet = quiet;
        _savedConfig = JsonSerializer.Serialize(Config, MonitorJson.Options);
        Plugins = new(directory);
        Traffic = new(directory); Traffic.Load();
        if (!Store.Available) Status = Store.Error!;
    }
    public void Restore() { if (Config.Enabled && Store.Available) Start(Config.FpsEnabled); }
    public void SetFpsEnabled(bool enabled)
    {
        if (_disposed || !Store.Available) return;
        Config.FpsEnabled = enabled;
        Config.SelectedMetrics.RemoveAll(id => id == "FPS" || id.StartsWith("FPS.", StringComparison.Ordinal));
        if (enabled) { Config.SelectedMetrics.Add("FPS"); Config.Enabled = true; }
        if (!Save()) return;
        if (enabled && !Snapshot.Elevated && !_elevationRequested) RestartElevated();
    }
    public void SetEnabled(bool enabled)
    {
        if (_disposed || !Store.Available) return;
        Config.Enabled = enabled;
        if (!Save()) return;
        if (enabled && Running && _run?.IsCancellationRequested == true) _ = RestartAfterAsync(_running, Config.FpsEnabled);
        else if (enabled) { _restartCount = 0; Start(Config.FpsEnabled); } else Stop();
    }
    public bool Save()
    {
        var saved = Persist();
        if (!saved)
        {
            var previous = JsonSerializer.Deserialize<MonitorConfig>(_savedConfig, MonitorJson.Options)!;
            foreach (var property in typeof(MonitorConfig).GetProperties()) property.SetValue(Config, property.GetValue(previous));
            Status = Store.Error!; Stop();
        }
        if (!Config.PluginsEnabled) { _pluginRun?.Cancel(); }
        Changed?.Invoke(); return saved;
    }
    private bool Persist()
    {
        if (!Store.Save(Config)) return false;
        _savedConfig = JsonSerializer.Serialize(Config, MonitorJson.Options); return true;
    }
    public void RestartElevated()
    {
        if (_disposed || !Store.Available || !Config.Enabled) return;
        if (_elevationRequested) return;
        _elevationRequested = true;
        var previous = _running; Stop();
        _ = RestartAfterAsync(previous, true);
    }
    private async Task RestartAfterAsync(Task? previous, bool elevated)
    {
        if (previous is not null) await previous;
        if (!_disposed && Config.Enabled) Start(elevated);
    }
    public void Start(bool elevated)
    {
        if (_disposed || Running || !Store.Available) return;
        _run?.Dispose();
        _elevationRequested = elevated;
        _run = new(); Status = "正在启动监控…"; Changed?.Invoke();
        _running = RunAsync(elevated, _run.Token);
    }
    private async Task RunAsync(bool elevated, CancellationToken token)
    {
        Process? worker = null;
        var reconnect = false;
        try
        {
            var executable = Path.Combine(AppContext.BaseDirectory, "monitor", "DesktopPet.MonitorWorker.exe");
            if (!File.Exists(executable)) executable = Path.Combine(AppContext.BaseDirectory, "DesktopPet.MonitorWorker.exe");
            if (!File.Exists(executable)) throw new FileNotFoundException("发布包缺少内置监控后台，请保留完整程序目录", executable);
            var name = "DesktopPet.Monitor." + Guid.NewGuid().ToString("N");
            using var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            using var parent = Process.GetCurrentProcess();
            var start = new ProcessStartInfo(executable) { UseShellExecute = elevated, CreateNoWindow = !elevated,
                WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = Path.GetDirectoryName(executable)! };
            if (elevated) start.Verb = "runas";
            start.ArgumentList.Add(name); start.ArgumentList.Add(Environment.ProcessId.ToString());
            start.ArgumentList.Add(parent.StartTime.ToUniversalTime().Ticks.ToString());
            worker = Process.Start(start) ?? throw new IOException("后台未能启动");
            WorkerProcessId = worker.Id;
            using var connect = CancellationTokenSource.CreateLinkedTokenSource(token);
            connect.CancelAfter(TimeSpan.FromSeconds(15));
            await pipe.WaitForConnectionAsync(connect.Token);
            if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle.DangerousGetHandle(), out var clientPid) || clientPid != worker.Id)
                throw new IOException("监控后台身份不匹配");
            using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, true);
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true) { AutoFlush = true };
            var configuredMs = 0;
            var configuredFps = false;
            while (!token.IsCancellationRequested)
            {
                var cycle = Stopwatch.StartNew();
                var interval = Config.FpsEnabled ? Math.Min(Config.RefreshMs, 500) : Config.RefreshMs;
                var command = configuredMs == Config.RefreshMs && configuredFps == Config.FpsEnabled ? new MonitorCommand("sample")
                    : new MonitorCommand("configure", RefreshMs: Config.RefreshMs, FpsEnabled: Config.FpsEnabled);
                configuredMs = Config.RefreshMs; configuredFps = Config.FpsEnabled;
                await writer.WriteLineAsync(JsonSerializer.Serialize(command, MonitorJson.Options).AsMemory(), token);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(15));
                var line = await reader.ReadLineAsync(deadline.Token);
                if (line is null || line.Length > 2_000_000) throw new IOException("后台数据中断或超出限制");
                var snapshot = JsonSerializer.Deserialize<MonitorSnapshot>(line, MonitorJson.Options) ?? throw new IOException("后台数据为空");
                if (snapshot.ProtocolVersion != 1 || snapshot.Devices is null || snapshot.Metrics is null || snapshot.Metrics.Count > 4096 ||
                    snapshot.Metrics.Any(m => m is null || string.IsNullOrWhiteSpace(m.Id) || string.IsNullOrWhiteSpace(m.Name) || m.Unit is null || m.Source is null))
                    throw new InvalidDataException("监控后台协议或指标目录不兼容");
                await _dispatcher.InvokeAsync(() => Apply(snapshot), DispatcherPriority.Background, token);
                _ = PollPluginsAsync(token);
                // RefreshMs is the complete sampling period, including sensor/pipe latency.
                var remaining = interval - (int)Math.Min(int.MaxValue, cycle.ElapsedMilliseconds);
                if (remaining > 0) await Task.Delay(remaining, token);
            }
        }
        catch (OperationCanceledException)
        { if (!token.IsCancellationRequested) { reconnect = true; await UpdateStatus("后台启动或采集超时；Pet 继续运行"); } }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or JsonException or InvalidOperationException)
        {
            reconnect = e is IOException and not FileNotFoundException;
            if (e is System.ComponentModel.Win32Exception { NativeErrorCode: 1223 })
            {
                CancelElevation(); reconnect = true;
            }
            if (!token.IsCancellationRequested) await UpdateStatus(e is System.ComponentModel.Win32Exception { NativeErrorCode: 1223 } ? "已取消管理员授权；可以普通权限重新启用" : "监控不可用：" + e.Message);
        }
        finally
        {
            _elevationRequested = false;
            if (worker is not null)
            {
                try
                {
                    // Only the exact process started by this service is ever stopped.
                    using var timeout = new CancellationTokenSource(3000);
                    await worker.WaitForExitAsync(timeout.Token);
                }
                catch (OperationCanceledException) { try { if (!worker.HasExited) worker.Kill(); } catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { } }
                finally { WorkerProcessId = null; worker.Dispose(); }
            }
            if (reconnect && !token.IsCancellationRequested && !_disposed && Config.Enabled) _ = ReconnectAsync(token);
        }
    }
    private void CancelElevation()
    {
        Config.FpsEnabled = false;
        Config.SelectedMetrics.RemoveAll(id => id == "FPS" || id.StartsWith("FPS.", StringComparison.Ordinal));
        Save();
    }
    private async Task ReconnectAsync(CancellationToken token)
    {
        if (DateTimeOffset.UtcNow - _restartWindow > TimeSpan.FromMinutes(10)) { _restartWindow = DateTimeOffset.UtcNow; _restartCount = 0; }
        if (_restartCount++ >= 3) return;
        try
        {
            await Task.Delay(3000, token);
            if (!_disposed && Config.Enabled && !token.IsCancellationRequested)
                await _dispatcher.InvokeAsync(() => { if (!_disposed && Config.Enabled && !token.IsCancellationRequested) Start(false); });
        }
        catch (OperationCanceledException) { }
    }
    private async Task PollPluginsAsync(CancellationToken token)
    {
        if (!Config.Enabled || !Config.PluginsEnabled) return;
        if (_pluginRun is null || _pluginRun.IsCancellationRequested)
        { _pluginRun?.Dispose(); _pluginRun = CancellationTokenSource.CreateLinkedTokenSource(token); }
        var pluginToken = _pluginRun.Token;
        try
        {
            await Plugins.PollAsync(Config.Enabled && Config.PluginsEnabled, pluginToken);
            if (!pluginToken.IsCancellationRequested && !_disposed) await _dispatcher.InvokeAsync(() => Changed?.Invoke());
        }
        catch (OperationCanceledException) { }
        catch (Exception e) when (e is HttpRequestException or IOException or JsonException or InvalidOperationException) { }
    }
    private Task UpdateStatus(string status) => _dispatcher.HasShutdownStarted ? Task.CompletedTask
        : _dispatcher.InvokeAsync(() => { if (!_disposed) { Status = status; Snapshot = MonitorSnapshot.Empty; Changed?.Invoke(); } }).Task;
    private void Apply(MonitorSnapshot snapshot)
    {
        if (_disposed || !Config.Enabled) return;
        if (Config.FpsEnabled)
        {
            var capabilities = new Dictionary<string, string>(snapshot.Capabilities ?? []);
            capabilities["fps-foreground"] = string.Join(',', FpsTargets());
            snapshot = snapshot with { Capabilities = capabilities };
        }
        var hardwareChanged = Snapshot.Timestamp != snapshot.Timestamp;
        Snapshot = snapshot; Status = snapshot.Status;
        var normalized = Config.SelectedMetrics.Select(id => id == "system/memory/used" ? "MEM.Used" : snapshot.Metrics.FirstOrDefault(m => m.Id == id) is MonitorMetric metric && MonitorSelection.Defaults.Any(d => d.Id == metric.Kind) ? metric.Kind : id)
            .Where(id => Config.FpsEnabled || !(id == "FPS" || id.StartsWith("FPS.", StringComparison.Ordinal))).Distinct().ToList();
        if (Config.FpsEnabled && !normalized.Contains("FPS")) normalized.Add("FPS");
        if (!Config.SelectedMetrics.SequenceEqual(normalized)) { Config.SelectedMetrics = normalized; if (!Save()) return; }
        // FPS-only replies carry the unchanged hardware stamp: no duplicate alerts/history/traffic.
        if (!hardwareChanged) { Changed?.Invoke(); return; }
        Traffic.Observe(snapshot, Config);
        if (Config.AutoCleanOwnMemory && Environment.TickCount64 - _trimAt >= Config.CleanMinutes * 60000L)
        { _trimAt = Environment.TickCount64; _ = Task.Run(MonitorMemory.TrimOwn); }
            var catalog = Catalog; var now = DateTimeOffset.UtcNow;
        var before = JsonSerializer.Serialize(new[] { Config.Cpu, Config.Gpu }, MonitorJson.Options);
        var alerts = new List<(string Key, MonitorMetric Metric)>();
        foreach (var (key, rule) in new[] { ("CPU.Temp", Config.Cpu), ("GPU.Temp", Config.Gpu) })
        {
            var metric = catalog.FirstOrDefault(m => m.Id == key);
            if (_episodes.Observe(key, rule, metric, metric?.SampledAt ?? snapshot.Timestamp, now, _quiet(), Config.RefreshMs))
                alerts.Add((key, metric!));
        }
        var after = JsonSerializer.Serialize(new[] { Config.Cpu, Config.Gpu }, MonitorJson.Options);
        if (before != after && !Save()) return;
        foreach (var alert in alerts) HighTemperature?.Invoke(alert.Key, alert.Metric);
        if (Config.HistoryEnabled && Environment.TickCount64 - _historyAt >= 10000)
        {
            _historyAt = Environment.TickCount64;
            try
            {
                var dir = Path.Combine(Store.DirectoryPath, "history"); Directory.CreateDirectory(dir);
                var file = Path.Combine(dir, now.ToLocalTime().ToString("yyyy-MM-dd") + ".jsonl");
                var line = JsonSerializer.Serialize(snapshot with { Metrics = snapshot.Metrics.Concat(Config.PluginsEnabled ? Plugins.Metrics : []).ToList() }, MonitorJson.Options) + "\n";
                var size = File.Exists(file) ? new FileInfo(file).Length : 0;
                if (Encoding.UTF8.GetByteCount(line) <= 32 * 1024 * 1024 - size) File.AppendAllText(file, line);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Status += "；历史写入失败"; }
        }
        Changed?.Invoke();
    }
    public void Snooze(string key) { Rule(key).SnoozeUntil = DateTimeOffset.UtcNow.AddHours(1); Save(); }
    public void SilenceEpisode(string key) { Rule(key).InEpisode = true; Save(); }
    private TemperatureRule Rule(string key) => key == "CPU.Temp" ? Config.Cpu : Config.Gpu;
    public void Stop()
    {
        _run?.Cancel(); _pluginRun?.Cancel(); _episodes.ResetTiming(); Snapshot = MonitorSnapshot.Empty;
        Web.Stop(); Traffic.Save(); Traffic.ResetTiming();
        if (Store.Available) Status = "未启用";
        Changed?.Invoke();
    }
    // Legacy web fields are retained in monitor.json; Pet never starts a listener.
    public void Dispose() { if (_disposed) return; _disposed = true; Stop(); Plugins.Dispose(); _pluginRun?.Dispose(); Web.Dispose(); SystemTools.Dispose(); }
}
