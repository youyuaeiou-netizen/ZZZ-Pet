namespace DesktopPet.Behavior;

public sealed class DragonState
{
    public string Form { get; set; } = "human";
    public DateTimeOffset LastFedAt { get; set; }
    public DateTimeOffset? ProtectedUntil { get; set; }
}

// Pure, clock-injected policy; independent of artwork, tools and quota transport.
public sealed class DragonFormRules(DragonState state)
{
    private readonly double?[] _previous = new double?[2];
    private readonly double?[] _remaining = new double?[2];
    private DateTimeOffset? _retrievedAt;
    public DragonState State => state;
    public void Initialize(DateTimeOffset now)
    {
        if (state.Form is not ("human" or "chibi")) state.Form = "human";
        if (state.LastFedAt == default || state.LastFedAt > now) state.LastFedAt = now;
        if (state.ProtectedUntil > now.AddMinutes(3)) state.ProtectedUntil = now.AddMinutes(3);
        Evaluate(now);
    }
    public void Enlarge(DateTimeOffset now)
    {
        state.Form = "human";
        state.ProtectedUntil = now.AddMinutes(3);
    }
    public void Feed(DateTimeOffset now) { state.LastFedAt = now; Enlarge(now); }
    public void MakeSmall() { state.Form = "chibi"; state.ProtectedUntil = null; }
    public void Observe(double? primary, double? secondary, DateTimeOffset retrievedAt, DateTimeOffset now)
    {
        if (now - retrievedAt > TimeSpan.FromMinutes(2) || retrievedAt > now.AddSeconds(5)) return;
        var values = new[] { primary, secondary };
        var recovered = false;
        for (var i = 0; i < 2; i++)
        {
            var value = values[i];
            if (value is not double n || !double.IsFinite(n) || n < 0 || n > 100)
            { _remaining[i] = null; continue; }
            if (n == 100 && _previous[i] is double previous && previous < 100) recovered = true;
            _previous[i] = _remaining[i] = n;
        }
        _retrievedAt = retrievedAt;
        if (recovered) Enlarge(now);
        Evaluate(now);
    }
    public void QuotaUnavailable() { _retrievedAt = null; Array.Clear(_remaining); }
    public void Evaluate(DateTimeOffset now)
    {
        if (state.ProtectedUntil is { } until && now < until) return;
        state.ProtectedUntil = null;
        var hungry = now - state.LastFedAt >= TimeSpan.FromHours(8);
        var lowQuota = _retrievedAt is { } at && now - at <= TimeSpan.FromMinutes(2)
            && _remaining.Any(n => n is < 10);
        if (hungry || lowQuota) state.Form = "chibi";
    }
}
