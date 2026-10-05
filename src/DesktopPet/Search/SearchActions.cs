using System.Diagnostics;
using System.IO;

namespace DesktopPet.Search;

internal static class SearchActions
{
    internal static ProcessStartInfo StartInfo(SearchEntry entry, bool reveal = false)
    {
        if (entry.Target.StartsWith("shell:AppsFolder\\", StringComparison.OrdinalIgnoreCase) && entry.Kind == SearchKind.Application)
        {
            if (reveal) throw new IOException("此应用没有可定位的文件目录。");
            var shell = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe")) { UseShellExecute = true };
            shell.ArgumentList.Add(entry.Target); return shell;
        }
        if (!Path.IsPathFullyQualified(entry.Target) || (!File.Exists(entry.Target) && !Directory.Exists(entry.Target)))
            throw new FileNotFoundException("文件已移动或删除，请刷新搜索。");
        if (!reveal && entry.Source == "Steam 快捷方式" && WindowsSearchProvider.SteamShortcut(entry.Target) is null)
            throw new IOException("Steam 快捷方式已更改或不可用，请刷新搜索。");
        if (!reveal) return new ProcessStartInfo(entry.Target) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(entry.Target) };
        var explorer = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe")) { UseShellExecute = true };
        explorer.ArgumentList.Add("/select," + entry.Target); return explorer;
    }
    internal static void Open(SearchEntry entry, bool reveal = false) => Process.Start(StartInfo(entry, reveal));
}
