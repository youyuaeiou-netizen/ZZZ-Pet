using System.Globalization;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using DesktopPet.Tools;
using W = System.Windows.Controls;

namespace DesktopPet;

internal static class MonitorSettings
{
    public static void Watch(FrameworkElement control, MonitorService service, Action refresh)
    {
        refresh();
        control.Loaded += (_, _) => { service.Changed -= refresh; service.Changed += refresh; refresh(); };
        control.Unloaded += (_, _) => service.Changed -= refresh;
    }
    public static W.CheckBox Toggle(MonitorService service, string label, Func<bool> get, Action<bool> set, Func<bool>? available = null)
    {
        var box = new W.CheckBox { Content = label, Margin = new Thickness(0, 5, 0, 5) };
        Watch(box, service, () => { box.IsChecked = get(); box.IsEnabled = service.Store.Available && (available?.Invoke() ?? true); });
        box.Click += (_, _) =>
        {
            set(box.IsChecked == true);
            if (!service.Save()) box.ToolTip = service.Status;
            box.IsChecked = get();
        };
        return box;
    }
    public static W.StackPanel Text(MonitorService service, string label, Func<string> get, Action<string> set,
        Func<string, bool> valid, string error, int maxLength = 100)
    {
        var panel = new W.StackPanel { Margin = new Thickness(0, 3, 0, 3) };
        var box = new W.TextBox { Text = get(), MaxLength = maxLength, Name = "SettingInput" };
        var status = ToolsWindow.Text(""); status.Foreground = System.Windows.Media.Brushes.Firebrick; status.Visibility = Visibility.Collapsed;
        panel.Children.Add(ToolsWindow.Text(label)); panel.Children.Add(box); panel.Children.Add(status);
        Watch(box, service, () => { if (!box.IsKeyboardFocusWithin) box.Text = get(); box.IsEnabled = service.Store.Available; });
        void Commit()
        {
            if (box.Text == get()) return;
            if (!valid(box.Text)) { status.Text = error; status.Visibility = Visibility.Visible; return; }
            set(box.Text);
            var saved = service.Save();
            status.Text = saved ? "" : service.Status; status.Visibility = saved ? Visibility.Collapsed : Visibility.Visible;
            box.Text = get();
        }
        box.LostKeyboardFocus += (_, _) => Commit();
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Commit(); e.Handled = true; } };
        return panel;
    }
    public static W.StackPanel Number(MonitorService service, string label, Func<double> get, Action<double> set,
        double minimum, double maximum, Func<double, bool>? extra = null) => Text(service, label,
            () => get().ToString(CultureInfo.InvariantCulture), s => set(double.Parse(s, CultureInfo.InvariantCulture)),
            s => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && double.IsFinite(n)
                && n >= minimum && n <= maximum && (extra?.Invoke(n) ?? true),
            $"请输入 {minimum}–{maximum} 范围内的有效数值，并检查关联设置。", 12);
    private sealed class ChoiceLabel<T>(T value) : INotifyPropertyChanged
    {
        public T Value { get; } = value;
        private string _name = "";
        public string Name { get => _name; set { if (_name == value) return; _name = value; PropertyChanged?.Invoke(this, new(nameof(Name))); } }
        public event PropertyChangedEventHandler? PropertyChanged;
    }
    public static W.ComboBox Choice<T>(MonitorService service, IEnumerable<T> choices, Func<T> get, Action<T> set, Func<T, string>? label = null)
    {
        var values = choices.ToArray(); var box = new W.ComboBox(); var updating = false; string? language = null;
        if (label is null) box.ItemsSource = values;
        var labeled = values.Select(value => new ChoiceLabel<T>(value)).ToArray();
        if (label is not null) { box.ItemsSource = labeled; box.DisplayMemberPath = "Name"; box.SelectedValuePath = "Value"; MonitorLocalizer.Preserve(box); }
        Watch(box, service, () =>
        {
            updating = true;
            if (label is not null && language != service.Config.Language)
            { language = service.Config.Language; foreach (var item in labeled) item.Name = label(item.Value); }
            if (label is null) box.SelectedItem = get(); else box.SelectedValue = get();
            box.IsEnabled = service.Store.Available; updating = false;
        });
        box.SelectionChanged += (_, _) => { if (updating || box.SelectedValue is not T value) return; set(value); service.Save(); };
        return box;
    }
    public static W.ScrollViewer Scroll(object content) => new() { Content = content,
        VerticalScrollBarVisibility = W.ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = W.ScrollBarVisibility.Disabled };
}
