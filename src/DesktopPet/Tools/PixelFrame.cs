using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;

namespace DesktopPet.Tools;

internal sealed class PixelFrame : Decorator
{
    public bool TailLeft { get; set; } = true;
    public bool ShowTail { get; set; }
    public double CornerRadius { get; set; } = 16;
    public Brush Fill { get; set; } = new SolidColorBrush(Color.FromRgb(255, 249, 242));
    public static void Apply(FrameworkElement element) => element.Resources.MergedDictionaries.Add(new ResourceDictionary
        { Source = new Uri("/DesktopPet;component/Tools/PixelTheme.xaml", UriKind.Relative) });
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        var w = ActualWidth; var h = ActualHeight;
        if (w < 24 || h < 24) return;
        var shape = new StreamGeometry();
        using (var g = shape.Open())
        {
            g.BeginFigure(new Point(10, 2), true, true);
            g.PolyLineTo(new Point[] { new(w-10,2), new(w-10,6), new(w-6,6), new(w-6,10), new(w-2,10),
                new(w-2,h-10), new(w-6,h-10), new(w-6,h-6), new(w-10,h-6), new(w-10,h-2),
                new(10,h-2), new(10,h-6), new(6,h-6), new(6,h-10), new(2,h-10), new(2,10), new(6,10), new(6,6), new(10,6) }, true, false);
        }
        var ink = new SolidColorBrush(Color.FromRgb(205,180,217));
        if (CornerRadius > 0) dc.DrawRoundedRectangle(Fill, new Pen(ink, 2), new Rect(2, 2, w - 4, h - 4), CornerRadius, CornerRadius);
        else dc.DrawGeometry(Fill, new Pen(ink, 2), shape);
        if (ShowTail)
        {
            var x = TailLeft ? 2 : w-2; var end = TailLeft ? -7 : w+7;
            var tail = new StreamGeometry();
            using (var g = tail.Open()) { g.BeginFigure(new(x,40),true,true); g.LineTo(new(end,48),true,false); g.LineTo(new(x,56),true,false); }
            dc.DrawGeometry(Fill,new Pen(ink,2),tail);
        }
    }
}
