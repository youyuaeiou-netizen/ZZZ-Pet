using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PetLocalModels;

internal sealed class ClientIntegration
{
    readonly Gateway gateway;
    readonly string statePath;
    readonly object gate = new();
    readonly Dictionary<string, PreviewState> previews = new();
    List<Target> targets;
    const string Provider = "pet-local";
    static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    public ClientIntegration(Gateway gateway, string? path = null)
    {
        this.gateway = gateway; statePath = path ?? Path.Combine(ModelRuntime.DataRoot, "model-clients.json");
        targets = File.Exists(statePath) ? JsonSerializer.Deserialize<List<Target>>(File.ReadAllText(statePath)) ?? [] : [];
    }
    public object Status()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var openRoot = Path.Combine(Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") ?? Path.Combine(home, ".config"), "opencode");
        var configOverride = Environment.GetEnvironmentVariable("OPENCODE_CONFIG");
        var open = new[] { configOverride, Path.Combine(openRoot, "opencode.jsonc"), Path.Combine(openRoot, "opencode.json") }.Where(p => p is not null && File.Exists(p)).Select(p => new { kind = "opencode", path = p! });
        var profileRoot = Path.Combine(Environment.GetEnvironmentVariable("DSH_HOME") ?? Path.Combine(home, ".dsh"), "profiles");
        var dsh = Directory.Exists(profileRoot) ? Directory.GetDirectories(profileRoot).Select(p => new { kind = "dsh", path = Path.Combine(p, "cordis.patch.yml") }).Where(p => File.Exists(p.path)) : [];
        lock (gate) return new { candidates = open.Concat(dsh).Distinct().ToArray(), suggestedOpenCode = Path.Combine(openRoot, "opencode.json"), targets = targets.Select(t => new { t.Path, t.Kind, t.AutoSync, t.Message, paused = t.Paused }).ToArray() };
    }
    public async Task<object> Preview(JsonObject body, CancellationToken ct)
    {
        var kind = body["kind"]?.GetValue<string>() ?? "";
        var path = System.IO.Path.GetFullPath(body["path"]?.GetValue<string>() ?? throw new ArgumentException("请选择配置文件。"));
        if (kind is not ("opencode" or "dsh")) throw new ArgumentException("不支持的客户端。");
        if (kind == "dsh" && !File.Exists(path)) throw new ArgumentException("请选择 DSH 已有配置文件。");
        var text = File.Exists(path) ? File.ReadAllText(path) : "{}\n";
        var catalog = await gateway.Catalog(ct);
        if (catalog.Count == 0) throw new ArgumentException("没有可接入的对话模型。");
        var changeDefault = body["setDefault"]?.GetValue<bool>() == true;
        var selected = body["model"]?.GetValue<string>() ?? "";
        if (!catalog.Any(n => n!["id"]!.GetValue<string>() == selected && n["context"] is not null))
        {
            if (changeDefault) throw new ArgumentException("默认模型的容量元数据不可用。");
            selected = catalog.FirstOrDefault(n => n!["context"] is not null)?["id"]!.GetValue<string>() ?? throw new ArgumentException("没有容量元数据可用的模型。");
        }
        selected = CanonicalModelId(catalog, selected);
        var previous = ReadProvider(kind, text);
        var owned = targets.FirstOrDefault(t => t.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
        if (previous is not null && owned is null) throw new ArgumentException("pet-local 提供方已存在但不属于插件，请先更名或移除后重试。");
        var provider = BuildProvider(kind, catalog);
        var after = WriteProvider(kind, text, provider);
        var originalDefault = ReadDefault(kind, text);
        var newDefault = kind == "opencode" ? JsonSerializer.Serialize(Provider + "/" + selected) : "  config:\n    provider: pet-local\n    model: " + JsonSerializer.Serialize(selected) + "\n";
        if (changeDefault) after = WriteDefault(kind, after, newDefault);
        var token = Guid.NewGuid().ToString("N");
        lock (gate)
        {
            foreach (var old in previews.Where(p => p.Value.Created < DateTime.UtcNow.AddMinutes(-10)).Select(p => p.Key).ToArray()) previews.Remove(old);
            if (previews.Count > 20) previews.Clear();
            previews[token] = new(path, kind, Hash(text), after, provider, originalDefault, changeDefault ? newDefault : null, body["autoSync"]?.GetValue<bool>() == true, previous, kind == "dsh" && Entry(text, "llm-pi-ai").Start < 0, DateTime.UtcNow);
        }
        return new { token, path, kind, before = previous ?? "（新增 pet-local 提供方）", after = provider, defaultBefore = changeDefault ? originalDefault : null, defaultAfter = changeDefault ? newDefault : "保留原默认模型", note = "确认后备份并写入。请重启客户端或开启新会话读取模型和上下文设置。项目级配置可能覆盖全局默认值；项目配置可另行选择接入。" };
    }
    public object Apply(JsonObject body)
    {
        lock (gate)
        {
            if (!previews.Remove(body["token"]?.GetValue<string>() ?? "", out var p) || DateTime.UtcNow - p.Created > TimeSpan.FromMinutes(10)) throw new ArgumentException("预览已过期，请重新预览。");
            var current = File.Exists(p.Path) ? File.ReadAllText(p.Path) : "{}\n";
            if (Hash(current) != p.Expected) throw new ArgumentException("配置已发生变化，未写入；请重新预览。");
            var existing = targets.FirstOrDefault(t => t.Path.Equals(p.Path, StringComparison.OrdinalIgnoreCase));
            var backup = WriteFile(p.Path, current, p.After);
            var target = existing ?? new Target { Path = p.Path, Kind = p.Kind, OriginalProvider = p.BeforeProvider, OriginalDefault = p.OriginalDefault, AddedProviderEntry = p.AddedProviderEntry };
            target.OwnedProvider = p.Provider;
            if (p.Default is not null) target.OwnedDefault = p.Default;
            target.AutoSync = p.AutoSync; target.Paused = false; target.Message = "已接入；修改后请重启客户端或开启新会话。";
            if (existing is null) targets.Add(target);
            Save(); return new { backup, message = target.Message };
        }
    }
    public object Detach(JsonObject body)
    {
        lock (gate)
        {
            var target = targets.FirstOrDefault(t => t.Path.Equals(body["path"]?.GetValue<string>(), StringComparison.OrdinalIgnoreCase)) ?? throw new ArgumentException("未找到接入记录。");
            var text = File.ReadAllText(target.Path);
            if (ReadProvider(target.Kind, text) != target.OwnedProvider || target.OwnedDefault is not null && ReadDefault(target.Kind, text) != target.OwnedDefault) throw new ArgumentException("配置有人工修改，解除接入已暂停。请先保留修改并手动移除 pet-local，或恢复插件部分后重试。");
            var after = WriteProvider(target.Kind, text, target.OriginalProvider);
            if (target.Kind == "dsh" && target.AddedProviderEntry)
            {
                var empty = Entry(after, "llm-pi-ai");
                var created = "- id: llm-pi-ai\n  name: '@deepseek-ai/dsh-llm-pi-ai'\n  config:\n    providers:\n";
                if (empty.Start >= 0 && empty.Value!.Replace("\r\n", "\n").TrimEnd() == created.TrimEnd()) after = after[..empty.Start] + after[empty.End..];
                else if (empty.Start >= 0) after = after[..empty.Start] + Regex.Replace(empty.Value!, @"(?m)^    providers:[ \t]*\r?\n(?=  [^ ]|- |\z)", "    providers: {}" + Newline(after)) + after[empty.End..];
            }
            if (target.OwnedDefault is not null) after = WriteDefault(target.Kind, after, target.OriginalDefault);
            var backup = WriteFile(target.Path, text, after); targets.Remove(target); Save(); return new { backup };
        }
    }
    public async Task Run(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                bool enabled; lock (gate) enabled = targets.Any(t => t.AutoSync && !t.Paused);
                if (enabled)
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(20));
                    var catalog = await gateway.Catalog(timeout.Token);
                    Synchronize(catalog);
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            { lock (gate) foreach (var t in targets.Where(t => t.AutoSync && !t.Paused)) t.Message = "同步暂不可用：" + (ex is HttpRequestException or OperationCanceledException ? "Ollama 离线或超时。" : ex.Message); }
            try { await Task.Delay(TimeSpan.FromSeconds(10), ct); } catch (OperationCanceledException) { break; }
        }
    }
    internal void Synchronize(JsonArray catalog)
    {
        lock (gate)
        {
            foreach (var target in targets.Where(t => t.AutoSync && !t.Paused))
            {
                try
                {
                    var text = File.ReadAllText(target.Path);
                    if (ReadProvider(target.Kind, text) != target.OwnedProvider || target.OwnedDefault is not null && ReadDefault(target.Kind, text) != target.OwnedDefault) throw new InvalidOperationException("插件管理的配置被人工修改，请重新预览或解除接入。");
                    var defaultValue = ReadDefault(target.Kind, text) ?? "";
                    var usesPet = target.Kind == "opencode" ? JsonSerializer.Deserialize<string>(defaultValue.Length == 0 ? "null" : defaultValue)?.StartsWith("pet-local/", StringComparison.Ordinal) == true : Regex.IsMatch(defaultValue, @"(?m)^\s+provider:\s*['""]?pet-local['""]?\s*$");
                    if (usesPet)
                    {
                        var defaultId = target.Kind == "opencode" ? JsonSerializer.Deserialize<string>(defaultValue)!["pet-local/".Length..] : Regex.Match(defaultValue, @"(?m)^\s+model:\s*(.+)$").Groups[1].Value.Trim().Trim('"', '\'');
                        if (!catalog.Any(n => n!["id"]!.GetValue<string>() == defaultId && n["context"] is not null)) throw new InvalidOperationException("默认模型已删除或不可用，请选择替代模型；未擅自切换。");
                    }
                    var provider = BuildProvider(target.Kind, catalog);
                    if (provider == target.OwnedProvider) continue;
                    var after = WriteProvider(target.Kind, text, provider);
                    WriteFile(target.Path, text, after); target.OwnedProvider = provider;
                    target.Message = "模型/上下文已同步；请重启客户端或开启新会话。";
                }
                catch (Exception ex) { target.Paused = true; target.Message = "同步已暂停：" + ex.Message; }
            }
            Save();
        }
    }
    void Save() { Directory.CreateDirectory(System.IO.Path.GetDirectoryName(statePath)!); File.WriteAllText(statePath + ".tmp", JsonSerializer.Serialize(targets)); File.Move(statePath + ".tmp", statePath, true); }
    static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    static string WriteFile(string path, string expected, string text)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        var exists = File.Exists(path);
        if (exists && File.ReadAllText(path) != expected) throw new IOException("配置发生变化，已取消写入。");
        var backup = path + ".pet-backup-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff") + "-" + Guid.NewGuid().ToString("N")[..6];
        var bom = exists && File.ReadAllBytes(path).Take(3).SequenceEqual(new byte[] { 239, 187, 191 });
        var temp = path + ".pet-" + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, text, new UTF8Encoding(bom));
            if (exists) File.Replace(temp, path, backup); else File.Move(temp, path);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
        return exists ? backup : "新建配置文件";
    }
    internal static string BuildProvider(string kind, JsonArray catalog)
    {
        var stable = StableCatalog(catalog);
        var valid = stable.Where(n => n?["context"]?.GetValue<int>() >= 1024).ToArray();
        if (valid.Length == 0) throw new ArgumentException("没有具有可用容量元数据的模型。");
        if (kind == "opencode")
        {
            var models = new JsonObject();
            foreach (var n in valid)
            {
                var caps = n!["capabilities"]?.AsArray();
                var context = n["context"]!.GetValue<int>();
                models[n["id"]!.GetValue<string>()] = new JsonObject { ["name"] = DisplayName(n), ["limit"] = new JsonObject { ["context"] = context, ["output"] = Math.Min(8192, context / 4) }, ["tool_call"] = caps?.Any(c => c?.GetValue<string>() == "tools") == true, ["reasoning"] = caps?.Any(c => c?.GetValue<string>() == "thinking") == true, ["attachment"] = caps?.Any(c => c?.GetValue<string>() == "vision") == true, ["modalities"] = new JsonObject { ["input"] = caps?.Any(c => c?.GetValue<string>() == "vision") == true ? new JsonArray("text", "image") : new JsonArray("text"), ["output"] = new JsonArray("text") } };
            }
            return new JsonObject { ["name"] = "Pet 本地模型", ["npm"] = "@ai-sdk/openai-compatible", ["options"] = new JsonObject { ["baseURL"] = ModelRuntime.GatewayEndpoint + "/v1", ["apiKey"] = "pet-local" }, ["models"] = models }.ToJsonString(JsonOptions);
        }
        var yaml = "      pet-local:\n        displayName: Pet 本地模型\n        api: openai-completions\n        baseURL: " + ModelRuntime.GatewayEndpoint + "/v1\n        headers:\n          Authorization: Bearer pet-local\n        models:\n";
        foreach (var n in valid)
        {
            var context = n!["context"]!.GetValue<int>();
            yaml += "          - id: " + JsonSerializer.Serialize(n["id"]!.GetValue<string>()) + "\n            name: " + JsonSerializer.Serialize(DisplayName(n)) + $"\n            contextWindow: {context}\n            maxTokens: {Math.Min(8192, context / 4)}\n            reasoning: " + (n["capabilities"]?.AsArray().Any(c => c?.GetValue<string>() == "thinking") == true ? "true" : "false") + "\n            input:\n              - text\n";
            if (n["capabilities"]?.AsArray().Any(c => c?.GetValue<string>() == "vision") == true) yaml += "              - image\n";
            if (n["capabilities"]?.AsArray().Any(c => c?.GetValue<string>() == "thinking") == true)
            {
                if (n["thinking"]?["values"] is JsonArray values)
                {
                    var levels = new List<string>();
                    if (values.Any(v => JsonNode.DeepEquals(v, JsonValue.Create(false)))) levels.Add("              off: none\n");
                    if (values.Any(v => JsonNode.DeepEquals(v, JsonValue.Create(true)))) levels.Add("              high: high\n");
                    foreach (var level in new[] { "minimal", "low", "medium", "high", "xhigh", "max" })
                        if (values.Any(v => JsonNode.DeepEquals(v, JsonValue.Create(level)))) levels.Add($"              {level}: {level}\n");
                    if (levels.Count > 0) yaml += "            reasoningEfforts:\n" + string.Concat(levels.Distinct());
                }
                else yaml += "            reasoningEfforts:\n              off: none\n              high: high\n";
            }
        }
        return yaml;
    }
    internal static JsonArray StableCatalog(JsonArray catalog)
    {
        var stable = new JsonArray();
        foreach (var group in catalog.Where(n => n is not null).GroupBy(n => n!["family"]?.GetValue<string>() ?? n["id"]!.GetValue<string>(), StringComparer.Ordinal))
        {
            var canonical = group.FirstOrDefault(n => n!["id"]!.GetValue<string>() == group.Key) ?? group.First();
            var model = canonical!.DeepClone().AsObject();
            model["name"] = DisplayName(model);
            stable.Add(model);
        }
        return stable;
    }
    internal static string CanonicalModelId(JsonArray catalog, string model)
    {
        var entry = catalog.FirstOrDefault(n => n?["id"]?.GetValue<string>() == model);
        return entry?["family"]?.GetValue<string>() ?? model;
    }
    static string DisplayName(JsonNode model) => model["name"]?.GetValue<string>() ?? ContextRecommendation.DisplayName(model["id"]!.GetValue<string>());
    internal static string? ReadProvider(string kind, string text) => kind == "opencode" ? JsonC.Get(text, ["provider", Provider]) : YamlProvider(text).Value;
    internal static string WriteProvider(string kind, string text, string? value)
    {
        if (kind == "opencode") return JsonC.Set(text, ["provider", Provider], value);
        var span = YamlProvider(text);
        if (span.Start >= 0) return text[..span.Start] + (value ?? "").Replace("\n", NewlineAt(text, span.Start)) + text[span.End..];
        if (value is null) return text;
        var providers = Regex.Matches(text, @"(?m)^    providers:[ \t]*(?:\r?\n|$)");
        var entry = Entry(text, "llm-pi-ai");
        if (entry.Start < 0)
            return text.TrimEnd() + Newline(text) + ("- id: llm-pi-ai\n  name: '@deepseek-ai/dsh-llm-pi-ai'\n  config:\n    providers:\n" + value).Replace("\n", Newline(text));
        if (providers.Count == 0 && Regex.IsMatch(entry.Value!, @"(?m)^  config:[ \t]*\r?\n") && !Regex.IsMatch(entry.Value!, @"(?m)^    providers:"))
        {
            var config = Regex.Match(entry.Value!, @"(?m)^  config:[ \t]*\r?\n");
            var insert = entry.Start + config.Index + config.Length;
            return text[..insert] + ("    providers:\n" + value).Replace("\n", NewlineAt(text, insert)) + text[insert..];
        }
        if (providers.Count != 1) throw new ArgumentException("DSH 配置需有唯一的 llm-pi-ai/config/providers 块；内联或复杂 YAML 请先手动整理，不会覆盖原配置。");
        if (entry.Start < 0 || providers[0].Index < entry.Start || providers[0].Index >= entry.End) throw new ArgumentException("DSH providers 不位于 llm-pi-ai 配置内。");
        var at = providers[0].Index + providers[0].Length;
        return text[..at] + value.Replace("\n", NewlineAt(text, at)) + text[at..];
    }
    static string? ReadDefault(string kind, string text)
    {
        if (kind == "opencode") return JsonC.Get(text, ["model"]);
        var entry = Entry(text, "agent-default-model");
        if (entry.Start < 0) return null;
        var raw = text[entry.Start..entry.End]; var match = Regex.Match(raw, @"(?ms)^  config:[ \t]*\r?\n.*?(?=^  [^ \r\n#]|\z)");
        return match.Success ? match.Value.Replace("\r\n", "\n") : null;
    }
    static string WriteDefault(string kind, string text, string? value)
    {
        if (kind == "opencode") return JsonC.Set(text, ["model"], value);
        var entry = Entry(text, "agent-default-model");
        if (entry.Start < 0)
        {
            if (value is null) return text;
            return text.TrimEnd() + Newline(text) + ("- id: agent-default-model\n  name: '@deepseek-ai/dsh-agent-default-model'\n" + value).Replace("\n", Newline(text));
        }
        var raw = text[entry.Start..entry.End];
        var match = Regex.Match(raw, @"(?ms)^  config:[ \t]*\r?\n.*?(?=^  [^ \r\n#]|\z)");
        if (!match.Success) throw new ArgumentException("DSH 默认模型配置结构不受支持。");
        if (value is null) return text[..entry.Start] + text[entry.End..];
        var start = entry.Start + match.Index;
        var end = start + match.Length;
        return text[..start] + value.Replace("\n", NewlineAt(text, start)) + text[end..];
    }
    static (int Start, int End, string? Value) Entry(string text, string id)
    {
        var matches = Regex.Matches(text, @"(?m)^- id:[ \t]*['"" ]?" + Regex.Escape(id) + @"['"" ]?[ \t]*(?:\r?\n|$)");
        if (matches.Count > 1) throw new ArgumentException("DSH 配置存在重复插件条目，未修改。");
        if (matches.Count == 0) return (-1, -1, null);
        var match = matches[0]; var next = Regex.Match(text[(match.Index + match.Length)..], @"(?m)^- ");
        var end = next.Success ? match.Index + match.Length + next.Index : text.Length;
        return (match.Index, end, text[match.Index..end]);
    }
    static (int Start, int End, string? Value) YamlProvider(string text)
    {
        var entry = Entry(text, "llm-pi-ai"); if (entry.Start < 0) return (-1, -1, null);
        var raw = text[entry.Start..entry.End];
        var matches = Regex.Matches(raw, @"(?m)^      pet-local:[ \t]*(?:\r?\n|$)");
        if (matches.Count > 1) throw new ArgumentException("DSH pet-local 配置重复。");
        if (matches.Count == 0) return (-1, -1, null);
        var match = matches[0]; var next = Regex.Match(raw[(match.Index + match.Length)..], @"(?m)^ {0,6}[^ \r\n#]");
        var start = entry.Start + match.Index; var end = next.Success ? start + match.Length + next.Index : entry.End;
        return (start, end, text[start..end].Replace("\r\n", "\n"));
    }
    static string Newline(string text) => text.Contains("\r\n") ? "\r\n" : "\n";
    static string NewlineAt(string text, int index)
    {
        var next = text.IndexOf('\n', Math.Clamp(index, 0, text.Length));
        if (next >= 0) return next > 0 && text[next - 1] == '\r' ? "\r\n" : "\n";
        return Newline(text);
    }
    sealed record PreviewState(string Path, string Kind, string Expected, string After, string Provider, string? OriginalDefault, string? Default, bool AutoSync, string? BeforeProvider, bool AddedProviderEntry, DateTime Created);
    internal sealed class Target
    {
        public string Path { get; set; } = "";
        public string Kind { get; set; } = "";
        public string? OriginalProvider { get; set; }
        public string OwnedProvider { get; set; } = "";
        public string? OriginalDefault { get; set; }
        public string? OwnedDefault { get; set; }
        public bool AutoSync { get; set; }
        public bool AddedProviderEntry { get; set; }
        public bool Paused { get; set; }
        public string Message { get; set; } = "";
    }
}

