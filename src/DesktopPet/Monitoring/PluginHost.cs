using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DesktopPet.Monitoring;
using LiteMonitor.src.Core;
using LiteMonitor.src.Plugins;
using LiteMonitor.src.SystemServices.InfoService;

namespace DesktopPet;

public sealed class PluginHost : IDisposable
{
    private readonly string _directory;
    private readonly PluginExecutor _executor = new();
    private readonly Dictionary<string, DateTimeOffset> _lastRun = [];
    private readonly Dictionary<string, List<MonitorMetric>> _results = [];
    private bool _busy;
    private bool _disposed;
    private int _revision;
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("DesktopPet.LiteMonitor.plugins.v1");
    public List<PluginTemplate> Templates { get; } = [];
    public List<PluginInstanceConfig> Instances { get; private set; } = [];
    public string? Error { get; private set; }
    public string? SaveError { get; private set; }
    public List<MonitorMetric> Metrics => _results.Where(p => Instances.Any(i => i.Id == p.Key && i.Enabled))
        .SelectMany(p => p.Value.Select(m =>
        {
            var instance = Instances.First(i => i.Id == p.Key);
            var execution = Templates.FirstOrDefault(t => t.Id == instance.TemplateId)?.Execution;
            var interval = Math.Max(1, Math.Max(execution?.Interval ?? 60, execution?.MinInterval ?? 0));
            return m.SampledAt is DateTimeOffset sampled && DateTimeOffset.UtcNow - sampled <= TimeSpan.FromSeconds(interval * 2 + 5)
                ? m : m with { Value = null, Text = "已断开" };
        })).ToList();
    public PluginHost(string directory)
    {
        _directory = directory; LoadTemplates();
        var file = Path.Combine(directory, "plugins.bin");
        try
        {
            if (File.Exists(file)) Instances = JsonSerializer.Deserialize<List<PluginInstanceConfig>>(
                ProtectedData.Unprotect(File.ReadAllBytes(file), Entropy, DataProtectionScope.CurrentUser), MonitorJson.Options)
                ?? throw new InvalidDataException("插件配置为空");
            if (Instances.Count > 100 || Instances.Any(i => !ValidInstance(i)) ||
                Instances.Select(i => i.Id).Distinct().Count() != Instances.Count) throw new InvalidDataException("插件配置损坏");
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or CryptographicException)
        { Error = "插件配置不可用，原文件保留；" + e.GetType().Name; Instances = []; }
    }
    public void LoadTemplates()
    {
        Templates.Clear();
        foreach (var directory in new[] { Path.Combine(AppContext.BaseDirectory, "monitor-plugins"), Path.Combine(_directory, "plugins") })
        {
            if (!Directory.Exists(directory)) continue;
            foreach (var file in Directory.EnumerateFiles(directory, "*.json"))
            {
                try
                {
                    if (new FileInfo(file).Length > 1024 * 1024) continue;
                    var t = JsonSerializer.Deserialize<PluginTemplate>(File.ReadAllText(file));
                    if (ValidTemplate(t) && Templates.All(x => x.Id != t!.Id)) Templates.Add(t!);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { }
            }
        }
    }
    public bool Save()
    {
        if (Error is not null) return false;
        if (Instances.Count > 100 || Instances.Any(i => !ValidInstance(i))) { SaveError = "插件实例或输入配置超出上限。"; return false; }
        try
        {
            Directory.CreateDirectory(_directory);
            var data = ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(Instances, MonitorJson.Options), Entropy, DataProtectionScope.CurrentUser);
            var path = Path.Combine(_directory, "plugins.bin"); File.WriteAllBytes(path + ".tmp", data); File.Move(path + ".tmp", path, true);
            _revision++; _executor.ClearCache(); _lastRun.Clear(); _results.Clear(); SaveError = null; return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or CryptographicException) { Error = "插件配置保存失败；" + e.GetType().Name; return false; }
    }
    public bool Import(string path)
    {
        if (Error is not null) return false;
        try
        {
            if (new FileInfo(path).Length > 1024 * 1024) throw new InvalidDataException("插件文件过大");
            var text = File.ReadAllText(path); var t = JsonSerializer.Deserialize<PluginTemplate>(text) ?? throw new InvalidDataException("插件模板为空");
            if (!ValidTemplate(t) || Templates.Any(x => x.Id == t.Id)) throw new InvalidDataException("插件模板无效或标识已存在");
            var dir = Path.Combine(_directory, "plugins"); Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, Guid.NewGuid().ToString("N") + ".json"), text); LoadTemplates(); return true;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or JsonException) { return false; }
    }
    private static bool ValidTemplate(PluginTemplate? t) => t is not null && t.Id is { Length: > 0 and <= 200 } && !string.IsNullOrWhiteSpace(t.Id)
        && t.Meta is not null && !string.IsNullOrWhiteSpace(t.Meta.Name) && t.Display is not null
        && t.Execution is not null && t.Execution.Extract is not null && t.Execution.Interval is >= 0 and <= 86400
        && t.Execution.MinInterval is >= 0 and <= 86400
        && t.Inputs is not null && t.Inputs.Count <= 100 && t.Inputs.All(i => i is not null && !string.IsNullOrWhiteSpace(i.Key)
            && (i.Options is null || i.Options.All(o => o is not null)))
        && t.Outputs is not null && t.Outputs.Count <= 100 && t.Outputs.All(o => o is not null && !string.IsNullOrWhiteSpace(o.Key))
        && (t.Execution.Steps is null || (t.Execution.Steps.Count <= 100 && t.Execution.Steps.All(s => s is not null && s.Extract is not null)));
    public async Task PollAsync(bool enabled, CancellationToken token)
    {
        if (!enabled || Error is not null || _busy || _disposed) return;
        _busy = true;
        var revision = _revision;
        try
        {
            foreach (var instance in Instances.Where(i => i.Enabled && ValidInstance(i)).ToArray())
            {
                if (revision != _revision || _disposed || token.IsCancellationRequested) return;
                var template = Templates.FirstOrDefault(t => t.Id == instance.TemplateId); if (template is null) continue;
                var interval = Math.Max(1, Math.Max(template.Execution.Interval, template.Execution.MinInterval));
                var now = DateTimeOffset.UtcNow;
                if (_lastRun.TryGetValue(instance.Id, out var last) && now - last < TimeSpan.FromSeconds(interval)) continue;
                _lastRun[instance.Id] = now;
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(30000);
                var success = await Task.Run(() => _executor.ExecuteInstanceAsync(instance, template, deadline.Token), deadline.Token);
                if (revision != _revision) { _executor.ClearCache(); return; }
                if (token.IsCancellationRequested || _disposed) return;
                var values = new List<MonitorMetric>();
                for (var index = 0; index < Math.Max(1, instance.Targets.Count); index++)
                {
                    var suffix = instance.Targets.Count > 0 ? "." + index : "";
                    foreach (var output in template.Outputs)
                    {
                        var injected = instance.Id + suffix + "." + output.Key; var id = "DASH." + injected;
                        var raw = InfoService.Instance.GetValue(injected); var name = InfoService.Instance.GetValue("PROP.Label." + id);
                        var unit = InfoService.Instance.GetValue(injected + ".Unit");
                        double? number = double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && double.IsFinite(n) ? n : null;
                        values.Add(new(id, string.IsNullOrEmpty(name) ? template.Meta.Name + " · " + output.Key : name,
                            instance.Id, "PLUGIN", unit, success ? number : null, template.Meta.Name, success ? raw + unit : "不可用", DateTimeOffset.UtcNow));
                    }
                    if (template.Execution.Type == "api_text") values.Add(new("DASH." + instance.Id + suffix, template.Meta.Name,
                        instance.Id, "PLUGIN", "", null, template.Meta.Name, success ? InfoService.Instance.GetValue(instance.Id + suffix) : "不可用", DateTimeOffset.UtcNow));
                }
                _results[instance.Id] = values;
            }
        }
        finally { _busy = false; }
    }
    public void Dispose() { _disposed = true; _executor.Dispose(); }
    private static bool ValidInstance(PluginInstanceConfig? i) => i is not null && i.Id is { Length: > 0 and <= 200 }
        && i.TemplateId is { Length: > 0 and <= 200 } && i.InputValues is not null && ValidInputs(i.InputValues)
        && i.Targets is not null && i.Targets.Count <= 100 && i.Targets.All(t => t is not null && ValidInputs(t));
    private static bool ValidInputs(Dictionary<string, string> inputs) => inputs.Count <= 100
        && inputs.All(p => p.Key is { Length: > 0 and <= 200 } && p.Value is { Length: <= 4096 });
}
