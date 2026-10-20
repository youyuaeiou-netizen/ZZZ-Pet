using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace PetLocalModels;

internal sealed class ModelRuntime
{
    public static string DataRoot => Environment.GetEnvironmentVariable("PET_MODEL_DATA_ROOT") is { Length: > 0 } root ? Path.GetFullPath(root) : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DesktopPet");
    public static string GatewayEndpoint => "http://127.0.0.1:" + (int.TryParse(Environment.GetEnvironmentVariable("PET_MODEL_GATEWAY_PORT"), out var port) && port is >= 1024 and <= 65535 ? port : 11435);
    public static string ConfigPath => Path.Combine(DataRoot, "model-runtime.json");
    public string Endpoint { get; set; } = "http://127.0.0.1:11434";
    public string? OllamaExecutable { get; set; }
    public bool LoginStartup { get; set; }
    public static ModelRuntime Read() => File.Exists(ConfigPath) ? JsonSerializer.Deserialize<ModelRuntime>(File.ReadAllText(ConfigPath)) ?? new() : new();
    public static string ValidateEndpoint(string value)
    {
        if (!value.Contains("://")) value = "http://" + value;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "http" || !uri.IsLoopback || !string.IsNullOrEmpty(uri.UserInfo) || uri.AbsolutePath != "/" || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new ArgumentException("Ollama 地址必须是本机 HTTP 地址，例如 http://127.0.0.1:11434。");
        return uri.GetLeftPart(UriPartial.Authority);
    }
    public void Validate()
    {
        Endpoint = ValidateEndpoint(Endpoint);
        if (OllamaExecutable is not null && (!File.Exists(OllamaExecutable) || !Path.GetFileName(OllamaExecutable).Equals("ollama.exe", StringComparison.OrdinalIgnoreCase))) throw new ArgumentException("请选择有效的 ollama.exe。");
    }
    public void Save()
    {
        Validate();
        Directory.CreateDirectory(DataRoot);
        File.WriteAllText(ConfigPath + ".tmp", JsonSerializer.Serialize(this)); File.Move(ConfigPath + ".tmp", ConfigPath, true);
    }
    public static object Discover()
    {
        var current = Read();
        var endpoints = new List<string> { current.Endpoint, "http://127.0.0.1:11434" };
        foreach (var variable in new[] { Environment.GetEnvironmentVariable("OLLAMA_HOST"), Environment.GetEnvironmentVariable("OLLAMA_HOST", EnvironmentVariableTarget.User) })
            if (!string.IsNullOrWhiteSpace(variable)) try { endpoints.Add(ValidateEndpoint(variable)); } catch (ArgumentException) { }
        var exes = new List<string?> { current.OllamaExecutable, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Ollama", "ollama.exe") };
        exes.AddRange((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).Where(p => p.Length > 0).Select(p => Path.Combine(p.Trim('"'), "ollama.exe")));
        foreach (var root in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            using var uninstall = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
            foreach (var name in uninstall?.GetSubKeyNames() ?? [])
            {
                using var key = uninstall!.OpenSubKey(name);
                if (key?.GetValue("DisplayName") is string display && display.StartsWith("Ollama", StringComparison.OrdinalIgnoreCase) && key.GetValue("InstallLocation") is string location && location.Length > 0) exes.Add(Path.Combine(location, "ollama.exe"));
            }
        }
        return new { endpoints = endpoints.Distinct().ToArray(), executables = exes.Where(p => p is not null && File.Exists(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), configured = current };
    }
    public string FindExecutable()
    {
        var discovery = JsonSerializer.SerializeToNode(Discover())!;
        var candidates = discovery["executables"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray();
        if (OllamaExecutable is not null && File.Exists(OllamaExecutable)) return OllamaExecutable;
        if (candidates.Length == 1) return candidates[0];
        throw new InvalidOperationException(candidates.Length == 0 ? "未找到 Ollama，请先安装或在环境设置中选择 ollama.exe。" : "发现多个 Ollama 安装，请在环境设置中选择一个。");
    }
    public Process StartOllama() => Process.Start(new ProcessStartInfo(FindExecutable(), "serve")
    {
        UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(FindExecutable())!,
        Environment = { ["OLLAMA_HOST"] = new Uri(Endpoint).Authority }
    }) ?? throw new InvalidOperationException("Ollama 启动失败。");
}
