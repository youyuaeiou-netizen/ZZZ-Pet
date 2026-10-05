namespace DesktopPet.Behavior;

public readonly record struct PointerPosition(double X, double Y);

public sealed class PointerGestures(int doubleClickMs, int doubleClickWidth, int doubleClickHeight,
    int dragWidth, int dragHeight, bool immediateClick = false)
{
    private PointerPosition _press;
    private PointerPosition _lastRelease;
    private double _lastReleaseMs;
    private bool _pressed;
    private bool _dragging;
    private bool _pendingClick;
    private bool _secondPress;
    private bool _clickDelivered;

    public bool IsDragging => _dragging;
    public bool IsPressed => _pressed;

    public void Press(PointerPosition position, double nowMs)
    {
        _press = position;
        _pressed = true;
        _dragging = false;
        _secondPress = _pendingClick && nowMs - _lastReleaseMs <= doubleClickMs
            && Math.Abs(position.X - _lastRelease.X) <= doubleClickWidth
            && Math.Abs(position.Y - _lastRelease.Y) <= doubleClickHeight;
        if (_secondPress) _pendingClick = false;
    }

    public string? Move(PointerPosition position)
    {
        if (!_pressed || _dragging) return null;
        if (Math.Abs(position.X - _press.X) < dragWidth
            && Math.Abs(position.Y - _press.Y) < dragHeight) return null;
        _dragging = true;
        _pendingClick = false;
        _secondPress = false;
        return "drag_start";
    }

    public string? Release(PointerPosition position, double nowMs)
    {
        if (!_pressed) return null;
        _pressed = false;
        if (_dragging)
        {
            _dragging = false;
            return "drag_release";
        }
        if (_secondPress)
        {
            _secondPress = false;
            return "double_click";
        }
        _lastRelease = position;
        _lastReleaseMs = nowMs;
        _pendingClick = true;
        _clickDelivered = immediateClick;
        return immediateClick ? "click" : null;
    }

    public string? Advance(double nowMs)
    {
        if (!_pendingClick || nowMs - _lastReleaseMs < doubleClickMs) return null;
        _pendingClick = false;
        return _clickDelivered ? null : "click";
    }
}
