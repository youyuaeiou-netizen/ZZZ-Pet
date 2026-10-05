using System.Runtime.CompilerServices;
using System.Windows;
using DesktopPet.Monitoring;
using System.Windows.Threading;
using W = System.Windows.Controls;

namespace DesktopPet;

internal static class MonitorLocalizer
{
    internal static readonly MonitorLanguage Language = new(System.IO.Path.Combine(AppContext.BaseDirectory, "monitor-languages"));
    private sealed class Remembered { public string Original = ""; public string Applied = ""; public Func<object[]>? Values; }
    private static readonly ConditionalWeakTable<DependencyObject, Remembered> Texts = new();
    private static readonly DependencyProperty PreserveProperty = DependencyProperty.RegisterAttached("Preserve", typeof(bool), typeof(MonitorLocalizer), new PropertyMetadata(false));
    // Template authors and user-entered names are data, even when they match a UI label.
    public static void Preserve(DependencyObject item) => item.SetValue(PreserveProperty, true);
    public static void Formatted(W.TextBlock item, string template, params object[] values) => Formatted(item, template, () => values);
    public static void Formatted(W.TextBlock item, string template, Func<object[]> values)
    {
        var remembered = Texts.GetOrCreateValue(item);
        remembered.Original = template; remembered.Values = values;
        item.Text = remembered.Applied = Language.Format("zh", template, values());
    }
    public static void Attach(Window window, MonitorService service)
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        var title = window.Title;
        void Apply() { window.Title = Language.Text(service.Config.Language, title); Translate(window, service.Config.Language); }
        timer.Tick += (_, _) => Apply(); window.Closed += (_, _) => timer.Stop(); window.Loaded += (_, _) => { Apply(); timer.Start(); };
    }
    public static void Translate(DependencyObject item, string code)
    {
        if ((bool)item.GetValue(PreserveProperty)) return;
        string? text = item switch { W.TextBlock t => t.Text, W.HeaderedContentControl h => h.Header as string,
            W.ContentControl c => c.Content as string, _ => null };
        if (text is not null)
        {
            var remembered = Texts.GetOrCreateValue(item);
            if (text != remembered.Applied) { remembered.Original = text; remembered.Values = null; }
            var translated = remembered.Values is null ? Language.Text(code, remembered.Original)
                : Language.Format(code, remembered.Original, remembered.Values()); remembered.Applied = translated;
            switch (item) { case W.TextBlock t: t.Text = translated; break; case W.HeaderedContentControl h: h.Header = translated; break; case W.ContentControl c: c.Content = translated; break; }
        }
        // Never translate editable user input or passwords.
        if (item is W.TextBox or W.PasswordBox) return;
        foreach (var child in LogicalTreeHelper.GetChildren(item).OfType<DependencyObject>()) Translate(child, code);
    }
}
