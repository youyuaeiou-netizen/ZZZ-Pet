using System.Text.Json;
using System.Text.Json.Nodes;
using PetLocalModels;

internal static class GatewayHost
{
    internal static WebApplication Create(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.WebHost.UseUrls(ModelRuntime.GatewayEndpoint);
        builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 16 * 1024 * 1024);
        builder.Logging.ClearProviders(); // Never log conversation bodies or credentials.
        builder.Services.AddSingleton<Gateway>(); builder.Services.AddSingleton<ClientIntegration>();
        builder.Services.AddHostedService<ModelSync>();
        var app = builder.Build();
        var gateway = app.Services.GetRequiredService<Gateway>();
        var integration = app.Services.GetRequiredService<ClientIntegration>();
        app.Use(async (ctx, next) =>
        {
            if (ctx.Request.Headers.ContainsKey("Origin") || ctx.Request.Headers.ContainsKey("Sec-Fetch-Site")) { ctx.Response.StatusCode = 403; return; }
            var inference = ctx.Request.Path is var path && (path == "/v1/chat/completions" || path == "/control/load" || path == "/control/unload");
            if (inference) gateway.RequestStarted();
            try { await next(ctx); }
            catch (OperationCanceledException) when (ctx.RequestAborted.IsCancellationRequested) { }
            catch (Exception ex)
            {
                if (ctx.Response.HasStarted) { ctx.Abort(); return; }
                if (ctx.Response.StatusCode < 400) ctx.Response.StatusCode = ex is ArgumentException or JsonException ? 400 : 502;
                await ctx.Response.WriteAsJsonAsync(new { error = new { message = ex is HttpRequestException ? "Ollama 服务不可用，请在 Pet 中启动服务。" : ex.Message, type = "local_gateway_error" } });
            }
            finally { if (inference) gateway.RequestEnded(); }
        });
        app.MapGet("/v1/models", async (CancellationToken ct) => new { @object = "list", data = ClientIntegration.StableCatalog(await gateway.Catalog(ct)).Select(m => new { id = m!["id"]!.GetValue<string>(), @object = "model", owned_by = "pet-local" }) });
        app.MapGet("/control/status", gateway.Status);
        app.MapGet("/control/environment", () => ModelRuntime.Discover());
        app.MapPost("/control/environment", (Delegate)(async (HttpContext ctx) => { var config = (await Read(ctx)).Deserialize<ModelRuntime>(new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? throw new ArgumentException("缺少环境设置。"); config.Validate(); gateway.Configure(config); config.Save(); return Results.Ok(new { saved = true }); }));
        app.MapPost("/control/restart", () => { if (!gateway.Idle) return Results.Conflict(new { error = new { message = "当前有请求，插件已安装；请请求结束后重试启动网关。" } }); app.Lifetime.StopApplication(); return Results.Ok(); });
        app.MapGet("/control/clients", () => integration.Status());
        app.MapPost("/control/clients/preview", (Delegate)(async (HttpContext ctx) => Results.Json(await integration.Preview(await Read(ctx), ctx.RequestAborted))));
        app.MapPost("/control/clients/apply", (Delegate)(async (HttpContext ctx) => Results.Json(integration.Apply(await Read(ctx)))));
        app.MapPost("/control/clients/detach", (Delegate)(async (HttpContext ctx) => Results.Json(integration.Detach(await Read(ctx)))));
        app.MapPost("/control/service/start", async (CancellationToken ct) => { await gateway.StartService(ct); return Results.Ok(new { started = true }); });
        app.MapPost("/control/settings", (Delegate)(async (HttpContext ctx) => Results.Json(await gateway.Settings(await Read(ctx), ctx.RequestAborted))));
        app.MapPost("/control/load", (Delegate)(async (HttpContext ctx) => { await gateway.Residency(await Read(ctx), true, ctx.RequestAborted); return Results.Ok(new { loaded = true }); }));
        app.MapPost("/control/unload", (Delegate)(async (HttpContext ctx) => { await gateway.Residency(await Read(ctx), false, ctx.RequestAborted); return Results.Ok(new { unloaded = true }); }));
        app.MapPost("/v1/chat/completions", async (HttpContext ctx) => await gateway.Chat(ctx, await Read(ctx)));
        return app;
    }
    static async Task<JsonObject> Read(HttpContext ctx) => (await JsonNode.ParseAsync(ctx.Request.Body, cancellationToken: ctx.RequestAborted))?.AsObject() ?? throw new ArgumentException("缺少 JSON 请求。");
    sealed class ModelSync(Gateway gateway, ClientIntegration integration) : BackgroundService
    {
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (ModelRuntime.Read().LoginStartup) try { await gateway.StartService(stoppingToken); } catch { /* Native status page offers recovery. */ }
            await integration.Run(stoppingToken);
        }
    }
}
