using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using PetLocalModels;

namespace DesktopPet.Tools;

internal sealed class LocalModelPackage
{
    internal const string CatalogAsset = "pet-local-model-catalog.json";
    const string ReleaseApi = "https://api.github.com/repos/youyuaeiou-netizen/ZZZ-Pet/releases?per_page=20";
    static readonly HttpClient Http = CreateHttp();
    static HttpClient CreateHttp() { var http = new HttpClient { Timeout = TimeSpan.FromMinutes(20) }; http.DefaultRequestHeaders.UserAgent.ParseAdd("DesktopPet-PluginMarket/1.0"); return http; }
    public sealed class Catalog
    {
        public string Id { get; set; } = "";
        public string Version { get; set; } = "";
        public string Url { get; set; } = "";
        public string Sha256 { get; set; } = "";
    }
    public sealed class Manifest
    {
        public string Id { get; set; } = "";
        public string Version { get; set; } = "";
        public string MinimumPetVersion { get; set; } = "";
        public Dictionary<string, string> Files { get; set; } = new();
    }
    public static async Task<Catalog> FetchCatalog(CancellationToken ct)
    {
        var releases = await Http.GetFromJsonAsync<System.Text.Json.Nodes.JsonArray>(ReleaseApi, ct) ?? throw new InvalidDataException("无法读取插件市场。");
        var asset = releases.SelectMany(r => r?["assets"]?.AsArray() ?? []).FirstOrDefault(a => a?["name"]?.GetValue<string>() == CatalogAsset);
        if (asset is null) throw new InvalidOperationException("官方 Release 尚未发布本地模型插件包。可以导入本地插件包，或稍后重试。");
        var url = asset["browser_download_url"]!.GetValue<string>(); ValidateOfficial(url);
        var catalog = await Http.GetFromJsonAsync<Catalog>(url, ct) ?? throw new InvalidDataException("插件目录为空。");
        ValidateOfficial(catalog.Url);
        if (catalog.Id != "pet-local-model" || !Version.TryParse(catalog.Version, out _) || !IsHash(catalog.Sha256)) throw new InvalidDataException("插件目录格式无效。");
        return catalog;
    }
    static bool IsHash(string value) => value.Length == 64 && value.All(Uri.IsHexDigit);
    static void ValidateOfficial(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Host != "github.com" || !uri.AbsolutePath.StartsWith("/youyuaeiou-netizen/ZZZ-Pet/releases/download/", StringComparison.Ordinal)) throw new InvalidDataException("插件必须来自官方 GitHub Release。");
    }
    public static async Task<(string Directory, string Version)> Download(Catalog catalog, IProgress<string> progress, CancellationToken ct)
    {
        ValidateOfficial(catalog.Url);
        var temp = Path.Combine(ModelRuntime.DataRoot, "plugin-download-" + Guid.NewGuid().ToString("N") + ".zip");
        Directory.CreateDirectory(ModelRuntime.DataRoot);
        try
        {
            using var response = await Http.GetAsync(catalog.Url, HttpCompletionOption.ResponseHeadersRead, ct); response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > 512L * 1024 * 1024) throw new InvalidDataException("插件包超过大小限制。");
            await using (var output = File.Create(temp))
            await using (var input = await response.Content.ReadAsStreamAsync(ct))
            {
                var buffer = new byte[81920]; long bytes = 0; int count;
                while ((count = await input.ReadAsync(buffer, ct)) > 0)
                {
                    bytes += count; if (bytes > 512L * 1024 * 1024) throw new InvalidDataException("插件包超过大小限制。");
                    await output.WriteAsync(buffer.AsMemory(0, count), ct); progress.Report($"下载中：{bytes / 1048576} MB");
                }
            }
            await using (var file = File.OpenRead(temp))
                if (!Convert.ToHexString(await SHA256.HashDataAsync(file, ct)).Equals(catalog.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("下载摘要不匹配，原版本保留。");
            return await Install(temp, ct, catalog.Version);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    internal static async Task<(string Directory, string Version)> Install(string zipPath, CancellationToken ct, string? expectedVersion = null)
    {
        if (new FileInfo(zipPath).Length > 512L * 1024 * 1024) throw new InvalidDataException("插件包超过大小限制。");
        using var zip = ZipFile.OpenRead(zipPath);
        var manifestEntry = zip.GetEntry("plugin.json") ?? throw new InvalidDataException("缺少 plugin.json。");
        if (manifestEntry.Length > 1024 * 1024) throw new InvalidDataException("插件清单过大。");
        Manifest manifest;
        await using (var input = manifestEntry.Open()) manifest = await JsonSerializer.DeserializeAsync<Manifest>(input, cancellationToken: ct) ?? throw new InvalidDataException("插件清单为空。");
        var hostVersion = System.Version.Parse(System.Diagnostics.FileVersionInfo.GetVersionInfo(typeof(LocalModelPackage).Assembly.Location).FileVersion!.Split('+')[0]);
        if (manifest.Id != "pet-local-model" || !Version.TryParse(manifest.Version, out _) || !Version.TryParse(manifest.MinimumPetVersion, out var minimum) || hostVersion < minimum || expectedVersion is not null && manifest.Version != expectedVersion)
            throw new InvalidDataException("插件版本不兼容，请升级 Pet 或选择兼容包。");
        if (!manifest.Files.ContainsKey("payload/DesktopPet.ModelGateway.exe") || manifest.Files.Count > 2000) throw new InvalidDataException("网关组件缺失或清单过大。");
        var root = Path.Combine(ModelRuntime.DataRoot, "plugins", "pet-local-model"); Directory.CreateDirectory(root);
        var stage = Path.Combine(root, ".stage-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(stage);
        try
        {
            long total = 0; var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in zip.Entries)
            {
                ct.ThrowIfCancellationRequested();
                if (entry.FullName == "plugin.json") continue;
                if (entry.FullName.EndsWith('/')) continue;
                var relative = entry.FullName.Replace('\\', '/');
                if (relative.Split('/').Any(part => part.Length == 0 || part is "." or ".." || part.EndsWith('.') || part.EndsWith(' ') || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || System.Text.RegularExpressions.Regex.IsMatch(part, @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)", System.Text.RegularExpressions.RegexOptions.IgnoreCase))) throw new InvalidDataException("插件包含非法 Windows 文件名。");
                var target = Path.GetFullPath(Path.Combine(stage, relative));
                if (!relative.StartsWith("payload/", StringComparison.Ordinal) || relative.Contains(':') || !target.StartsWith(stage + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !seen.Add(relative) || !manifest.Files.TryGetValue(relative, out var digest) || !IsHash(digest) || ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000)
                    throw new InvalidDataException("插件包含非法、重复或未登记的文件路径。");
                total += entry.Length; if (total > 1024L * 1024 * 1024) throw new InvalidDataException("解压大小超过限制。");
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await using (var input = entry.Open())
                await using (var output = File.Create(target))
                {
                    var buffer = new byte[81920]; long copied = 0; int count;
                    while ((count = await input.ReadAsync(buffer, ct)) > 0)
                    {
                        copied += count;
                        if (copied > entry.Length || copied > 1024L * 1024 * 1024) throw new InvalidDataException("插件实际解压大小超过限制。");
                        await output.WriteAsync(buffer.AsMemory(0, count), ct);
                    }
                    if (copied != entry.Length) throw new InvalidDataException("插件文件大小不完整。");
                }
                await using var file = File.OpenRead(target);
                if (!Convert.ToHexString(await SHA256.HashDataAsync(file, ct)).Equals(digest, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("插件文件摘要不匹配。");
            }
            if (seen.Count != manifest.Files.Count) throw new InvalidDataException("插件文件清单不完整。");
            await File.WriteAllTextAsync(Path.Combine(stage, "plugin.json"), JsonSerializer.Serialize(manifest), ct);
            ct.ThrowIfCancellationRequested();
            var installed = Path.Combine(root, manifest.Version + "-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.Move(stage, installed); return (Path.Combine(installed, "payload"), manifest.Version);
        }
        finally { if (Directory.Exists(stage)) Directory.Delete(stage, true); }
    }
}
