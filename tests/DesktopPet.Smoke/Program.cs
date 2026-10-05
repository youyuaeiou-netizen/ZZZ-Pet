using DesktopPet.Behavior;
using DesktopPet.Character;
using DesktopPet;

var root = Path.Combine(AppContext.BaseDirectory, "characters");
var ellen = CharacterLoader.Load(Path.Combine(root, "ellen-flat2d"));
Flat2dChecks.RunInstalledPack(Check, Path.Combine(root, "ellen-flat2d"));
// Alternate action names are exercised with a tiny runtime fixture, not a
// placeholder character shipped to friends.
var alternate = Path.Combine(AppContext.BaseDirectory, "alternate-character-fixture");
Directory.CreateDirectory(alternate);
File.WriteAllBytes(Path.Combine(alternate, "safe.png"), Convert.FromBase64String(
    "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/lS0AAAAASUVORK5CYII="));
File.WriteAllText(Path.Combine(alternate, "character.json"), """
    {"schemaVersion":1,"id":"fixture-orbit","immediateClick":false,"defaultActionId":"hover",
     "canvasWidth":160,"canvasHeight":160,"anchorX":80,"anchorY":150,"randomIntervalMs":1800,"sleepThresholdMs":0,
     "actions":[
       {"id":"hover","frames":[{"path":"safe.png","durationMs":600}],"loop":true,"interruptibleBy":["random","click"]},
       {"id":"orbit","frames":[{"path":"safe.png","durationMs":360}],"triggers":["random"],"priority":4,"nextActionId":"hover","interruptibleBy":["click"]},
       {"id":"flash","frames":[{"path":"safe.png","durationMs":240}],"triggers":["click"],"priority":12,"nextActionId":"hover"}]}
    """);
var orbit = CharacterLoader.Load(alternate);
Flat2dChecks.Run(Check, alternate);
var bundledCatalog = CharacterCatalog.Load(root, Path.Combine(root, ".no-user-fixture"));
Check(bundledCatalog.Warnings.Count == 0 && bundledCatalog.Characters.Count >= 1, "all bundled roles valid");
Check(ellen.Character.SchemaVersion == 1 && orbit.Character.SchemaVersion == 1, "schema version");
Check(ellen.Character.ActionMap.Count == 11, "Ellen stationary action count");
Check(ellen.Character.CanvasWidth == 308 && ellen.Character.CanvasHeight == 328,
    "Ellen logical canvas");
Check(ellen.Character.DefaultScale == 0.75, "Ellen default 240 DIP height");
Check(ellen.Character.ActionMap["sleep_idle"].GetViewport(ellen.Character) == (308, 328, 154, 315),
    "Ellen sleep viewport");
Check(orbit.Character.ActionMap[orbit.Character.DefaultActionId].GetViewport(orbit.Character)
    == (orbit.Character.CanvasWidth, orbit.Character.CanvasHeight, orbit.Character.AnchorX, orbit.Character.AnchorY),
    "legacy viewport fallback");
Check(ellen.Character.Actions.All(a => a.Move is null), "Ellen has no autonomous movement");
Check(orbit.Character.ActionMap.Count == 3 && !orbit.Character.ActionMap.ContainsKey("idle_breathe"),
    "different action names");
Check(ellen.Character.Actions.All(a => a.Frames.All(f => File.Exists(f.FullPath))), "frame paths");

Check(ellen.Character.ActionMap["sleep_idle"].Frames.Count == 32, "sleep is an animated loop");
Check(ellen.Character.ImmediateClick == true && orbit.Character.ImmediateClick == false,
    "click behavior explicitly independent of sleeping indicator");

var orbitRunner = new ActionRunner(orbit.Character, new Random(2));
Check(!orbitRunner.Trigger("notification"), "optional notification absent in other character");
orbitRunner.Advance(TimeSpan.FromMilliseconds(1810));
Check(orbitRunner.Current.Id == "orbit", "second character random action");
Check(orbitRunner.Trigger("click") && orbitRunner.Current.Id == "flash", "second character click action");
orbitRunner.Advance(TimeSpan.FromMilliseconds(250));
Check(orbitRunner.Current.Id == "hover", "second character return");

