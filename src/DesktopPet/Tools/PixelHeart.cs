using System.Windows;
using System.Windows.Media;
using Color = System.Windows.Media.Color;
namespace DesktopPet.Tools;
internal sealed class PixelHeart : FrameworkElement
{
    public PixelHeart() { Width=32; Height=28; Margin=new Thickness(0,4,10,4); }
    protected override void OnRender(DrawingContext dc)
    {
        var cells=new[] { ".##.##.", "#######", "#######", ".#####.", "..###..", "...#..." };
        var ink=new SolidColorBrush(Color.FromRgb(85,65,107));
        var pink=new SolidColorBrush(Color.FromRgb(255,169,211));
        for(var y=0;y<cells.Length;y++) for(var x=0;x<7;x++) if(cells[y][x]=='#')
            dc.DrawRectangle(y==0 || x==0 || x==6 || y>=4 ? ink : pink,null,new Rect(x*4,y*4,4,4));
        dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(255,251,216)),null,new Rect(8,4,4,4));
    }
}
