using System.Windows;
using System.Windows.Media;
using W = System.Windows.Controls;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using FontFamily = System.Windows.Media.FontFamily;
namespace DesktopPet.Tools;
internal sealed class CompanionWindow : Window
{
    private readonly ToolsService _service;
    private readonly Action _review;
    private readonly W.StackPanel _panel = new() { Margin = new Thickness(14,10,14,10) };
    public CompanionWindow(ToolsService service, Action review)
    {
        _service=service; _review=review;
        Width=232; SizeToContent=SizeToContent.Height; Title="艾莲布 · 计时与待处理";
        WindowStyle=WindowStyle.None; ResizeMode=ResizeMode.NoResize; ShowActivated=false;
        ShowInTaskbar=false; Topmost=true; AllowsTransparency=true; Background=Brushes.Transparent;
        FontFamily=new FontFamily("Microsoft YaHei UI"); PixelFrame.Apply(this);
        Content=new PixelFrame { Child=_panel, Fill=new SolidColorBrush(Color.FromRgb(224,248,239)) };
        NoticeWindow.NonActivating(this); Refresh();
    }
    public void Refresh()
    {
        _panel.Children.Clear();
        var count=_service.PendingCount+(_service.State.Focus.AwaitingNext?1:0);
        if(count>0) _panel.Children.Add(ToolsWindow.Button($"♥ 待处理 {count} · 回看",_review));
        if(!_service.State.ShowTimerCard) return;
        var f=_service.State.Focus; var seconds=(int)Math.Ceiling(f.RemainingSeconds);
        _panel.Children.Add(ToolsWindow.Text($"{(f.Stage==FocusStage.Focus?"专注":"休息")}  {seconds/60:00}:{seconds%60:00}",18));
        var bar=new W.WrapPanel(); var total=(f.Stage==FocusStage.Focus?f.FocusMinutes:f.BreakMinutes)*60;
        for(var i=0;i<16;i++) bar.Children.Add(new W.Border { Width=10,Height=7,Margin=new Thickness(1),
            CornerRadius = new CornerRadius(3), Background=new SolidColorBrush(i < (1-f.RemainingSeconds/total)*16 ? Color.FromRgb(157,128,183) : Color.FromRgb(255,213,232)) });
        _panel.Children.Add(bar);
        var buttons=new W.WrapPanel();
        buttons.Children.Add(ToolsWindow.Button(f.Running?"暂停":f.AwaitingNext?"下一阶段":"继续",()=>
        { if(f.Running) _service.PauseFocus(); else _service.StartFocus(); _service.Confirm("计时状态已更新。"); }));
        buttons.Children.Add(ToolsWindow.Button("收起",()=>_service.SetPresentation(_service.State.AutoHideBubble,_service.State.BubbleSeconds,_service.State.WakeOnImportant,false)));
        _panel.Children.Add(buttons);
    }
}
