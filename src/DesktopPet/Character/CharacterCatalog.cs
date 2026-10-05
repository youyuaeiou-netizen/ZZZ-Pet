using System.IO;
using System.Text.Json;

namespace DesktopPet.Character;

public sealed record CharacterCatalogResult(IReadOnlyDictionary<string, CharacterDefinition> Characters,
    IReadOnlyList<string> Warnings);

public static class CharacterCatalog
{
    public const string DefaultCharacter = "ellen-flat2d";

    public static string ResolveSelection(IReadOnlyDictionary<string, CharacterDefinition> characters, string preferred)
        => characters.ContainsKey(preferred) ? preferred
            : characters.ContainsKey(DefaultCharacter) ? DefaultCharacter
            : characters.Keys.Order(StringComparer.OrdinalIgnoreCase).First();

    public static CharacterCatalogResult Load(string bundledRoot, string userRoot)
    {
        var characters = new Dictionary<string, CharacterDefinition>(StringComparer.OrdinalIgnoreCase);
        var warnings = new List<string>();
        foreach (var root in new[] { bundledRoot, userRoot }.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(root)) continue;
            string[] directories;
            try { directories = Directory.GetDirectories(root); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                warnings.Add($"无法读取角色目录：{root} ({exception.Message})");
                continue;
            }
            foreach (var directory in directories.Order(StringComparer.OrdinalIgnoreCase))
            {
                var key = Path.GetFileName(directory);
                if (key.StartsWith('.')) continue; // Reserved runtime/support directories.
                if (characters.ContainsKey(key))
                {
                    warnings.Add($"忽略同名用户角色：{key}。自带角色保留优先级，请为新角色使用独立目录名。");
                    continue;
                }
                try
                {
                    var loaded = CharacterLoader.Load(directory);
                    characters.Add(key, loaded.Character);
                    warnings.AddRange(loaded.Warnings.Select(warning => $"{key}: {warning}"));
                }
                catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException
                    or JsonException or ArgumentException or NotSupportedException)
                {
                    warnings.Add($"跳过无效角色 {key}：{exception.Message}");
                }
            }
        }
        return new CharacterCatalogResult(characters, warnings);
    }
}
