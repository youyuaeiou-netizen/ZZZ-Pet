using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DesktopPet;
using DesktopPet.Character;
using DesktopPet.Tools;
using DesktopPet.Behavior;
using W = System.Windows.Controls;

internal static class Program
{
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out Rect rect);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern int GetWindowLong(nint window, int index);
    [DllImport("user32.dll")] private static extern bool PrintWindow(nint window, nint dc, uint flags);
    private static readonly List<string> Passed = [];
    private static string _output = "";
    private static DpiScale _dpi;

    [STAThread]
    private static int Main()
    {
        _output = Path.Combine(AppContext.BaseDirectory, "ui-evidence", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_output);
        var app = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        try
        {
            Run();
            File.WriteAllText(Path.Combine(_output, "verification.json"), JsonSerializer.Serialize(new
                { passed = Passed, dpi = _dpi, note = "Real current-DPI WPF; foreground unchanged; power transitions simulated, no system DPI changes." },
                new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"PASS: {Passed.Count} WPF integration checks; evidence: {_output}");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally
        {
            foreach (Window window in app.Windows.Cast<Window>().ToArray()) window.Close();
            app.Shutdown();
        }
    }

    private static void Run()
    {
        var paths = new RuntimePaths(new StartupOptions(null, Path.Combine(_output, "data"), null));
        var store = new SettingsStore(paths.SettingsPath);
        var settings = store.Load();
        var catalog = CharacterCatalog.Load(paths.CharacterRoot, paths.UserCharacterRoot);
        CheckSpriteSharing(catalog.Characters["ellen-flat2d"]);
        CheckSpriteEviction(catalog.Characters["ellen-flat2d"]);
        var pet = new PetWindow(catalog.Characters, paths.CharacterRoot, "ellen-flat2d", settings, store, paths);
        System.Windows.Application.Current.MainWindow = pet;
        pet.Show(); Pump(150);
        _dpi = VisualTreeHelper.GetDpi(pet);
        CheckInteractionMenu(pet, store);
        if (catalog.Characters["ellen-flat2d"].ActionMap.ContainsKey("sleep_stand")) CheckFourPoseAnimation(pet);
        var controller = Field<ToolsController>(pet, "_tools");
        var service = Field<ToolsService>(controller, "_service");
        var position = (pet.Left, pet.Top, pet.Width, pet.Height);
        ((W.MenuItem)pet.ContextMenu.Items[1]).RaiseEvent(new RoutedEventArgs(W.MenuItem.ClickEvent));
        Pump(100);
        var panel = Field<ToolsWindow>(controller, "_panel");
        Check(panel.IsVisible && Field<W.TabControl>(panel, "_tabs").SelectedIndex == 1, "pet right menu opens reminder tab");
        Check(position == (pet.Left, pet.Top, pet.Width, pet.Height), "tools do not change pet position or viewport");
        CheckModernControls(panel);
        foreach (var screen in System.Windows.Forms.Screen.AllScreens)
        {
            var work = screen.WorkingArea;
            SetWindowPos(new WindowInteropHelper(pet).Handle, 0, work.Right - 180, work.Bottom - 240, 0, 0, 0x0015);
            Pump(50); controller.ShowPanel(1); Pump(50);
            GetWindowRect(new WindowInteropHelper(panel).Handle, out var bounds);
            Check(bounds.Left >= work.Left && bounds.Top >= work.Top && bounds.Right <= work.Right && bounds.Bottom <= work.Bottom,
                $"tools clamped inside available monitor {screen.DeviceName}");
        }

        Field<W.TextBox>(panel, "_title").Text = "写完报告，出去走走";
        var future = DateTime.Now.AddDays(1).Date.AddHours(14).AddMinutes(30);
        Field<W.DatePicker>(panel, "_date").SelectedDate = future.Date;
        Field<W.TextBox>(panel, "_time").Text = "14:30";
        Field<W.ComboBox>(panel, "_repeat").SelectedIndex = 1;
        Click(panel, "保存提醒");
        Check(service.State.Reminders.Count == 1 && service.State.Reminders[0].Repeat == RepeatRule.Daily, "reminder form saves local repeating schedule");
        Click(panel, "编辑");
        Field<W.TextBox>(panel, "_title").Text = "准备组会材料";
        Click(panel, "保存提醒");
        Check(service.State.Reminders.Single().Title == "准备组会材料", "reminder form edits existing entry");
        service.Tick(future, TimeSpan.Zero);
        Pump(100);
        Click(panel, "10 分钟后提醒");
        Check(service.State.Reminders.Single().SnoozeUntil is not null, "pending row snoozes reminder");
        Click(panel, "完成");
        Check(service.PendingCount == 0 && !service.State.Reminders.Single().Completed, "pending row completes only current repeat");
        Capture(panel, "reminders.png");
        panel.SelectTab(0); Pump(60); Capture(panel, "overview.png");
        var quiet = Field<W.CheckBox>(panel, "_quiet");
        quiet.IsChecked = true; quiet.RaiseEvent(new RoutedEventArgs(W.Primitives.ButtonBase.ClickEvent));
        Check(service.State.Quiet, "quiet checkbox connected");
        Pump(1200);
        Check(GetField(controller, "_popup") is null, "quiet mode suppresses popup");
        quiet.IsChecked = false; quiet.RaiseEvent(new RoutedEventArgs(W.Primitives.ButtonBase.ClickEvent));
        Pump(1200);
        var summary = (Window)GetField(controller, "_popup")!;
        Check(summary is not null, "quiet off displays single summary");
        Click(summary!, "收起");

        panel.SelectTab(2); Pump(60);
        Field<W.TextBox>(panel, "_focusMinutes").Text = "1";
        Field<W.TextBox>(panel, "_breakMinutes").Text = "1";
        Click(panel, "应用时长"); Click(panel, "开始／继续");
        Pump(100);
        Check(service.State.Focus.Running && service.State.Focus.FocusMinutes == 1, "focus controls apply and start");
        Click(panel, "暂停");
        Check(!service.State.Focus.Running, "focus controls pause");
        Capture(panel, "focus.png");
        Click(panel, "开始／继续");
        Invoke(controller, "Suspend");
        var remaining = service.State.Focus.RemainingSeconds;
        Pump(1200);
        Check(service.State.Focus.RemainingSeconds == remaining && !service.State.Focus.Running, "suspend saves and freezes focus");
        Invoke(controller, "Resume");
        Check(service.State.Focus.Running, "resume continues previously running focus");
        service.PauseFocus(); Invoke(controller, "Suspend"); Invoke(controller, "Resume");
        Check(!service.State.Focus.Running, "resume does not start manually paused focus");

        // A real independent typing window makes focus theft measurable without touching user apps.
        var typing = new Window { Title = "DesktopPet test typing target", Width = 280, Height = 110,
            Content = new W.TextBox { Text = "输入焦点保持测试", Margin = new Thickness(12) } };
        typing.Show(); typing.Activate(); ((W.TextBox)typing.Content).Focus(); Pump(100);
        var foreground = GetForegroundWindow();
        Console.WriteLine($"Foreground target established: {foreground == new WindowInteropHelper(typing).Handle}");
        service.Upsert(null, "到点了，记得喝水", DateTime.Now.AddSeconds(-1), RepeatRule.Once);
        Pump(1500);
        var popup = (Window)GetField(controller, "_popup")!;
        Check(popup is not null && popup.IsVisible && !popup.ShowActivated, "runtime delivers real nonactivating popup");
        Check((GetWindowLong(new WindowInteropHelper(popup!).Handle, -20) & 0x08000000) != 0, "popup has native NOACTIVATE style");
        Console.WriteLine($"Foreground unchanged: {GetForegroundWindow() == foreground}; popup foreground: {GetForegroundWindow() == new WindowInteropHelper(popup!).Handle}");
        Check(GetForegroundWindow() == foreground,
            "automatic popup preserves foreground window");
        Capture(popup!, "notice.png");
        Click(popup!, "10 分钟后提醒");
        Check(service.State.Reminders.Single(r => r.Title.StartsWith("到点了")).SnoozeUntil is not null, "popup action snoozes");
        typing.Close();

        panel.SelectTab(1); Pump(80); Click(panel, "删除");
        Check(service.State.Reminders.Count == 1, "reminder row deletes exact entry");
        var tray = Field<TrayService>(pet, "_tray");
        Invoke(tray, "RebuildMenu");
        // Tray callbacks are native WinForms items, so invoke their existing PerformClick API.
        var menu = GetField(tray, "_menu")!;
        var items = (System.Collections.IEnumerable)menu.GetType().GetProperty("Items")!.GetValue(menu)!;
        var toolItem = items.Cast<object>().First(i => i.GetType().GetProperty("Text")?.GetValue(i)?.ToString()?.StartsWith("轻工具") == true);
        toolItem.GetType().GetMethod("PerformClick")!.Invoke(toolItem, null);
        Check(Field<W.TabControl>(panel, "_tabs").SelectedIndex == 0, "tray opens same tools window");
        CheckNewPresentation(pet,controller,service);
        service.StartFocus();
        pet.Close(); Pump(50);
        var saved = new ToolsStore(paths.ToolsStatePath).Load();
        Check(!saved.Focus.Running && !panel.IsVisible, "exit saves paused focus and closes owned tools");
    }

    private static void CheckModernControls(ToolsWindow panel)
    {
        panel.SelectTab(1); Pump(60);
        var date = Field<W.DatePicker>(panel, "_date");
        var original = date.SelectedDate;
        date.IsDropDownOpen = true; Pump(80);
        var popup = (W.Primitives.Popup)date.Template.FindName("PART_Popup", date);
        var calendar = Walk(popup.Child).OfType<W.Calendar>().Single();
        Check(popup.IsOpen && calendar.Template.FindName("CalendarFrame", calendar) is W.Border { CornerRadius.TopLeft: 14 }, "date chooser uses complete rounded calendar template");
        var item = Walk(calendar).OfType<W.Primitives.CalendarItem>().Single();
        var month = (W.Grid)item.Template.FindName("PART_MonthView", item);
        CaptureElement(calendar, "date-calendar-initial.png");
        Check(month.Children.Cast<FrameworkElement>().Count(c => W.Grid.GetRow(c) == 0 && Walk(c).OfType<W.TextBlock>().Any(t => !string.IsNullOrEmpty(t.Text))) == 7, "calendar renders seven weekday headings");
        var next = (W.Button)item.Template.FindName("PART_NextButton", item);
        var display = calendar.DisplayDate;
        next.RaiseEvent(new RoutedEventArgs(W.Primitives.ButtonBase.ClickEvent)); Pump(40);
        Check(calendar.DisplayDate.Month == display.AddMonths(1).Month, "styled calendar next-month navigation works");
        ((W.Button)item.Template.FindName("PART_HeaderButton", item)).RaiseEvent(new RoutedEventArgs(W.Primitives.ButtonBase.ClickEvent)); Pump(40);
        Check(calendar.DisplayMode == W.CalendarMode.Year && Walk(calendar).OfType<W.Primitives.CalendarButton>().Count(b => b.IsVisible) == 12, "styled calendar supports choosing all twelve months");
        calendar.DisplayMode = W.CalendarMode.Month; calendar.DisplayDate = original!.Value; Pump(50);
        var day = Walk(calendar).OfType<W.Primitives.CalendarDayButton>().First(b => !b.IsInactive && !b.IsBlackedOut && b.DataContext is DateTime d && d.Day == 15);
        CaptureElement(calendar, "date-calendar.png");
        var selectedDay = (DateTime)day.DataContext;
        calendar.SelectedDate = selectedDay; Pump(40);
        Check(date.SelectedDate == selectedDay, "calendar selection updates reminder date");
        date.IsDropDownOpen = false; date.SelectedDate = original;
        var repeat = Field<W.ComboBox>(panel, "_repeat"); repeat.IsDropDownOpen = true; Pump(40);
        var choices = (W.Primitives.Popup)repeat.Template.FindName("PART_Popup", repeat);
        Check(choices.IsOpen && Walk(choices.Child).OfType<W.ComboBoxItem>().Count() == 3, "repeat dropdown renders all three choices");
        CaptureElement(choices.Child, "repeat-dropdown.png");
        repeat.SelectedIndex = 2; repeat.IsDropDownOpen = false;
        Check(repeat.Text == "每周", "styled repeat dropdown retains selected label"); repeat.SelectedIndex = 0;
        var time = Field<W.TextBox>(panel, "_time"); time.Text = "14:30";
        Check(time.ActualWidth > 100 && ((W.ScrollViewer)time.Template.FindName("PART_ContentHost", time)).ScrollableWidth == 0, "full HH:mm fits without clipping");
        for (var tab = 0; tab < 3; tab++)
        {
            panel.Width = panel.MinWidth; panel.Height = panel.MinHeight; panel.SelectTab(tab); Pump(40);
            var scroll = (W.ScrollViewer)((W.TabItem)Field<W.TabControl>(panel, "_tabs").Items[tab]).Content;
            Check(scroll.ViewportWidth > 200 && scroll.ViewportHeight > 80 && scroll.ScrollableWidth == 0, "minimum tools window fits page " + tab);
            scroll.ScrollToBottom(); Pump(40); Capture(panel, "minimum-tab-" + tab + ".png", true);
            Check(scroll.VerticalOffset > 0, "minimum page scroll reaches lower controls " + tab);
            scroll.ScrollToTop();
        }
        panel.Width = 520; panel.Height = 720; panel.SelectTab(0); Pump(40);
        var overview = (W.ScrollViewer)((W.TabItem)Field<W.TabControl>(panel, "_tabs").Items[0]).Content;
        overview.ScrollToBottom(); Pump(40); Capture(panel, "overview-preferences.png", true); overview.ScrollToTop();
    }

    private static void CaptureElement(DependencyObject element, string filename)
    {
        var view = (FrameworkElement)element; view.UpdateLayout(); var dpi = VisualTreeHelper.GetDpi(view);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(view.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(view.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bitmap.Render(view); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(_output, filename)); encoder.Save(stream);
    }

    private static void CheckNewPresentation(PetWindow pet, ToolsController controller, ToolsService service)
    {
        foreach(var r in service.State.Reminders.ToArray()) service.Delete(r.Id);
        service.StopFocus(); ((Window?)GetField(controller,"_popup"))?.Close();
        var runner=Field<ActionRunner>(pet,"_runner"); EnterSleep(runner);
        var before=runner.Generation;
        service.Upsert(null,"重要事项 A",DateTime.Now.AddSeconds(-2),RepeatRule.Once);
        service.Upsert(null,new string('长',200),DateTime.Now.AddSeconds(-2),RepeatRule.Once);
        service.ConfigureFocus(1,1); service.StartFocus(); service.Tick(DateTime.Now,TimeSpan.FromMinutes(1));
        Invoke(controller,"Tick"); Pump(50);
        var popup=(Window)GetField(controller,"_popup")!;
        GetWindowRect(new WindowInteropHelper(popup).Handle,out var bubbleBeforeMove);
        pet.Left -= 50; Pump(50);
        GetWindowRect(new WindowInteropHelper(popup).Handle,out var bubbleAfterMove);
        Check(bubbleAfterMove.Left != bubbleBeforeMove.Left,"bubble follows actual pet movement");
        Check(runner.Current.Id=="wake" && runner.Generation==before+1,"one wake request for simultaneous reminder and focus batch");
        runner.Advance(TimeSpan.FromMilliseconds(5001));
        Check(runner.Current.Id=="wave","notification plays after the waking animation");
        Check(service.PendingCount==2 && service.State.Focus.AwaitingNext,"batch pending state retained");
        Click(popup,"下一件");
        Check(service.PendingCount==2,"browse does not complete reminder");
        Capture(popup,"group-long-title.png");
        service.Confirm("保存成功");
        Check(ReferenceEquals(popup,GetField(controller,"_popup")),"success status does not replace important bubble");
        SetField(controller,"_popupOpened",TimeSpan.FromDays(-1)); Invoke(controller,"Tick");
        Check(ReferenceEquals(popup,GetField(controller,"_popup")),"default important bubble remains until handled");
        Click(popup,"收起");
        Check(service.PendingCount==2 && runner.Current.ShowSleepIndicator,"collapse retains pending and restores sleep without interaction");
        var companion=(Window)GetField(controller,"_companion")!;
        Check(companion.IsVisible && !companion.ShowActivated,"pending badge remains nonactivating");
        Check((GetWindowLong(new WindowInteropHelper(companion).Handle,-20)&0x08000000)!=0,"companion native NOACTIVATE style");
        Invoke(controller,"ReviewPending"); popup=(Window)GetField(controller,"_popup")!;
        Check(popup.IsVisible,"badge review reopens pending bubble"); Click(popup,"收起");
        service.SetPresentation(true,10,true,true); ((Window?)GetField(controller,"_popup"))?.Close();
        Invoke(controller,"ReviewPending"); popup=(Window)GetField(controller,"_popup")!;
        SetField(controller,"_popupOpened",TimeSpan.FromDays(-1)); Invoke(controller,"Tick");
        Check(GetField(controller,"_popup") is null && service.PendingCount==2,"auto hide never completes pending reminders");
        companion=(Window)GetField(controller,"_companion")!; Capture(companion,"timer-card.png");
        Click(companion,"下一阶段");
        Check(service.State.Focus.Stage==FocusStage.Break && service.State.Focus.Running,"timer card starts next phase");
        Click(companion,"暂停"); Check(!service.State.Focus.Running,"timer card pauses");
        Click(companion,"继续"); Check(service.State.Focus.Running,"timer card resumes");
        Click(companion,"收起"); Check(!service.State.ShowTimerCard && GetField(controller,"_companion") is not null,"timer collapse keeps pending badge");
        service.SetQuiet(true); ((Window?)GetField(controller,"_popup"))?.Close(); EnterSleep(runner);
        before=runner.Generation;
        service.Upsert(null,"安静期间到期",DateTime.Now.AddSeconds(-1),RepeatRule.Once); Invoke(controller,"Tick");
        Check(GetField(controller,"_popup") is null && runner.Generation==before,"quiet suppresses popup and wake");
        service.Confirm("安静期间手动确认");
        Check(GetField(controller,"_popup") is not null,"quiet allows manual confirmation");
        SetField(controller,"_popupOpened",TimeSpan.FromDays(-1)); Invoke(controller,"Tick");
        Check(GetField(controller,"_popup") is null,"feedback auto closes");
        service.SetQuiet(false); Invoke(controller,"Tick"); popup=(Window)GetField(controller,"_popup")!;
        SetField(pet,"_interactionRevision",Field<long>(pet,"_interactionRevision")+1); Click(popup,"收起");
        Check(!runner.Current.ShowSleepIndicator,"character interaction prevents forced sleep restoration");
        EnterSleep(runner);
        var gestures=Field<PointerGestures>(pet,"_gestures");
        gestures.Press(new PointerPosition(0,0),0); gestures.Move(new PointerPosition(100,100)); runner.Trigger("drag_start");
        before=runner.Generation;
        service.Upsert(null,"拖动期间到期",DateTime.Now.AddSeconds(-1),RepeatRule.Once); Invoke(controller,"Tick");
        Check(runner.IsDragging && runner.Generation==before && Field<bool>(pet,"_attentionPending"),"drag defers notification action while bubble still appears");
        gestures.Release(new PointerPosition(100,100),50); runner.Trigger("drag_release");
        SetField(pet,"_interactionRevision",Field<long>(pet,"_interactionRevision")+1); Invoke(pet,"WakeForAttention");
        runner.Advance(TimeSpan.FromMilliseconds(3001));
        Check(runner.Current.Id=="wave","deferred notification delivered after drag");
        ((Window?)GetField(controller,"_popup"))?.Close();
        Check(!runner.Current.ShowSleepIndicator,"drag interaction prevents forced restoration");
        service.SetPresentation(false,20,false,false); ((Window?)GetField(controller,"_popup"))?.Close(); EnterSleep(runner); before=runner.Generation;
        service.Upsert(null,"关闭唤醒",DateTime.Now.AddSeconds(-1),RepeatRule.Once); Invoke(controller,"Tick");
        Check(runner.Generation==before && GetField(controller,"_popup") is not null,"wake off still shows important bubble");
        // Treat all current items using the actual bubble controls, including repeat-free reminder ids.
        while(service.State.Reminders.Any(r=>r.PendingOccurrence is not null && r.SnoozeUntil is null))
        { popup=(Window)GetField(controller,"_popup")!; Click(popup,"完成"); }
        Check(service.PendingCount==0,"bubble handles all grouped reminders individually");
        service.StopFocus(); ((Window?)GetField(controller,"_popup"))?.Close();
        Check(GetField(controller,"_companion") is null,"badge disappears only after pending handled");
        service.ConfigureFocus(1,1); service.StartFocus(); service.Tick(DateTime.Now,TimeSpan.FromMinutes(1)); Invoke(controller,"Tick");
        popup=(Window)GetField(controller,"_popup")!; Click(popup,"开始休息");
        Check(service.State.Focus.Stage==FocusStage.Break && service.State.Focus.Running,"bubble directly starts break");
        service.Tick(DateTime.Now,TimeSpan.FromMinutes(1)); Invoke(controller,"Tick");
        popup=(Window)GetField(controller,"_popup")!; Click(popup,"开始下一轮");
        Check(service.State.Focus.Stage==FocusStage.Focus && service.State.Focus.Running,"bubble directly starts next focus round");
        service.StopFocus(); ((Window?)GetField(controller,"_popup"))?.Close();
    }
    private static void SetField(object target,string name,object value) => target.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(target,value);
    private static object? GetField(object target, string name) => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target);
    private static void EnterSleep(ActionRunner runner)
    {
        runner.PlayAction("sleep_idle");
    }

    private static void CheckFourPoseAnimation(PetWindow pet)
    {
        var runner=Field<ActionRunner>(pet,"_runner");
        var indicator=Field<W.TextBlock>(pet,"SleepIndicator");
        var viewport=(pet.Left,pet.Top,pet.Width,pet.Height);
        Check(runner.Current.Id=="sleep_stand" && indicator.Visibility==Visibility.Collapsed,
            "four-pose startup stands masked without ZZZ");
        CheckRenderedFrame(pet,runner,"stand-masked.png");
        Capture(pet,"pet-standing-mask.png",true);
        runner.Advance(TimeSpan.FromMilliseconds(2000)); Pump(40);
        Check(runner.Current.Id=="sleep_sit" && indicator.Visibility==Visibility.Visible,
            "four-pose seated sleep renders with ZZZ");
        Check(viewport==(pet.Left,pet.Top,pet.Width,pet.Height),$"mask to seated preserves actual window viewport; before={viewport}; after={(pet.Left,pet.Top,pet.Width,pet.Height)}");
        CheckRenderedFrame(pet,runner,"sleep.png");
        Capture(pet,"pet-seated.png",true);
        runner.Trigger("click"); runner.Advance(TimeSpan.FromMilliseconds(2)); Pump(40);
        Check(runner.Current.Id=="awake_idle" && indicator.Visibility==Visibility.Collapsed,
            "four-pose click renders standing and hides ZZZ");
        CheckRenderedFrame(pet,runner,"rest.png");
        Capture(pet,"pet-standing.png",true);
        runner.Trigger("double_click"); Pump(40);
        Check(runner.Current.Id=="wave","four-pose double click renders wave");
        Check(viewport==(pet.Left,pet.Top,pet.Width,pet.Height),"standing to wave preserves actual window viewport");
        CheckRenderedFrame(pet,runner,Path.GetFileName(runner.CurrentFramePath));
        Capture(pet,"pet-wave.png",true);
        if (runner.CurrentFramePath.Contains("four-actions-pivot-"))
        {
            runner.Advance(TimeSpan.FromMilliseconds(360)); Pump(40);
            Check(viewport==(pet.Left,pet.Top,pet.Width,pet.Height),"pivot rotation preserves actual window viewport");
            CheckRenderedFrame(pet,runner,Path.GetFileName(runner.CurrentFramePath));
            Capture(pet,"pet-wave-rotated.png",true);
        }
        runner.Trigger("click"); runner.Advance(TimeSpan.FromMilliseconds(2)); Pump(40);
        runner.Trigger("idle_timeout"); Pump(40);
        Check(runner.Current.Id=="sleep_stand" && indicator.Visibility==Visibility.Collapsed,
            "four-pose idle timeout renders mask before seated sleep");
        runner.Advance(TimeSpan.FromMilliseconds(2000)); Pump(40);
    }

    private static void CheckSpriteSharing(CharacterDefinition character)
    {
        var image=new W.Image(); var player=new SpritePlayer(image); var runner=new ActionRunner(character);
        var decoded=new Dictionary<string,BitmapImage>(); var paths=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var action in character.Actions)
        {
            typeof(ActionRunner).GetProperty(nameof(ActionRunner.Current))!.SetValue(runner,action);
            double elapsed=0;
            foreach (var frame in action.Frames)
            {
                SetField(runner,"_actionElapsedMs",elapsed+0.001); player.Show(runner); elapsed+=frame.DurationMs;
                var source=(BitmapImage)image.Source; paths.Add(runner.CurrentFramePath);
                var hash=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(runner.CurrentFramePath)));
                var expected=new BitmapImage(new Uri(runner.CurrentFramePath));
                Check(SamePixels(source,expected),$"cached frame preserves every pixel: {frame.Path}");
                if (decoded.TryGetValue(hash,out var previous) &&
                    Field<Dictionary<string,BitmapImage>>(player,"_contentCache").Values.Contains(previous))
                    Check(ReferenceEquals(previous,source),$"identical PNG shares frozen decoded image: {frame.Path}");
                decoded[hash]=source;
            }
        }
        Check(player.CachedPixelBytes <= Field<long>(player,"_budget") &&
            Field<Dictionary<string,BitmapImage>>(player,"_contentCache").Values.All(b=>b.IsFrozen),
            $"all {paths.Count} frame paths preserve pixels within the bounded cache");
        var before=image.Source; runner.Flip(); player.Show(runner);
        Check(ReferenceEquals(before,image.Source) && ((ScaleTransform)image.RenderTransform).ScaleX==runner.Facing,
            "facing changes preserve cached pixels");
    }

    private static bool SamePixels(BitmapSource left,BitmapSource right)
    {
        if (left.PixelWidth!=right.PixelWidth || left.PixelHeight!=right.PixelHeight || left.Format!=right.Format) return false;
        var stride=(left.PixelWidth*left.Format.BitsPerPixel+7)/8;
        var a=new byte[left.PixelHeight*stride]; var b=new byte[a.Length];
        left.CopyPixels(a,stride,0); right.CopyPixels(b,stride,0); return a.AsSpan().SequenceEqual(b);
    }

    private static void CheckSpriteEviction(CharacterDefinition character)
    {
        var image = new W.Image();
        var player = new SpritePlayer(image, 5L * 1024 * 1024);
        var runner = new ActionRunner(character);
        var first = character.Actions[0];
        typeof(ActionRunner).GetProperty(nameof(ActionRunner.Current))!.SetValue(runner, first);
        player.Show(runner);
        var original = (BitmapImage)image.Source;
        foreach (var action in character.Actions)
        {
            typeof(ActionRunner).GetProperty(nameof(ActionRunner.Current))!.SetValue(runner, action);
            double elapsed = 0;
            foreach (var frame in action.Frames)
            {
                SetField(runner, "_actionElapsedMs", elapsed + .001); player.Show(runner);
                elapsed += frame.DurationMs;
                Check(player.CachedPixelBytes <= 5L * 1024 * 1024, "LRU decoded pixel budget remains bounded");
                Check(SamePixels((BitmapImage)image.Source, new BitmapImage(new Uri(frame.FullPath))),
                    "eviction preserves displayed frame pixels");
            }
        }
        typeof(ActionRunner).GetProperty(nameof(ActionRunner.Current))!.SetValue(runner, first);
        SetField(runner, "_actionElapsedMs", .001); player.Show(runner);
        Check(SamePixels(original, (BitmapImage)image.Source), "evicted first frame reloads with identical pixels");
        var uncached = new SpritePlayer(new W.Image(), 1);
        uncached.Show(runner);
        Check(uncached.CachedPixelBytes == 0 && uncached.CachedImageCount == 0,
            "oversized current image is displayed without violating cache budget");
    }

    private static void CheckInteractionMenu(PetWindow pet, SettingsStore store)
    {
        var menu = Field<W.MenuItem>(pet, "_interactionMenu");
        var categories = menu.Items.OfType<W.MenuItem>().Where(item => item.Items.Count > 0).ToArray();
        Check(categories.Any(c => (string)c.Header == "回应") && categories.Any(c => (string)c.Header == "休息"),
            "interaction menu groups real legacy actions with readable names");
        CheckSubmenus(pet, menu, categories[0]);
        var wave = categories.SelectMany(c => c.Items.OfType<W.MenuItem>()).Single(item => (string)item.Header == "打招呼");
        wave.RaiseEvent(new RoutedEventArgs(W.MenuItem.ClickEvent));
        Check(Field<ActionRunner>(pet, "_runner").Current.Id == "wave", "menu action reaches real action runner");
        var automatic = menu.Items.OfType<W.MenuItem>().Single(item => (string)item.Header == "自动小动作");
        automatic.IsChecked = false; automatic.RaiseEvent(new RoutedEventArgs(W.MenuItem.ClickEvent));
        Check(!store.Load().AutomaticActions["ellen-flat2d"] && !Field<ActionRunner>(pet, "_runner").AutomaticActionsEnabled,
            "automatic action menu persists per-role disabled state");
        typeof(PetWindow).GetMethod("SelectCharacter", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(pet, ["ellen-flat2d"]);
        Check(!Field<ActionRunner>(pet, "_runner").AutomaticActionsEnabled, "automatic switch survives role reload");
        menu = Field<W.MenuItem>(pet, "_interactionMenu");
        automatic = menu.Items.OfType<W.MenuItem>().Single(item => (string)item.Header == "自动小动作");
        automatic.IsChecked = true; automatic.RaiseEvent(new RoutedEventArgs(W.MenuItem.ClickEvent));
        typeof(PetWindow).GetMethod("SelectCharacter", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(pet, ["ellen-flat2d"]);
    }

    private static void CheckSubmenus(PetWindow pet, W.MenuItem menu, W.MenuItem category)
    {
        pet.ContextMenu.IsOpen = true; Pump(100);
        menu.IsSubmenuOpen = true; Pump(100);
        menu.ApplyTemplate();
        var popup = menu.Template.FindName("PART_Popup", menu) as W.Primitives.Popup;
        Check(popup is { IsOpen: true, Child.IsVisible: true }, "interaction submenu actually displays its popup");
        category.IsSubmenuOpen = true; Pump(100);
        category.ApplyTemplate();
        var nested = category.Template.FindName("PART_Popup", category) as W.Primitives.Popup;
        Check(nested is { IsOpen: true, Child.IsVisible: true }, "action category actually displays its nested popup");
        Capture(pet, "interaction-menu.png");
        pet.ContextMenu.IsOpen = false; Pump(40);
    }

    private static void CheckRenderedFrame(PetWindow pet,ActionRunner runner,string filename)
    {
        var sprite=Field<W.Image>(pet,"SpriteImage");
        var source=(BitmapImage)sprite.Source;
        var player=Field<SpritePlayer>(pet,"_player");
        var expected=new BitmapImage();
        expected.BeginInit(); expected.CacheOption=BitmapCacheOption.OnLoad;
        expected.UriSource=new Uri(runner.CurrentFramePath); expected.EndInit(); expected.Freeze();
        Check(Path.GetFileName(runner.CurrentFramePath)==filename &&
            Field<string>(player,"_shown")==runner.CurrentFramePath && source.IsFrozen &&
            SamePixels(source,expected),
            $"WPF image pixels and requested path match the {filename} action frame");
        if (runner.CurrentFramePath.Contains("four-actions-reference-20261001") ||
            runner.CurrentFramePath.Contains("four-actions-fixed-motion-20261001") ||
            runner.CurrentFramePath.Contains("four-actions-pivot-"))
            Check(source.PixelWidth==616 && source.PixelHeight==640 &&
                RenderOptions.GetBitmapScalingMode(sprite)==BitmapScalingMode.HighQuality,
                $"{filename} uses native-density PNG and high-quality scaling");
    }

    private static T Field<T>(object target, string name) => (T)GetField(target, name)!;
    private static void Invoke(object target, string name) => target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, null);
    private static void Check(bool result, string message)
    {
        if (!result) throw new InvalidOperationException(message);
        Passed.Add(message);
    }
    private static IEnumerable<DependencyObject> Walk(DependencyObject root)
    {
        yield return root;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Walk(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
    private static void Click(Window window, string label)
    {
        window.UpdateLayout();
        var button = Walk(window).OfType<W.Button>().First(b => b.Content?.ToString() == label);
        button.RaiseEvent(new RoutedEventArgs(W.Primitives.ButtonBase.ClickEvent));
        Pump(40);
    }
    private static void Pump(int milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start(); Dispatcher.PushFrame(frame);
    }
    private static void Capture(Window window, string filename,bool renderContent=true)
    {
        window.UpdateLayout();
        var handle = new WindowInteropHelper(window).Handle;
        GetWindowRect(handle, out var rect);
        if (!renderContent)
        {
            using (var bitmap = new System.Drawing.Bitmap(rect.Right - rect.Left, rect.Bottom - rect.Top))
            using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
            {
                var dc = graphics.GetHdc();
                bool captured;
                try { captured = PrintWindow(handle, dc, 2); }
                finally { graphics.ReleaseHdc(dc); }
                if (captured)
                {
                    bitmap.Save(Path.Combine(_output, filename), System.Drawing.Imaging.ImageFormat.Png);
                    return;
                }
            }
        }
        var content = (FrameworkElement)window.Content;
        var dpi = VisualTreeHelper.GetDpi(content);
        var width = content.ActualWidth + content.Margin.Left + content.Margin.Right;
        var height = content.ActualHeight + content.Margin.Top + content.Margin.Bottom;
        var image = new RenderTargetBitmap((int)Math.Ceiling(width * dpi.DpiScaleX),
            (int)Math.Ceiling(height * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        var background = new DrawingVisual();
        using (var drawing = background.RenderOpen())
            drawing.DrawRectangle(window.Background, null, new System.Windows.Rect(0, 0, width, height));
        image.Render(background);
        image.Render(content);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = File.Create(Path.Combine(_output, filename)); encoder.Save(stream);
    }
}

