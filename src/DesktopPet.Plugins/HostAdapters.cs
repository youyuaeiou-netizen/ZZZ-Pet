using System.Collections.Concurrent;
using System.Text.Json.Serialization;
using LiteMonitor.src.Plugins;

namespace LiteMonitor.src.Core
{
    // Adapter for upstream's string helper. No process-wide intern pool for custom plugin strings.
    public static class UIUtils { public static string Intern(string value) => value; }
    public sealed class PluginInstanceConfig
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string TemplateId { get; set; } = "";
        public bool Enabled { get; set; }
        public Dictionary<string, string> InputValues { get; set; } = new();
        public List<Dictionary<string, string>> Targets { get; set; } = new();
        [JsonIgnore] public ConcurrentDictionary<string, PluginOutputKeys> KeyCache { get; } = new();
    }
}
namespace LiteMonitor.src.SystemServices.InfoService
{
    public sealed class InfoService
    {
        public static InfoService Instance { get; } = new();
        private readonly ConcurrentDictionary<string, string> _values = new();
        public string GetValue(string key) => _values.TryGetValue(key, out var value) ? value : "";
        public void InjectValue(string key, string value) => _values[key] = value;
        public void Clear(string prefix) { foreach (var key in _values.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal) || k.Contains("DASH." + prefix, StringComparison.Ordinal))) _values.TryRemove(key, out _); }
    }
}
