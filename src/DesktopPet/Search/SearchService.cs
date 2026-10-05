using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace DesktopPet.Search;

internal sealed class SearchService : IDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _files = new(1);
    private readonly string _settingsPath;
    private readonly FolderIndex _folders;
    private readonly List<string> _roots = [];
    private Task<IReadOnlyList<SearchEntry>>? _applications;
    private Task? _initialization;
    private DateTimeOffset _appBuilt;
    private bool _disposed, _settingsInvalid;
    private readonly Dictionary<string, SearchEntry> _aliasEntries = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string[]> _aliases = new(StringComparer.OrdinalIgnoreCase);
    internal bool BilingualEnabled { get; private set; }
    internal SearchScope FileScope { get; private set; } = SearchScope.All;
    internal string? Error { get; private set; }
    internal IReadOnlyList<string> Roots => _roots.ToArray();
    internal event Action? Changed;

    internal SearchService(string settingsPath)
    {
        _settingsPath = settingsPath; _folders = new(_lifetime.Token); _folders.Changed += OnChanged;
        if (!File.Exists(settingsPath)) return;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(settingsPath));
            foreach (var path in doc.RootElement.GetProperty("roots").EnumerateArray())
                if (path.GetString() is { } root && Path.IsPathFullyQualified(root) && !_roots.Contains(root, StringComparer.OrdinalIgnoreCase)) _roots.Add(root);
            if (doc.RootElement.TryGetProperty("bilingual", out var bilingual)) BilingualEnabled = bilingual.GetBoolean();
            if (doc.RootElement.TryGetProperty("fileScope", out var scope))
            {
                try { FileScope = SearchScope.Parse(scope.GetString()); }
                catch (ArgumentException e) { throw new JsonException("搜索盘符设置无效", e); }
            }
            if (doc.RootElement.TryGetProperty("aliases", out var aliases))
            foreach (var alias in aliases.EnumerateArray())
            {
                var entry = JsonSerializer.Deserialize<SearchEntry>(alias.GetProperty("entry").GetRawText()) ?? throw new JsonException("Missing alias target");
                if (!Path.IsPathFullyQualified(entry.Target) && !entry.Target.StartsWith("shell:AppsFolder\\", StringComparison.OrdinalIgnoreCase)) continue;
                _aliasEntries[entry.Target] = entry;
                _aliases[entry.Target] = alias.GetProperty("names").EnumerateArray().Select(n => SearchMatch.Normalize(n.GetString() ?? "")).Where(n => n.Length is > 0 and <= 200).Distinct().Take(20).ToArray();
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or KeyNotFoundException)
        { _settingsInvalid = true; Error = "搜索目录设置无法读取；已保留原文件"; }
    }
    private void OnChanged() { if (!_disposed) Changed?.Invoke(); }
    internal Task InitializeAsync() => _initialization ??= InitializeCoreAsync();
    private async Task InitializeCoreAsync()
    {
        _ = Applications();
        foreach (var root in _roots.ToArray())
        {
            try { await _folders.AddAsync(root); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Error = "部分搜索目录不可用"; }
        }
    }
    private Task<IReadOnlyList<SearchEntry>> Applications(bool force = false)
    {
        if (_applications is null || _applications.IsCompleted && (force || DateTimeOffset.UtcNow - _appBuilt > TimeSpan.FromMinutes(5)))
        { _appBuilt = DateTimeOffset.UtcNow; _applications = WindowsSearchProvider.OnSta(() => WindowsSearchProvider.Applications(_lifetime.Token), _lifetime.Token); }
        return _applications;
    }
    internal async Task AddRootAsync(string root)
    {
        if (_settingsInvalid) throw new IOException(Error);
        root = Path.GetFullPath(root);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException("所选目录不存在。");
        if (_roots.Contains(root, StringComparer.OrdinalIgnoreCase)) return;
        var next = _roots.Append(root).ToArray();
        Save("roots", JsonSerializer.SerializeToNode(next));
        _roots.Add(root); await _folders.AddAsync(root); OnChanged();
    }
    private void Save(string field, System.Text.Json.Nodes.JsonNode? value)
    {
        if (_settingsInvalid) throw new IOException(Error);
        Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
        // Preserve unknown fields and use an atomic replacement, without backing up user data.
        var settings = File.Exists(_settingsPath) ? System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(_settingsPath))!.AsObject() : new System.Text.Json.Nodes.JsonObject();
        settings["roots"] ??= JsonSerializer.SerializeToNode(_roots);
        settings[field] = value;
        var temp = _settingsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temp, settings.ToJsonString(new() { WriteIndented = true })); File.Move(temp, _settingsPath, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    internal void SetBilingual(bool enabled)
    {
        if (enabled == BilingualEnabled) return;
        Save("bilingual", JsonSerializer.SerializeToNode(enabled)); BilingualEnabled = enabled; OnChanged();
    }
    internal void AddAlias(SearchEntry entry, string alias)
    {
        alias = SearchMatch.Normalize(alias.Trim());
        if (alias.Length is < 1 or > 200) throw new ArgumentException("别名需为 1–200 个字符。");
        if (!Path.IsPathFullyQualified(entry.Target) && !entry.Target.StartsWith("shell:AppsFolder\\", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("别名目标无效。");
        var next = _aliases.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
        if (next.TryGetValue(entry.Target, out var existing) && existing.Length >= 20 && !existing.Contains(alias))
            throw new ArgumentException("每个目标最多保存 20 个别名。");
        next[entry.Target] = (next.GetValueOrDefault(entry.Target) ?? []).Append(alias).Distinct().Take(20).ToArray();
        Save("aliases", JsonSerializer.SerializeToNode(next.Select(pair => new { entry = pair.Key.Equals(entry.Target, StringComparison.OrdinalIgnoreCase) ? entry : _aliasEntries[pair.Key], names = pair.Value }).ToArray()));
        _aliasEntries[entry.Target] = entry; _aliases[entry.Target] = next[entry.Target]; OnChanged();
    }
    internal void SetFileScope(SearchScope scope)
    {
        if (scope.Key == FileScope.Key) return;
        Save("fileScope", JsonSerializer.SerializeToNode(scope.Key)); FileScope = scope; OnChanged();
    }
    internal async Task RefreshAsync()
    { await Applications(true); await _folders.RefreshAsync(); OnChanged(); }

    internal async Task<SearchReply> QueryAsync(string query, SearchFilter filter, CancellationToken cancellation)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, _lifetime.Token);
        var token = linked.Token; var watch = Stopwatch.StartNew();
        query = query.Trim();
        if (query.Length == 0) return new([], "", 0);
        var entries = new List<SearchEntry>(); var status = new List<string>(); var scope = FileScope;
        var bilingual = BilingualEnabled; var plan = SearchPlan.Literal(query);
        var aliases = bilingual ? _aliases.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase) : null;
        if (bilingual)
        {
            entries.AddRange(_aliasEntries.Values.Where(e => e.Kind != SearchKind.Application));
            try { var lexicon = await BilingualLexicon.GetAsync(token); plan = await Task.Run(() => lexicon.Plan(query), token); }
            catch (Exception e) when (e is not OperationCanceledException) { status.Add("中英词库不可用，原文与自定义别名仍可检索"); }
        }
        if (filter != SearchFilter.Files)
        {
            try
            {
                var applications = await Applications().WaitAsync(token); entries.AddRange(applications);
                if (bilingual)
                {
                    var installed = applications.Select(e => e.Target).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    entries.AddRange(_aliasEntries.Values.Where(e => e.Kind == SearchKind.Application && installed.Contains(e.Target)));
                }
            }
            catch (Exception e) when (e is not OperationCanceledException) { status.Add("应用索引不可用，请刷新"); }
        }
        if (filter != SearchFilter.Applications)
        {
            status.Add(scope.Label); var beforeFolders = entries.Count; entries.AddRange(_folders.Entries.Where(e => scope.Contains(e.Target)));
            var folderCount = entries.Count - beforeFolders;
            await _files.WaitAsync(token);
            try
            {
                token.ThrowIfCancellationRequested(); var everything = EverythingProvider.Available; var completed = false;
                if (everything)
                {
                    try { entries.AddRange(await EverythingProvider.FilesAsync(plan, token, scope)); status.Add("Everything"); completed = true; }
                    catch (Exception e) when (e is not OperationCanceledException) { status.Add("Everything 不可用"); }
                }
                if (!completed)
                {
                    try { entries.AddRange(await WindowsSearchProvider.OnSta(() => WindowsSearchProvider.Files(plan, token, scope), token)); status.Add("Windows 已索引位置"); }
                    catch (Exception e) when (e is not OperationCanceledException) { status.Add("Windows 索引不可用"); }
                }
            }
            finally { _files.Release(); }
            if (_roots.Count > 0) status.Add($"指定目录 {folderCount:N0} 项");
            if (_folders.Status.Length > 0) status.Add(_folders.Status);
            if (_initialization?.IsCompleted == false) status.Add("目录索引建立中");
        }
        token.ThrowIfCancellationRequested();
        if (Error is not null) status.Add(Error);
        // A direct existing path works even outside every search index; no shell expression is evaluated.
        if (Path.IsPathFullyQualified(query) && (File.Exists(query) || Directory.Exists(query)))
            entries.Add(new(Path.GetFileName(query.TrimEnd('\\')), query, Directory.Exists(query) ? SearchKind.Folder : SearchKind.File, "直接路径"));
        var ranked = await Task.Run(() => SearchMatch.Rank(entries.Where(e => e.Kind == SearchKind.Application || scope.Contains(e.Target)), query, filter, plan: plan, aliases: aliases), token);
        var available = ranked.Where(e => e.Kind == SearchKind.Application || File.Exists(e.Target) || Directory.Exists(e.Target))
            .Select(e => e.Kind == SearchKind.File && Directory.Exists(e.Target) ? e with { Kind = SearchKind.Folder } : e).ToArray();
        return new(available, string.Join(" · ", status), watch.Elapsed.TotalMilliseconds);
    }
    public void Dispose()
    { if (_disposed) return; _disposed = true; _lifetime.Cancel(); _folders.Changed -= OnChanged; _folders.Dispose(); }
}
