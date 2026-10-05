using System.IO;

namespace DesktopPet;

public sealed record StartupOptions(string? PreviewPack, string? DataDirectory, string? Character)
{
    public static StartupOptions Parse(string[] args)
    {
        string? preview = null, data = null, character = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < args.Length; i += 2)
        {
            if (i + 1 >= args.Length || !seen.Add(args[i]) || string.IsNullOrWhiteSpace(args[i + 1]))
                throw new ArgumentException("启动参数缺少值或重复。支持 --character、--preview-pack 和 --data-dir。");
            switch (args[i])
            {
                case "--preview-pack": preview = args[i + 1]; break;
                case "--data-dir": data = args[i + 1]; break;
                case "--character": character = args[i + 1]; break;
                default: throw new ArgumentException($"不支持的启动参数：{args[i]}");
            }
        }
        if (preview is not null && data is not null)
            throw new ArgumentException("--preview-pack 与 --data-dir 不能同时使用。");
        return new StartupOptions(preview, data, character);
    }
}

public sealed class RuntimePaths
{
    public string CharacterRoot { get; }
    public string DataDirectory { get; }
    private readonly bool _preview;
    private string RuntimeDataRoot => _preview ? Path.Combine(DataDirectory, ".runtime") : DataDirectory;
    public string UserCharacterRoot => Path.Combine(RuntimeDataRoot, "characters");
    public string LogDirectory => Path.Combine(RuntimeDataRoot, "logs");
    public string ToolsStatePath => Path.Combine(RuntimeDataRoot, "tools-state.json");
    public string SearchSettingsPath => Path.Combine(RuntimeDataRoot, "search-settings.json");
    public string MonitorDirectory => Path.Combine(RuntimeDataRoot, "monitor");
    public string SettingsPath { get; }

    public RuntimePaths(StartupOptions options, string? applicationDirectory = null)
    {
        applicationDirectory ??= AppContext.BaseDirectory;
        _preview = options.PreviewPack is not null;
        CharacterRoot = Path.GetFullPath(options.PreviewPack ?? Path.Combine(applicationDirectory, "characters"));
        DataDirectory = Path.GetFullPath(options.PreviewPack ?? options.DataDirectory ??
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DesktopPet"));
        SettingsPath = Path.Combine(DataDirectory, options.PreviewPack is null ? "settings.json" : "preview-settings.json");
    }
}
