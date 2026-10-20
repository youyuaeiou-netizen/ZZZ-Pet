using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Text.Json;
using PetLocalModels;

if (args.Contains("--ollama-service"))
{
    await OllamaService.Run();
    return;
}

await using var app = GatewayHost.Create(args);
await app.RunAsync();

sealed class Gateway
{
    HttpClient http;
    readonly bool injected;
    readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTime Time, JsonObject Data)> metadata = new();
    readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> families = new();
    readonly SemaphoreSlim serial = new(1);
    readonly object settingsLock = new();
    readonly string settingsPath;
    string selected = "";
    readonly Dictionary<string, int> contexts = new(StringComparer.Ordinal);
    int queued;
    int requests;
    string? active;
    public bool Idle => active is null && Volatile.Read(ref queued) == 0 && Volatile.Read(ref requests) == 0;
    public void RequestStarted() => Interlocked.Increment(ref requests);
    public void RequestEnded() => Interlocked.Decrement(ref requests);
    public Gateway(HttpClient? client = null, string? statePath = null)
    {
        injected = client is not null;
        http = client ?? new HttpClient { BaseAddress = new Uri(ModelRuntime.Read().Endpoint), Timeout = Timeout.InfiniteTimeSpan };
        settingsPath = statePath ?? Path.Combine(ModelRuntime.DataRoot, "model-gateway.json");
        if (!File.Exists(settingsPath)) return;
        var state = JsonNode.Parse(File.ReadAllText(settingsPath))!.AsObject();
        selected = state["model"]?.GetValue<string>() ?? "";
        if (state["contexts"] is JsonObject values)
            foreach (var p in values) if (p.Value is not null) contexts[p.Key] = p.Value.GetValue<int>();
    }
    public void Configure(ModelRuntime config)
    {
        if (!Idle) throw new ArgumentException("请求运行中，请结束后修改服务地址。");
        if (injected) throw new ArgumentException("测试实例不可修改服务地址。");
        http = new HttpClient { BaseAddress = new Uri(config.Endpoint), Timeout = Timeout.InfiniteTimeSpan }; metadata.Clear();
    }
    async Task<JsonObject> Metadata(string model, CancellationToken ct)
    {
        if (metadata.TryGetValue(model, out var cached) && DateTime.UtcNow - cached.Time < TimeSpan.FromSeconds(30)) return cached.Data;
        using var response = await http.PostAsJsonAsync("/api/show", new { model }, ct);
        var data = await Decode(response, ct); metadata[model] = (DateTime.UtcNow, data); return data;
    }
    public async Task<JsonArray> Catalog(CancellationToken ct)
    {
        var models = await Models(ct); var result = new JsonArray();
        var descriptions = new List<(string Model, JsonObject Data)>();
        foreach (var model in models) descriptions.Add((model, await Metadata(model, ct)));
        foreach (var model in models) families[model] = model;
        foreach (var group in descriptions.GroupBy(p => WeightIdentity(p.Data)))
        {
            if (group.Key is null) continue;
            var canonical = group.Select(p => p.Model).OrderBy(m => ContextRecommendation.BaseModel(m) == m ? 0 : 1).ThenBy(VariantOrder).ThenBy(m => m, StringComparer.Ordinal).First();
            foreach (var item in group) families[item.Model] = canonical;
        }
        foreach (var group in descriptions.GroupBy(p => ContextRecommendation.BaseModel(p.Model)).Where(g => g.Count() > 1 && g.Any(p => p.Model != g.Key)))
        {
            if (group.Select(p => WeightIdentity(p.Data)).Where(id => id is not null).Distinct(StringComparer.Ordinal).Count() > 1) continue;
            var canonical = group.Select(p => p.Model).OrderBy(m => m.Equals(group.Key, StringComparison.OrdinalIgnoreCase) ? 0 : 1).ThenBy(VariantOrder).ThenBy(m => m, StringComparer.Ordinal).First();
            foreach (var item in group) families[item.Model] = canonical;
        }
        foreach (var obsolete in families.Keys.Where(m => !models.Contains(m)).ToArray()) families.TryRemove(obsolete, out _);
        foreach (var model in models)
        {
            var data = await Metadata(model, ct); var capacity = await Capacity(model, ct);
            var capabilities = data["capabilities"]?.DeepClone();
            if (capabilities is JsonArray caps && !caps.Any(c => c?.GetValue<string>() == "completion")) continue;
            var context = Context(model, capacity);
            lock (settingsLock) { if (capacity is not null && !contexts.ContainsKey(Family(model))) { contexts[Family(model)] = context; SaveSettings(); } }
            var tiers = new JsonArray(); foreach (var tier in ContextRecommendation.AvailableTiers(model, capacity)) tiers.Add(tier);
            result.Add(new JsonObject { ["id"] = model, ["name"] = ContextRecommendation.DisplayName(model), ["family"] = Family(model), ["context"] = capacity is null ? null : JsonValue.Create(context), ["capacity"] = capacity, ["maximumContext"] = ContextRecommendation.Maximum(model, capacity), ["contextTiers"] = tiers, ["capabilities"] = capabilities, ["thinking"] = data["thinking"]?.DeepClone() });
        }
        return result;
    }
    static int VariantOrder(string model)
    {
        var match = System.Text.RegularExpressions.Regex.Match(model, @"-(\d+)k$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return match.Success && int.TryParse(match.Groups[1].Value, out var value) ? value : int.MaxValue;
    }
    static string? WeightIdentity(JsonObject data)
    {
        var match = System.Text.RegularExpressions.Regex.Match(data["modelfile"]?.GetValue<string>() ?? "", @"(?im)^FROM\s+.*?sha256-([a-f0-9]{64})(?:[\s""']|$)");
        return match.Success ? match.Groups[1].Value.ToLowerInvariant() : null;
    }
    void SaveSettings()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
        File.WriteAllText(settingsPath + ".tmp", JsonSerializer.Serialize(new { model = selected, contexts })); File.Move(settingsPath + ".tmp", settingsPath, true);
    }
    async Task<JsonObject> Get(string url, CancellationToken ct)
    {
        using var response = await http.GetAsync(url, ct);
        return await Decode(response, ct);
    }
    static async Task<JsonObject> Decode(HttpResponseMessage response, CancellationToken ct)
    {
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"Ollama {(int)response.StatusCode}: {body[..Math.Min(body.Length, 600)]}");
        return JsonNode.Parse(body)!.AsObject();
    }
    public async Task<string[]> Models(CancellationToken ct) => (await Get("/api/tags", ct))["models"]!.AsArray().Select(m => m!["name"]!.GetValue<string>()).ToArray();
    async Task<int?> Capacity(string model, CancellationToken ct)
    {
        var info = (await Metadata(model, ct))["model_info"]?.AsObject();
        return info?.Where(p => p.Key.EndsWith(".context_length", StringComparison.Ordinal)).Select(p => p.Value?.GetValue<int>()).FirstOrDefault();
    }
    int Context(string model, int? capacity)
    {
        lock (settingsLock)
        {
            var key = Family(model);
            var legacy = System.Text.RegularExpressions.Regex.Replace(model, @"-\d+k$", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            var saved = contexts.TryGetValue(key, out var value) || key == model && contexts.TryGetValue(legacy, out value);
            var limit = ContextRecommendation.Maximum(model, capacity) ?? 32768;
            var desired = saved ? value : ContextRecommendation.ForMemory(ContextRecommendation.DedicatedBytes);
            var tiers = ContextRecommendation.AvailableTiers(model, capacity);
            var selected = tiers.LastOrDefault(t => t <= Math.Min(desired, limit));
            if (selected > 0) return selected;
            if (tiers.Length > 0) return tiers[0];
            return Math.Min(desired, limit);
        }
    }
    string Family(string model)
    {
        if (families.TryGetValue(model, out var family)) return family;
        var legacy = System.Text.RegularExpressions.Regex.Replace(model, @"-\d+k$", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        lock (settingsLock) return contexts.ContainsKey(legacy) ? legacy : model;
    }
    public async Task<object> Status(CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(4));
        try
        {
            var catalog = await Catalog(timeout.Token);
            var models = catalog.Select(m => m!["id"]!.GetValue<string>()).ToArray();
            string model;
            lock (settingsLock) model = selected.Length > 0 ? selected : models.FirstOrDefault(m => m.EndsWith("-64k")) ?? models.FirstOrDefault() ?? "";
            var missingModel = model.Length > 0 && !models.Contains(model);
            var capacity = model.Length == 0 || missingModel ? null : await Capacity(model, timeout.Token);
            var maximumContext = ContextRecommendation.Maximum(model, capacity);
            var contextTiers = new JsonArray(); foreach (var tier in ContextRecommendation.AvailableTiers(model, capacity)) contextTiers.Add(tier);
            var running = (await Get("/api/ps", timeout.Token))["models"];
            return new { gateway = "pet-local-model", gatewayVersion = "1.1.1", online = true, models, catalog, model, missingModel, capacity, maximumContext, contextTiers, context = Context(model, capacity), running, active, queued = Volatile.Read(ref queued), endpoint = http.BaseAddress!.ToString(), recommendedContext = ContextRecommendation.ForMemory(ContextRecommendation.DedicatedBytes), dedicatedBytes = ContextRecommendation.DedicatedBytes };
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        { return new { gateway = "pet-local-model", gatewayVersion = "1.0.0", online = false, model = selected, active, queued = Volatile.Read(ref queued), message = "Ollama 服务离线，可点击启动服务。" }; }
    }
    public async Task<object> Settings(JsonObject body, CancellationToken ct)
    {
        var model = await ValidateModel(body, ct);
        var capacity = await Capacity(model, ct);
        if (capacity is null && body["context"] is not null) throw new ArgumentException("模型容量元数据不可用，无法设置上下文。");
        var value = body["context"]?.GetValue<int>() ?? Context(model, capacity);
        var maximum = ContextRecommendation.Maximum(model, capacity);
        if (body["context"] is not null && (!ContextRecommendation.IsTier(value) || maximum is null || value > maximum)) throw new ArgumentException($"上下文仅支持 48K、64K、96K、128K 固定档位；该模型当前最高为 {((maximum ?? 0) / 1024)}K。");
        lock (settingsLock)
        {
            selected = model; if (capacity is not null) contexts[Family(model)] = value;
            SaveSettings();
        }
        return new { model, context = value };
    }
    async Task<string> ValidateModel(JsonObject body, CancellationToken ct)
    {
        var model = body["model"]?.GetValue<string>() ?? throw new ArgumentException("请选择模型。");
        if (!(await Models(ct)).Contains(model)) throw new ArgumentException("模型标签未安装。");
        return model;
    }
    public async Task StartService(CancellationToken ct)
    {
        using var probe = CancellationTokenSource.CreateLinkedTokenSource(ct);
        probe.CancelAfter(TimeSpan.FromSeconds(3));
        try { await Models(probe.Token); return; }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException && !ct.IsCancellationRequested) { }
        if (Process.GetProcessesByName("ollama").Length > 0)
            throw new InvalidOperationException("Ollama 进程已存在，但 API 无响应。请检查 Ollama 日志后重试。");
        ModelRuntime.Read().StartOllama();
    }
    async Task Enter(CancellationToken ct)
    {
        Interlocked.Increment(ref queued);
        try { await serial.WaitAsync(ct); }
        finally { Interlocked.Decrement(ref queued); }
    }
    public async Task Residency(JsonObject body, bool load, CancellationToken ct)
    {
        var model = await ValidateModel(body, ct);
        await Catalog(ct);
        await Enter(ct);
        try
        {
            active = load ? "加载模型" : "卸载模型";
            if (!load)
            {
                var running = (await Get("/api/ps", ct))["models"]!.AsArray();
                var tags = running.Select(n => n!["name"]!.GetValue<string>()).Where(tag => Family(tag) == Family(model)).Append(model).Distinct();
                foreach (var tag in tags)
                {
                    using var unload = await http.PostAsJsonAsync("/api/generate", new { model = tag, stream = false, keep_alive = 0 }, ct);
                    await Decode(unload, ct);
                }
                return;
            }
            var capacity = await Capacity(model, ct);
            var request = new JsonObject { ["model"] = model, ["stream"] = false, ["keep_alive"] = load ? JsonValue.Create(-1) : JsonValue.Create(0) };
            if (load) request["options"] = new JsonObject { ["num_ctx"] = Context(model, capacity) };
            using var response = await http.PostAsJsonAsync("/api/generate", request, ct);
            await Decode(response, ct);
        }
        finally { active = null; serial.Release(); }
    }
    public async Task Chat(HttpContext ctx, JsonObject input)
    {
        var ct = ctx.RequestAborted;
        var model = await ValidateModel(input, ct);
        var capacity = await Capacity(model, ct);
        if (capacity is null) throw new ArgumentException("模型容量元数据不可用，无法应用上下文设置。");
        // Snapshot before queuing: settings changes apply only to new requests.
        var context = Context(model, capacity);
        var request = Translate(input);
        ApplyThinking(input, request, await Metadata(model, ct));
        request["options"]!["num_ctx"] = context;
        request["keep_alive"] = -1;
        var streaming = input["stream"]?.GetValue<bool>() == true;
        request["stream"] = streaming;
        await Enter(ct);
        try
        {
            active = model;
            using var message = new HttpRequestMessage(HttpMethod.Post, "/api/chat") { Content = JsonContent.Create(request) };
            using var response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode) { ctx.Response.StatusCode = (int)response.StatusCode; await Decode(response, ct); }
            var id = "chatcmpl-" + Guid.NewGuid().ToString("N");
            var created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (!streaming)
            {
                var result = await Decode(response, ct);
                var index = 0;
                var output = OutputMessage(result["message"]!.AsObject(), false, ref index);
                await ctx.Response.WriteAsJsonAsync(new { id, @object = "chat.completion", created, model, choices = new[] { new { index = 0, message = output, finish_reason = Finish(result) } }, usage = Usage(result) }, ct);
                return;
            }
            ctx.Response.ContentType = "text/event-stream";
            ctx.Response.Headers.CacheControl = "no-cache";
            using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(ct));
            var toolIndex = 0;
            var hadTools = false;
            await Event(ctx, new { id, @object = "chat.completion.chunk", created, model, choices = new[] { new { index = 0, delta = new { role = "assistant" }, finish_reason = (string?)null } } }, ct);
            while (await reader.ReadLineAsync(ct) is { } line)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var result = JsonNode.Parse(line)!.AsObject();
                if (result["error"] is { } error) { await Event(ctx, new { error = new { message = error.GetValue<string>(), type = "ollama_error" } }, ct); break; }
                var delta = result["message"] is JsonObject output ? OutputMessage(output, true, ref toolIndex) : new JsonObject();
                hadTools |= delta["tool_calls"] is not null;
                var done = result["done"]?.GetValue<bool>() == true;
                await Event(ctx, new { id, @object = "chat.completion.chunk", created, model, choices = new[] { new { index = 0, delta, finish_reason = done ? hadTools ? "tool_calls" : Finish(result) : null } } }, ct);
                if (done)
                {
                    if (input["stream_options"]?["include_usage"]?.GetValue<bool>() == true)
                        await Event(ctx, new { id, @object = "chat.completion.chunk", created, model, choices = Array.Empty<object>(), usage = Usage(result) }, ct);
                    break;
                }
            }
            await ctx.Response.WriteAsync("data: [DONE]\n\n", ct);
            await ctx.Response.Body.FlushAsync(ct);
        }
        finally { active = null; serial.Release(); }
    }
    static async Task Event(HttpContext ctx, object value, CancellationToken ct)
    { await ctx.Response.WriteAsync("data: " + JsonSerializer.Serialize(value) + "\n\n", ct); await ctx.Response.Body.FlushAsync(ct); }
    static object Usage(JsonObject result)
    {
        var prompt = result["prompt_eval_count"]?.GetValue<int>() ?? 0;
        var completion = result["eval_count"]?.GetValue<int>() ?? 0;
        return new { prompt_tokens = prompt, completion_tokens = completion, total_tokens = prompt + completion };
    }
    static string Finish(JsonObject result) => result["message"]?["tool_calls"] is JsonArray a && a.Count > 0 ? "tool_calls" : result["done_reason"]?.GetValue<string>() == "length" ? "length" : "stop";
    static JsonObject OutputMessage(JsonObject native, bool stream, ref int index)
    {
        var output = new JsonObject();
        if (!stream) output["role"] = "assistant";
        if (native["content"] is { } content) output["content"] = content.DeepClone();
        if (native["thinking"] is { } thinking) output["reasoning_content"] = thinking.DeepClone();
        if (native["tool_calls"] is JsonArray calls && calls.Count > 0)
        {
            var mapped = new JsonArray();
            foreach (var call in calls)
            {
                var tool = new JsonObject { ["id"] = "call_" + Guid.NewGuid().ToString("N"), ["type"] = "function", ["function"] = new JsonObject { ["name"] = call!["function"]!["name"]!.DeepClone(), ["arguments"] = call["function"]!["arguments"]?.ToJsonString() ?? "{}" } };
                if (stream) tool["index"] = index++;
                mapped.Add(tool);
            }
            output["tool_calls"] = mapped;
        }
        return output;
    }
    internal static JsonObject Translate(JsonObject input)
    {
        var messages = new JsonArray();
        var toolNames = new Dictionary<string, string>();
        foreach (var original in input["messages"]?.AsArray() ?? throw new ArgumentException("缺少 messages。"))
        {
            var m = original!.DeepClone().AsObject();
            if (m["content"] is JsonArray parts)
            {
                var texts = new List<string>(); var images = new JsonArray();
                foreach (var part in parts)
                {
                    var type = part?["type"]?.GetValue<string>();
                    if (type == "text") texts.Add(part!["text"]!.GetValue<string>());
                    else if (type == "image_url")
                    {
                        var url = part!["image_url"]!["url"]!.GetValue<string>();
                        if (!url.StartsWith("data:image/") || !url.Contains(";base64,")) throw new ArgumentException("图片仅支持内嵌 base64，不支持远程 URL。");
                        images.Add(url[(url.IndexOf(',') + 1)..]);
                    }
                    else throw new ArgumentException("不支持的消息内容类型。");
                }
                m["content"] = string.Join("\n", texts);
                if (images.Count > 0) m["images"] = images;
            }
            m["content"] ??= JsonValue.Create("");
            if (m["reasoning_content"] is { } reasoning)
            { m["thinking"] = reasoning.DeepClone(); m.Remove("reasoning_content"); }
            if (m["role"]?.GetValue<string>() == "developer") m["role"] = "system";
            if (m["tool_calls"] is JsonArray calls)
                foreach (var call in calls)
                {
                    var f = call!["function"]!.AsObject();
                    if (call["id"] is { } id) toolNames[id.GetValue<string>()] = f["name"]!.GetValue<string>();
                    if (f["arguments"] is JsonValue arguments) f["arguments"] = JsonNode.Parse(arguments.GetValue<string>());
                    call.AsObject().Remove("id"); call.AsObject().Remove("type");
                }
            if (m["tool_call_id"] is { } toolId)
            { if (toolNames.TryGetValue(toolId.GetValue<string>(), out var name)) m["tool_name"] = name; m.Remove("tool_call_id"); }
            messages.Add(m);
        }
        var options = new JsonObject();
        foreach (var key in new[] { "temperature", "top_p", "seed", "stop" }) if (input[key] is { } value) options[key] = value.DeepClone();
        if ((input["max_completion_tokens"] ?? input["max_tokens"]) is { } max) options["num_predict"] = max.DeepClone();
        var request = new JsonObject { ["model"] = input["model"]!.DeepClone(), ["messages"] = messages, ["options"] = options };
        if (input["tools"] is { } tools && input["tool_choice"]?.ToJsonString() != "\"none\"") request["tools"] = tools.DeepClone();
        if (input["tool_choice"] is JsonObject || input["tool_choice"]?.ToJsonString() == "\"required\"") throw new ArgumentException("Ollama 不支持强制 tool_choice；请使用 auto。");
        if (input["response_format"]?["type"]?.GetValue<string>() == "json_object") request["format"] = "json";
        if (input["response_format"]?["type"]?.GetValue<string>() == "json_schema") request["format"] = input["response_format"]!["json_schema"]!["schema"]!.DeepClone();
        // An omitted effort follows the model default; explicit values control thinking.
        if (input["reasoning_effort"] is { } effort) request["think"] = effort.GetValue<string>() != "none";
        return request;
    }
    internal static void ApplyThinking(JsonObject input, JsonObject request, JsonObject description)
    {
        var effort = input["reasoning_effort"]?.GetValue<string>();
        if (effort is null) { request.Remove("think"); return; }
        if (description["thinking"]?["values"] is JsonArray values)
        {
            bool Has(JsonNode value) => values.Any(v => JsonNode.DeepEquals(v, value));
            if (effort is null or "none")
            {
                if (Has(JsonValue.Create(false))) request["think"] = false;
                else if (effort is null) request.Remove("think");
                else throw new ArgumentException("该模型不支持关闭推理，请选择模型支持的强度。");
            }
            else if (Has(JsonValue.Create(effort))) request["think"] = effort;
            else if (Has(JsonValue.Create(true))) request["think"] = true;
            else throw new ArgumentException("该模型不支持此推理强度，请重新选择。");
        }
        else if (description["capabilities"] is JsonArray caps && !caps.Any(c => c?.GetValue<string>() == "thinking")) request.Remove("think");
        else if (description["model_info"]?["general.architecture"]?.GetValue<string>() == "gptoss")
        {
            // Older Ollama versions omit thinking metadata for this named-level model.
            if (effort is null) request.Remove("think");
            else if (effort is "low" or "medium" or "high") request["think"] = effort;
            else throw new ArgumentException("此 GPT-OSS 模型支持 low / medium / high 推理强度。");
        }
    }
}