var gestures = new PointerGestures(500, 8, 8, 5, 5);
gestures.Press(new PointerPosition(10, 10), 0);
Check(gestures.Release(new PointerPosition(10, 10), 40) is null, "defer single click");
Check(gestures.Advance(400) is null && gestures.Advance(540) == "click", "single click deadline");
gestures.Press(new PointerPosition(10, 10), 1000);
gestures.Release(new PointerPosition(10, 10), 1030);
gestures.Press(new PointerPosition(12, 12), 1100);
Check(gestures.Release(new PointerPosition(12, 12), 1130) == "double_click", "double click");
Check(gestures.Advance(2000) is null, "double click suppresses single");
gestures.Press(new PointerPosition(10, 10), 3000);
Check(gestures.Move(new PointerPosition(16, 10)) == "drag_start", "drag threshold");
Check(gestures.Release(new PointerPosition(20, 10), 3100) == "drag_release", "drag release gesture");
Check(gestures.Advance(4000) is null, "drag suppresses click");

var immediate = new PointerGestures(500, 8, 8, 5, 5, immediateClick: true);
immediate.Press(new PointerPosition(10, 10), 0);
Check(immediate.Release(new PointerPosition(10, 10), 40) == "click", "Ellen single click on release without delay");
immediate.Press(new PointerPosition(12, 12), 100);
Check(immediate.Release(new PointerPosition(12, 12), 140) == "double_click", "immediate single still permits double click");
Check(immediate.Advance(700) is null, "no delayed click after immediate double");
immediate.Press(new PointerPosition(10, 10), 1000);
Check(immediate.Release(new PointerPosition(10, 10), 1040) == "click", "next immediate single");
Check(immediate.Advance(1600) is null, "immediate click is not delivered twice");

var move = Movement.Step(95, 20, 10, 10, new Bounds(0, 0, 100, 100),
    new MoveDefinition { HorizontalSpeed = 50 }, 1, TimeSpan.FromSeconds(1));
Check(move.BoundaryHit && move.Left == 90, "movement boundary");

