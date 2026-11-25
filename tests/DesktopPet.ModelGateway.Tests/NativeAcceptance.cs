using System.Net.Http.Json;
using System.Text.Json.Nodes;
using PetLocalModels;

internal static class NativeAcceptance
{
    internal static async Task Run(string root, int seconds = 300)
    {
        root = Path.GetFullPath(root); var data = Path.Combine(root, "data"); Directory.CreateDirectory(data);
        Environment.SetEnvironmentVariable("PET_MODEL_DATA_ROOT", data);
        Environment.SetEnvironmentVariable("PET_MODEL_GATEWAY_PORT", "11437");
        // All state and generated client configs remain in the acceptance directory.
        await using var app = GatewayHost.Create([]);
        await app.StartAsync();
        using var http = new HttpClient { BaseAddress = new Uri(ModelRuntime.GatewayEndpoint), Timeout = TimeSpan.FromSeconds(30) };
        var original = ModelRuntime.Read();
        using (var browserRequest = new HttpRequestMessage(HttpMethod.Get, "control/status"))
        {
            browserRequest.Headers.Add("Origin", "https://example.invalid"); using var denied = await http.SendAsync(browserRequest);
            if ((int)denied.StatusCode != 403) throw new Exception("Browser-origin control was not rejected.");
            Console.WriteLine("PASS production origin rejection");
        }
        using (var offline = await http.PostAsJsonAsync("control/environment", new { endpoint = "http://127.0.0.1:11439", loginStartup = false })) offline.EnsureSuccessStatusCode();
        var unavailable = await http.GetFromJsonAsync<JsonObject>("control/status");
        if (unavailable?["online"]?.GetValue<bool>() != false) throw new Exception("Offline endpoint not detected.");
        using (var offlineModels = await http.GetAsync("v1/models")) if ((int)offlineModels.StatusCode != 502) throw new Exception("Offline provider did not fail explicitly.");
        using (var restore = await http.PostAsJsonAsync("control/environment", original)) restore.EnsureSuccessStatusCode();
        Console.WriteLine("PASS production environment JSON binding offline state and recovery");
        var state = await http.GetFromJsonAsync<JsonObject>("control/status");
        if (state?["online"]?.GetValue<bool>() != true) throw new Exception("Native Ollama is offline.");
        var selected = state["models"]!.AsArray().Any(n => n!.GetValue<string>() == "qwen3.5:9b") ? "qwen3.5:9b" : state["model"]!.GetValue<string>();
        using (var settings = await http.PostAsJsonAsync("control/settings", new { model = selected, context = 65536 })) settings.EnsureSuccessStatusCode();
        foreach (var kind in new[] { "opencode", "dsh" })
        {
            var path = Path.Combine(root, kind == "dsh" ? "cordis.patch.yml" : "opencode.json");
            if (File.Exists(path)) throw new Exception("Use a new acceptance output directory.");
            await File.WriteAllTextAsync(path, kind == "dsh" ? "# isolated acceptance profile\n" : "{}\n");
            using var previewResponse = await http.PostAsJsonAsync("control/clients/preview", new { kind, path, model = selected, setDefault = true, autoSync = true }); previewResponse.EnsureSuccessStatusCode();
            var preview = await previewResponse.Content.ReadFromJsonAsync<JsonObject>();
            using var apply = await http.PostAsJsonAsync("control/clients/apply", new { token = preview!["token"]!.GetValue<string>() }); apply.EnsureSuccessStatusCode();
        }
        foreach (var context in new[] { 32768, 65536 })
        {
            using var settings = await http.PostAsJsonAsync("control/settings", new { model = selected, context }); settings.EnsureSuccessStatusCode();
            var deadline = DateTime.UtcNow.AddSeconds(20); bool synced;
            do
            {
                await Task.Delay(200);
                var config = JsonNode.Parse(JsonC.Get(await File.ReadAllTextAsync(Path.Combine(root, "opencode.json")), ["provider", "pet-local"])!)!;
                var yaml = await File.ReadAllTextAsync(Path.Combine(root, "cordis.patch.yml"));
                var idAt = yaml.IndexOf("- id: " + System.Text.Json.JsonSerializer.Serialize(selected), StringComparison.Ordinal);
                var budget = idAt < 0 ? 0 : int.Parse(System.Text.RegularExpressions.Regex.Match(yaml[idAt..], @"contextWindow:\s*(\d+)").Groups[1].Value);
                synced = config["models"]![selected]!["limit"]!["context"]!.GetValue<int>() == context && budget == context;
            } while (!synced && DateTime.UtcNow < deadline);
            if (!synced) throw new Exception("Background budget synchronization failed.");
            Console.WriteLine("PASS production background DSH/OpenCode budget sync " + context);
        }
        await File.WriteAllTextAsync(Path.Combine(root, "acceptance.txt"), "PET_LOCAL_PLUGIN_TEST_OK");
        Console.WriteLine("READY finite production host " + ModelRuntime.GatewayEndpoint);
        Console.WriteLine("Hardware recommendation " + state["recommendedContext"] + " bytes " + state["dedicatedBytes"]);
        Console.WriteLine("Verified families " + string.Join(",", state["catalog"]!.AsArray().Select(n => n!["family"]!.GetValue<string>()).Distinct()));
        // A foreground test lease; no OS task registration, installed program updates or persistent helper.
        await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(seconds, 10, 300)));
        await app.StopAsync();
        Console.WriteLine("PASS finite test host stopped");
    }
}
