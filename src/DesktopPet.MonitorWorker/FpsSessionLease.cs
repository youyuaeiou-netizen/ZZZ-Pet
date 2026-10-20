namespace DesktopPet.MonitorWorker;

// A held file lock marks a live owner. After a crash the file remains, but its
// lock is released, so the next worker can reclaim only that abandoned session.
internal sealed class FpsSessionLease : IDisposable
{
    private FileStream? _owner;
    private readonly string _path, _session;
    private readonly Func<string, bool> _stop;
    internal FpsSessionLease(string directory, string session, Func<string, bool> stop)
    {
        Directory.CreateDirectory(directory);
        Reclaim(directory, stop);
        _session = session; _stop = stop;
        _path = Path.Combine(directory, session + ".lease");
        _owner = new FileStream(_path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read);
    }
    internal static void Reclaim(string directory, Func<string, bool> stop)
    {
        foreach (var path in Directory.EnumerateFiles(directory, "DesktopPet.FPS.*.lease"))
        {
            var session = Path.GetFileNameWithoutExtension(path);
            if (!session.StartsWith("DesktopPet.FPS.", StringComparison.Ordinal) ||
                !Guid.TryParseExact(session[15..], "N", out _)) continue;
            try
            {
                bool stopped;
                using (var abandoned = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                    stopped = stop(session);
                if (stopped) File.Delete(path);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }
    public void Dispose()
    {
        var owner = Interlocked.Exchange(ref _owner, null);
        if (owner is null) return;
        var stopped = _stop(_session);
        owner.Dispose();
        if (stopped)
            try { File.Delete(_path); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
