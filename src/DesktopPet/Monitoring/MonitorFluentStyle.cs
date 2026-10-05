using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using DesktopPet.Tools;
using W = System.Windows.Controls;
using Media = System.Windows.Media;

namespace DesktopPet;

internal static class MonitorFluentStyle
{
    [StructLayout(LayoutKind.Sequential)] private struct Margins { public int Left, Right, Top, Bottom; }
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint window, int attribute, ref int value, int size);
    [DllImport("dwmapi.dll")] private static extern int DwmExtendFrameIntoClientArea(nint window, ref Margins margins);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint window, int attribute, out int value, int size);
    public static void Apply(Window window)
    {
        window.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/DesktopPet;component/Monitoring/MonitorFluentTheme.xaml", UriKind.Relative) });
        window.FontFamily = new Media.FontFamily("Segoe UI Variable, Microsoft YaHei UI"); window.FontSize = 13;
        window.Foreground = Brush("#353344"); window.Background = Brush("#F0EDF5");
        window.UseLayoutRounding = true;
        window.SourceInitialized += (_, _) =>
        {
            if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621)) return;
            var handle = new WindowInteropHelper(window).Handle;
            var acrylic = 3;
            if (DwmSetWindowAttribute(handle, 38, ref acrylic, sizeof(int)) != 0) return;
            var margins = new Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 };
            if (DwmExtendFrameIntoClientArea(handle, ref margins) != 0) return;
            if (HwndSource.FromHwnd(handle)?.CompositionTarget is { } target) target.BackgroundColor = Media.Colors.Transparent;
            window.Background = Media.Brushes.Transparent;
            var corners = 2; DwmSetWindowAttribute(handle, 33, ref corners, sizeof(int));
        };
    }
    internal static bool AcrylicEnabled(Window window) => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621)
        && DwmGetWindowAttribute(new WindowInteropHelper(window).Handle, 38, out var value, sizeof(int)) == 0 && value == 3;
    public static Media.Brush Brush(string color) => new Media.SolidColorBrush((Media.Color)Media.ColorConverter.ConvertFromString(color));
    public static W.Border Card(UIElement child) => new() { Child = child, Background = Brush("#CCFFFFFF"), CornerRadius = new CornerRadius(12),
        BorderBrush = Brush("#DFFFFFFF"), BorderThickness = new Thickness(1), Padding = new Thickness(18), Margin = new Thickness(0, 0, 0, 12) };
    public static W.TextBlock Heading(string text, double size = 22) { var label = ToolsWindow.Text(text, size); label.FontWeight = FontWeights.SemiBold; return label; }
    public static W.TextBlock Hint(string text) { var label = ToolsWindow.Text(text, 12); label.Foreground = Brush("#6B687A"); return label; }
}