Exception? trayError = null;
var trayThread = new Thread(() =>
{
    try
    {
        var exits = 0;
        var auxiliaryClicks = 0;
        using var tray = new TrayService(() => ["ellen-flat2d"], () => "ellen-flat2d", () => 1,
            () => true, _ => { }, _ => { }, _ => { }, () => { }, () => exits++,
            showAbout: () => auxiliaryClicks++, openHelp: () => auxiliaryClicks++,
            openUserCharacters: () => auxiliaryClicks++, openLogs: () => auxiliaryClicks++,
            getCharacterName: _ => "Ellen 灰鲨");
        var notifyIcon = (System.Windows.Forms.NotifyIcon)typeof(TrayService)
            .GetField("_icon", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(tray)!;
        using var iconStream = typeof(TrayService).Assembly.GetManifestResourceStream("DesktopPet.AppIcon.ico")!;
        using var expectedIcon = new System.Drawing.Icon(iconStream, System.Windows.Forms.SystemInformation.SmallIconSize);
        using var expectedBitmap = expectedIcon.ToBitmap();
        using var trayBitmap = notifyIcon.Icon!.ToBitmap();
        Check(trayBitmap.Size == expectedBitmap.Size &&
            Enumerable.Range(0, trayBitmap.Width).All(x => Enumerable.Range(0, trayBitmap.Height)
                .All(y => trayBitmap.GetPixel(x, y) == expectedBitmap.GetPixel(x, y))),
            "tray uses the shared application icon at the system small-icon size");
        var rebuild = typeof(TrayService).GetMethod("RebuildMenu",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        rebuild.Invoke(tray, null);
        rebuild.Invoke(tray, null);
        rebuild.Invoke(tray, null);
        var menu = (System.Windows.Forms.ContextMenuStrip)typeof(TrayService)
            .GetField("_menu", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(tray)!;
        Check(((System.Windows.Forms.ToolStripMenuItem)menu.Items[0]).DropDownItems[0].Text == "Ellen 灰鲨",
            "friendly character name in tray");
        foreach (var label in new[] { "关于 DesktopPet", "使用与更新说明", "打开用户角色目录", "打开日志目录" })
            menu.Items.Cast<System.Windows.Forms.ToolStripItem>().Single(item => item.Text == label).PerformClick();
        Check(auxiliaryClicks == 4, "all release menu callbacks connected after rebuilding");
        menu.Items[^1].PerformClick();
        Check(exits == 1, "tray exit callback");
    }
    catch (Exception exception) { trayError = exception; }
});
trayThread.SetApartmentState(ApartmentState.STA);
trayThread.Start();
trayThread.Join();
Check(trayError is null, $"repeated tray menu opening: {trayError}");

var settingsPath = Path.Combine(AppContext.BaseDirectory, "settings-smoke", "settings.json");
var settingsStore = new SettingsStore(settingsPath);
settingsStore.Save(new UserSettings
{
    WindowLeft = -320,
    WindowTop = 140,
    Scale = 1.4,
    SelectedCharacter = "fixture-orbit",
    AlwaysOnTop = false
});
var persisted = settingsStore.Load();
Check(persisted.WindowLeft == -320 && persisted.WindowTop == 140 && persisted.Scale == 1.4
    && persisted.SelectedCharacter == "fixture-orbit" && !persisted.AlwaysOnTop,
    "user settings persistence");

// Loader fixtures are generated under build output, never in either source character pack.
var fixture = Path.Combine(AppContext.BaseDirectory, "loader-fixture");
Directory.CreateDirectory(fixture);
File.WriteAllBytes(Path.Combine(fixture, "safe.png"), Convert.FromBase64String(
    "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/lS0AAAAASUVORK5CYII="));
string Manifest(int version, string defaultId, string nextId = "") => $$"""
    {"schemaVersion":{{version}},"id":"fixture","defaultActionId":"{{defaultId}}",
     "actions":[{"id":"safe","loop":true,"frames":[{"path":"safe.png","durationMs":100}],"nextActionId":"{{nextId}}"},
                {"id":"broken","frames":[{"path":"missing.png","durationMs":100}]}]}
    """;
File.WriteAllText(Path.Combine(fixture, "character.json"), Manifest(1, "safe"));
var withBrokenOptional = CharacterLoader.Load(fixture);
Check(withBrokenOptional.Character.ActionMap.Count == 1 && withBrokenOptional.Warnings.Count > 0,
    "skip bad optional resource");
File.WriteAllText(Path.Combine(fixture, "character.json"), Manifest(1, "safe", "missing"));
var withBrokenNext = CharacterLoader.Load(fixture);
Check(withBrokenNext.Character.ActionMap["safe"].NextActionId is null
    && withBrokenNext.Warnings.Any(w => w.Contains("nextActionId")), "invalid next action fallback");
File.WriteAllText(Path.Combine(fixture, "character.json"),
    Manifest(1, "safe").Replace("\"id\":\"broken\"", "\"id\":\"broken\",\"viewportWidth\":200")
        .Replace("missing.png", "safe.png"));
Check(CharacterLoader.Load(fixture).Warnings.Any(w => w.Contains("viewport")),
    "incomplete optional viewport is skipped");
File.WriteAllText(Path.Combine(fixture, "character.json"), Manifest(1, "broken"));
ExpectInvalid(() => CharacterLoader.Load(fixture), "reject bad default");
File.WriteAllText(Path.Combine(fixture, "character.json"), Manifest(2, "safe"));
ExpectInvalid(() => CharacterLoader.Load(fixture), "reject unsupported schema");

Console.WriteLine("PASS: loader, approved character and generated fixture, gestures, movement, repeated tray menu and exit");

{
    var releaseFixture = Path.Combine(AppContext.BaseDirectory, "release-fixture", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(releaseFixture);
    var pathA = new RuntimePaths(new StartupOptions(null, releaseFixture, null), Path.Combine(releaseFixture, "program-a"));
    var pathB = new RuntimePaths(new StartupOptions(null, releaseFixture, null), Path.Combine(releaseFixture, "program-b"));
    Check(pathA.SettingsPath == pathB.SettingsPath && pathA.UserCharacterRoot == pathB.UserCharacterRoot,
        "different release folders share only user data");
    var legacy = """{"WindowLeft":123,"WindowTop":234,"Scale":0.9,"SelectedCharacter":"fixture-orbit","AlwaysOnTop":false,"FuturePreference":{"enabled":true}}""";
    File.WriteAllText(pathA.SettingsPath, legacy);
    var storeA = new SettingsStore(pathA.SettingsPath);
    var saved = storeA.Load();
    Check(saved.SchemaVersion == 1 && saved.WindowLeft == 123 && saved.Scale == .9, "legacy unversioned settings load");
    storeA.Save(saved);
    var fromB = new SettingsStore(pathB.SettingsPath).Load();
    Check(fromB.WindowTop == 234 && fromB.SelectedCharacter == "fixture-orbit" && !fromB.AlwaysOnTop
        && fromB.AdditionalFields!["FuturePreference"].GetProperty("enabled").GetBoolean(),
        "update retains settings and unknown future fields");
    var brokenPath = Path.Combine(releaseFixture, "broken.json");
    File.WriteAllText(brokenPath, "{broken");
    var broken = new SettingsStore(brokenPath);
    broken.Load();
    broken.Save(new UserSettings());
    Check(File.ReadAllText(brokenPath) == "{broken" && broken.Warning is not null, "broken settings never overwritten");
    var futurePath = Path.Combine(releaseFixture, "future.json");
    File.WriteAllText(futurePath, """{"SchemaVersion":99,"Scale":2}""");
    var future = new SettingsStore(futurePath);
    Check(future.Load().Scale == 1, "unsupported settings use defaults");
    future.Save(new UserSettings());
    Check(File.ReadAllText(futurePath).Contains("99"), "unsupported settings preserved");

    using (var first = new SingleInstanceGuard(releaseFixture))
    using (var duplicate = new SingleInstanceGuard(releaseFixture))
    using (var separate = new SingleInstanceGuard(Path.Combine(releaseFixture, "isolated")))
    using (var signaled = new ManualResetEventSlim())
    {
        Check(first.IsPrimary && !duplicate.IsPrimary && separate.IsPrimary, "one instance per data directory");
        first.Listen(() => signaled.Set());
        duplicate.NotifyPrimary();
        Check(signaled.Wait(5000), "duplicate launch notifies running instance");
    }
    using (var restarted = new SingleInstanceGuard(releaseFixture))
        Check(restarted.IsPrimary, "instance can restart after exit");

    Directory.CreateDirectory(pathA.UserCharacterRoot);
    var userPack = Path.Combine(pathA.UserCharacterRoot, "different-actions");
    Directory.CreateDirectory(userPack);
    File.Copy(Path.Combine(fixture, "safe.png"), Path.Combine(userPack, "safe.png"));
    File.WriteAllText(Path.Combine(userPack, "character.json"), Manifest(1, "safe"));
    var duplicatePack = Path.Combine(pathA.UserCharacterRoot, "ellen-flat2d");
    Directory.CreateDirectory(duplicatePack);
    File.WriteAllText(Path.Combine(duplicatePack, "character.json"), "{}");
    var tooNew = Path.Combine(pathA.UserCharacterRoot, "needs-future-version");
    Directory.CreateDirectory(tooNew);
    File.WriteAllText(Path.Combine(tooNew, "character.json"), Manifest(1, "safe")
        .Replace("\"schemaVersion\":1", "\"schemaVersion\":1,\"minimumAppVersion\":\"99.0.0\""));
    var catalog = CharacterCatalog.Load(root, pathA.UserCharacterRoot);
    Check(catalog.Characters.ContainsKey("different-actions") && catalog.Characters.Count == bundledCatalog.Characters.Count + 1,
        "external user role loads without replacing bundled roles");
    Check(catalog.Characters["ellen-flat2d"].Actions.Count == 11 && catalog.Warnings.Count >= 2,
        "duplicate and incompatible user packs skipped with feedback");
    var log = new DiagnosticLog(pathA.LogDirectory);
    log.Write("WARN", "smoke warning");
    Check(File.ReadAllText(log.FilePath).Contains(AppInfo.Version), "local diagnostic contains release version");
    Check(StartupOptions.Parse(["--data-dir", releaseFixture, "--character", "different-actions"]).Character == "different-actions",
        "composable isolated startup options");
    Console.WriteLine("PASS: release identity, single instance, update persistence, preserved invalid settings and user character catalog");
}

Console.WriteLine("PASS: approved 2D actions, sleep, wake, drag and settling");

ToolsChecks.Run(Check);

static void Check(bool condition, string name)
{
    if (!condition) throw new Exception($"FAIL: {name}");
}

static void ExpectInvalid(Action action, string name)
{
    try { action(); }
    catch (InvalidDataException) { return; }
    throw new Exception($"FAIL: {name}");
}
