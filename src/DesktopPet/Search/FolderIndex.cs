using System.Collections.Concurrent;
using System.IO;

namespace DesktopPet.Search;

internal sealed class FolderIndex : IDisposable
{
    private readonly CancellationToken _token;
    private readonly SemaphoreSlim _scan = new(1);
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly ConcurrentDictionary<string, byte> _roots = new(StringComparer.OrdinalIgnoreCase);
    private readonly System.Threading.Timer _debounce;
    private SearchEntry[] _entries = [];
    private bool _disposed;
    internal IReadOnlyList<SearchEntry> Entries => Volatile.Read(ref _entries);
    internal string Status { get; private set; } = "";
    internal event Action? Changed;
    internal const int Limit = 200_000;
    private static readonly HashSet<string> Excluded = new(StringComparer.OrdinalIgnoreCase)
        { ".git", ".harness", "node_modules", "bin", "obj", "$RECYCLE.BIN", "System Volume Information" };

    internal FolderIndex(CancellationToken token)
    { _token = token; _debounce = new(_ => { if (!_disposed) _ = RefreshAsync(); }, null, Timeout.Infinite, Timeout.Infinite); }

    internal async Task AddAsync(string root)
    {
        root = Path.GetFullPath(root);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException("所选目录不存在。");
        if (_roots.TryAdd(root, 0))
        {
            try
            {
                var watcher = new FileSystemWatcher(root) { IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName, InternalBufferSize = 32 * 1024 };
                watcher.Created += Dirty; watcher.Deleted += Dirty; watcher.Renamed += Dirty; watcher.Error += (_, _) => Schedule();
                lock (_watchers) _watchers.Add(watcher); watcher.EnableRaisingEvents = true;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Status = "目录变化监听不可用，请手动刷新"; }
        }
        await RefreshAsync();
    }
    private void Dirty(object sender, FileSystemEventArgs e)
    {
        var parts = Path.GetRelativePath(((FileSystemWatcher)sender).Path, e.FullPath).Split(Path.DirectorySeparatorChar);
        if (!parts.Any(Excluded.Contains)) Schedule();
    }
    private void Schedule()
    { if (!_disposed) { try { _debounce.Change(600, Timeout.Infinite); } catch (ObjectDisposedException) { } } }

    internal async Task RefreshAsync()
    {
        try
        {
            await _scan.WaitAsync(_token);
            try
            {
                await Task.Run(() =>
                {
                    var entries = new Dictionary<string, SearchEntry>(StringComparer.OrdinalIgnoreCase); var skipped = 0; var capped = false;
                    foreach (var root in _roots.Keys.Order(StringComparer.OrdinalIgnoreCase))
                    {
                        var pending = new Stack<string>(); pending.Push(root);
                        while (pending.Count > 0 && !capped)
                        {
                            _token.ThrowIfCancellationRequested(); var dir = pending.Pop();
                            try
                            {
                                foreach (var path in Directory.EnumerateFileSystemEntries(dir))
                                {
                                    _token.ThrowIfCancellationRequested();
                                    FileAttributes attrs;
                                    try { attrs = File.GetAttributes(path); }
                                    catch (Exception e) when (e is IOException or UnauthorizedAccessException) { skipped++; continue; }
                                    if ((attrs & FileAttributes.ReparsePoint) != 0) continue;
                                    var folder = (attrs & FileAttributes.Directory) != 0;
                                    if (folder && Excluded.Contains(Path.GetFileName(path))) continue;
                                    entries[path] = new(Path.GetFileName(path), path, folder ? SearchKind.Folder : SearchKind.File, "指定目录");
                                    if (folder) pending.Push(path);
                                    if (entries.Count >= Limit) { capped = true; break; }
                                }
                            }
                            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { skipped++; }
                        }
                    }
                    Volatile.Write(ref _entries, entries.Values.ToArray());
                    Status = capped ? "目录索引已达 20 万项上限；请缩小目录或使用 Everything" : skipped > 0 ? $"{skipped} 个目录无法读取，结果不完整" : "";
                }, _token);
            }
            finally { _scan.Release(); }
            if (!_disposed) Changed?.Invoke();
        }
        catch (OperationCanceledException) { }
    }
    public void Dispose()
    {
        _disposed = true; _debounce.Dispose();
        lock (_watchers) { foreach (var watcher in _watchers) watcher.Dispose(); _watchers.Clear(); }
    }
}
