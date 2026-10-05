using System.Windows;
using System.Windows.Media.Animation;
using DesktopPet.Monitoring;
using W = System.Windows.Controls;
using M = System.Windows.Media;

namespace DesktopPet;

internal sealed class MonitorMetricRow : W.Grid
{
    private static readonly DependencyProperty DisplayedValueProperty = DependencyProperty.Register("DisplayedValue", typeof(double), typeof(MonitorMetricRow),
        new PropertyMetadata(double.NaN, (o, _) => ((MonitorMetricRow)o).UpdateValue()));
    private readonly W.TextBlock _value;
    private readonly W.ProgressBar _bar;
    private readonly MonitorMetric _metric;
    private readonly MonitorConfig _config;
    internal MonitorMetricRow(MonitorMetric metric, string label, MonitorConfig config, double? previous, bool compact = false)
    {
        _metric = metric; _config = config;
        Margin = new Thickness(0, config.RowSpacing / 2, 0, config.RowSpacing / 2);
        MinHeight = config.Visual.RowHeight; ToolTip = metric.Source;
        RowDefinitions.Add(new() { Height = GridLength.Auto }); RowDefinitions.Add(new() { Height = GridLength.Auto });
        ColumnDefinitions.Add(new() { Width = compact ? GridLength.Auto : new GridLength(1, GridUnitType.Star) }); ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var name = new W.TextBlock { Text = label, FontSize = config.FontSize, FontFamily = new M.FontFamily(config.FontFamily),
            FontWeight = config.Visual.Bold ? FontWeights.Bold : FontWeights.Normal, TextWrapping = TextWrapping.Wrap,
            Foreground = Brush(config.Foreground), Margin = new Thickness(0, 0, compact ? 4 : 10, 0) };
        _value = new W.TextBlock { FontSize = config.Visual.ValueSize, FontFamily = new M.FontFamily(config.Visual.ValueFamily),
            FontWeight = config.Visual.Bold ? FontWeights.Bold : FontWeights.Normal, Foreground = Brush(MonitorTheme.Color(metric, config)),
            Text = MonitorLocalizer.Language.Text(config.Language, metric.Display), VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = compact ? TextWrapping.NoWrap : TextWrapping.Wrap,
            MaxWidth = compact ? double.PositiveInfinity : Math.Max(80, config.PanelWidth * .5 - config.Visual.Padding - 20) };
        SetColumn(_value, 1); Children.Add(name); Children.Add(_value);
        _bar = new W.ProgressBar { Minimum = 0, Maximum = 100, Height = 4, Margin = new Thickness(0, 3, 0, 0),
            Background = Brush(config.Visual.BarBackground), BorderThickness = new Thickness(0),
            Foreground = Brush(MonitorTheme.Level(metric, config) switch { 2 => config.Visual.BarHigh, 1 => config.Visual.BarMid, _ => config.Visual.BarLow }),
            Visibility = config.Visual.ShowBars && MonitorTheme.BarPercent(metric, config) is not null ? Visibility.Visible : Visibility.Collapsed };
        SetRow(_bar, 1); SetColumnSpan(_bar, 2); Children.Add(_bar);
        if (metric.Valid)
        {
            SetValue(DisplayedValueProperty, metric.Value!.Value);
            if (config.Visual.SmoothValues && previous is double from && metric.Text is null && from != metric.Value)
                BeginAnimation(DisplayedValueProperty, new DoubleAnimation(from, metric.Value!.Value, TimeSpan.FromMilliseconds(config.Visual.SmoothMs)) { FillBehavior = FillBehavior.Stop });
        }
    }
    private void UpdateValue()
    {
        var number = (double)GetValue(DisplayedValueProperty); if (!double.IsFinite(number)) return;
        var displayed = _metric with { Value = number };
        _value.Text = MonitorLocalizer.Language.Text(_config.Language, displayed.Display);
        _bar.Value = MonitorTheme.BarPercent(displayed, _config) ?? 0;
    }
    private static M.SolidColorBrush Brush(string color) => new((M.Color)M.ColorConverter.ConvertFromString(color));
}
