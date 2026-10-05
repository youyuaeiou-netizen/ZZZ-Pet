namespace DesktopPet.Monitoring;

public sealed class BubbleLifetime
{
    public bool Open { get; private set; }
    private DateTimeOffset? _left;
    public void Show() { Open = true; _left = null; }
    public void Close() { Open = false; _left = null; }
    public bool Tick(DateTimeOffset now, bool overPet, bool overBubble, bool dragging = false)
    {
        if (!Open) return false;
        if (overPet || overBubble || dragging) _left = null;
        else
        {
            _left ??= now;
            if (now - _left >= TimeSpan.FromSeconds(5)) { Close(); return true; }
        }
        return false;
    }
}
