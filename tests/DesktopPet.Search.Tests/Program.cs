using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DesktopPet;
using DesktopPet.Character;
using DesktopPet.Search;
using W = System.Windows.Controls;

internal static class Program
{
    private static readonly List<string> Passed = [];
    private static readonly List<object> Measurements = [];
    private static string _output = "";
    [STAThread] private static int Main(string[] args)
    {
        _output = Path.Combine(Path.GetFullPath(args[0]), Guid.NewGuid().ToString("N")); Directory.CreateDirectory(_output);
        var app = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        try
        {
            if (args.Contains("--disk-search-check")) { Matching(); Protocol(); SteamShortcuts(); ClosingRegression(); StableResults(); DiskCoverage(); }
            else { Matching(); Protocol(); ServiceAndLive(); ClosingRegression(); SteamShortcuts(); Bilingual(); Scopes(); UiAndLifecycle(); }
            File.WriteAllText(Path.Combine(_output, "result.json"), JsonSerializer.Serialize(new { passed = Passed, measurements = Measurements,
                note = args.Contains("--disk-search-check") ? "Direct filesystem coverage fixtures plus live local C drive searches; isolated settings; no injected input or launched apps."
                    : "isolated metadata fixtures; real installed apps and Windows Search; Everything IPC uses a local fake server, not real Everything; current DPI, no injected input or launched apps" }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"PASS {Passed.Count} search checks; {_output}"); return 0;
        }
        catch (Exception error) { File.WriteAllText(Path.Combine(_output, "failure.json"), JsonSerializer.Serialize(new { error = error.Message, passed = Passed, measurements = Measurements })); Console.Error.WriteLine(error); return 1; }
        finally { foreach (Window window in app.Windows.Cast<Window>().ToArray()) window.Close(); app.Shutdown(); }
    }
    private static void Check(bool pass, string name) { if (!pass) throw new Exception(name); Passed.Add(name); }
    private static void StableResults()
    {
        using var service = new SearchService(Path.Combine(_output, "stable-settings.json"));
        var window = new SearchWindow(service) { ShowActivated = false }; window.Show(); Pump(80);
        Field<W.TextBox>(window, "_query").Text = "stability";
        Field<DispatcherTimer>(window, "_debounce").Stop();
        var first = new SearchEntry("first", Path.Combine(_output, "first.txt"), SearchKind.File, "fixture");
        var second = new SearchEntry("second", Path.Combine(_output, "second.txt"), SearchKind.File, "fixture");
        window.Render(new([first, second], "扫描中…", 10) { Complete = false });
        var results = Field<W.ListBox>(window, "_results"); var firstRow = results.Items[0]; var secondRow = results.Items[1];
        results.SelectedIndex = 1;
        typeof(SearchService).GetMethod("OnChanged", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(service, []); Pump(60);
        Check(results.Items.Count == 2 && ReferenceEquals(results.Items[0], firstRow), "background index changes never clear visible results or restart the query");
        var late = new SearchEntry("late", Path.Combine(_output, "late.txt"), SearchKind.File, "fixture");
        window.Render(new([late, second, first], "扫描中…", 20) { Complete = false });
        Check(ReferenceEquals(results.Items[0], firstRow) && ReferenceEquals(results.Items[1], secondRow), "provisional results retain existing row objects and positions when provider ranking changes");
        Check(ReferenceEquals(results.SelectedItem, secondRow), "incremental results preserve selection");
        var status = Field<W.TextBlock>(window, "_status");
        Check(status.Text.Contains("正在补全") && status.TextWrapping == TextWrapping.NoWrap, "results found state is explicit and progress cannot resize the result area");
        window.Render(new([late, second, first], "扫描完成", 30));
        Check(ReferenceEquals(results.Items[0], firstRow) && status.Text.Contains("检索完成"), "completed results stay in place and scanning state ends");
        window.Close();
    }
    private static void DiskCoverage()
    {
        var root = Path.Combine(_output, "disk-a"); var other = Path.Combine(_output, "disk-b");
        Directory.CreateDirectory(root); Directory.CreateDirectory(other);
        var paths = new List<string>();
        foreach (var folder in new[] { ".git", "bin", "obj", "node_modules", "normal" })
        {
            var directory = Path.Combine(root, folder); Directory.CreateDirectory(directory);
            var file = Path.Combine(directory, "coverage_unique_9517.txt"); File.WriteAllText(file, "fixture"); paths.Add(file);
        }
        File.SetAttributes(paths[0], FileAttributes.Hidden);
        var remoteFile = Path.Combine(other, "coverage_unique_9517.txt"); File.WriteAllText(remoteFile, "fixture");
        var plan = SearchPlan.Literal("coverage_unique_9517");
        var scan = Await(DiskSearch.SearchAsync(plan, [root], CancellationToken.None));
        Check(scan.Complete && scan.Skipped == 0 && paths.All(path => scan.Entries.Any(e => e.Target == path)), "direct traversal includes hidden, .git, bin, obj and node_modules files without opt-in roots");
        Check(scan.Entries.All(e => !e.Target.StartsWith(other)), "selected storage root cannot return another root");
        scan = Await(DiskSearch.SearchAsync(plan, [root, other], CancellationToken.None));
        Check(scan.Entries.Any(e => e.Target == remoteFile) && paths.All(path => scan.Entries.Any(e => e.Target == path)), "all storage roots contribute direct results");
        scan = Await(DiskSearch.SearchAsync(plan, [root, Path.Combine(_output, "missing-drive")], CancellationToken.None));
        Check(scan.Skipped == 1 && scan.Status.Contains("结果不完整") && scan.Entries.Count == paths.Count, "unreadable storage reports incomplete coverage while retaining readable results");
        using (var canceled = new CancellationTokenSource())
        {
            canceled.Cancel();
            try { Await(DiskSearch.SearchAsync(plan, [root], canceled.Token)); Check(false, "canceled scan"); }
            catch (OperationCanceledException) { Check(true, "disk traversal respects cancellation"); }
        }
        for (var i = 0; i < 400; i++) File.WriteAllText(Path.Combine(root, $"exact9517-long-{i:D4}.txt"), "fixture");
        var exact = Path.Combine(root, "exact9517.txt"); File.WriteAllText(exact, "fixture");
        scan = Await(DiskSearch.SearchAsync(SearchPlan.Literal("exact9517"), [root], CancellationToken.None));
        Check(scan.Visited > 400 && scan.Entries.Count == 80 && scan.Entries[0].Target == exact, "result display cap does not truncate traversal or hide later exact matches");
        var rankedResults = scan.Entries;
        var link = Path.Combine(root, "linked-storage");
        using (var junction = Process.Start(new ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            ArgumentList = { "/c", "mklink", "/J", link, other }
        })!)
        {
            junction.WaitForExit(); Check(junction.ExitCode == 0, "isolated cross-root junction fixture created");
        }
        scan = Await(DiskSearch.SearchAsync(SearchPlan.Literal("linked-storage"), [root], CancellationToken.None));
        Check(scan.Links == 1 && scan.Entries.Any(e => e.Target == link) && scan.Entries.All(e => e.Target != Path.Combine(link, Path.GetFileName(remoteFile))),
            "directory link itself is searchable without expanding another storage root");
        var scopes = SearchScope.Available(SearchScope.All);
        Check(scopes.Where(s => s.Root is not null).All(s => DiskSearch.Roots(SearchScope.All).Contains(s.Root!, StringComparer.OrdinalIgnoreCase)), "all computer traversal covers every currently selectable disk");
        Check(DiskSearch.Roots(SearchScope.Parse("Z:\\")).SequenceEqual(new[] { "Z:\\" }), "unavailable selected disk never silently widens to all computer");
        using (var canceled = new CancellationTokenSource())
        {
            var scanTask = DiskSearch.SearchAsync(plan, DiskSearch.Roots(SearchScope.All), canceled.Token, _ => canceled.Cancel());
            try { Await(scanTask, 10000); Check(false, "active scan cancellation"); }
            catch (OperationCanceledException) { Check(true, "active whole-disk scan cancels after its first progress update"); }
        }
        var serviceSettings = Path.Combine(_output, "direct-settings.json");
        using var service = new SearchService(serviceSettings);
        service.SetFileScope(SearchScope.Parse("C:\\"));
        var progressCount = 0; SearchReply? last = null;
        var progress = new InlineProgress<SearchReply>(reply => { progressCount++; last = reply; });
        foreach (var name in new[] { "P3R", "Counter-Strike Global Offensive" })
        {
            var expected = Path.Combine(@"C:\steam app\steamapps\common", name);
            var reply = Await(service.QueryAsync(name, SearchFilter.Files, CancellationToken.None, progress), 180000);
            Check(reply.Complete && reply.Status.Contains("扫描完成") && reply.Entries.All(e => e.Target.StartsWith("C:\\", StringComparison.OrdinalIgnoreCase)), "live selected C disk completes direct coverage: " + name);
            if (Directory.Exists(expected)) Check(reply.Entries.Any(e => e.Target.Equals(expected, StringComparison.OrdinalIgnoreCase)), "unindexed installed game folder is found: " + name);
            Measurements.Add(new { name, milliseconds = reply.Milliseconds, status = reply.Status });
        }
        Check(progressCount > 0 && last is { Complete: false }, "live long search publishes provisional results and scanning state");
        service.SetFileScope(SearchScope.All);
        var all = Await(service.QueryAsync("coverage_unique_9517", SearchFilter.Files, CancellationToken.None), 180000);
        Check(all.Entries.Any(e => e.Target == paths[0]) && all.Status.Contains("全部电脑"), "all computer finds a newly created hidden fixture without adding a directory or Windows indexing");
        Measurements.Add(new { name = "all-computer", milliseconds = all.Milliseconds, status = all.Status });
        service.SetFileScope(SearchScope.Parse("Z:\\"));
        var unavailable = Await(service.QueryAsync("coverage_unique_9517", SearchFilter.Files, CancellationToken.None));
        Check(unavailable.Entries.Count == 0 && unavailable.Status.Contains("结果不完整"), "selected missing disk yields truthful incomplete result instead of searching C");
        var ui = new SearchWindow(service) { ShowActivated = false }; ui.Show(); Pump(40);
        ui.Render(new([], "扫描中…", 10) { Complete = false });
        Check(Field<W.TextBlock>(ui, "_empty").Text.Contains("正在搜索"), "unfinished disk scan never displays no matches");
        ui.Render(new(rankedResults, "扫描中…", 10) { Complete = false });
        var results = Field<W.ListBox>(ui, "_results"); results.SelectedIndex = 2;
        var selected = ((SearchEntry)((W.ListBoxItem)results.SelectedItem).Tag).Target;
        ui.Render(new(rankedResults, "扫描完成", 20));
        Check(((SearchEntry)((W.ListBoxItem)results.SelectedItem).Tag).Target == selected, "progress rendering preserves the selected result");
        ui.Close();
        service.SetFileScope(SearchScope.All);
        var liveUi = new SearchWindow(service) { ShowActivated = false }; liveUi.Show(); Pump(40);
        Field<W.TextBox>(liveUi, "_query").Text = "P3R";
        var liveResults = Field<W.ListBox>(liveUi, "_results");
        var expectedGame = @"C:\steam app\steamapps\common\P3R";
        if (Directory.Exists(expectedGame))
        {
            Until(() => liveResults.Items.OfType<W.ListBoxItem>().Any(item => ((SearchEntry)item.Tag).Target == expectedGame), 90000);
            Check((Field<W.TextBlock>(liveUi, "_status").ToolTip as string)?.Contains("磁盘直接检索") == true, "real search window finds P3R through direct disk traversal");
            Capture(liveUi, "all-disks-p3r-live.png");
        }
        var activeRequest = Field<CancellationTokenSource?>(liveUi, "_request");
        var activeToken = activeRequest?.Token;
        liveUi.Close(); Pump(80);
        Check(activeToken?.IsCancellationRequested == true, "closing real search UI cancels its active whole-disk request");
    }
    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    { public void Report(T value) => report(value); }
    private static T Field<T>(object instance, string name) => (T)instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(instance)!;
    private static void Pump(int milliseconds)
    {
        var frame = new DispatcherFrame(); var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; }; timer.Start(); Dispatcher.PushFrame(frame);
    }
    private static T Await<T>(Task<T> task, int timeout = 10000)
    { var clock = Stopwatch.StartNew(); while (!task.IsCompleted && clock.ElapsedMilliseconds < timeout) Pump(15); if (!task.IsCompleted) throw new TimeoutException("test task exceeded bound"); return task.GetAwaiter().GetResult(); }
    private static void Await(Task task, int timeout = 10000) => Await(AsValue(task), timeout);
    private static async Task<bool> AsValue(Task task) { await task; return true; }
    private static void Until(Func<bool> predicate, int timeout = 6000)
    { var clock = Stopwatch.StartNew(); while (!predicate() && clock.ElapsedMilliseconds < timeout) Pump(20); Check(predicate(), "async condition completes within bound"); }
    private static void Matching()
    {
        SearchEntry Entry(string name, string target, SearchKind kind = SearchKind.File, string? alias = null) => new(name, target, kind, "fixture", alias);
        var entries = new[] { Entry("报告.pdf", @"F:\one\报告.pdf"), Entry("报告.pdf", @"F:\two\报告.pdf"),
            Entry("报告说明.pdf", @"F:\one\报告说明.pdf"), Entry("组会 2026 报告.pdf", @"F:\组会 2026 报告.pdf"),
            Entry("Visual Studio Code", "shell:AppsFolder\\Code", SearchKind.Application, "code.exe"), Entry("Obsidian", "shell:AppsFolder\\Obsidian", SearchKind.Application) };
        var ranked = SearchMatch.Rank(entries, "报告", SearchFilter.Files);
        Check(ranked.Count == 4 && ranked[0].Name == "报告.pdf" && ranked[1].Name == "报告.pdf", "Chinese exact filename stem ranks ahead of partial matches; duplicate names retain both paths");
        Check(SearchMatch.Rank(entries, "2026 报告", SearchFilter.All).Single().Name == "组会 2026 报告.pdf", "all query tokens must match");
        Check(SearchMatch.Rank(entries, "VSC", SearchFilter.Applications).Single().Name == "Visual Studio Code", "English word initials match apps");
        Check(SearchMatch.Rank(entries, "ＯＢＳＩＤＩＡＮ", SearchFilter.All).Single().Name == "Obsidian", "Unicode width and case normalization");
        Check(SearchMatch.Rank(entries, "qzzzz", SearchFilter.All).Count == 0, "unrelated items do not become fuzzy matches");
        Check(SearchMatch.Rank(entries.Concat(entries), "报告", SearchFilter.All).Count == 4, "same target is deduplicated");
        Check(SearchMatch.Rank(entries, "报告", SearchFilter.Applications).Count == 0, "applications filter excludes files");
        var sql = WindowsSearchProvider.Sql("50%_['x");
        Check(sql.Contains("50[%][_][[]''x"), "SQL wildcards and apostrophes are literal escaped");
        Check(!WindowsSearchProvider.Sql("readme").Contains("System.ItemPathDisplay") && WindowsSearchProvider.Sql(@"research\readme").Contains("System.ItemPathDisplay"), "ordinary filename query avoids full path scan; explicit path query enables it");
        Check(EverythingProvider.Pattern("foo|bar ext:exe").Contains(@"foo\|bar") && EverythingProvider.Pattern("\"").Contains(@"\x22"), "Everything syntax and quotes cannot escape literal regex query");
        var corpus = Enumerable.Range(0, 100000).Select(i => Entry($"研究报告_{i:D6}.pdf", $@"F:\fixtures\研究报告_{i:D6}.pdf")).ToArray();
        SearchMatch.Rank(corpus, "099999", SearchFilter.Files);
        var samples = new List<double>();
        for (var i = 0; i < 7; i++) { var clock = Stopwatch.StartNew(); var result = SearchMatch.Rank(corpus, "099999", SearchFilter.Files); samples.Add(clock.Elapsed.TotalMilliseconds); Check(result.Count == 1, "100k corpus precise match " + i); }
        Measurements.Add(new { name = "100k metadata ranking, warm", medianMs = samples.Order().ElementAt(3), maxMs = samples.Max(), synthetic = true });
    }
    private static byte[] Reply(string name, string path)
    {
        var first = Encoding.Unicode.GetBytes(name + '\0'); var second = Encoding.Unicode.GetBytes(path + '\0');
        var bytes = new byte[40 + first.Length + second.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(20), 1); BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(32), 40);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(36), (uint)(40 + first.Length)); first.CopyTo(bytes, 40); second.CopyTo(bytes, 40 + first.Length); return bytes;
    }
    private static void Protocol()
    {
        var data = Reply("中文 文件.pdf", @"F:\含 空格");
        Check(EverythingProvider.Parse(data).Single().Target == @"F:\含 空格\中文 文件.pdf", "Unicode IPC offsets and full paths decode correctly");
        var invalid = new byte[28]; BinaryPrimitives.WriteUInt32LittleEndian(invalid.AsSpan(20), uint.MaxValue);
        try { EverythingProvider.Parse(invalid); Check(false, "invalid IPC rejected"); } catch (InvalidDataException) { Check(true, "invalid IPC result count rejected"); }
        invalid = data.ToArray(); BinaryPrimitives.WriteUInt32LittleEndian(invalid.AsSpan(32), 0);
        try { EverythingProvider.Parse(invalid); Check(false, "bad offset rejected"); } catch (InvalidDataException) { Check(true, "IPC offsets cannot reference header"); }
        Check(!EverythingProvider.Available, "no real Everything instance interferes with isolated IPC server");
        using var server = new IpcServer();
        var query = EverythingProvider.FilesAsync("中文 文件", CancellationToken.None);
        var result = Await(query);
        Check(result.Single().Name == "中文 文件.pdf" && server.QueryReceived, "real WM_COPYDATA handshake to isolated Everything-compatible server");
        server.Silent = true; using var cancellation = new CancellationTokenSource();
        query = EverythingProvider.FilesAsync("cancel", cancellation.Token); Pump(60); cancellation.Cancel();
        try { Await(query); Check(false, "cancel query stops"); } catch (OperationCanceledException) { Check(true, "pending Everything query cancels and owns only its reply window"); }
        var clock = Stopwatch.StartNew();
        try { Await(EverythingProvider.FilesAsync("timeout", CancellationToken.None)); Check(false, "timeout"); }
        catch (TimeoutException) { Check(clock.ElapsedMilliseconds < 2500, "Everything no-reply timeout is bounded"); }
    }
    private static void ServiceAndLive()
    {
        var root = Path.Combine(_output, "files"); Directory.CreateDirectory(root); Directory.CreateDirectory(Path.Combine(root, "two"));
        File.WriteAllText(Path.Combine(root, "精确 报告.pdf"), "fixture metadata only"); File.WriteAllText(Path.Combine(root, "two", "精确 报告.pdf"), "fixture");
        File.WriteAllText(Path.Combine(root, "percent%_file.txt"), "fixture"); Directory.CreateDirectory(Path.Combine(root, "node_modules"));
        File.WriteAllText(Path.Combine(root, "node_modules", "精确 报告.pdf"), "excluded fixture");
        var settings = Path.Combine(_output, "search-settings.json"); File.WriteAllText(settings, "{\"roots\":[],\"futureField\":7}");
        using var service = new SearchService(settings); Await(service.InitializeAsync()); Await(service.AddRootAsync(root));
        var reply = Await(service.QueryAsync("精确 报告", SearchFilter.Files, CancellationToken.None));
        Check(reply.Entries.Count(e => e.Source == "指定目录") == 2, "explicit folder metadata index covers files outside Windows index, excludes dependency trees");
        Check(JsonDocument.Parse(File.ReadAllText(settings)).RootElement.GetProperty("futureField").GetInt32() == 7, "adding root preserves unknown search settings fields");
        reply = Await(service.QueryAsync("percent%_", SearchFilter.Files, CancellationToken.None));
        Check(reply.Entries.Any(e => e.Target == Path.Combine(root, "percent%_file.txt")), "literal wildcard filename remains searchable");
        var newPath = Path.Combine(root, "新增试验.txt"); File.WriteAllText(newPath, "fixture"); Pump(950);
        reply = Await(service.QueryAsync("新增试验", SearchFilter.Files, CancellationToken.None)); Check(reply.Entries.Any(e => e.Target == newPath), "watcher indexes newly created file");
        var moved = Path.Combine(root, "重命名试验.txt"); File.Move(newPath, moved); Pump(950);
        reply = Await(service.QueryAsync("重命名试验", SearchFilter.Files, CancellationToken.None)); Check(reply.Entries.Any(e => e.Target == moved), "watcher follows rename");
        File.Delete(moved); reply = Await(service.QueryAsync("重命名试验", SearchFilter.Files, CancellationToken.None)); Check(reply.Entries.All(e => e.Target != moved), "deleted file is excluded before watcher rescan");
        reply = Await(service.QueryAsync(Path.Combine(root, "精确 报告.pdf"), SearchFilter.Files, CancellationToken.None));
        Check(reply.Entries.Any(e => e.Target == Path.Combine(root, "精确 报告.pdf")), "direct full path resolves outside indexes");
        using var restored = new SearchService(settings); Await(restored.InitializeAsync()); Check(restored.Roots.SequenceEqual([root]), "selected scope survives restart");
        var damaged = Path.Combine(_output, "damaged.json"); File.WriteAllText(damaged, "{broken"); using var bad = new SearchService(damaged);
        try { Await(bad.AddRootAsync(root)); Check(false, "damaged settings"); } catch (IOException) { Check(File.ReadAllText(damaged) == "{broken", "corrupt settings are retained and cannot be overwritten"); }
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        try { Await(service.QueryAsync("anything", SearchFilter.All, cancelled.Token)); Check(false, "query cancellation"); } catch (OperationCanceledException) { Check(true, "pre-cancelled search avoids provider work"); }
        var apps = Await(WindowsSearchProvider.OnSta(() => WindowsSearchProvider.Applications(CancellationToken.None), CancellationToken.None));
        Check(apps.Count > 100 && apps.Any(e => e.Name.Contains("Obsidian", StringComparison.OrdinalIgnoreCase)), "real installed apps include Obsidian");
        var metrics = new List<double>();
        foreach (var term in new[] { "Obsidian", "Zotero", "Codex", "哔哩哔哩", "Obsidian", "Zotero", "Codex" })
        { reply = Await(service.QueryAsync(term, SearchFilter.Applications, CancellationToken.None)); metrics.Add(reply.Milliseconds); Check(reply.Entries.Any(e => e.Kind == SearchKind.Application), "real app query " + term); }
        Measurements.Add(new { name = "real app queries, warm", indexedApps = apps.Count, medianMs = metrics.Order().ElementAt(metrics.Count / 2), maxMs = metrics.Max(), synthetic = false });
        var live = Await(WindowsSearchProvider.OnSta(() => WindowsSearchProvider.Files("README", CancellationToken.None), CancellationToken.None));
        var real = live.FirstOrDefault(e => File.Exists(e.Target)); Check(real is not null, "Windows index URLs resolve to real paths, avoiding localized display-path errors");
        metrics.Clear();
        for (var i = 0; i < 5; i++) { reply = Await(service.QueryAsync(real!.Name, SearchFilter.Files, CancellationToken.None)); metrics.Add(reply.Milliseconds); Check(reply.Entries.Any(e => e.Target.Equals(real.Target, StringComparison.OrdinalIgnoreCase)), "real indexed file exact query " + i); }
        Measurements.Add(new { name = "real Windows indexed file queries, warm", medianMs = metrics.Order().ElementAt(2), maxMs = metrics.Max(), synthetic = false });
        reply = Await(service.QueryAsync("精确 报告", SearchFilter.Files, CancellationToken.None));
        File.WriteAllText(Path.Combine(_output, "file-results-summary.json"), JsonSerializer.Serialize(new { count = reply.Entries.Count, reply.Status, reply.Milliseconds }));
        var entry = reply.Entries.First(); Check(SearchActions.StartInfo(entry).FileName == entry.Target, "open action uses selected target, not query text");
        var info = SearchActions.StartInfo(entry, true); Check(info.ArgumentList.Single() == "/select," + entry.Target && info.FileName.EndsWith("explorer.exe"), "reveal preserves spaces as a single Explorer argument");
        var shellApp = apps.First(e => e.Target.StartsWith("shell:AppsFolder\\")); Check(SearchActions.StartInfo(shellApp).ArgumentList.Single() == shellApp.Target, "AppsFolder launch preserves AUMID");
        try { SearchActions.StartInfo(new("bad", "https://bad", SearchKind.File, "fixture")); Check(false, "unsafe target"); } catch (FileNotFoundException) { Check(true, "file actions reject URL or shell expression targets"); }
    }
    private static void UiAndLifecycle()
    {
        using var service = new SearchService(Path.Combine(_output, "ui-settings.json"));
        var window = new SearchWindow(service, (_, _) => { }) { ShowActivated = false }; window.Show(); Pump(120);
        var query = Field<W.TextBox>(window, "_query"); var results = Field<W.ListBox>(window, "_results"); var filter = Field<W.ComboBox>(window, "_filter");
        filter.SelectedIndex = 1; query.Text = "Obsidian";
        Until(() => results.Items.Count > 0); Check(((SearchEntry)((W.ListBoxItem)results.Items[0]).Tag).Name.Contains("Obsidian", StringComparison.OrdinalIgnoreCase), "actual WPF debounced app search");
        Capture(window, "search-apps-live.png");
        query.Text = "Zotero"; query.Text = "Codex"; Until(() => results.Items.Count > 0);
        Check(results.Items.Cast<W.ListBoxItem>().All(i => SearchMatch.Score((SearchEntry)i.Tag, "Codex") > 0), "new query cannot receive older query results");
        query.Text = " "; Pump(160); Check(results.Items.Count == 0 && !Field<W.Button>(window, "_launch").IsEnabled, "empty query clears selection and disables launch");
        var root = Path.Combine(_output, "ui-files"); Directory.CreateDirectory(root); Directory.CreateDirectory(Path.Combine(root, "归档"));
        File.WriteAllText(Path.Combine(root, "桌宠验收组会报告.pdf"), "metadata fixture"); File.WriteAllText(Path.Combine(root, "归档", "桌宠验收组会报告.pdf"), "metadata fixture");
        Await(service.AddRootAsync(root)); filter.SelectedIndex = 2; query.Text = "桌宠验收";
        Until(() => results.Items.Count == 2); Capture(window, "search-results.png");
        var scope = Field<W.ComboBox>(window, "_scope");
        Check(scope.Items.Cast<SearchScope>().Select(s => s.Key).Contains("F:\\") && scope.SelectedItem is SearchScope { Root: null }, "WPF file range dropdown includes local F drive and defaults to all computer");
        scope.SelectedItem = scope.Items.Cast<SearchScope>().Single(s => s.Key == "C:\\"); Pump(220);
        Until(() => Field<W.TextBlock>(window, "_empty").Text == "未找到匹配项");
        Check(results.Items.Count == 0 && service.FileScope.Key == "C:\\", "WPF C drive selection excludes actual F drive fixture and cancels prior results");
        scope.SelectedItem = scope.Items.Cast<SearchScope>().Single(s => s.Key == "F:\\"); Until(() => results.Items.Count == 2);
        Capture(window, "search-scope-f.png");
        window.Width = 440; window.Height = 340; Pump(80); Capture(window, "search-minimum.png");
        Check(Field<W.Button>(window, "_launch").ActualWidth > 0 && Field<W.Button>(window, "_reveal").ActualWidth > 0, "minimum search window exposes file actions");
        Check(scope.TransformToAncestor(window).Transform(new System.Windows.Point(scope.ActualWidth, scope.ActualHeight)).X <= window.ActualWidth - 8,
            "file range dropdown remains within the minimum width window");
        window.Render(new([new("Obsidian", "shell:AppsFolder\\Obsidian", SearchKind.Application, "fixture")], "", 0));
        Check(!Field<W.Button>(window, "_reveal").IsEnabled, "installed app without physical path disables reveal");
        var bilingual = Field<W.CheckBox>(window, "_bilingual"); bilingual.IsChecked = true; filter.SelectedIndex = 1; query.Text = "黑曜石";
        Check(!scope.IsEnabled && service.FileScope.Key == "F:\\", "applications-only mode disables file range dropdown without losing saved file scope");
        Until(() => results.Items.Count > 0);
        Check(results.Items.Cast<W.ListBoxItem>().Any(i => ((SearchEntry)i.Tag).Name.Contains("Obsidian", StringComparison.OrdinalIgnoreCase) && ((SearchEntry)i.Tag).MatchHint == "中英匹配"), "actual WPF toggle translates Chinese app name into installed English name");
        Capture(window, "search-bilingual-live.png");
        var aliasDialog = new SearchAliasWindow("OriginalName.pdf") { ShowActivated = false };
        var aliasInput = Field<W.TextBox>(aliasDialog, "_input"); aliasInput.Text = "中文译名 English alias";
        Check(aliasDialog.Alias == "中文译名 English alias", "alias dialog preserves typed bilingual phrase");
        aliasDialog.Close();
        scope.SelectedItem = scope.Items.Cast<SearchScope>().Single(s => s.Root is null);
        query.Text = "close-pending"; window.Close(); Pump(180); Check(!window.IsVisible, "closing window cancels debounce and pending search");
        var options = new StartupOptions(null, Path.Combine(_output, "pet-data"), null); var paths = new RuntimePaths(options);
        var store = new SettingsStore(paths.SettingsPath); var settings = store.Load(); var catalog = CharacterCatalog.Load(paths.CharacterRoot, paths.UserCharacterRoot);
        var pet = new PetWindow(catalog.Characters, paths.CharacterRoot, "ellen-flat2d", settings, store, paths) { ShowActivated = false };
        System.Windows.Application.Current.MainWindow = pet; pet.Show(); Pump(80);
        var controller = Field<SearchController>(pet, "_search");
        var menu = pet.ContextMenu.Items.OfType<W.MenuItem>().Single(i => i.Header?.ToString() == "搜索文件／应用");
        menu.RaiseEvent(new RoutedEventArgs(W.MenuItem.ClickEvent)); Pump(80);
        var opened = Field<SearchWindow>(controller, "_window"); Check(opened.IsVisible, "pet context menu opens search window");
        var tray = Field<TrayService>(pet, "_tray"); typeof(TrayService).GetMethod("RebuildMenu", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(tray, null);
        Check(Field<System.Windows.Forms.ContextMenuStrip>(tray, "_menu").Items.Cast<System.Windows.Forms.ToolStripItem>().Any(i => i.Text == "搜索文件／应用"), "tray search entry survives hidden pet workflow");
        typeof(PetWindow).GetMethod("SetPetHidden", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(pet, [true]); Pump(80);
        controller.Show(); Pump(50); opened = Field<SearchWindow>(controller, "_window"); Check(opened.Owner is null && opened.IsVisible, "hidden pet search is independent and visible");
        pet.Close(); Pump(50); Check(!opened.IsVisible, "pet exit closes independent search window and index watchers");
    }
    private static void Bilingual()
    {
        var cold = Stopwatch.StartNew(); var lexicon = Await(BilingualLexicon.GetAsync(CancellationToken.None), 20000);
        Check(lexicon.TermCount > 100000, "full offline dictionary builds a substantial bidirectional term index");
        Measurements.Add(new { name = "offline dictionary first load", milliseconds = cold.Elapsed.TotalMilliseconds, terms = lexicon.TermCount, synthetic = false });
        var root = Path.Combine(_output, "bilingual-files"); Directory.CreateDirectory(root);
        var english = Path.Combine(root, "Welding_Report_2026.pdf"); var chinese = Path.Combine(root, "焊接报告.pdf");
        var opaque = Path.Combine(root, "DOI-UNIQUE-9184.pdf");
        File.WriteAllText(english, "metadata fixture"); File.WriteAllText(chinese, "metadata fixture"); File.WriteAllText(opaque, "metadata fixture");
        var entries = new[] { new SearchEntry(Path.GetFileName(english), english, SearchKind.File, "fixture"), new SearchEntry(Path.GetFileName(chinese), chinese, SearchKind.File, "fixture") };
        var plan = lexicon.Plan("焊接报告"); var ranked = SearchMatch.Rank(entries, "焊接报告", SearchFilter.Files, plan: plan);
        Check(ranked.Count == 2 && ranked[0].Target == chinese && ranked[0].MatchHint is null && ranked[1].Target == english && ranked[1].MatchHint == "中英匹配", "Chinese compound query retains exact original first and finds English file with translated badge");
        plan = lexicon.Plan("welding report.pdf"); ranked = SearchMatch.Rank(entries, "welding report.pdf", SearchFilter.Files, plan: plan);
        Check(ranked.Any(e => e.Target == chinese), "English phrase and extension translate to Chinese filename");
        plan = lexicon.Plan("2026 焊接 report"); ranked = SearchMatch.Rank(entries, plan.Original, SearchFilter.Files, plan: plan);
        Check(ranked.Count == 1 && ranked[0].Target == english, "mixed Chinese-English query preserves year constraint");
        plan = lexicon.Plan("焊接 UNKNOWN9184 报告"); Check(SearchMatch.Rank(entries, plan.Original, SearchFilter.Files, plan: plan).Count == 0, "unknown token is never dropped to broaden translation matches");
        Check(!lexicon.Plan(@"F:\焊接\报告.pdf").Expanded, "full path is literal rather than translated");
        plan = lexicon.Plan("焊接报告"); var sql = WindowsSearchProvider.Sql(plan);
        Check(sql.Contains("welding") && sql.Contains("report") && sql.Contains(" AND ") && sql.Contains(" OR "), "Windows provider queries translated alternatives in one combined request");
        var pattern = EverythingProvider.Pattern(plan); Check(pattern.Contains("welding") && pattern.Contains("report"), "Everything query carries the same bilingual alternatives");
        using (var server = new IpcServer())
        { Await(EverythingProvider.FilesAsync(plan, CancellationToken.None)); Check(server.LastPattern.Contains("welding") && server.LastPattern.Contains("report"), "translated query actually crosses Unicode IPC to isolated server"); }
        var settings = Path.Combine(_output, "bilingual-settings.json"); File.WriteAllText(settings, "{\"roots\":[],\"futureField\":42}");
        using var service = new SearchService(settings); Await(service.InitializeAsync()); Await(service.AddRootAsync(root));
        Check(!service.BilingualEnabled, "legacy search settings default to original-language mode");
        var reply = Await(service.QueryAsync("黑曜石", SearchFilter.Applications, CancellationToken.None)); Check(reply.Entries.All(e => !e.Name.Contains("Obsidian", StringComparison.OrdinalIgnoreCase)), "translation disabled does not add translated app results");
        service.SetBilingual(true); reply = Await(service.QueryAsync("黑曜石", SearchFilter.Applications, CancellationToken.None));
        Check(reply.Entries.Any(e => e.Name.Contains("Obsidian", StringComparison.OrdinalIgnoreCase) && e.MatchHint == "中英匹配"), "translation enabled finds actual installed Obsidian from Chinese translation");
        reply = Await(service.QueryAsync("反恐精英2", SearchFilter.Applications, CancellationToken.None));
        var game = reply.Entries.Single(e => e.Name == "Counter-Strike 2");
        Check(game.Source == "Steam 快捷方式" && game.Target.EndsWith("Counter-Strike 2.url") && game.MatchHint == "中英匹配", "Chinese game title finds actual local Steam desktop shortcut");
        reply = Await(service.QueryAsync("反恐精英 2", SearchFilter.Applications, CancellationToken.None));
        Check(reply.Entries.Any(e => e.Target == game.Target), "Chinese game title with separated version finds same local entry");
        reply = Await(service.QueryAsync("CS2", SearchFilter.Applications, CancellationToken.None));
        Check(reply.Entries.Any(e => e.Target == game.Target), "game acronym finds actual Steam entry");
        reply = Await(service.QueryAsync("反恐精英3", SearchFilter.Applications, CancellationToken.None));
        Check(reply.Entries.All(e => e.Target != game.Target), "translated game name preserves wrong version constraint");
        reply = Await(service.QueryAsync("焊接报告", SearchFilter.Files, CancellationToken.None));
        Check(reply.Entries.Any(e => e.Target == english && e.MatchHint == "中英匹配") && reply.Entries.Any(e => e.Target == chinese), "file service merges original and translated queries");
        service.AddAlias(new(Path.GetFileName(opaque), opaque, SearchKind.File, "fixture"), "析出动力学模型");
        reply = Await(service.QueryAsync("析出动力学模型", SearchFilter.Files, CancellationToken.None));
        Check(reply.Entries.Any(e => e.Target == opaque && e.MatchHint == "别名匹配" && e.Name == "DOI-UNIQUE-9184.pdf"), "custom translation alias finds exact stored target without renaming file");
        Check(JsonDocument.Parse(File.ReadAllText(settings)).RootElement.GetProperty("futureField").GetInt32() == 42, "bilingual toggle and aliases preserve unknown settings fields");
        using var restored = new SearchService(settings); Check(restored.BilingualEnabled, "bilingual toggle survives restart");
        reply = Await(restored.QueryAsync("析出动力学模型", SearchFilter.Files, CancellationToken.None));
        Check(reply.Entries.Any(e => e.Target == opaque), "custom target alias survives restart even before directory indexing");
        service.AddAlias(new("Removed application", "shell:AppsFolder\\DesktopPet.Uninstalled.Fixture", SearchKind.Application, "fixture"), "已卸载软件唯一译名");
        reply = Await(service.QueryAsync("已卸载软件唯一译名", SearchFilter.Applications, CancellationToken.None));
        Check(reply.Entries.Count == 0, "stored aliases cannot resurrect an application absent from the current app index");
        for (var i = 1; i < 20; i++) service.AddAlias(new(Path.GetFileName(opaque), opaque, SearchKind.File, "fixture"), "term" + i);
        try { service.AddAlias(new(Path.GetFileName(opaque), opaque, SearchKind.File, "fixture"), "term21"); Check(false, "alias limit"); }
        catch (ArgumentException) { Check(true, "alias limit reports an error instead of falsely saving a discarded alias"); }
        service.SetBilingual(false); reply = Await(service.QueryAsync("析出动力学模型", SearchFilter.Files, CancellationToken.None));
        Check(reply.Entries.All(e => e.Target != opaque), "turning off bilingual mode removes expanded and custom alias matches");
        var samples = new List<double>();
        for (var i = 0; i < 5; i++)
        { var watch = Stopwatch.StartNew(); plan = lexicon.Plan("焊接报告"); SearchMatch.Rank(entries, plan.Original, SearchFilter.Files, plan: plan); samples.Add(watch.Elapsed.TotalMilliseconds); }
        Measurements.Add(new { name = "offline expansion and two-file match, warm", medianMs = samples.Order().ElementAt(2), maxMs = samples.Max(), synthetic = true });
        service.SetBilingual(true); samples.Clear();
        for (var i = 0; i < 5; i++) { reply = Await(service.QueryAsync("黑曜石", SearchFilter.Applications, CancellationToken.None)); samples.Add(reply.Milliseconds); }
        Measurements.Add(new { name = "real bilingual app query, warm", medianMs = samples.Order().ElementAt(2), maxMs = samples.Max(), synthetic = false });
    }
    private static void Scopes()
    {
        var c = SearchScope.Parse("c:\\"); var f = SearchScope.Parse("F:\\");
        Check(c.Key == "C:\\" && SearchScope.All.Contains(@"\\server\share\file.txt"), "drive scopes canonicalize case and all computer does not restrict existing search sources");
        Check(c.Contains(@"C:\files\report.pdf") && !c.Contains(@"F:\files\report.pdf") && !c.Contains(@"C:relative.txt"), "drive scope matches full root boundaries rather than arbitrary drive-like text");
        foreach (var invalid in new[] { "F:", @"F:\folder", @"\\server\share", "C:\\' OR 1=1", "Ｆ:\\" })
        { try { SearchScope.Parse(invalid); Check(false, "bad scope"); } catch (ArgumentException) { Check(true, "invalid scope rejected: " + invalid); } }
        var choices = SearchScope.Available(SearchScope.Parse("Z:\\"));
        Check(choices[0].Root is null && choices.Any(s => s.Key == "C:\\") && choices.Any(s => s.Key == "D:\\") && choices.Any(s => s.Key == "F:\\"), "range choices enumerate actual ready C D F drives with all computer first");
        Check(choices.Any(s => s.Key == "Z:\\"), "unavailable saved disk scope is retained rather than silently broadening search");
        Check(WindowsSearchProvider.Sql("report", scope: f).Contains("SCOPE='file:F:/'") && WindowsSearchProvider.Sql("report", true, c).Contains("SCOPE='file:C:/'"), "Windows exact and broad queries constrain drive before provider candidate limits");
        var lexicon = Await(BilingualLexicon.GetAsync(CancellationToken.None)); var plan = lexicon.Plan("焊接报告");
        Check(WindowsSearchProvider.Sql(plan, f).Contains("SCOPE='file:F:/'") && WindowsSearchProvider.Sql(plan, f).Contains("welding"), "translated Windows query retains both drive and translation constraints");
        var pattern = EverythingProvider.Pattern(plan, f); var regex = pattern[7..^1];
        Check(System.Text.RegularExpressions.Regex.IsMatch(@"F:\files\Welding_Report.pdf", regex, System.Text.RegularExpressions.RegexOptions.IgnoreCase) &&
            !System.Text.RegularExpressions.Regex.IsMatch(@"C:\F\Welding_Report.pdf", regex, System.Text.RegularExpressions.RegexOptions.IgnoreCase), "Everything scoped regex matches only the selected drive even with matching directory names elsewhere");
        pattern = EverythingProvider.Pattern(SearchPlan.Literal(@"F:\files\report.pdf"), f);
        Check(System.Text.RegularExpressions.Regex.IsMatch(@"F:\files\report.pdf", pattern[7..^1], System.Text.RegularExpressions.RegexOptions.IgnoreCase), "Everything scope anchor does not consume drive text required by a full-path query");
        using (var server = new IpcServer())
        {
            Await(EverythingProvider.FilesAsync(plan, CancellationToken.None, f));
            Check(server.LastPattern.Contains(@"(?=^F:\\)"), "actual WM_COPYDATA sends selected drive constraint to isolated Everything server");
        }
        var root = Path.Combine(_output, "scope-files"); Directory.CreateDirectory(root);
        var file = Path.Combine(root, "scope_unique_928467.pdf"); File.WriteAllText(file, "metadata fixture");
        var settings = Path.Combine(_output, "scope-settings.json"); File.WriteAllText(settings, "{\"roots\":[],\"futureField\":81}");
        using var service = new SearchService(settings); Await(service.AddRootAsync(root));
        var reply = Await(service.QueryAsync("scope_unique_928467", SearchFilter.Files, CancellationToken.None));
        Check(reply.Entries.Any(e => e.Target == file) && reply.Status.Contains("全部电脑"), "all computer includes actual explicitly indexed F drive file");
        service.SetBilingual(true); service.AddAlias(new(Path.GetFileName(file), file, SearchKind.File, "fixture"), "盘符范围测试译名");
        service.SetFileScope(c); reply = Await(service.QueryAsync("盘符范围测试译名", SearchFilter.Files, CancellationToken.None));
        Check(reply.Entries.All(e => e.Target != file), "custom file aliases cannot bypass selected drive");
        reply = Await(service.QueryAsync(file, SearchFilter.Files, CancellationToken.None)); Check(reply.Entries.All(e => e.Target != file), "direct full path cannot bypass selected drive");
        service.SetFileScope(f); reply = Await(service.QueryAsync("盘符范围测试译名", SearchFilter.Files, CancellationToken.None));
        Check(reply.Entries.Any(e => e.Target == file) && reply.Status.Contains("F 盘"), "F drive selection restores alias result and reports selected file range");
        Check(JsonDocument.Parse(File.ReadAllText(settings)).RootElement.GetProperty("futureField").GetInt32() == 81, "drive preference preserves unknown settings fields");
        using var restored = new SearchService(settings); Check(restored.FileScope.Key == "F:\\" && restored.BilingualEnabled, "selected drive survives restart alongside bilingual preference");
        var apps = Await(service.QueryAsync("Counter-Strike 2", SearchFilter.Applications, CancellationToken.None));
        Check(apps.Entries.Any(e => e.Name == "Counter-Strike 2"), "file range does not hide actual C drive Steam shortcut from application search");
        var live = Await(WindowsSearchProvider.OnSta(() => WindowsSearchProvider.Files(SearchPlan.Literal("README"), CancellationToken.None, c), CancellationToken.None));
        Check(live.Count > 0 && live.All(e => c.Contains(e.Target)), "real Windows SCOPE query returns existing C drive indexed paths only");
        service.SetFileScope(SearchScope.All); reply = Await(service.QueryAsync("scope_unique_928467", SearchFilter.Files, CancellationToken.None));
        Check(reply.Entries.Any(e => e.Target == file), "returning to all computer removes drive constraint");
    }
    private static void SteamShortcuts()
    {
        var root = Path.Combine(_output, "steam-shortcuts"); Directory.CreateDirectory(root);
        var file = Path.Combine(root, "Counter-Strike 2.url");
        File.WriteAllText(file, "[InternetShortcut]\nURL=steam://rungameid/730\nIconIndex=0\n");
        var entry = WindowsSearchProvider.SteamShortcut(file)!;
        Check(entry is { Name: "Counter-Strike 2", Kind: SearchKind.Application, Source: "Steam 快捷方式" } && entry.Target == file, "Steam game URL shortcut becomes an application entry with its exact local path");
        Check(SearchActions.StartInfo(entry).FileName == file && SearchActions.StartInfo(entry).UseShellExecute, "Steam launch uses selected local shortcut rather than evaluating the search query");
        Check(SearchActions.StartInfo(entry, true).ArgumentList.Single() == "/select," + file, "Steam shortcut can be located in Explorer");
        foreach (var url in new[] { "https://example.com", "file:///C:/Windows/notepad.exe", "steam://rungameid/730 -bad", "steam://rungameid/730?x=1", "steam://rungameid/0", "steam://rungameid/4294967296" })
        {
            File.WriteAllText(file, "[InternetShortcut]\nURL=" + url);
            Check(WindowsSearchProvider.SteamShortcut(file) is null, "non-game or malformed URL excluded from app index: " + url);
        }
        File.WriteAllText(file, "[InternetShortcut]\nURL=steam://rungameid/730\nURL=https://example.com");
        Check(WindowsSearchProvider.SteamShortcut(file) is null, "ambiguous duplicate Steam URL is not indexed");
        File.WriteAllText(file, "[Other]\nURL=steam://rungameid/730");
        Check(WindowsSearchProvider.SteamShortcut(file) is null, "URL outside InternetShortcut section is not indexed");
        File.WriteAllText(file, "[InternetShortcut]\nURL=steam://rungameid/730\n" + new string('x', 16384));
        Check(WindowsSearchProvider.SteamShortcut(file) is null, "oversized URL file excluded from bounded metadata read");
        File.WriteAllText(file, "[InternetShortcut]\nURL=https://example.com");
        try { SearchActions.StartInfo(entry); Check(false, "changed Steam shortcut"); }
        catch (IOException) { Check(true, "opening revalidates a Steam shortcut changed after indexing"); }
        File.Delete(file); // Do not leave fake game shortcuts in real whole-disk search results.
    }
    private static void ClosingRegression()
    {
        using var service = new SearchService(Path.Combine(_output, "closing-settings.json"));
        void Deactivate(Window window) => typeof(Window).GetMethod("OnDeactivated", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, [EventArgs.Empty]);
        var legacy = new SearchWindow(service, (_, _) => { }) { ShowActivated = false }; var reproduced = false;
        legacy.Show(); Pump(20);
        EventHandler oldHandler = (_, _) => legacy.Close();
        legacy.Deactivated += oldHandler;
        legacy.Closing += (_, _) =>
        {
            try { Deactivate(legacy); }
            catch (TargetInvocationException e) when (e.InnerException is InvalidOperationException) { reproduced = true; }
            finally { legacy.Deactivated -= oldHandler; }
        };
        legacy.Close(); Check(reproduced, "legacy synchronous Close on deactivation reproduces user's WPF closing exception");
        for (var i = 0; i < 8; i++)
        {
            var window = new SearchWindow(service, (_, _) => { }) { ShowActivated = false };
            window.Show(); Pump(20); var raised = false;
            window.Closing += (_, _) => { raised = true; Deactivate(window); window.Dismiss(); };
            window.Close(); Pump(20);
            Check(raised && !window.IsVisible && window.IsClosing, "close + synchronous deactivation and repeated dismiss cannot reenter WPF " + i);
        }
        var deferred = new SearchWindow(service, (_, _) => { }) { ShowActivated = false };
        deferred.Show(); Pump(20); Deactivate(deferred); Deactivate(deferred);
        Check(deferred.IsVisible, "deactivation queues dismissal instead of closing inside activation message");
        deferred.Close(); Pump(30); Check(!deferred.IsVisible, "queued deactivation cannot close an already closing or closed window");
        var reactivated = new SearchWindow(service, (_, _) => { }) { ShowActivated = false };
        reactivated.Show(); Pump(20); SendActivation(new WindowInteropHelper(reactivated).Handle, 0x6, 1, 0); SendActivation(new WindowInteropHelper(reactivated).Handle, 0x6, 0, 0); Pump(40);
        Check(!reactivated.IsVisible, "external focus loss still dismisses an inactive search window");
    }
    [DllImport("user32.dll", EntryPoint = "SendMessageW")] private static extern nint SendActivation(nint window, uint message, nint wParam, nint lParam);
    private static void Capture(Window window, string name)
    {
        window.UpdateLayout(); var dpi = VisualTreeHelper.GetDpi(window);
        var image = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(window.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        image.Render(window); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = File.Create(Path.Combine(_output, name)); encoder.Save(stream);
    }
    private sealed class IpcServer : IDisposable
    {
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate nint WindowProc(nint window, uint message, nint wParam, nint lParam);
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct WindowClass
        { public uint Size, Style; public WindowProc Proc; public int ClassExtra, WindowExtra; public nint Instance, Icon, Cursor, Background; public string? Menu, ClassName; public nint SmallIcon; }
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern ushort RegisterClassEx(ref WindowClass cls);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint CreateWindowEx(uint exStyle, string cls, string caption, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
        [DllImport("user32.dll")] private static extern nint DefWindowProc(nint window, uint message, nint wParam, nint lParam);
        [DllImport("user32.dll")] private static extern nint SendMessage(nint window, uint message, nint wParam, ref EverythingProvider.CopyData data);
        [DllImport("user32.dll")] private static extern bool DestroyWindow(nint window);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool UnregisterClass(string cls, nint instance);
        private readonly WindowProc _proc; private readonly nint _window;
        internal bool QueryReceived, Silent;
        internal string LastPattern = "";
        internal IpcServer()
        {
            _proc = Handle; var cls = new WindowClass { Size = (uint)Marshal.SizeOf<WindowClass>(), Proc = _proc, ClassName = "EVERYTHING_TASKBAR_NOTIFICATION" };
            if (RegisterClassEx(ref cls) == 0) throw new Exception("isolated IPC class registration failed");
            _window = CreateWindowEx(0, cls.ClassName!, "isolated test-only server", 0, 0, 0, 0, 0, 0, 0, 0, 0);
            if (_window == 0) throw new Exception("isolated IPC server creation failed");
        }
        private nint Handle(nint window, uint message, nint wParam, nint lParam)
        {
            if (message == 0x4A)
            {
                var data = Marshal.PtrToStructure<EverythingProvider.CopyData>(lParam);
                if (data.Id != 2) return 0;
                var bytes = new byte[data.Size]; Marshal.Copy(data.Data, bytes, 0, bytes.Length);
                LastPattern = Encoding.Unicode.GetString(bytes, 20, bytes.Length - 22);
                QueryReceived = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(16)) == 800 && BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8)) == 4;
                if (!Silent)
                {
                    var reply = Reply("中文 文件.pdf", @"F:\含 空格"); var buffer = Marshal.AllocHGlobal(reply.Length);
                    try { Marshal.Copy(reply, 0, buffer, reply.Length); var result = new EverythingProvider.CopyData { Id = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4)), Size = reply.Length, Data = buffer }; SendMessage(wParam, 0x4A, window, ref result); }
                    finally { Marshal.FreeHGlobal(buffer); }
                }
                return 1;
            }
            return DefWindowProc(window, message, wParam, lParam);
        }
        public void Dispose() { DestroyWindow(_window); UnregisterClass("EVERYTHING_TASKBAR_NOTIFICATION", 0); GC.KeepAlive(_proc); }
    }
}
