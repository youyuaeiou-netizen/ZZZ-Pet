using System.Globalization;
using System.Text;
using System.IO;

namespace DesktopPet.Search;

internal enum SearchKind { Application, File, Folder }
internal enum SearchFilter { All, Applications, Files }
internal sealed record SearchEntry(string Name, string Target, SearchKind Kind, string Source, string? Alias = null)
{
    internal string? MatchHint { get; init; }
    internal string NameSearch { get; } = SearchMatch.Normalize(Name);
    internal string StemSearch { get; } = SearchMatch.Normalize(Path.GetFileNameWithoutExtension(Name));
    internal string TargetSearch { get; } = SearchMatch.Normalize(Target);
    internal string AliasSearch { get; } = SearchMatch.Normalize(Alias ?? "");
    public string Location => Target.StartsWith("shell:AppsFolder\\", StringComparison.OrdinalIgnoreCase) ? "已安装应用" : Target;
    public string Category => Kind switch { SearchKind.Application => "应用", SearchKind.Folder => "目录", _ => "文件" };
}
internal sealed record SearchReply(IReadOnlyList<SearchEntry> Entries, string Status, double Milliseconds)
{
    internal bool Complete { get; init; } = true;
}

internal static class SearchMatch
{
    internal static string Normalize(string value) => value.Normalize(NormalizationForm.FormKC).ToLowerInvariant();
    internal static string[] Tokens(string query) => Normalize(query.Trim()).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

    internal static int Score(SearchEntry entry, string query)
    {
        return Score(entry, Normalize(query.Trim()), Tokens(query));
    }
    private static int Score(SearchEntry entry, string text, string[] tokens)
    {
        var name = entry.NameSearch;
        if (text.Length == 0) return 0;
        var stem = entry.StemSearch;
        if (name == text || stem == text) return 1000;
        if (name.StartsWith(text, StringComparison.Ordinal)) return 850;
        if (name.Contains(text, StringComparison.Ordinal)) return 700;
        if (tokens.All(t => name.Contains(t, StringComparison.Ordinal))) return 600;
        if (entry.Kind == SearchKind.Application)
        {
            var alias = entry.AliasSearch;
            if (tokens.All(t => alias.Contains(t, StringComparison.Ordinal))) return 500;
            var initials = string.Concat(name.Split([' ', '-', '_', '.'], StringSplitOptions.RemoveEmptyEntries).Select(s => s[0]));
            if (text.Length >= 2 && initials == text) return 450;
        }
        var target = entry.TargetSearch;
        return tokens.All(t => name.Contains(t, StringComparison.Ordinal) || target.Contains(t, StringComparison.Ordinal)) ? 300 : 0;
    }

    internal static IReadOnlyList<SearchEntry> Rank(IEnumerable<SearchEntry> entries, string query, SearchFilter filter, int limit = 80,
        SearchPlan? plan = null, IReadOnlyDictionary<string, string[]>? aliases = null)
    {
        var text = Normalize(query.Trim()); var tokens = Tokens(query);
        (SearchEntry Entry, int Score) Match(SearchEntry entry)
        {
            var score = Score(entry, text, tokens);
            if (score > 0) return (entry, score);
            if (aliases?.TryGetValue(entry.Target, out var names) == true && names.Any(n => tokens.All(t => n.Contains(t, StringComparison.Ordinal))))
                return (entry with { MatchHint = "别名匹配" }, 250);
            if (plan?.Expanded == true && plan.Matches(entry)) return (entry with { MatchHint = "中英匹配" }, 200);
            return (entry, 0);
        }
        return entries.Where(e => filter != SearchFilter.Applications || e.Kind == SearchKind.Application)
            .Where(e => filter != SearchFilter.Files || e.Kind != SearchKind.Application)
            .Select(Match).Where(e => e.Score > 0)
            .OrderByDescending(e => e.Score).ThenBy(e => e.Entry.Kind == SearchKind.Application ? 0 : 1)
            .ThenBy(e => e.Entry.Name.Length).ThenBy(e => e.Entry.Target, StringComparer.OrdinalIgnoreCase)
            .DistinctBy(e => e.Entry.Target, StringComparer.OrdinalIgnoreCase).Take(limit).Select(e => e.Entry).ToArray();
    }
}
