using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DesktopPet.Tools;
using PetLocalModels;

internal static class FeatureChecks
{
    static void Check(bool value, string label) { if (!value) throw new Exception(label); Console.WriteLine("PASS " + label); }
    static async Task Reject(Func<Task> action, string label) { try { await action(); } catch (Exception ex) when (ex is ArgumentException or InvalidDataException or InvalidOperationException) { Check(true, label); return; } throw new Exception("accepted: " + label); }
    public static async Task Run(Gateway gateway, string root, FakeOllama handler)
    {
        foreach (var (gib, expected) in new[] { (8UL, 16384), (16UL, 32768), (24UL, 49152), (48UL, 65536) }) Check(ContextRecommendation.ForMemory(gib * 1073741824) == expected, "recommendation " + gib + " GiB");
        Check(ContextRecommendation.ForMemory(null) == 32768, "unknown hardware fallback 32K");
        var originalTags = handler.Tags;
        handler.Tags = handler.Tags.Append("discovered:4b").ToArray();
        Check((await gateway.Catalog(default)).Any(m => m!["id"]!.GetValue<string>() == "discovered:4b"), "Ollama newly added models discovered");
        await gateway.Settings(new JsonObject { ["model"] = "qwen:9b", ["context"] = 65536 }, default);
        handler.Tags = handler.Tags.Where(t => t != "qwen:9b").ToArray();
        Check(JsonSerializer.SerializeToNode(await gateway.Status(default))!["missingModel"]!.GetValue<bool>(), "deleted selection stays unavailable without automatic switch");
        handler.Tags = originalTags;
        var reasoningInput = new JsonObject { ["reasoning_effort"] = "high" }; var reasoningRequest = new JsonObject { ["think"] = true };
        Gateway.ApplyThinking(reasoningInput, reasoningRequest, new JsonObject { ["thinking"] = new JsonObject { ["values"] = new JsonArray("low", "medium", "high") } });
        Check(reasoningRequest["think"]!.GetValue<string>() == "high", "named reasoning levels follow model metadata");
        reasoningInput["reasoning_effort"] = "none";
        await Reject(() => { Gateway.ApplyThinking(reasoningInput, reasoningRequest, new JsonObject { ["thinking"] = new JsonObject { ["values"] = new JsonArray("low", "high") } }); return Task.CompletedTask; }, "reject unsupported thinking off");
        Check(ModelRuntime.ValidateEndpoint("localhost:11434") == "http://localhost:11434", "loopback custom address");
        foreach (var bad in new[] { "https://localhost:11434", "http://192.168.1.1:11434", "http://localhost:11434/api", "http://user:password@localhost" }) await Reject(() => { ModelRuntime.ValidateEndpoint(bad); return Task.CompletedTask; }, "reject non-local endpoint");
        var text = "{\n // keep comment\n \"provider\": {\"cloud\": {\"private\": \"unchanged\"}},\n \"model\": \"cloud/default\",\n \"theme\": \"light\",\n}\n";
        var path = Path.Combine(root, "opencode.jsonc"); File.WriteAllText(path, text);
        var integration = new ClientIntegration(gateway, Path.Combine(root, "clients.json"));
        async Task<JsonObject> Preview(string kind, string file, bool setDefault = false) => JsonSerializer.SerializeToNode(await integration.Preview(new JsonObject { ["kind"] = kind, ["path"] = file, ["model"] = "qwen:9b", ["setDefault"] = setDefault, ["autoSync"] = true }, default))!.AsObject();
        var preview = await Preview("opencode", path);
        Check(File.ReadAllText(path) == text, "preview never writes config");
        integration.Apply(new JsonObject { ["token"] = preview["token"]!.DeepClone() });
        var saved = File.ReadAllText(path);
        Check(saved.Contains("// keep comment") && saved.Contains("\"private\": \"unchanged\"") && JsonC.Get(saved, ["model"]) == "\"cloud/default\"", "preserve JSONC comments private values and default");
        Check(Directory.GetFiles(root, "*.pet-backup-*").Length > 0, "backup before write");
        var catalog = new JsonArray(new JsonObject { ["id"] = "qwen:9b", ["context"] = 32768, ["capabilities"] = new JsonArray("completion", "tools", "vision", "thinking") }, new JsonObject { ["id"] = "new:7b", ["context"] = 16384, ["capabilities"] = new JsonArray("completion") });
        File.WriteAllText(path, saved.Replace("\"light\"", "\"dark\"")); integration.Synchronize(catalog);
        saved = File.ReadAllText(path);
        Check(saved.Contains("new:7b") && saved.Contains("\"dark\"") && JsonNode.Parse(JsonC.Get(saved, ["provider", "pet-local"])!)!["models"]!["qwen:9b"]!["limit"]!["context"]!.GetValue<int>() == 32768, "sync new models context and preserve unrelated edits");
        var edited = JsonNode.Parse(JsonC.Get(saved, ["provider", "pet-local"])!)!.AsObject(); edited["name"] = "My models";
        File.WriteAllText(path, JsonC.Set(saved, ["provider", "pet-local"], edited.ToJsonString())); integration.Synchronize(catalog);
        Check(JsonSerializer.SerializeToNode(integration.Status())!["targets"]![0]!["paused"]!.GetValue<bool>(), "manual owned edit pauses sync");
        Check(File.ReadAllText(path).Contains("My models"), "conflict never overwrites manual edits");
        var restore = await Preview("opencode", path); integration.Apply(new JsonObject { ["token"] = restore["token"]!.DeepClone() });
        integration.Detach(new JsonObject { ["path"] = path }); saved = File.ReadAllText(path);
        Check(JsonC.Get(saved, ["provider", "pet-local"]) is null && saved.Contains("\"dark\""), "detach only owned provider");
        preview = await Preview("opencode", path, true); integration.Apply(new JsonObject { ["token"] = preview["token"]!.DeepClone() });
        Check(JsonC.Get(File.ReadAllText(path), ["model"]) == "\"pet-local/qwen:9b\"", "explicit default opt in");
        integration.Synchronize(new JsonArray(new JsonObject { ["id"] = "new:7b", ["context"] = 16384 }));
        Check(JsonSerializer.SerializeToNode(integration.Status())!["targets"]![0]!["paused"]!.GetValue<bool>(), "missing default model pauses sync");
        integration.Detach(new JsonObject { ["path"] = path });
        Check(JsonC.Get(File.ReadAllText(path), ["model"]) == "\"cloud/default\"", "detach restores original default");
        preview = await Preview("opencode", path); File.AppendAllText(path, "\n// concurrent edit");
        await Reject(() => { integration.Apply(new JsonObject { ["token"] = preview["token"]!.DeepClone() }); return Task.CompletedTask; }, "stale preview rejects write");
        foreach (var sample in new[] { "{\"a\":1,\"b\":2}", "{\"a\":1,\"b\":2,}", "{\"a\":1,\"b\":2,\"c\":3}" }) Check(JsonC.Get(JsonC.Set(sample, ["b"], null), ["a"]) == "1", "JSONC removal commas");
        var yaml = "# private comment\n- id: llm-pi-ai\n  config:\n    timeoutMs: 1234\n    providers:\n      cloud:\n        apiKeyEnv: PRIVATE_ENV\n        models: []\n- id: agent-default-model\n  config:\n    provider: cloud\n    model: default\n- insert:\n    - id: custom\n      config: !!js function() {}\n";
        var ypath = Path.Combine(root, "cordis.patch.yml"); File.WriteAllText(ypath, yaml);
        preview = await Preview("dsh", ypath, true); integration.Apply(new JsonObject { ["token"] = preview["token"]!.DeepClone() });
        saved = File.ReadAllText(ypath);
        Check(saved.Contains("apiKeyEnv: PRIVATE_ENV") && saved.Contains("timeoutMs: 1234") && saved.Contains("!!js function() {}") && saved.Contains("Authorization: Bearer pet-local"), "DSH preserves other providers fields JS and uses non-secret placeholder");
        integration.Synchronize(catalog); Check(File.ReadAllText(ypath).Contains("contextWindow: 32768"), "DSH context budget synchronization");
        integration.Detach(new JsonObject { ["path"] = ypath });
        Check(File.ReadAllText(ypath) == yaml, "DSH detach restores owned subtree only");
        var clean = Path.Combine(root, "new-profile.yml"); File.WriteAllText(clean, "# clean profile\n");
        preview = await Preview("dsh", clean, true); integration.Apply(new JsonObject { ["token"] = preview["token"]!.DeepClone() });
        Check(File.ReadAllText(clean).Contains("providers:"), "new DSH profile initialization");
        integration.Detach(new JsonObject { ["path"] = clean });
        Check(!File.ReadAllText(clean).Contains("agent-default-model"), "detach removes added default entry");
        Check(File.ReadAllText(clean).TrimEnd() == "# clean profile", "detach removes plugin-created empty DSH entry");
        await PackageChecks(root);
        Console.WriteLine("Evidence directory: " + root);
    }
    static async Task PackageChecks(string root)
    {
        string MakeZip(string name, string entryName = "payload/DesktopPet.ModelGateway.exe", bool wrongHash = false, string minimum = "0.3.1.4")
        {
            var file = Path.Combine(root, name + ".zip"); var content = Encoding.UTF8.GetBytes("fixture only, never executed");
            var manifest = new LocalModelPackage.Manifest { Id = "pet-local-model", Version = "1.0.0", MinimumPetVersion = minimum, Files = new() { [entryName] = wrongHash ? new string('0', 64) : Convert.ToHexString(SHA256.HashData(content)) } };
            if (entryName != "payload/DesktopPet.ModelGateway.exe") manifest.Files["payload/DesktopPet.ModelGateway.exe"] = Convert.ToHexString(SHA256.HashData(content));
            using var archive = ZipFile.Open(file, ZipArchiveMode.Create);
            using (var writer = new StreamWriter(archive.CreateEntry("plugin.json").Open())) writer.Write(JsonSerializer.Serialize(manifest));
            using (var stream = archive.CreateEntry(entryName).Open()) stream.Write(content);
            return file;
        }
        await Reject(async () => await LocalModelPackage.Install(MakeZip("wrong-hash", wrongHash: true), default), "package file digest mismatch");
        await Reject(async () => await LocalModelPackage.Install(MakeZip("traversal", "payload/../../escape.exe"), default), "package traversal rejected");
        await Reject(async () => await LocalModelPackage.Install(MakeZip("future", minimum: "99.0.0.0"), default), "incompatible host rejected");
        var installed = await LocalModelPackage.Install(MakeZip("valid"), default);
        Check(File.Exists(Path.Combine(installed.Directory, "DesktopPet.ModelGateway.exe")) && installed.Version == "1.0.0", "verified package stages and atomically installs");
        var installedRoot = Path.GetFullPath(Path.GetDirectoryName(installed.Directory)!);
        var expectedRoot = Path.GetFullPath(Path.Combine(ModelRuntime.DataRoot, "plugins", "pet-local-model")) + Path.DirectorySeparatorChar;
        if (!installedRoot.StartsWith(expectedRoot, StringComparison.OrdinalIgnoreCase)) throw new Exception("fixture cleanup escaped its package root");
        Directory.Delete(installedRoot, true);
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        try { await LocalModelPackage.Install(MakeZip("canceled"), canceled.Token); throw new Exception("cancel ignored"); } catch (OperationCanceledException) { Check(true, "package installation cancellation"); }
    }
}
