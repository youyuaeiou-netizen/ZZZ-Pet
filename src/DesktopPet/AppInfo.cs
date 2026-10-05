using System.Reflection;

namespace DesktopPet;

public static class AppInfo
{
    public static string Version => typeof(AppInfo).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "unknown";
    public static string Label => $"DesktopPet {Version}";
    public static Version CompatibilityVersion
    {
        get
        {
            var version = typeof(AppInfo).Assembly.GetName().Version!;
            return new Version(version.Major, version.Minor, Math.Max(0, version.Build));
        }
    }
}
