using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace DesktopPet.Search;

internal static class WindowsSearchProvider
{
    private static readonly object AppIndexLock = new();
    internal static Task<T> OnSta<T>(Func<T> work, CancellationToken token)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { token.ThrowIfCancellationRequested(); completion.TrySetResult(work()); }
            catch (OperationCanceledException) { completion.TrySetCanceled(token); }
            catch (Exception error) { completion.TrySetException(error); }
        }) { IsBackground = true, Name = "DesktopPet search" };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        return completion.Task;
    }

    internal static void Release(object? value)
    { if (value is not null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value); }

    internal static IReadOnlyList<SearchEntry> Applications(CancellationToken token)
    {
        // Shell automation can return shared RCWs across STA callers; serialize owned enumerations.
        lock (AppIndexLock) { token.ThrowIfCancellationRequested(); return ApplicationsCore(token); }
    }
    private static IReadOnlyList<SearchEntry> ApplicationsCore(CancellationToken token)
    {
        var entries = new List<SearchEntry>();
        object? shell = null, folder = null, items = null;
        try
        {
            shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application", true)!);
            folder = ((dynamic)shell!).Namespace("shell:AppsFolder"); items = ((dynamic)folder!).Items();
            for (var i = 0; i < (int)((dynamic)items).Count; i++)
            {
                token.ThrowIfCancellationRequested(); object? item = null;
                try
                {
                    item = ((dynamic)items).Item(i);
                    string name = ((dynamic)item).Name; string id = ((dynamic)item).Path;
                    if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(id))
                        entries.Add(new(name, "shell:AppsFolder\\" + id, SearchKind.Application, "Windows 应用", id));
                }
                finally { Release(item); }
            }
        }
        catch (COMException) { /* Start-menu shortcuts remain available if AppsFolder is unavailable. */ }
        finally { Release(items); Release(folder); Release(shell); }
        foreach (var root in new[] { Environment.SpecialFolder.StartMenu, Environment.SpecialFolder.CommonStartMenu,
                     Environment.SpecialFolder.DesktopDirectory, Environment.SpecialFolder.CommonDesktopDirectory })
        {
            var path = Environment.GetFolderPath(root);
            if (!Directory.Exists(path)) continue;
            foreach (var file in Directory.EnumerateFiles(path, "*", new EnumerationOptions
                     { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }))
            {
                token.ThrowIfCancellationRequested();
                if (Path.GetExtension(file).ToLowerInvariant() is ".lnk" or ".appref-ms" or ".exe")
                    entries.Add(new(Path.GetFileNameWithoutExtension(file), file, SearchKind.Application, "开始菜单／桌面"));
                else if (Path.GetExtension(file).Equals(".url", StringComparison.OrdinalIgnoreCase) && SteamShortcut(file) is { } game)
                    entries.Add(game);
            }
        }
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using var registry = RegistryKey.OpenBaseKey(hive, view);
            using var paths = registry.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths");
            if (paths is null) continue;
            foreach (var key in paths.GetSubKeyNames())
            {
                token.ThrowIfCancellationRequested(); using var app = paths.OpenSubKey(key);
                var target = (app?.GetValue(null) as string)?.Trim('"');
                if (target is null || !File.Exists(target)) continue;
                entries.Add(new(Path.GetFileNameWithoutExtension(key), target, SearchKind.Application, "应用注册", key));
            }
        }
        // AppsFolder is authoritative; suppress duplicate shortcuts with the same displayed name.
        return entries.DistinctBy(e => SearchMatch.Normalize(e.Name)).ToArray();
    }

    internal static SearchEntry? SteamShortcut(string file)
    {
        try
        {
            // Only bounded Steam game shortcut metadata, not arbitrary web links, enters the app index.
            if (!Path.GetExtension(file).Equals(".url", StringComparison.OrdinalIgnoreCase) || new FileInfo(file).Length > 16384) return null;
            var inShortcut = false; string? url = null;
            foreach (var line in File.ReadLines(file))
            {
                var text = line.Trim();
                if (text.StartsWith('[')) { inShortcut = text.Equals("[InternetShortcut]", StringComparison.OrdinalIgnoreCase); continue; }
                if (!inShortcut || !text.StartsWith("URL=", StringComparison.OrdinalIgnoreCase)) continue;
                if (url is not null) return null;
                url = text[4..].Trim();
            }
            const string prefix = "steam://rungameid/";
            if (url is null || !url.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
            var idText = url[prefix.Length..];
            if (!idText.All(char.IsAsciiDigit) || !uint.TryParse(idText, out var id) || id == 0) return null;
            return new(Path.GetFileNameWithoutExtension(file), file, SearchKind.Application, "Steam 快捷方式", "steam " + id);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    internal static string EscapeLike(string text) => text.Replace("'", "''").Replace("[", "[[]").Replace("%", "[%]").Replace("_", "[_]");
    private static string ScopeSql(SearchScope? scope) => scope?.Root is { } root ? " AND SCOPE='file:" + root.Replace('\\', '/') + "'" : "";
    internal static string Sql(string query, bool exact = false, SearchScope? scope = null)
    {
        var pathQuery = query.Contains('\\') || query.Contains('/');
        var predicate = exact
            ? "System.FileName = '" + query.Trim().Replace("'", "''") + "'"
            : string.Join(" AND ", SearchMatch.Tokens(query).Select(t => "(System.FileName LIKE '%" + EscapeLike(t) +
                "%'" + (pathQuery ? " OR System.ItemPathDisplay LIKE '%" + EscapeLike(t) + "%'" : "") + ")"));
        return "SELECT TOP 400 System.ItemUrl FROM SystemIndex WHERE System.ItemUrl LIKE 'file:%' AND " + predicate + ScopeSql(scope);
    }
    internal static string Sql(SearchPlan plan, SearchScope? scope = null)
    {
        if (!plan.Expanded) return Sql(plan.Original, scope: scope);
        var predicate = string.Join(" AND ", plan.Terms.Select(term => "(" + string.Join(" OR ",
            term.Alternatives.Select(word => "System.FileName LIKE '%" + EscapeLike(word) + "%'")) + ")"));
        return "SELECT TOP 400 System.ItemUrl FROM SystemIndex WHERE System.ItemUrl LIKE 'file:%' AND " + predicate + ScopeSql(scope);
    }

    internal static IReadOnlyList<SearchEntry> Files(string query, CancellationToken token) => Files(SearchPlan.Literal(query), token);
    internal static IReadOnlyList<SearchEntry> Files(SearchPlan plan, CancellationToken token, SearchScope? scope = null)
    {
        object? connection = null, rows = null; var entries = new List<SearchEntry>();
        try
        {
            connection = Activator.CreateInstance(Type.GetTypeFromProgID("ADODB.Connection", true)!);
            dynamic ado = connection!; ado.ConnectionTimeout = 2; ado.CommandTimeout = 2;
            ado.Open("Provider=Search.CollatorDSO;Extended Properties='Application=Windows';");
            // Exact matches are fetched separately so a common prefix cannot hide them at the provider cap.
            foreach (var sql in new[] { Sql(plan.Original, true, scope), Sql(plan, scope) })
            {
                token.ThrowIfCancellationRequested(); rows = ado.Execute(sql);
                while (!(bool)((dynamic)rows).EOF)
                {
                    token.ThrowIfCancellationRequested(); object? fields = null, field = null;
                    try
                    {
                        fields = ((dynamic)rows).Fields; field = ((dynamic)fields).Item(0);
                        string url = Convert.ToString(((dynamic)field).Value) ?? "";
                        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.IsFile)
                        {
                            var path = uri.LocalPath;
                            entries.Add(new(Path.GetFileName(path.TrimEnd('\\')), path, SearchKind.File, "Windows 索引"));
                        }
                    }
                    finally { Release(field); Release(fields); }
                    ((dynamic)rows).MoveNext();
                }
                ((dynamic)rows).Close(); Release(rows); rows = null;
            }
        }
        finally
        {
            Release(rows);
            if (connection is not null) { try { ((dynamic)connection).Close(); } catch (COMException) { } Release(connection); }
        }
        return entries;
    }
}