static class OllamaService
{
    public static async Task Run()
    {
        using var probe = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        var runtime = ModelRuntime.Read();
        try { using var response = await probe.GetAsync(runtime.Endpoint + "/api/tags"); if (response.IsSuccessStatusCode) return; }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { }
        var exe = runtime.FindExecutable();
        var logs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DesktopPet", "logs");
        Directory.CreateDirectory(logs);
        using var process = Process.Start(new ProcessStartInfo(exe, "serve")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
            RedirectStandardError = true, WorkingDirectory = Path.GetDirectoryName(exe)!, Environment = { ["OLLAMA_HOST"] = new Uri(runtime.Endpoint).Authority }
        }) ?? throw new InvalidOperationException("Ollama 启动失败。");
        await using var output = new FileStream(Path.Combine(logs, "ollama.stdout.log"), FileMode.Append, FileAccess.Write, FileShare.Read);
        await using var error = new FileStream(Path.Combine(logs, "ollama.stderr.log"), FileMode.Append, FileAccess.Write, FileShare.Read);
        await Task.WhenAll(process.StandardOutput.BaseStream.CopyToAsync(output), process.StandardError.BaseStream.CopyToAsync(error), process.WaitForExitAsync());
        Environment.ExitCode = process.ExitCode;
    }
}
