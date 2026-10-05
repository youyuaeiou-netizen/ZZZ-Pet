using System.Text.Json.Serialization;

namespace DesktopPet.Character;

public sealed class CharacterDefinition
{
    public int SchemaVersion { get; set; }
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string PackVersion { get; set; } = "1.0.0";
    public string MinimumAppVersion { get; set; } = "0.0.0";
    public bool? ImmediateClick { get; set; }
    public string DefaultActionId { get; set; } = "";
    public string DefaultFacing { get; set; } = "right";
    public double DefaultScale { get; set; } = 1;
    public int CanvasWidth { get; set; } = 160;
    public int CanvasHeight { get; set; } = 160;
    public int AnchorX { get; set; } = 80;
    public int AnchorY { get; set; } = 160;
    public int RandomIntervalMs { get; set; } = 3500;
    public int SleepThresholdMs { get; set; } = 12000;
    public string? WakeActionId { get; set; }
    public bool AvoidRepeatedActions { get; set; }
    public List<ActionDefinition> Actions { get; set; } = [];

    [JsonIgnore] public Dictionary<string, ActionDefinition> ActionMap { get; internal set; } = [];
}

public sealed class ActionDefinition
{
    public string Id { get; set; } = "";
    public string? DisplayName { get; set; }
    public string? Category { get; set; }
    public List<FrameDefinition> Frames { get; set; } = [];
    public List<string> Triggers { get; set; } = [];
    public List<string> InterruptibleBy { get; set; } = [];
    public int Weight { get; set; } = 1;
    public int CooldownMs { get; set; }
    public int Priority { get; set; }
    public bool Loop { get; set; }
    public bool ShowSleepIndicator { get; set; }
    public int? DurationMs { get; set; }
    public MoveDefinition? Move { get; set; }
    public string? NextActionId { get; set; }
    public int? ViewportWidth { get; set; }
    public int? ViewportHeight { get; set; }
    public int? ViewportAnchorX { get; set; }
    public int? ViewportAnchorY { get; set; }

    public (int Width, int Height, int AnchorX, int AnchorY) GetViewport(CharacterDefinition character) =>
        (ViewportWidth ?? character.CanvasWidth, ViewportHeight ?? character.CanvasHeight,
         ViewportAnchorX ?? character.AnchorX, ViewportAnchorY ?? character.AnchorY);
}

public sealed class FrameDefinition
{
    public string Path { get; set; } = "";
    public int DurationMs { get; set; } = 180;
    [JsonIgnore] public string FullPath { get; internal set; } = "";
}

public sealed class MoveDefinition
{
    public double HorizontalSpeed { get; set; }
    public double VerticalSpeed { get; set; }
    public bool FlipAtBoundary { get; set; }
}
