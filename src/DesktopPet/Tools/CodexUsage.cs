using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace DesktopPet.Tools;

internal sealed record UsageWindow(double? Remaining, int? Minutes, DateTimeOffset? ResetsAt);
internal sealed record UsageBucket(string Name, UsageWindow? Primary, UsageWindow? Secondary, string? Credits);
internal sealed record CodexUsage(DateTimeOffset RetrievedAt, IReadOnlyList<UsageBucket> Buckets)
{
    internal static CodexUsage Parse(JsonElement result)
    {
        if (result.ValueKind != JsonValueKind.Object) throw new InvalidDataException("额度回复格式无效。");
        static string? String(JsonElement element, string key) => element.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        static UsageWindow? Window(JsonElement bucket, string key)
        {
            if (!bucket.TryGetProperty(key, out var value) || value.ValueKind != JsonValueKind.Object) return null;
            double? remaining = value.TryGetProperty("usedPercent", out var used) && used.ValueKind == JsonValueKind.Number && used.TryGetDouble(out var n) && double.IsFinite(n) && n >= 0 ? Math.Clamp(100 - n, 0, 100) : null;
            int? minutes = value.TryGetProperty("windowDurationMins", out var duration) && duration.ValueKind == JsonValueKind.Number && duration.TryGetInt32(out var m) && m > 0 ? m : null;
            DateTimeOffset? reset = value.TryGetProperty("resetsAt", out var at) && at.ValueKind == JsonValueKind.Number && at.TryGetInt64(out var seconds) && seconds is >= 0 and <= 253402300799 ? DateTimeOffset.FromUnixTimeSeconds(seconds) : null;
            return new(remaining, minutes, reset);
        }
        static UsageBucket Bucket(JsonElement value, string fallback)
        {
            var name = String(value, "limitName") ?? String(value, "limitId") ?? fallback;
            name = name.Length > 80 ? name[..80] : name;
            string? credit = null;
            if (value.TryGetProperty("credits", out var credits) && credits.ValueKind == JsonValueKind.Object)
            {
                if (credits.TryGetProperty("unlimited", out var unlimited) && unlimited.ValueKind == JsonValueKind.True) credit = "积分：不限量";
                else if (decimal.TryParse(String(credits, "balance"), NumberStyles.Number, CultureInfo.InvariantCulture, out var balance) && balance >= 0)
                    credit = $"积分：{balance:0.##}";
            }
            return new(name.Equals("codex", StringComparison.OrdinalIgnoreCase) ? "Codex" : name, Window(value, "primary"), Window(value, "secondary"), credit);
        }
        var buckets = new List<UsageBucket>();
        if (result.TryGetProperty("rateLimitsByLimitId", out var map) && map.ValueKind == JsonValueKind.Object)
            foreach (var property in map.EnumerateObject().OrderBy(p => p.Name != "codex").ThenBy(p => p.Name, StringComparer.Ordinal).Take(20))
                if (property.Value.ValueKind == JsonValueKind.Object) buckets.Add(Bucket(property.Value, property.Name));
        if (buckets.Count == 0 && result.TryGetProperty("rateLimits", out var single) && single.ValueKind == JsonValueKind.Object)
            buckets.Add(Bucket(single, "Codex"));
        return new(DateTimeOffset.UtcNow, buckets);
    }
}

internal static class CodexUsageClient
{
    internal static string? FindExecutable()
    {
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (!Path.IsPathFullyQualified(directory.Trim('"'))) continue;
            var path = Path.Combine(directory.Trim('"'), "codex.exe");
            if (File.Exists(path)) return path;
        }
        var installed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenAI", "Codex", "bin");
        if (!Directory.Exists(installed)) return null;
        return Directory.EnumerateDirectories(installed).Select(d => Path.Combine(d, "codex.exe")).Where(File.Exists)
            .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
    }

    internal static async Task<CodexUsage> ReadAsync(CancellationToken cancellationToken)
    {
        var executable = FindExecutable() ?? throw new FileNotFoundException("请先安装 Codex，并使用 ChatGPT 账号登录。");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); deadline.CancelAfter(TimeSpan.FromSeconds(25));
        var token = deadline.Token;
        using var process = new Process { StartInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
        } };
        process.StartInfo.ArgumentList.Add("app-server"); process.StartInfo.ArgumentList.Add("--listen"); process.StartInfo.ArgumentList.Add("stdio://");
        process.ErrorDataReceived += (_, _) => { }; // Never persist CLI output, account identifiers, or authentication details.
        if (!process.Start()) throw new IOException("无法启动 Codex 额度读取。");
        process.BeginErrorReadLine();
        try
        {
            async Task Write(object message) { await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(message).AsMemory(), token); await process.StandardInput.FlushAsync(token); }
            async Task<JsonElement> Response(int id)
            {
                while (await process.StandardOutput.ReadLineAsync(token) is { } line)
                {
                    if (line.Length > 2_000_000) throw new InvalidDataException();
                    using var document = JsonDocument.Parse(line); var root = document.RootElement;
                    if (!root.TryGetProperty("id", out var requestId) || requestId.ValueKind != JsonValueKind.Number || !requestId.TryGetInt32(out var number) || number != id) continue;
                    if (root.TryGetProperty("error", out _)) throw new InvalidOperationException("无法读取额度，请确认 Codex 已使用 ChatGPT 账号登录，然后刷新。");
                    if (!root.TryGetProperty("result", out var result)) throw new InvalidDataException("额度回复缺少结果。");
                    return result.Clone();
                }
                throw new IOException("Codex 额度连接已断开，请稍后刷新。");
            }
            await Write(new { id = 1, method = "initialize", @params = new { clientInfo = new { name = "desktop_pet_usage", title = "DesktopPet", version = AppInfo.Version }, capabilities = new { experimentalApi = false } } });
            await Response(1);
            await Write(new { method = "initialized" });
            await Write(new { id = 2, method = "account/rateLimits/read" });
            return CodexUsage.Parse(await Response(2));
        }
        finally
        {
            // Own process only; do not stop the user's Codex app or its daemon.
            try { process.StandardInput.Close(); }
            catch (Exception e) when (e is IOException or InvalidOperationException) { }
            try
            {
                using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try { await process.WaitForExitAsync(stop.Token); }
                catch (OperationCanceledException)
                {
                    if (!process.HasExited) process.Kill(entireProcessTree: true);
                    using var killed = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    try { await process.WaitForExitAsync(killed.Token); } catch (OperationCanceledException) { }
                }
            }
            catch (Exception e) when (e is IOException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }
    }
}
