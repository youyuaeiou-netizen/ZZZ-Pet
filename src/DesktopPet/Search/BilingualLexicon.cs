using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace DesktopPet.Search;

internal sealed record SearchTerm(string Original, IReadOnlyList<string> Alternatives);
internal sealed record SearchPlan(string Original, IReadOnlyList<SearchTerm> Terms)
{
    internal bool Expanded => Terms.Any(t => t.Alternatives.Count > 1);
    internal static SearchPlan Literal(string query) => new(query, SearchMatch.Tokens(query).Select(t => new SearchTerm(t, new[] { t })).ToArray());
    internal bool Matches(SearchEntry entry) => Terms.Count > 0 && Terms.All(t => t.Alternatives.Any(a => entry.NameSearch.Contains(a, StringComparison.Ordinal)));
}

internal sealed class BilingualLexicon
{
    private readonly Dictionary<string, string[]> _terms;
    internal int TermCount => _terms.Count;
    private static readonly Lazy<Task<BilingualLexicon>> Shared = new(() => Task.Run(Load));
    internal static Task<BilingualLexicon> GetAsync(CancellationToken token) => Shared.Value.WaitAsync(token);
    private BilingualLexicon(Dictionary<string, string[]> terms) => _terms = terms;

    private static BilingualLexicon Load()
    {
        using var raw = typeof(BilingualLexicon).Assembly.GetManifestResourceStream("DesktopPet.CC-CEDICT.gz")
            ?? throw new IOException("中英词库缺失。");
        using var gzip = new GZipStream(raw, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip, Encoding.UTF8);
        var terms = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        void Add(string key, string value)
        {
            key = SearchMatch.Normalize(key.Trim()); value = SearchMatch.Normalize(value.Trim());
            if (key.Length == 0 || value.Length == 0 || key == value) return;
            if (!terms.TryGetValue(key, out var values)) terms[key] = values = [];
            if (values.Count < 6 && !values.Contains(value, StringComparer.Ordinal)) values.Add(value);
        }
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.StartsWith('#')) continue;
            var first = line.IndexOf(' '); var second = line.IndexOf(' ', first + 1); var slash = line.IndexOf(" /", StringComparison.Ordinal);
            if (first < 1 || second <= first || slash < second) continue;
            var traditional = line[..first]; var simplified = line[(first + 1)..second];
            foreach (var gloss in line[(slash + 2)..].Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                var word = Regex.Replace(gloss, @"\([^)]*\)", "").Trim();
                if (word.StartsWith("to ", StringComparison.Ordinal)) word = word[3..];
                if (!Regex.IsMatch(word, @"^[A-Za-z][A-Za-z '+-]{0,43}$") || word.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length > 4) continue;
                Add(simplified, word); Add(traditional, word); Add(word, simplified); Add(word, traditional);
            }
        }
        // Product names and common file terms supplement dictionary definitions, not automatic language detection.
        foreach (var pair in new[] { ("微信", "wechat"), ("黑曜石", "obsidian"), ("记事本", "notepad"), ("计算器", "calculator"),
                     ("画图", "paint"), ("资源管理器", "explorer"), ("设置", "settings"), ("任务管理器", "task manager"),
                     ("焊接", "welding"), ("报告", "report"), ("试验", "experiment"), ("实验", "experiment"),
                     ("论文", "paper"), ("参考文献", "references"), ("热处理", "heat treatment"), ("显微组织", "microstructure"),
                     ("反恐精英", "counter-strike"), ("反恐精英2", "counter-strike 2"), ("cs2", "counter-strike 2") })
        {
            // Prefer familiar search terms without discarding the full set of retained dictionary alternatives.
            void Prefer(string key, string value)
            { if (!terms.TryGetValue(key, out var list)) terms[key] = list = []; list.Remove(value); list.Insert(0, value); if (list.Count > 6) list.RemoveAt(6); }
            Prefer(pair.Item1, pair.Item2); Prefer(pair.Item2, pair.Item1);
        }
        // Reuse dictionary key strings for translations as well. The temporary set
        // is released after loading; no strings are interned for the process lifetime.
        var words = new HashSet<string>(terms.Keys, StringComparer.Ordinal);
        foreach (var values in terms.Values)
            for (var i = 0; i < values.Count; i++)
                if (words.TryGetValue(values[i], out var word)) values[i] = word;
        return new(terms.ToDictionary(t => t.Key, t => t.Value.ToArray(), StringComparer.Ordinal));
    }

    internal SearchPlan Plan(string query)
    {
        var text = SearchMatch.Normalize(query.Trim());
        if (text.Length == 0 || text.Contains('\\') || text.Contains('/')) return SearchPlan.Literal(query);
        var groups = new List<SearchTerm>();
        void Group(string word)
        {
            groups.Add(new(word, _terms.TryGetValue(word, out var alternatives) ? new[] { word }.Concat(alternatives).Distinct().ToArray() : new[] { word }));
        }
        // Prefer a whole dictionary phrase before breaking Chinese words or mixed file-name segments apart.
        if (_terms.ContainsKey(text)) { Group(text); return new(query, groups); }
        var parts = Regex.Matches(text, @"[\p{IsCJKUnifiedIdeographs}\p{IsCJKUnifiedIdeographsExtensionA}]+|\.[a-z0-9]+|[a-z0-9'+]+|[^\s_-]").Select(m => m.Value).ToArray();
        for (var i = 0; i < parts.Length; i++)
        {
            var part = parts[i];
            if (IsChinese(part[0]))
            {
                for (var position = 0; position < part.Length;)
                {
                    var length = Math.Min(10, part.Length - position);
                    while (length > 1 && !_terms.ContainsKey(part.Substring(position, length))) length--;
                    // Unknown characters remain literal and are never dropped from the query.
                    Group(part.Substring(position, length)); position += length;
                }
            }
            else
            {
                var count = Math.Min(4, parts.Length - i);
                while (count > 1 && !_terms.ContainsKey(string.Join(' ', parts.Skip(i).Take(count)))) count--;
                Group(string.Join(' ', parts.Skip(i).Take(count))); i += count - 1;
            }
        }
        return new(query, groups);
    }
    private static bool IsChinese(char value) => value is >= '\u3400' and <= '\u9fff';
}
