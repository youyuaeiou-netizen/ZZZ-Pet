using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DesktopPet;
using DesktopPet.Behavior;
using DesktopPet.Character;

internal static class Program
{
    private static readonly List<string> Passed = [];
    private static void Check(bool ok, string label)
    {
        if (!ok) throw new InvalidDataException(label);
        Passed.Add(label);
    }
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            var pack = Path.GetFullPath(args[0]);
            var output = Path.GetFullPath(args[1]);
            Directory.CreateDirectory(output);
            var loaded = CharacterLoader.Load(pack);
            var catalogCharacter = loaded.Character;
            var merged = catalogCharacter.Id == "gpt-dragon" && catalogCharacter.Forms is not null;
            var character = merged ? catalogCharacter.Forms!["chibi"] : catalogCharacter;
            Check(loaded.Warnings.Count == 0, "character loads without warnings");
            var expanded = character.ActionMap.ContainsKey("feed");
            var singing = character.ActionMap.ContainsKey("sing_loop");
            var expectedActions = (expanded
                ? new[] { "idle", "heart", "drag", "release", "feed", "sleep_enter", "sleep", "wake" }
                : new[] { "idle", "heart", "drag", "release" }).ToList();
            if (singing) expectedActions.AddRange(["stretch", "fire", "sing_enter", "sing_loop", "sing_exit"]);
            Check(character.ActionMap.Keys.ToHashSet().SetEquals(expectedActions), "only supplied video actions are included");
            Check(character.Forms is null && character.SleepThresholdMs == 0 && character.WakeActionId == (expanded ? "wake" : null), "independent pack preserves manual operation and configured wake transition");
            var runner = new ActionRunner(character) { AutomaticActionsEnabled = false };
            if (singing)
            {
                void RunFor(int ms) { for (var i = 0; i < ms; i += 10) runner.Advance(TimeSpan.FromMilliseconds(10)); }
                Check(!runner.PlayAction("sing_exit"), "singing exit is unavailable while idle");
                foreach (var id in new[] { "stretch", "fire" })
                {
                    Check(runner.PlayAction(id) && runner.Current.Id == id, id + " starts from idle");
                    RunFor(5100);
                    Check(runner.Current.Id == "idle", id + " returns to idle");
                }
                runner.PlayAction("sing_enter"); RunFor(character.ActionMap["sing_enter"].Frames.Sum(f => f.DurationMs) + 50);
                Check(runner.Current.Id == "sing_loop", "singing entry reaches continuous loop");
                Check(!runner.PlayAction("wake") && runner.Current.Id == "sing_loop", "singing rejects a prone-starting wake clip");
                var loopMs = character.ActionMap["sing_loop"].Frames.Sum(f => f.DurationMs);
                RunFor(loopMs * 3 + 530);
                Check(runner.Current.Id == "sing_loop", "singing persists for repeated forward loops");
                runner.PlayAction("sing_exit"); runner.Advance(TimeSpan.FromMilliseconds(1));
                Check(runner.Current.Id == "sing_loop", "stopping after several loops waits for the current boundary");
                RunFor(loopMs);
                Check(runner.Current.Id == "sing_exit", "loop boundary starts the supplied ending");
                Check(runner.PlayAction("sing_exit"), "repeated stop is harmless while ending");
                RunFor(character.ActionMap["sing_exit"].Frames.Sum(f => f.DurationMs) + 50);
                Check(runner.Current.Id == "idle", "singing ending returns to idle");
                runner.PlayAction("sing_enter"); RunFor(300);
                runner.PlayAction("fire");
                RunFor(character.ActionMap["sing_enter"].Frames.Sum(f => f.DurationMs));
                Check(runner.Current.Id == "sing_loop", "request during entry finishes entering before exiting");
                RunFor(loopMs);
                Check(runner.Current.Id == "sing_exit", "queued action uses singing ending");
                RunFor(character.ActionMap["sing_exit"].Frames.Sum(f => f.DurationMs));
                Check(runner.Current.Id == "fire", "queued fire follows singing ending");
                RunFor(5100);
                runner.PlayAction("sing_enter"); RunFor(character.ActionMap["sing_enter"].Frames.Sum(f => f.DurationMs) + 50);
                runner.Trigger("click"); RunFor(loopMs);
                Check(runner.Current.Id == "sing_exit", "click exits singing through ending");
                RunFor(character.ActionMap["sing_exit"].Frames.Sum(f => f.DurationMs));
                Check(runner.Current.Id == "heart", "click response survives singing exit");
                RunFor(6000);
                runner.PlayAction("sing_loop"); runner.PlayAction("sing_exit");
                runner.Trigger("drag_start");
                Check(runner.IsDragging && runner.Current.Id == "drag", "drag immediately cancels pending singing exit");
                runner.Trigger("drag_release"); RunFor(6000);
                Check(runner.Current.Id == "idle", "drag cancellation leaves no pending singing action");
            }
            Check(runner.Trigger("click") && runner.Current.Id == "heart", "click starts heart");
            runner.Advance(TimeSpan.FromSeconds(6));
            Check(runner.Current.Id == "idle", "heart returns to idle");
            runner.Trigger("drag_start");
            var first = runner.CurrentFramePath;
            runner.Advance(TimeSpan.FromMilliseconds(500));
            Check(runner.IsDragging && runner.Current.Id == "drag" && runner.CurrentFramePath != first, "looping drag advances without a wake action");
            runner.Advance(TimeSpan.FromMilliseconds(5000));
            Check(runner.Current.Id == "drag" && runner.CurrentFramePath != first, "drag loops while held");
            Check(runner.Trigger("drag_release") && runner.Current.Id == "release", "release starts landing celebration");
            runner.Advance(TimeSpan.FromSeconds(6));
            Check(runner.Current.Id == "idle", "landing returns to idle");
            if (expanded)
            {
                Check(!runner.PlayAction("wake") && runner.Current.Id == "idle", "awake pet rejects a prone-starting wake clip");
                runner.PlayAction("sleep_enter"); runner.Advance(TimeSpan.FromMilliseconds(750));
                Check(runner.Trigger("click") && runner.Current.Id == "sleep_enter", "click during lying down waits for the sleep transition");
                runner.Advance(TimeSpan.FromSeconds(3));
                Check(runner.Current.Id == "wake", "completed lying down wakes before the queued click");
                runner.Advance(TimeSpan.FromSeconds(5));
                Check(runner.Current.Id == "heart", "waking preserves the queued click response");
                runner.Advance(TimeSpan.FromSeconds(6));
                runner.PlayAction("sleep_enter"); runner.Advance(TimeSpan.FromSeconds(3));
                Check(runner.Current.Id == "sleep", "sleep entry reaches prone loop");
                runner.Advance(TimeSpan.FromSeconds(10));
                Check(runner.Current.Id == "sleep", "prone loop stays asleep across repeated loops");
                Check(runner.PlayAction("feed") && runner.Current.Id == "wake", "feeding asleep plays wake first");
                runner.Advance(TimeSpan.FromSeconds(5));
                Check(runner.Current.Id == "feed", "feeding follows wake");
                runner.Advance(TimeSpan.FromSeconds(5));
                Check(runner.Current.Id == "idle", "token feeding returns to idle");
                runner.PlayAction("sleep_enter"); runner.Advance(TimeSpan.FromMilliseconds(750));
                runner.PlayAction("wake");
                Check(runner.Current.Id == "sleep_enter", "manual wake waits while lying down");
                runner.Advance(TimeSpan.FromSeconds(3));
                Check(runner.Current.Id == "wake", "manual wake follows completed lying down");
                runner.Advance(TimeSpan.FromSeconds(5));
                Check(runner.Current.Id == "idle", "manual wake ends in idle");
            }
            var drag = character.ActionMap["drag"];
            drag.Loop = false;
            var held = new ActionRunner(character);
            held.Trigger("drag_start"); first = held.CurrentFramePath;
            held.Advance(TimeSpan.FromMilliseconds(500));
            Check(expanded ? held.CurrentFramePath != first : held.CurrentFramePath == first,
                "nonlooping drag follows the configured wake-aware playback behavior");
            drag.Loop = true;

