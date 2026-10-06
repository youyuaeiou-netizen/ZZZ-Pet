using System.Diagnostics;
using System.IO;

namespace DesktopPet.Search;

internal sealed record DiskSearchResult(IReadOnlyList<SearchEntry> Entries, long Visited, int Skipped, int Links, bool Complete)
{
    internal string Status => $"磁盘直接检索 · 已检查 {Visited:N0} 项 · " + (Complete ? "扫描完成" : "扫描中…")
        + (Skipped > 0 ? $" · {Skipped:N0} 个位置不可读取，结果不完整" : "")
        + (Links > 0 ? $" · {Links:N0} 个链接目录未展开" : "");
}

// Search names directly instead of treating an external index as disk coverage.
// Keep only ranked matches; never retain every filename or read file contents.
internal static class DiskSearch
{
    internal static IReadOnlyList<string> Roots(SearchScope scope)
    {
        if (scope.Root is { } selected) return [selected];
        return DriveInfo.GetDrives().Where(d => d.DriveType is DriveType.Fixed or DriveType.Removable
            or DriveType.Network or DriveType.CDRom).Select(d => d.Name).ToArray();
    }

    internal static Task<DiskSearchResult> SearchAsync(SearchPlan plan, IEnumerable<string> roots,
        CancellationToken token, Action<DiskSearchResult>? progress = null) => Task.Run(() =>
    {
        var matches = new List<SearchEntry>(); var tokens = SearchMatch.Tokens(plan.Original);
        long visited = 0; var skipped = 0; var links = 0; var clock = Stopwatch.StartNew();
        var options = new EnumerationOptions { RecurseSubdirectories = false, IgnoreInaccessible = false,
            AttributesToSkip = 0, ReturnSpecialDirectories = false, BufferSize = 64 * 1024 };
        DiskSearchResult Snapshot(bool complete)
        {
            var ranked = SearchMatch.Rank(matches, plan.Original, SearchFilter.Files, plan: plan);
            matches.Clear(); matches.AddRange(ranked);
            return new(ranked, visited, skipped, links, complete);
        }
        void Publish()
        {
            if (clock.ElapsedMilliseconds < 500) return;
            progress?.Invoke(Snapshot(false)); clock.Restart();
        }
        foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            // Visit shallow directories first so ordinary storage folders appear early.
            var pending = new Queue<string>(); pending.Enqueue(root);
            while (pending.TryDequeue(out var directory))
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    foreach (var item in new DirectoryInfo(directory).EnumerateFileSystemInfos("*", options))
                    {
                        token.ThrowIfCancellationRequested(); visited++;
                        try
                        {
                            var attributes = item.Attributes; var folder = (attributes & FileAttributes.Directory) != 0;
                            var name = SearchMatch.Normalize(item.Name);
                            var path = SearchMatch.Normalize(item.FullName);
                            if (tokens.All(t => name.Contains(t, StringComparison.Ordinal))
                                || tokens.All(t => path.Contains(t, StringComparison.Ordinal))
                                || plan.Expanded && plan.Terms.All(t => t.Alternatives.Any(a => name.Contains(a, StringComparison.Ordinal))))
                            {
                                matches.Add(new(item.Name, item.FullName, folder ? SearchKind.Folder : SearchKind.File, "磁盘直接检索"));
                                if (matches.Count >= 256) Snapshot(false);
                            }
                            if (folder)
                            {
                                // Record the link itself, but do not follow it outside the selected disk or into a cycle.
                                if ((attributes & FileAttributes.ReparsePoint) != 0) links++;
                                else pending.Enqueue(item.FullName);
                            }
                        }
                        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { skipped++; }
                        Publish();
                    }
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { skipped++; }
                Publish();
            }
        }
        token.ThrowIfCancellationRequested();
        return Snapshot(true);
    }, token);
}
