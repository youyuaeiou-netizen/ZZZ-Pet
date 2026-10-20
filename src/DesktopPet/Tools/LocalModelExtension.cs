using System.IO;
using System.Text.Json;

namespace DesktopPet.Tools;

// Pet hosts the native UI; installed backend payloads and model data have independent lifetimes.
internal sealed class LocalModelExtension
{
    public static LocalModelExtension Current { get; } = new();
    readonly string path = Path.Combine(PetLocalModels.ModelRuntime.DataRoot, "local-model-plugin.json");
    State state;
    public bool Installed => state.Installed;
    public bool Enabled => state.Installed && state.Enabled;
    public string? Error { get; private set; }
    public string Version => state.Version ?? "随 Pet 发布的旧版";
    public string GatewayExecutable => Path.Combine(state.Directory ?? Path.Combine(AppContext.BaseDirectory, "model-gateway"), "DesktopPet.ModelGateway.exe");
    public event Action? Changed;
    LocalModelExtension()
    {
        var migrated = File.Exists(Path.Combine(Path.GetDirectoryName(path)!, "model-gateway.json"));
        state = new State { Installed = migrated, Enabled = migrated };
        try { if (File.Exists(path)) state = JsonSerializer.Deserialize<State>(File.ReadAllText(path)) ?? throw new InvalidDataException("插件状态为空。"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        { state = new(); Error = "插件状态不可用，原文件保留：" + ex.Message; }
    }
    public void Install() => Save(new State { Installed = true, Enabled = true });
    public void InstallPackage(string directory, string version) => Save(new State { Installed = true, Enabled = true, Directory = directory, Version = version });
    public void Uninstall() => Save(new State { Directory = state.Directory, Version = state.Version });
    public void SetEnabled(bool enabled) => Save(new State { Installed = state.Installed, Enabled = enabled && state.Installed, Directory = state.Directory, Version = state.Version });
    void Save(State next)
    {
        if (Error is not null) throw new InvalidOperationException(Error);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(next));
        File.Move(path + ".tmp", path, true);
        state = next; Changed?.Invoke();
    }
    sealed class State
    {
        public bool Installed { get; set; }
        public bool Enabled { get; set; }
        public string? Directory { get; set; }
        public string? Version { get; set; }
    }
}
