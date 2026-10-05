using System.IO;
using System.Text.Json;

namespace DesktopPet.Character;

public sealed record CharacterLoadResult(CharacterDefinition Character, IReadOnlyList<string> Warnings);

public static class CharacterLoader
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    public static CharacterLoadResult Load(string directory)
    {
        var root = Path.GetFullPath(directory);
        var jsonPath = Path.Combine(root, "character.json");
        var character = JsonSerializer.Deserialize<CharacterDefinition>(File.ReadAllText(jsonPath), Options)
            ?? throw new InvalidDataException("Character manifest is empty.");
        if (character.SchemaVersion != 1)
            throw new InvalidDataException($"Unsupported schemaVersion: {character.SchemaVersion}.");
        if (!Version.TryParse(character.MinimumAppVersion, out var minimum)
            || minimum > AppInfo.CompatibilityVersion)
            throw new InvalidDataException($"角色需要 DesktopPet {character.MinimumAppVersion} 或更新版本。");
        if (string.IsNullOrWhiteSpace(character.DefaultActionId))
            throw new InvalidDataException("defaultActionId is required.");
        if (character.CanvasWidth <= 0 || character.CanvasHeight <= 0 || !double.IsFinite(character.DefaultScale)
            || character.DefaultScale <= 0 || character.Actions is null)
            throw new InvalidDataException("Canvas size and defaultScale must be positive.");
        if (character.AnchorX < 0 || character.AnchorX > character.CanvasWidth
            || character.AnchorY < 0 || character.AnchorY > character.CanvasHeight)
            throw new InvalidDataException("Character anchor must be inside the canvas.");

        var warnings = new List<string>();
        var playable = new Dictionary<string, ActionDefinition>(StringComparer.Ordinal);
        foreach (var action in character.Actions)
        {
            if (action is null || string.IsNullOrWhiteSpace(action.Id) || playable.ContainsKey(action.Id))
            {
                warnings.Add($"Skipped empty or duplicate action ID: {action?.Id}");
                continue;
            }
            if (action.Frames is null || action.Frames.Count == 0
                || action.Frames.Any(frame => frame is null || !IsValidFrame(root, frame)))
            {
                warnings.Add($"Skipped action with missing or invalid frames: {action.Id}");
                continue;
            }
            if (action.DurationMs is <= 0 || action.CooldownMs < 0 || action.Weight < 0
                || action.Triggers is null || action.InterruptibleBy is null
                || (action.Move is not null && (!double.IsFinite(action.Move.HorizontalSpeed)
                    || !double.IsFinite(action.Move.VerticalSpeed))))
            {
                warnings.Add($"Skipped action with invalid timing: {action.Id}");
                continue;
            }
            var viewportParts = new int?[] { action.ViewportWidth, action.ViewportHeight,
                action.ViewportAnchorX, action.ViewportAnchorY };
            if (viewportParts.Any(part => part.HasValue) && viewportParts.Any(part => !part.HasValue))
            {
                warnings.Add($"Skipped action with incomplete viewport: {action.Id}");
                continue;
            }
            var viewport = action.GetViewport(character);
            if (viewport.Width <= 0 || viewport.Height <= 0 || viewport.AnchorX < 0
                || viewport.AnchorX > viewport.Width || viewport.AnchorY < 0
                || viewport.AnchorY > viewport.Height)
            {
                warnings.Add($"Skipped action with invalid viewport: {action.Id}");
                continue;
            }
            playable.Add(action.Id, action);
        }

        if (!playable.ContainsKey(character.DefaultActionId))
            throw new InvalidDataException($"Default action is missing or unplayable: {character.DefaultActionId}");

        // Invalid optional transitions are omitted; the default action remains playable.
        foreach (var action in playable.Values)
        {
            if (action.NextActionId is { Length: > 0 } next && !playable.ContainsKey(next))
            {
                warnings.Add($"Invalid nextActionId on {action.Id}: {next}; using default action.");
                action.NextActionId = null;
            }
        }
        character.Actions = playable.Values.ToList();
        if (character.WakeActionId is { Length: > 0 } wake
            && (!playable.TryGetValue(wake, out var waking) || waking.Loop))
        {
            warnings.Add("Invalid wakeActionId; using direct interaction.");
            character.WakeActionId = null;
        }
        character.ActionMap = playable;
        return new CharacterLoadResult(character, warnings);
    }

    private static bool IsValidFrame(string root, FrameDefinition frame)
    {
        try
        {
            if (frame.DurationMs <= 0 || string.IsNullOrWhiteSpace(frame.Path)
                || Path.IsPathRooted(frame.Path)) return false;
            var fullPath = Path.GetFullPath(Path.Combine(root, frame.Path));
            if (!fullPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || !fullPath.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                || !File.Exists(fullPath)) return false;
            frame.FullPath = fullPath;
            return true;
        }
        catch (ArgumentException) { return false; }
        catch (NotSupportedException) { return false; }
    }
}
