using System.Net;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;

if (args.Length >= 2 && args[0] == "--native-acceptance")
{
    await NativeAcceptance.Run(args[1], args.Length > 2 ? int.Parse(args[2]) : 300); return;
}

if (args.Length == 2 && args[0] == "--install-package")
{
    var installed = await DesktopPet.Tools.LocalModelPackage.Install(args[1], default);
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { installed.Directory, installed.Version }));
    return;
}

static void Check(bool value, string label) { if (!value) throw new Exception(label); Console.WriteLine("PASS " + label); }
static JsonObject Input(bool stream = false) => new() { ["model"] = "qwen:9b", ["stream"] = stream, ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "hello" }) };
static DefaultHttpContext Context(CancellationToken ct = default)
{
    var context = new DefaultHttpContext { RequestAborted = ct };
    context.Response.Body = new MemoryStream(); return context;
}
var path = Path.Combine(Path.GetTempPath(), "pet-gateway-test-" + Guid.NewGuid().ToString("N"), "settings.json");
var handler = new FakeOllama();
var client = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:11434"), Timeout = Timeout.InfiniteTimeSpan };
var gateway = new Gateway(client, path);
foreach (var n in new[] { 65536, 131072, 204800 })
{
    await gateway.Settings(new JsonObject { ["model"] = "qwen:9b", ["context"] = n }, default);
    await gateway.Chat(Context(), Input());
    Check(handler.LastContext == n, $"num_ctx={n}");
}
var state = JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(await new Gateway(client, path).Status(default)))!;
Check(state["context"]!.GetValue<int>() == 204800, "settings persistence");
await gateway.Settings(new JsonObject { ["model"] = "qwen:9b-128k", ["context"] = 65536 }, default);
await gateway.Chat(Context(), Input()); Check(handler.LastContext == 65536, "legacy aliases share family context");
try { await gateway.Settings(new JsonObject { ["model"] = "qwen:9b", ["context"] = 263168 }, default); throw new Exception("accepted invalid capacity"); }
catch (ArgumentException) { Check(true, "capacity validation"); }
var toolInput = JsonNode.Parse("""
{"model":"qwen:9b","messages":[{"role":"assistant","content":null,"tool_calls":[{"id":"c1","type":"function","function":{"name":"echo","arguments":"{\"text\":\"ok\"}"}}]},{"role":"tool","tool_call_id":"c1","content":"ok"}]}
""")!.AsObject();
var translated = Gateway.Translate(toolInput);
Check(translated["messages"]![0]!["tool_calls"]![0]!["function"]!["arguments"]!["text"]!.GetValue<string>() == "ok", "tool arguments decoded");
Check(translated["messages"]![1]!["tool_name"]!.GetValue<string>() == "echo", "tool result name mapping");
var stream = Context(); await gateway.Chat(stream, Input(true));
stream.Response.Body.Position = 0;
var sse = await new StreamReader(stream.Response.Body).ReadToEndAsync();
Check(sse.Contains("\"tool_calls\"") && sse.Contains("\"index\":0") && sse.EndsWith("data: [DONE]\n\n"), "stream tools and DONE");
handler.Block = true;
using var cancel = new CancellationTokenSource();
var first = gateway.Chat(Context(cancel.Token), Input());
await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
var unload = gateway.Residency(new JsonObject { ["model"] = "qwen:9b" }, false, default);
using var queuedCancel = new CancellationTokenSource();
var queued = gateway.Chat(Context(queuedCancel.Token), Input());
queuedCancel.Cancel();
try { await queued; throw new Exception("queued cancellation ignored"); } catch (OperationCanceledException) { }
Check(!unload.IsCompleted && handler.Unloads == 0, "unload waits active request");
cancel.Cancel();
try { await first; throw new Exception("active cancellation ignored"); } catch (OperationCanceledException) { }
await unload.WaitAsync(TimeSpan.FromSeconds(3));
Check(handler.Unloads == 1, "cancel releases queue and unload completes");
handler.Block = false;
await gateway.Chat(Context(), Input()); Check(true, "subsequent request works after cancellation/unload");
await FeatureChecks.Run(gateway, Path.GetDirectoryName(path)!, handler);
File.Delete(path);

sealed class FakeOllama : HttpMessageHandler
{
    public string[] Tags = ["qwen:9b", "qwen:9b-128k"];
    public int LastContext, Unloads;
    public bool Block;
    public TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        string body;
        switch (request.RequestUri!.AbsolutePath)
        {
            case "/api/tags": body = System.Text.Json.JsonSerializer.Serialize(new { models = Tags.Select(name => new { name }) }); break;
            case "/api/show": body = "{\"model_info\":{\"qwen.context_length\":262144}}"; break;
            case "/api/ps": body = "{\"models\":[]}"; break;
            case "/api/generate": Unloads++; body = "{\"done\":true}"; break;
            default:
                var input = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!;
                LastContext = input["options"]!["num_ctx"]!.GetValue<int>();
                if (Block) { Entered.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
                body = input["stream"]!.GetValue<bool>() ? "{\"message\":{\"content\":\"\",\"tool_calls\":[{\"function\":{\"name\":\"echo\",\"arguments\":{\"text\":\"ok\"}}}]},\"done\":false}\n{\"message\":{\"content\":\"\"},\"done\":true}\n" : "{\"message\":{\"role\":\"assistant\",\"content\":\"ok\"},\"done\":true}";
                break;
        }
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
    }
}
