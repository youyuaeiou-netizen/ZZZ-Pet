using System.Text.Json;
using System.Text.RegularExpressions;

namespace DesktopPet.Monitoring;

public sealed record PetUpdateManifest(string Component, string Platform, string Version, string PackageUrl,
    string Sha256, string Notes)
{
    public static bool ValidUrl(string? value) => value is { Length: <= 2048 } && Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme == "https" && string.IsNullOrEmpty(uri.UserInfo);
    public static PetUpdateManifest Parse(string json)
    {
        if (json.Length > 128 * 1024) throw new InvalidDataException("更新清单过大。");
        var manifest = JsonSerializer.Deserialize<PetUpdateManifest>(json, MonitorJson.Options);
        if (manifest is null || manifest.Component != "DesktopPet" || manifest.Platform != "win-x64" ||
            !VersionPattern().IsMatch(manifest.Version ?? "") || !ValidUrl(manifest.PackageUrl) ||
            manifest.Sha256 is not { Length: 64 } || !manifest.Sha256.All(Uri.IsHexDigit) || manifest.Notes is not { Length: <= 4000 })
            throw new InvalidDataException("仅支持完整 Pet Windows x64 包的兼容更新清单。");
        return manifest;
    }
    public static bool IsNewer(string candidate, string current)
    {
        var next = VersionPattern().Match(candidate); var previous = VersionPattern().Match(current);
        if (!next.Success || !previous.Success) return false;
        for (var i = 1; i <= 3; i++)
        {
            if (!int.TryParse(next.Groups[i].Value, out var a) || !int.TryParse(previous.Groups[i].Value, out var b)) return false;
            if (a != b) return a > b;
        }
        var n = next.Groups[4].Value; var p = previous.Groups[4].Value;
        if (n.Length == 0) return p.Length > 0;
        if (p.Length == 0) return false;
        return int.TryParse(n, out var build) && int.TryParse(p, out var previousBuild) && build > previousBuild;
    }
    private static Regex VersionPattern() => new(@"^(\d{1,5})\.(\d{1,5})\.(\d{1,5})(?:-beta\.(\d{1,5}))?$", RegexOptions.CultureInvariant);
}