// Token offsets allow replacing only owned values, preserving JSONC comments and other providers.
internal static class JsonC
{
    sealed record Property(string Name, int KeyStart, int ValueStart, int End);
    static (byte[] Bytes, List<Property> Properties, int Close) Parse(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text); var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) throw new ArgumentException("OpenCode 配置必须是 JSON/JSONC 对象。");
        var props = new List<Property>();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName) throw new JsonException();
            var name = reader.GetString()!; var start = (int)reader.TokenStartIndex; reader.Read(); var value = (int)reader.TokenStartIndex;
            reader.Skip(); props.Add(new(name, start, value, (int)reader.BytesConsumed));
        }
        var close = (int)reader.TokenStartIndex;
        if (reader.Read()) throw new JsonException("配置含有额外 JSON 内容。");
        if (props.GroupBy(p => p.Name).Any(g => g.Count() > 1)) throw new ArgumentException("配置包含重复键，未修改。");
        return (bytes, props, close);
    }
    internal static string? Get(string text, string[] path)
    {
        var (bytes, props, _) = Parse(text); var p = props.SingleOrDefault(p => p.Name == path[0]);
        if (p is null) return null;
        var value = Encoding.UTF8.GetString(bytes[p.ValueStart..p.End]);
        return path.Length == 1 ? value : Get(value, path[1..]);
    }
    internal static string Set(string text, string[] path, string? value)
    {
        var (bytes, props, close) = Parse(text); var p = props.SingleOrDefault(p => p.Name == path[0]);
        string Decode(int start, int end) => Encoding.UTF8.GetString(bytes[start..end]);
        if (path.Length > 1)
        {
            if (p is null && value is null) return text;
            var nested = Set(p is null ? "{}" : Decode(p.ValueStart, p.End), path[1..], value);
            return Set(text, [path[0]], nested);
        }
        if (p is not null && value is not null) return Decode(0, p.ValueStart) + value + Decode(p.End, bytes.Length);
        if (p is not null)
        {
            // Remove an adjacent comma without discarding surrounding comments.
            var start = p.KeyStart; var end = p.End;
            var trailing = new Utf8JsonReader(bytes.AsSpan(end), new JsonReaderOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            var scan = end;
            while (scan < close && char.IsWhiteSpace((char)bytes[scan])) scan++;
            if (scan < close && bytes[scan] == ',') return Decode(0, start) + Decode(end, scan) + Decode(scan + 1, bytes.Length);
            var previous = props.TakeWhile(x => x != p).LastOrDefault();
            if (previous is not null)
            {
                var comma = Array.IndexOf(bytes, (byte)',', previous.End, start - previous.End);
                if (comma >= 0) return Decode(0, comma) + Decode(comma + 1, start) + Decode(end, bytes.Length);
            }
            return Decode(0, start) + Decode(end, bytes.Length);
        }
        if (value is null) return text;
        var hasTrailing = props.Count > 0 && Decode(props[^1].End, close).TrimStart().StartsWith(',');
        return Decode(0, close) + (props.Count > 0 && !hasTrailing ? "," : "") + "\n  " + JsonSerializer.Serialize(path[0]) + ": " + value + "\n" + Decode(close, bytes.Length);
    }
}
