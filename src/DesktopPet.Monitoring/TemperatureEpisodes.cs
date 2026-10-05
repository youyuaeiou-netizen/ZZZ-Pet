namespace DesktopPet.Monitoring;

public sealed class TemperatureEpisodes
{
    private sealed class Timing { public DateTimeOffset? Last; public double Hot; public double Cool; public bool WasHot; public bool WasCool; public string? Device; public double Threshold; }
    private readonly Dictionary<string, Timing> _timing = [];
    public bool Observe(string key, TemperatureRule rule, MonitorMetric? metric, DateTimeOffset timestamp,
        DateTimeOffset now, bool quiet, int refreshMs = 1000)
    {
        if (!_timing.TryGetValue(key, out var timer)) _timing[key] = timer = new();
        if (timer.Device != metric?.DeviceId || timer.Threshold != rule.Threshold)
        {
            timer.Last = null; timer.Hot = timer.Cool = 0; timer.WasHot = timer.WasCool = false;
            timer.Device = metric?.DeviceId; timer.Threshold = rule.Threshold;
        }
        if (!rule.Enabled || metric?.Valid != true || timestamp > now || now - timestamp > TimeSpan.FromSeconds(5))
        { timer.Last = null; timer.Hot = timer.Cool = 0; timer.WasHot = timer.WasCool = false; return false; }
        if (rule.InEpisode && rule.EpisodeDeviceId != metric.DeviceId)
        {
            // A device change is a new measurement source, not evidence that the old device cooled down.
            rule.EpisodeDeviceId = metric.DeviceId;
            timer.Last = null; timer.Hot = timer.Cool = 0;
        }
        var delta = timer.Last is DateTimeOffset last && timestamp > last && timestamp - last <= TimeSpan.FromMilliseconds(Math.Max(3000, refreshMs * 1.5))
            ? (timestamp - last).TotalSeconds : 0;
        if (timer.Last == timestamp) return false;
        if (delta == 0) timer.Hot = timer.Cool = 0;
        timer.Last = timestamp;
        if (metric.Value > rule.Threshold)
        {
            timer.Cool = 0; timer.Hot = timer.WasHot ? timer.Hot + delta : 0;
            timer.WasHot = true; timer.WasCool = false;
            if (!rule.InEpisode && timer.Hot >= 10)
            {
                rule.InEpisode = true; rule.EpisodeDeviceId = metric.DeviceId;
                return !quiet && !(rule.SnoozeUntil > now);
            }
        }
        else
        {
            timer.Hot = 0;
            timer.WasHot = false;
            if (metric.Value <= rule.Threshold - 5)
            {
                timer.Cool = timer.WasCool ? timer.Cool + delta : 0; timer.WasCool = true;
                if (timer.Cool >= 60) { rule.InEpisode = false; rule.EpisodeDeviceId = null; }
            }
            else { timer.Cool = 0; timer.WasCool = false; }
        }
        return false;
    }
    public void ResetTiming() => _timing.Clear();
}
