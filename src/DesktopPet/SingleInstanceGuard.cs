using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace DesktopPet;

public sealed class SingleInstanceGuard : IDisposable
{
    private readonly Mutex _mutex;
    private readonly EventWaitHandle _signal;
    private RegisteredWaitHandle? _listener;
    public bool IsPrimary { get; }

    public SingleInstanceGuard(string dataDirectory)
    {
        var identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            Path.GetFullPath(dataDirectory).TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant())));
        var name = @"Local\DesktopPet." + identity;
        _signal = new EventWaitHandle(false, EventResetMode.AutoReset, name + ".show");
        // The named object remains alive for the owning process's lifetime.
        // This also rejects another launch from a different application folder.
        _mutex = new Mutex(false, name, out var created);
        IsPrimary = created;
    }

    public void NotifyPrimary() => _signal.Set();

    public void Listen(Action onDuplicate)
    {
        if (!IsPrimary) throw new InvalidOperationException("Only the primary instance can listen.");
        _listener = ThreadPool.RegisterWaitForSingleObject(_signal, (_, _) => onDuplicate(), null, -1, false);
    }

    public void Dispose()
    {
        _listener?.Unregister(null);
        _signal.Dispose();
        _mutex.Dispose();
    }
}
