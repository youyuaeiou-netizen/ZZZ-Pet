using System.IO;

namespace DesktopPet.Search;

internal sealed record SearchScope
{
    internal string? Root { get; }
    internal string Key => Root ?? "all";
    internal string Label { get; }
    internal static SearchScope All { get; } = new(null, "全部电脑");
    private SearchScope(string? root, string label) { Root = root; Label = label; }
    internal static SearchScope Parse(string? key)
    {
        if (key is null || key.Equals("all", StringComparison.OrdinalIgnoreCase)) return All;
        if (key.Length != 3 || !char.IsAsciiLetter(key[0]) || key[1] != ':' || key[2] != '\\')
            throw new ArgumentException("无效的搜索盘符。");
        var letter = char.ToUpperInvariant(key[0]);
        return new(letter + ":\\", letter + " 盘");
    }
    internal bool Contains(string target) => Root is null || target.StartsWith(Root, StringComparison.OrdinalIgnoreCase);
    internal static IReadOnlyList<SearchScope> Available(SearchScope selected)
    {
        var options = new List<SearchScope> { All };
        foreach (var drive in DriveInfo.GetDrives().OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (drive.DriveType is DriveType.Fixed or DriveType.Removable or DriveType.Network or DriveType.CDRom && drive.IsReady)
                    options.Add(Parse(drive.Name));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        if (!options.Any(s => s.Key == selected.Key)) options.Add(new(selected.Root, selected.Label + "（未就绪）"));
        return options;
    }
    public override string ToString() => Label;
}
