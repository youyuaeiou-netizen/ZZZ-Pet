using DesktopPet.Character;

namespace DesktopPet.Behavior;

public readonly record struct Bounds(double Left, double Top, double Right, double Bottom);
public readonly record struct MoveResult(double Left, double Top, bool BoundaryHit);

public static class Movement
{
    public static MoveResult Step(double left, double top, double width, double height,
        Bounds workArea, MoveDefinition move, int facing, TimeSpan elapsed)
    {
        var seconds = Math.Max(0, elapsed.TotalSeconds);
        var x = left + facing * move.HorizontalSpeed * seconds;
        var y = top + move.VerticalSpeed * seconds;
        var clampedX = Math.Clamp(x, workArea.Left, Math.Max(workArea.Left, workArea.Right - width));
        var clampedY = Math.Clamp(y, workArea.Top, Math.Max(workArea.Top, workArea.Bottom - height));
        return new MoveResult(clampedX, clampedY, clampedX != x || clampedY != y);
    }
}
