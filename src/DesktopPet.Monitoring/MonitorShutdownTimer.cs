namespace DesktopPet.Monitoring;

// Monotonic, process-local scheduling: cancelling never cancels another program's shutdown.
public sealed class MonitorShutdownTimer(Action shutdown)
{
    public long? Due { get; private set; }
    public int Remaining(long tick) => Due is long due ? (int)Math.Clamp((due - tick + 999) / 1000, 0, int.MaxValue) : 0;
    public void Schedule(int minutes, long tick)
    {
        if (minutes is < 1 or > 1440) throw new ArgumentOutOfRangeException(nameof(minutes));
        Due = checked(tick + minutes * 60000L);
    }
    public void Cancel() => Due = null;
    public void Poll(long tick)
    {
        if (Due is not long due || tick < due) return;
        Due = null; // Clear before invoking, including on an OS failure.
        shutdown();
    }
}