            var app = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            var paths = new RuntimePaths(new StartupOptions(null, Path.Combine(output, "runtime"), character.Id));
            var settings = new UserSettings { SelectedCharacter = character.Id, AlwaysOnTop = false, Scale = 1, WindowLeft = 100, WindowTop = 100 };
            if (merged) settings.Dragon = new DragonState { Form = "chibi", LastFedAt = DateTimeOffset.UtcNow };
            settings.AutomaticActions[character.Id] = false;
            var window = new PetWindow(new Dictionary<string, CharacterDefinition> { [catalogCharacter.Id] = catalogCharacter }, Path.GetDirectoryName(pack)!, catalogCharacter.Id, settings, new SettingsStore(paths.SettingsPath), paths);
            window.Show(); window.UpdateLayout();
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            T Field<T>(string name) => (T)typeof(PetWindow).GetField(name, flags)!.GetValue(window)!;
            void Call(string name, params object[] values) => typeof(PetWindow).GetMethod(name, flags)!.Invoke(window, values);
            Field<DispatcherTimer>("_animationTimer").Stop();
            var actual = Field<ActionRunner>("_runner");
            var player = Field<SpritePlayer>("_player");
            if (expanded)
            {
                var menu = (System.Windows.Controls.MenuItem)typeof(PetWindow).GetField("_interactionMenu", flags)!.GetValue(window)!;
                var labels = menu.Items.OfType<System.Windows.Controls.MenuItem>()
                    .SelectMany(m => m.Items.OfType<System.Windows.Controls.MenuItem>()).Select(m => m.Header?.ToString()).ToArray();
                Check((merged ? menu.Items.OfType<System.Windows.Controls.MenuItem>().Any(m => m.Header?.ToString() == "喂食") : labels.Contains("吃Token"))
                    && labels.Contains("睡觉") && labels.Contains("睡醒"), "actual interaction menu exposes token, sleep and wake");
                if (singing) Check(new[] { "伸懒腰", "喷火", "悠闲哼歌", "结束哼歌" }.All(label => labels.Count(x => x == label) == 1), "new interactions each appear exactly once in actual menu");
            }
            var production = merged || character.Id == "gpt-dragon-chibi-v4";
            if (merged)
            {
                Call("SelectDragonForm", "human");
                Check(Field<string>("_shownDragonForm") == "human", "merged character switches to preserved human form");
                Call("SelectDragonForm", "chibi");
                Check(Field<string>("_shownDragonForm") == "chibi" && actual.CurrentFramePath.Contains("animations" + Path.DirectorySeparatorChar + "chibi"), "merged character switches back to the new chibi frames");
                Call("FeedDragon"); Call("UpdateDragon");
                Check(actual.Current.Id == "feed", "merged feeding entry plays the token clip");
                actual.Advance(TimeSpan.FromSeconds(5)); Call("UpdateDragon");
                Check(Field<string>("_shownDragonForm") == "human", "completed feeding preserves the enlarge policy");
                Call("SelectDragonForm", "chibi");
                actual.PlayAction("sleep_enter"); actual.Advance(TimeSpan.FromMilliseconds(750));
                Call("FeedDragon"); Call("UpdateDragon");
                Check(Field<bool>("_feeding") && actual.Current.Id == "sleep_enter", "feeding stays queued while the merged pet lies down");
                actual.Advance(TimeSpan.FromSeconds(3)); Call("UpdateDragon");
                actual.Advance(TimeSpan.FromSeconds(5)); Call("UpdateDragon");
                Check(actual.Current.Id == "feed", "queued merged feeding follows the full wake clip");
                actual.Advance(TimeSpan.FromSeconds(5)); Call("UpdateDragon");
                Check(Field<string>("_shownDragonForm") == "human", "queued feeding still completes the enlarge policy");
                Call("SelectDragonForm", "chibi");
            }
            void Set(string name, object value) => typeof(PetWindow).GetField(name, flags)!.SetValue(window, value);
            void StepFall()
            {
                var elapsed = TimeSpan.FromMilliseconds(25);
                var falling = (bool)typeof(PetWindow).GetMethod("AdvanceChibiFall", flags)!.Invoke(window, [elapsed])!;
                if (!falling) actual.Advance(elapsed);
                Call("ApplyViewport");
                var air = (string?)typeof(PetWindow).GetProperty("ChibiAirFramePath", flags)!.GetValue(window);
                player.Show(actual, air); window.UpdateLayout();
            }
            Call("HandleGesture", "click");
            Check(actual.Current.Id == "heart", "actual PetWindow click handler selects heart");
            Call("HandleGesture", "drag_start");
            first = actual.CurrentFramePath; actual.Advance(TimeSpan.FromMilliseconds(500));
            Check(actual.IsDragging && actual.CurrentFramePath != first, "actual PetWindow drag handler animates");
            Call("HandleGesture", "drag_release");
            Check(actual.Current.Id == "release", "actual PetWindow release handler selects landing");
            if (production)
                for (var tick = 0; tick < 20 && Field<bool>("_chibiFalling"); tick++) StepFall();
            actual.Advance(TimeSpan.FromSeconds(6));
            Check(actual.Current.Id == "idle", "actual PetWindow release returns to idle");
            if (production)
            {
                Check(character.ActionMap["release"].Frames.Take(6).All(f => Path.GetFileName(f.Path).StartsWith("air")), "airborne prefix is present before contact motion");
                foreach (var scale in new[] { .5, .8, 1, 2.5 })
                {
                    Call("SetScale", scale);
                    var floor = (double)typeof(PetWindow).GetProperty("ChibiFloor", flags)!.GetValue(window)!;
                    var start = floor - 200 * scale;
                    Set("_groundY", start); Call("ApplyViewport");
                    Call("HandleGesture", "drag_start"); Call("HandleGesture", "drag_release");
                    Check(Field<bool>("_chibiFalling") && Math.Abs(Field<double>("_chibiFallFloor") - start - 48 * scale) < .01, $"{scale}: drop distance remains 48 times scale");
                    first = actual.CurrentFramePath;
                    var initialY = Field<double>("_groundY");
                    StepFall(); var y1 = Field<double>("_groundY"); StepFall(); var y2 = Field<double>("_groundY");
                    Check(y1 > initialY && y2 - y1 > y1 - initialY && actual.CurrentFramePath == first, $"{scale}: gravity accelerates while impact waits for contact");
                    for (var tick = 0; tick < 20 && Field<bool>("_chibiFalling"); tick++) StepFall();
                    Check(!Field<bool>("_chibiFalling") && Math.Abs(Field<double>("_groundY") - Field<double>("_chibiFallFloor")) < .01 && !Path.GetFileName(actual.CurrentFramePath).StartsWith("air"), $"{scale}: contact starts landing without overshoot");
                    actual.Advance(TimeSpan.FromSeconds(6));
                    Check(actual.Current.Id == "idle", $"{scale}: celebration returns to idle");
                    Set("_groundY", floor - 10 * scale);
                    Call("HandleGesture", "drag_start"); Call("KeepChibiSuspended");
                    Check(Math.Abs(Field<double>("_groundY") - floor + 10 * scale) < .01, $"{scale}: dragging near bottom does not force a lift");
                    Call("HandleGesture", "drag_release");
                    for (var tick = 0; tick < 20 && Field<bool>("_chibiFalling"); tick++) StepFall();
                    Check(Math.Abs(Field<double>("_groundY") - floor) < .01, $"{scale}: near-bottom drop stops at floor");
                    actual.Advance(TimeSpan.FromSeconds(6));
                    Set("_groundY", floor);
                    Call("HandleGesture", "drag_start"); Call("HandleGesture", "drag_release");
                    Check(!Field<bool>("_chibiFalling") && !Path.GetFileName(actual.CurrentFramePath).StartsWith("air"), $"{scale}: release at floor skips air sequence");
                    actual.Advance(TimeSpan.FromSeconds(6));
                }
                Call("SetScale", .8);
                Set("_groundY", SystemParameters.WorkArea.Top + 400d); Set("_displayedAction", null!); Call("ApplyViewport");
                var cameraLeft = window.Left - 72; var cameraTop = window.Top - 36;
                Call("HandleGesture", "drag_start"); Call("HandleGesture", "drag_release");
                var film = Path.Combine(output, "physical-fall"); Directory.CreateDirectory(film);
                var visibleFilmFrames = 0;
                for (var index = 0; index < 220; index++)
                {
                    StepFall();
                    var sprite = new RenderTargetBitmap((int)Math.Ceiling(window.Width), (int)Math.Ceiling(window.Height), 96, 96, PixelFormats.Pbgra32);
                    sprite.Render(window);
                    var board = new DrawingVisual(); using (var drawing = board.RenderOpen())
                    {
                        drawing.DrawRectangle(new SolidColorBrush(Color.FromRgb(36, 36, 48)), null, new Rect(0, 0, 400, 400));
                        drawing.DrawImage(sprite, new Rect(window.Left-cameraLeft, window.Top-cameraTop, window.Width, window.Height));
                    }
                    var picture = new RenderTargetBitmap(400, 400, 96, 96, PixelFormats.Pbgra32); picture.Render(board);
                    var pixels = new byte[400*400*4]; picture.CopyPixels(pixels, 400*4, 0);
                    if (Enumerable.Range(0, 400*400).Count(p => Math.Abs(pixels[p*4]-48)+Math.Abs(pixels[p*4+1]-36)+Math.Abs(pixels[p*4+2]-36)>30)>100) visibleFilmFrames++;
                    var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(picture));
                    using var file = File.Create(Path.Combine(film, $"{index:0000}.png")); png.Save(file);
                }
                Check(visibleFilmFrames == 220, "fixed-camera captures contain visible character pixels in every frame");
                Check(actual.Current.Id == "idle" && !Field<bool>("_chibiFalling"), "fixed-camera real-window fall and celebration finish at idle");
                Call("SetScale", 1d);
            }
            var sizes = new HashSet<string>();
            var captures = new List<(string id, int ms)> { ("idle", 2000), ("heart", 2500), ("drag", 2000), ("release", 2200) };
            if (expanded) captures.AddRange(new[] { ("feed", 2600), ("sleep_enter", 2500), ("sleep", 500), ("wake", 2500) });
            foreach (var (id, ms) in captures)
            {
                if (expanded)
                {
                    actual.PlayAction("idle");
                    for (var settle = 0; settle < 3; settle++) actual.Advance(TimeSpan.FromSeconds(20));
                    if (id == "wake")
                    {
                        actual.PlayAction("sleep_enter"); actual.Advance(TimeSpan.FromSeconds(3));
                    }
                }
                actual.PlayAction(id); actual.Advance(TimeSpan.FromMilliseconds(ms));
                if (expanded) Check(actual.Current.Id == id, "actual window captures requested action: " + id);
                Call("ApplyViewport"); player.Show(actual); window.UpdateLayout();
                sizes.Add($"{window.Width}x{window.Height}");
                var image = new RenderTargetBitmap((int)window.Width, (int)window.Height, 96, 96, PixelFormats.Pbgra32);
                image.Render(window);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
                using var stream = File.Create(Path.Combine(output, id + "-actual-window.png")); encoder.Save(stream);
            }
            Check(sizes.Count == 1, "actual window viewport stays constant across actions");
            if (singing)
            {
                foreach (var id in new[] { "stretch", "fire", "sing_enter", "sing_loop", "sing_exit" })
                {
                    actual.PlayAction("idle");
                    for (var i = 0; i < 5; i++) actual.Advance(TimeSpan.FromSeconds(30));
                    if (id == "sing_exit")
                    {
                        actual.PlayAction("sing_loop"); actual.PlayAction(id);
                        actual.Advance(TimeSpan.FromMilliseconds(character.ActionMap["sing_loop"].Frames.Sum(f => f.DurationMs)));
                    }
                    else actual.PlayAction(id);
                    actual.Advance(TimeSpan.FromMilliseconds(id == "sing_enter" ? 1500 : id == "sing_exit" ? 700 : 2500));
                    Check(actual.Current.Id == id, "actual window captures new action: " + id);
                    var groundBefore = (Field<double>("_groundX"), Field<double>("_groundY"));
                    Call("ApplyViewport"); player.Show(actual); window.UpdateLayout();
                    Check(Math.Abs(Field<double>("_groundX") - groundBefore.Item1) <= .51
                        && Math.Abs(Field<double>("_groundY") - groundBefore.Item2) <= .51, "new action preserves anchor within WPF pixel rounding: " + id);
                    var image = new RenderTargetBitmap((int)Math.Ceiling(window.Width), (int)Math.Ceiling(window.Height), 96, 96, PixelFormats.Pbgra32);
                    image.Render(window);
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
                    using var stream = File.Create(Path.Combine(output, id + "-actual-window.png")); encoder.Save(stream);
                }
            }
            Check(player.CachedPixelBytes <= 64L * 1024 * 1024, "WPF sprite cache remains within budget");
            window.Close();
            File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new { passed = Passed.Count, checks = Passed, manualPointerAcceptance = "pending", physicalShortDrop = production ? "verified at scales 0.5,0.8,1,2.5 including near-bottom and already-grounded cases" : "not enabled for preview-only character" }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"Video preview checks passed: {Passed.Count}");
            app.Shutdown();
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
