using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DesktopPet;

public sealed class UserSettings
{
    public int SchemaVersion { get; set; } = 1;
    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }
    public double Scale { get; set; } = 1;
    public string SelectedCharacter { get; set; } = Character.CharacterCatalog.DefaultCharacter;
    public bool AlwaysOnTop { get; set; } = true;
    public bool PetHidden { get; set; }
    public Dictionary<string, bool> AutomaticActions { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    [JsonExtensionData] public Dictionary<string, JsonElement>? AdditionalFields { get; set; }
}

public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    private readonly string _path;
    private readonly Action<string>? _report;
    private bool _canSave = true;
    public string? Warning { get; private set; }
    public string DataDirectory => Path.GetDirectoryName(Path.GetFullPath(_path))!;

    public SettingsStore(string? path = null, Action<string>? report = null)
    {
        _path = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DesktopPet", "settings.json");
        _report = report;
    }

    public UserSettings Load()
    {
        try
        {
            if (!File.Exists(_path)) return new UserSettings();
            var settings = JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(_path), JsonOptions)
                ?? throw new JsonException("Settings are empty.");
            if (settings.SchemaVersion != 1)
            {
                _canSave = false;
                Warn("设置文件来自不兼容版本，本次使用默认设置并保留原文件。请使用匹配版本的程序。");
                return new UserSettings();
            }
            return settings;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            _canSave = false;
            Warn($"无法读取设置，本次使用默认设置并保留原文件：{exception.Message}");
            return new UserSettings();
        }
    }

    public void Save(UserSettings settings)
    {
        if (!_canSave || settings.SchemaVersion != 1) return;
        var directory = Path.GetDirectoryName(_path)!;
        var temporaryPath = _path + ".tmp";
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(settings, JsonOptions));
            File.Move(temporaryPath, _path, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Warn($"无法保存设置：{exception.Message}");
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
    }

    private void Warn(string message)
    {
        Warning = message;
        Debug.WriteLine(message);
        _report?.Invoke(message);
    }
}
