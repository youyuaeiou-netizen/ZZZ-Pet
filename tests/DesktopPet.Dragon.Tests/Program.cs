using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DesktopPet;
using DesktopPet.Behavior;
using DesktopPet.Character;
using Color = System.Windows.Media.Color;
using Brushes = System.Windows.Media.Brushes;
using FlowDirection = System.Windows.FlowDirection;

internal static class Program
{
    private static int _passed;
    private static void Check(bool value, string label)
    { if (!value) throw new Exception(label); _passed++; Console.WriteLine("PASS " + label); }
    [STAThread] private static int Main(string[] args)
    {
        try
        {
            var root=Path.GetFullPath(args[0]); var output=Path.GetFullPath(args[1]); Directory.CreateDirectory(output);
            if (args.Contains("--playback")) { Playback(root); return 0; }
            if (args.Contains("--render")) { Render(root,Path.Combine(output,"render.png"),700,1200); return 0; }
            if (args.Contains("--compare"))
            {
                foreach (var form in new[] { "human", "chibi" }) Compare(root,output,form);
                return 0;
            }
            if (args.Contains("--facecompare"))
            {
                foreach (var form in new[] { "human", "chibi" }) Compare(root,output,form,true);
                return 0;
            }
            if (args.Contains("--preview"))
            {
                foreach (var name in new[] { "human", "chibi", "human-heart" })
                    Render(Path.Combine(root,name+".svg"),Path.Combine(output,name+".png"),700,1200);
                return 0;
            }
            Rules();
            var character = CharacterLoader.Load(Path.Combine(root,"gpt-dragon")).Character;
            Check(character.Forms?.Count==2,"one character with two forms");
            if (args.Contains("--chibi-motion")) ChibiMotion(character.Forms!["chibi"], output);
            foreach (var (id,form) in character.Forms!)
            {
                foreach (var action in form.Actions)
                foreach (var file in action.Frames.Select(f=>f.FullPath).Distinct())
                {
                    var image=new BitmapImage(new Uri(file));
                    var videoFrame = file.Contains(Path.DirectorySeparatorChar + "animations" + Path.DirectorySeparatorChar);
                    var viewport=action.GetViewport(form);
                    Check(videoFrame ? image.PixelWidth==viewport.Width*2 && image.PixelHeight==viewport.Height*2
                        : image.PixelWidth>600 && image.PixelHeight>1000,"frame resolution "+id+"/"+Path.GetFileName(file));
                }
                foreach (var scale in new[] { .5,1,2.5 })
                    Render(form.ActionMap["idle"].Frames[0].FullPath,Path.Combine(output,$"{id}-{scale}.png"),
                        (int)(form.CanvasWidth*scale),(int)(form.CanvasHeight*scale));
                Render(form.ActionMap["sleep"].Frames[0].FullPath,Path.Combine(output,id+"-sleep.png"),440,760);
                Render(form.ActionMap["heart"].Frames[0].FullPath,Path.Combine(output,id+"-heart.png"),440,760);
            }
            var runner=new ActionRunner(character.Forms["human"]);
            runner.PlayAction("sleep"); runner.Advance(TimeSpan.FromMilliseconds(470)); runner.Flip();
            var time=runner.NowMs; runner.SwitchForm(character.Forms["chibi"]);
            Check(runner.Current.Id=="sleep" && runner.Facing==-1 && runner.NowMs==time,"form change preserves sleep, facing and clock");
            runner.PlayAction("feed"); Check(runner.Current.Id=="wake","feed wakes sleeping dragon");
            runner.SwitchForm(character.Forms["human"]);
            runner.Advance(TimeSpan.FromMilliseconds(character.Forms["human"].ActionMap["wake"].Frames.Sum(f=>f.DurationMs)));
            Check(runner.Current.Id=="feed","queued feed survives form change");
            runner.Advance(TimeSpan.FromMilliseconds(character.Forms["human"].ActionMap["feed"].Frames.Sum(f=>f.DurationMs)));
            Check(runner.Current.Id=="idle","feeding returns to idle");
            runner.Trigger("drag_start");
            var rejected=false; try { runner.SwitchForm(character.Forms["chibi"]); } catch(InvalidOperationException) { rejected=true; }
            Check(rejected,"form transition deferred during drag");
            runner.Trigger("drag_release"); runner.SwitchForm(character.Forms["chibi"]);
            Check(!runner.IsDragging && runner.Current.Id=="release","released dragon changes form while preserving landing");
            runner.Advance(TimeSpan.FromMilliseconds(character.Forms["chibi"].ActionMap["release"].Frames.Sum(f=>f.DurationMs)));
            Check(runner.Current.Id=="idle","landing returns to idle after form change");
            File.WriteAllText(Path.Combine(output,"result.json"),JsonSerializer.Serialize(new {passed=_passed}));
            Console.WriteLine($"Dragon checks passed: {_passed}"); return 0;
        }
        catch(Exception e) { Console.Error.WriteLine(e); return 1; }
    }
    private static void ChibiMotion(CharacterDefinition form, string output)
    {
        string[] ids=["idle","drag","release","heart","feed","sleep_enter","sleep","wake"];
        Check(form.ActionMap.Keys.ToHashSet().SetEquals(ids), "Q form preserves the shared eight action IDs");
        foreach(var id in ids)
        {
            var action=form.ActionMap[id];
            Check(action.Frames.Count>=24 && action.Frames.All(f=>f.Path.StartsWith("animations/chibi/")), "Q action has authored motion frames: "+id);
        }
        bool Same(string a, bool lastA, string b, bool lastB)
        {
            var pa=(lastA?form.ActionMap[a].Frames[^1]:form.ActionMap[a].Frames[0]).FullPath;
            var pb=(lastB?form.ActionMap[b].Frames[^1]:form.ActionMap[b].Frames[0]).FullPath;
            var ia=new FormatConvertedBitmap(new BitmapImage(new Uri(pa)),PixelFormats.Bgra32,null,0);
            var ib=new FormatConvertedBitmap(new BitmapImage(new Uri(pb)),PixelFormats.Bgra32,null,0);
            var width=Math.Min(ia.PixelWidth,ib.PixelWidth);var height=Math.Min(ia.PixelHeight,ib.PixelHeight);
            var xa=new byte[width*height*4];var xb=new byte[xa.Length];
            ia.CopyPixels(new Int32Rect((ia.PixelWidth-width)/2,0,width,height),xa,width*4,0);
            ib.CopyPixels(new Int32Rect((ib.PixelWidth-width)/2,0,width,height),xb,width*4,0);
            return xa.Zip(xb,(x,y)=>Math.Abs(x-y)).Average()<1;
        }
        foreach(var id in new[]{"idle","drag","sleep"}) Check(Same(id,false,id,true), "Q loop closes without a seam: "+id);
        foreach(var id in new[]{"release","heart","feed","wake"}) Check(Same(id,true,"idle",false), "Q action returns to the same idle artwork: "+id);
        Check(Same("sleep_enter",true,"sleep",false) && Same("wake",false,"sleep",false), "Q sleep and wake share the closed-eye endpoint");
        var runner=new ActionRunner(form) { AutomaticActionsEnabled=false };
        runner.Advance(TimeSpan.FromMilliseconds(form.SleepThresholdMs));
        Check(runner.Current.Id=="sleep_enter", "Q inactivity enters the sleep transition");
        runner.Advance(TimeSpan.FromMilliseconds(form.ActionMap["sleep_enter"].Frames.Sum(f=>f.DurationMs))); Check(runner.Current.Id=="sleep", "Q sleep transition enters the breathing loop");
        runner.Trigger("click"); Check(runner.Current.Id=="wake", "Q click wakes before responding");
        runner.Advance(TimeSpan.FromMilliseconds(form.ActionMap["wake"].Frames.Sum(f=>f.DurationMs))); Check(runner.Current.Id=="heart", "Q waking plays the queued response");
        runner.Advance(TimeSpan.FromMilliseconds(2000)); Check(runner.Current.Id=="idle", "Q response returns to idle");
        runner.Trigger("drag_start");var first=runner.CurrentFramePath;runner.Advance(TimeSpan.FromMilliseconds(500));
        Check(runner.Current.Id=="drag" && runner.CurrentFramePath!=first, "Q drag animates while the pointer holds the pet");
        runner.Trigger("drag_release");Check(runner.Current.Id=="release", "Q drag release plays landing");
        runner.Advance(TimeSpan.FromMilliseconds(form.ActionMap["release"].Frames.Sum(f=>f.DurationMs)));Check(runner.Current.Id=="idle", "Q landing returns to idle");
        runner.PlayAction("feed");runner.Advance(TimeSpan.FromMilliseconds(2500));Check(runner.Current.Id=="idle", "Q feeding returns to idle");

        // Render the actual WPF SpritePlayer, including its image decoding and transforms.
        var players=new List<(ActionRunner runner, System.Windows.Controls.Image image, SpritePlayer player)>();
        foreach(var id in ids)
        {
            var r=new ActionRunner(form) { AutomaticActionsEnabled=false };
            if(id=="drag") r.Trigger("drag_start"); else r.PlayAction(id);
            var image=new System.Windows.Controls.Image { Width=152, Height=232, Stretch=Stretch.Fill, RenderTransformOrigin=new(.5,.5) };
            image.Measure(new System.Windows.Size(152,232));image.Arrange(new Rect(0,0,152,232));
            players.Add((r,image,new SpritePlayer(image)));
        }
        var directory=Path.Combine(output,"wpf-playback");Directory.CreateDirectory(directory);
        for(var frame=0;frame<96;frame++)
        {
            var board=new DrawingVisual();using(var dc=board.RenderOpen())
            {
                dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(38,38,51)),null,new Rect(0,0,1040,640));
                for(var i=0;i<players.Count;i++)
                {
                    var (r,image,player)=players[i];if(frame>0)r.Advance(TimeSpan.FromMilliseconds(1000d/24));
                    var view=r.Current.GetViewport(form);image.Width=view.Width;image.Height=view.Height;
                    image.Measure(new System.Windows.Size(view.Width,view.Height));image.Arrange(new Rect(0,0,view.Width,view.Height));
                    player.Show(r);image.UpdateLayout();
                    var sprite=new RenderTargetBitmap(view.Width,view.Height,96,96,PixelFormats.Pbgra32);sprite.Render(image);
                    int x=i%4*260,y=i/4*320;
                    var title=new FormattedText(form.ActionMap[ids[i]].DisplayName!,System.Globalization.CultureInfo.GetCultureInfo("zh-CN"),FlowDirection.LeftToRight,new Typeface("Microsoft YaHei"),13,Brushes.White,1);
                    dc.DrawText(title,new System.Windows.Point(x+12,y+10));dc.DrawImage(sprite,new Rect(x+34+form.AnchorX-view.AnchorX,y+40+form.AnchorY-view.AnchorY,view.Width,view.Height));
                }
            }
            var bitmap=new RenderTargetBitmap(1040,640,96,96,PixelFormats.Pbgra32);bitmap.Render(board);
            var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream=File.Create(Path.Combine(directory,$"{frame:0000}.png"));encoder.Save(stream);
        }
        Check(players.All(p=>p.player.CachedPixelBytes<=64L*1024*1024), "Q playback stays within the existing sprite cache budget");
    }
    private static void Playback(string root)
    {
        var character=CharacterLoader.Load(Path.Combine(root,"gpt-dragon")).Character.Forms!["human"];
        var runner=new ActionRunner(character) { AutomaticActionsEnabled=false };
        var image=new System.Windows.Controls.Image { Stretch=Stretch.Fill, Width=450,Height=500 };
        var player=new SpritePlayer(image);
        var window=new Window { Title="GPT 龙娘动画检查",Content=image,Width=470,Height=545,
            Background=new SolidColorBrush(Color.FromRgb(30,48,40)),Topmost=true,Left=100,Top=70 };
        var clock=System.Diagnostics.Stopwatch.StartNew();var previous=clock.Elapsed;
        string[] sequence=["idle","idle","heart","drag","drag","release","idle","sleep_enter","sleep","wake","feed","idle"];
        var index=0;double due=0;
        var timer=new System.Windows.Threading.DispatcherTimer { Interval=TimeSpan.FromMilliseconds(1000d/24) };
        timer.Tick+=(_,_)=>
        {
            var now=clock.Elapsed;
            if(now.TotalMilliseconds>=due)
            {
                var id=sequence[index++ % sequence.Length];
                if(id=="drag")runner.Trigger("drag_start");
                else if(id=="release")runner.Trigger("drag_release");
                else runner.PlayAction(id);
                due=now.TotalMilliseconds+character.ActionMap[id].Frames.Sum(f=>f.DurationMs);
                window.Title="GPT 龙娘动画检查 · "+id;
            }
            runner.Advance(now-previous);previous=now;player.Show(runner);
        };
        window.Closed+=(_,_)=>timer.Stop();window.Loaded+=(_,_)=>timer.Start();
        new System.Windows.Application().Run(window);
    }
    private static void Rules()
    {
        var t=DateTimeOffset.Parse("2026-10-12T00:00:00Z");
        var state=new DragonState(); var rules=new DragonFormRules(state); rules.Initialize(t);
        Check(state.Form=="human" && state.LastFedAt==t,"first activation initializes feeding once");
        rules.Evaluate(t.AddHours(8).AddTicks(-1)); Check(state.Form=="human","before eight hours remains human");
        rules.Evaluate(t.AddHours(8)); Check(state.Form=="chibi","eight hours makes chibi");
        rules.Feed(t.AddHours(8)); Check(state.Form=="human","feeding enlarges and resets hunger");
        t=t.AddHours(8); rules.Observe(10,10,t,t); rules.Evaluate(t.AddMinutes(3)); Check(state.Form=="human","ten percent is not low");
        rules.Observe(9,70,t.AddMinutes(3),t.AddMinutes(3)); Check(state.Form=="chibi","primary below ten shrinks");
        rules.Enlarge(t.AddMinutes(3)); rules.Observe(8,6,t.AddMinutes(4),t.AddMinutes(4));
        Check(state.Form=="human","quota consumption does not interrupt three minute protection");
        rules.Observe(8,6,t.AddMinutes(6),t.AddMinutes(6)); Check(state.Form=="chibi","low quota shrinks at protection expiry");
        rules.Observe(100,6,t.AddMinutes(7),t.AddMinutes(7)); Check(state.Form=="human","one reset enlarges despite low week");
        var deadline=state.ProtectedUntil; rules.Observe(100,6,t.AddMinutes(8),t.AddMinutes(8));
        Check(state.ProtectedUntil==deadline,"repeated hundred percent does not extend protection");
        rules.MakeSmall(); rules.Observe(100,100,t.AddMinutes(9),t.AddMinutes(9)); Check(state.Form=="human","week reset independently enlarges");
        rules.MakeSmall(); rules.Observe(99,99,t.AddMinutes(10),t.AddMinutes(10)); Check(state.Form=="chibi","partial recovery does not enlarge");
        rules.Enlarge(t.AddMinutes(10)); rules.Observe(1,1,t.AddMinutes(10),t.AddMinutes(10));
        rules.QuotaUnavailable(); rules.Evaluate(t.AddMinutes(13)); Check(state.Form=="human","failed quota does not cause a shrink");
        rules.Observe(1,1,t.AddMinutes(14),t.AddMinutes(14)); rules.Enlarge(t.AddMinutes(14));
        rules.Evaluate(t.AddMinutes(17)); Check(state.Form=="human","stale quota does not cause a shrink");
        var restored=JsonSerializer.Deserialize<DragonState>(JsonSerializer.Serialize(state))!;
        var reboot=new DragonFormRules(restored); reboot.Initialize(t.AddMinutes(17));
        Check(restored.LastFedAt==state.LastFedAt && restored.ProtectedUntil is null,"restart does not renew protection or feeding");
        reboot.Evaluate(t.AddHours(8)); Check(restored.Form=="chibi","offline hunger is counted");
        reboot.Enlarge(t.AddHours(8)); reboot.Feed(t.AddHours(8).AddMinutes(2));
        Check(restored.ProtectedUntil==t.AddHours(8).AddMinutes(5),"another feed renews protection");
        reboot.MakeSmall(); Check(restored.Form=="chibi" && restored.ProtectedUntil is null,"manual chibi ends protection");
    }
    internal static void Render(string path,string output,int width,int height)
    {
        ImageSource image=path.EndsWith(".svg")?VectorFrameReader.Read(path):new BitmapImage(new Uri(path));
        var visual=new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(visual,BitmapScalingMode.HighQuality);
        using(var dc=visual.RenderOpen())
        {
            var ratio=Math.Min(width/image.Width,height/image.Height);
            dc.DrawImage(image,new Rect((width-image.Width*ratio)/2,(height-image.Height*ratio)/2,image.Width*ratio,image.Height*ratio));
        }
        var bitmap=new RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32); bitmap.Render(visual);
        var encoder=new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream=File.Create(output); encoder.Save(stream);
    }
    private static void Compare(string root,string output,string form,bool faceOnly=false)
    {
        var sources = new ImageSource[] {
            new BitmapImage(new Uri(Path.Combine(root,"dragon-art",form+".png"))),
            VectorFrameReader.Read(Path.Combine(root,"dragon-vtracer",form+".svg")),
            VectorFrameReader.Read(faceOnly ? Path.Combine("C:/Users/h/Desktop/素材/gpt/龙娘桌宠/VTracer脸部修订",form+".svg") : Path.Combine(root,"dragon-inkvec-corrected",form+".svg")) };
        var titles=faceOnly ? new[] { "原图 PNG", "VTracer · 原版本", "VTracer · 脸部高精度修订" } : new[] { "原图 PNG", "VTracer · 当前版本", "Inkvec · 消缝处理后" };
        var visual=new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(visual,BitmapScalingMode.HighQuality);
        using(var dc=visual.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(43,40,53)),null,new Rect(0,0,1290,1060));
            for(var i=0;i<3;i++)
            {
                var x=i*430;
                var text=new FormattedText(titles[i],System.Globalization.CultureInfo.GetCultureInfo("zh-CN"),FlowDirection.LeftToRight,
                    new Typeface("Microsoft YaHei"),21,Brushes.White,1);
                dc.DrawText(text,new System.Windows.Point(x+22,18));
                var image=sources[i]; var fit=Math.Min(390/image.Width,605/image.Height);
                if(faceOnly) fit=form=="human"?510.0/1148:255.0/1015;
                dc.DrawImage(image,new Rect(x+20+(390-image.Width*fit)/2,65,image.Width*fit,image.Height*fit));
                var label=new FormattedText(faceOnly?"上图为预计尺寸像素；下图为细节放大":"面部细节放大",System.Globalization.CultureInfo.GetCultureInfo("zh-CN"),FlowDirection.LeftToRight,
                    new Typeface("Microsoft YaHei"),16,Brushes.LightGray,1);
                dc.DrawText(label,new System.Windows.Point(x+22,685));
                dc.PushClip(new RectangleGeometry(new Rect(x+20,725,390,315)));
                // Both SVG outputs are normalized to the same padded viewBox below.
                var ox=i==0?0:20; var oy=i==0?0:20;
                var sx=form=="human"?265:220; var sy=form=="human"?105:310;
                var zoom=form=="human"?2.3:1.45;
                dc.DrawImage(image,new Rect(x+20-(sx+ox)*zoom,725-(sy+oy)*zoom,image.Width*zoom,image.Height*zoom));
                dc.Pop();
            }
        }
        var bitmap=new RenderTargetBitmap(1290,1060,96,96,PixelFormats.Pbgra32); bitmap.Render(visual);
        var encoder=new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream=File.Create(Path.Combine(output,form+(faceOnly?"-face-comparison.png":"-comparison.png"))); encoder.Save(stream);
    }
}
