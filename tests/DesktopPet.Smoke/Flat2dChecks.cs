using DesktopPet;
using DesktopPet.Behavior;
using DesktopPet.Character;

internal static class Flat2dChecks
{
    internal static void RunInstalledPack(Action<bool, string> check, string directory)
    {
        var loaded = CharacterLoader.Load(directory);
        var role = loaded.Character;
        check(loaded.Warnings.Count == 0 && role.ActionMap.Count == 11,
            "installed original flat2d has eleven playable actions and no warnings");
        check(role.Actions.Sum(a => a.Frames.Count) == 540 && role.CanvasWidth == 308
            && role.CanvasHeight == 328 && role.AnchorX == 154 && role.AnchorY == 315,
            "installed original flat2d retains all frames and logical geometry");
        var runner = new ActionRunner(role, new Random(37)) { AutomaticActionsEnabled = false };
        check(runner.Current.Id == "idle", "installed flat2d starts in idle");
        runner.Trigger("drag_start");
        var initialFrame = runner.CurrentFramePath;
        runner.Advance(TimeSpan.FromMilliseconds(100));
        check(runner.Current.Id == "drag_hold" && runner.IsDragging && runner.CurrentFramePath != initialFrame,
            "installed flat2d animates while dragged");
        runner.Trigger("drag_release");
        check(runner.Current.Id == "put_down", "installed flat2d release selects sitting and standing sequence");
        runner.Advance(TimeSpan.FromMilliseconds(3001));
        check(runner.Current.Id == "idle", "installed flat2d settles back to idle after three seconds");
        runner.PlayAction("sleep_enter");
        runner.Advance(TimeSpan.FromMilliseconds(5001));
        check(runner.Current.Id == "sleep_idle", "installed flat2d enters sleeping loop");
        runner.PlayAction("wave");
        check(runner.Current.Id == "wake", "installed flat2d wakes before a requested response");
        runner.Advance(TimeSpan.FromMilliseconds(5001));
        check(runner.Current.Id == "wave", "installed flat2d plays queued response after waking");
        runner.Advance(TimeSpan.FromMilliseconds(3501));
        check(runner.Current.Id == "idle", "installed flat2d response ends at idle");
    }

    internal static void Run(Action<bool, string> check, string fixtureRoot)
    {
        var role = new CharacterDefinition { DefaultActionId = "idle", WakeActionId = "wake",
            AvoidRepeatedActions = true, RandomIntervalMs = 12000, SleepThresholdMs = 60000 };
        ActionDefinition Add(string id, bool loop = false, params string[] triggers)
        {
            var action = new ActionDefinition { Id = id, DisplayName = id, Category = "回应", Loop = loop,
                Priority = id == "idle" ? 0 : 10, Triggers = [.. triggers], NextActionId = loop ? null : "idle",
                CooldownMs = 8000, InterruptibleBy = ["random", "idle_timeout", "click", "double_click", "drag_start"],
                Frames = [new() { FullPath = id + "-a", DurationMs = 100 }, new() { FullPath = id + "-b", DurationMs = 100 }] };
            role.Actions.Add(action); role.ActionMap.Add(id, action); return action;
        }
        Add("idle", true); Add("wave", false, "click", "double_click", "random");
        Add("happy", false, "click", "random"); Add("eat");
        Add("wake"); Add("sleep_enter", false, "idle_timeout").NextActionId = "sleep_idle";
        Add("sleep_idle", true).ShowSleepIndicator = true;
        Add("drag_hold", true, "drag_start"); Add("put_down", false, "drag_release");
        ActionRunner New() => new(role, new Random(37));
        var r = New();
        check(r.Trigger("click"), "flat2d click chooses response");
        var first = r.Current.Id; r.Advance(TimeSpan.FromMilliseconds(210)); r.Trigger("click");
        check(r.Current.Id != first, "flat2d click avoids repeat across responses");
        r = New(); r.PlayAction("sleep_idle");
        check(r.Trigger("click") && r.Current.Id == "wake", "flat2d sleep click wakes before response");
        r.PlayAction("wave"); r.PlayAction("eat"); r.Advance(TimeSpan.FromMilliseconds(210));
        check(r.Current.Id == "eat", "flat2d wake keeps only latest queued input");
        r.Advance(TimeSpan.FromMilliseconds(210));
        check(r.Current.Id == "idle", "flat2d queued action returns to idle without backlog");
        r.PlayAction("wave"); r.PlayAction("eat"); r.PlayAction("happy");
        r.Advance(TimeSpan.FromMilliseconds(210));
        check(r.Current.Id == "happy", "flat2d manual sequence replaces pending action");
        r.Trigger("drag_start");
        check(r.Current.Id == "drag_hold" && !r.PlayAction("eat"), "flat2d drag preempts and rejects point playback");
        r.Advance(TimeSpan.FromMilliseconds(110));
        check(r.CurrentFramePath == "drag_hold-b", "flat2d suspended pose animates during drag");
        r.Trigger("drag_release");
        check(r.Current.Id == "put_down", "flat2d release plays settling animation");
        r.Advance(TimeSpan.FromMilliseconds(210));
        check(r.Current.Id == "idle", "flat2d drag clears queued interactions");
        r = New(); r.AutomaticActionsPaused = true; r.Advance(TimeSpan.FromSeconds(70));
        check(r.Current.Id == "idle", "flat2d open menu pauses random and sleep");
        r.AutomaticActionsPaused = false; r.AutomaticActionsEnabled = false;
        r.Advance(TimeSpan.FromSeconds(12));
        check(r.Current.Id == "idle", "flat2d automatic toggle disables timed random");
        check(r.PlayRandomAction(), "flat2d manual random remains available when automatic is off");
        r = New(); r.Advance(TimeSpan.FromSeconds(60));
        check(r.Current.Id == "sleep_enter", "flat2d inactivity enters sleep");
        r.Advance(TimeSpan.FromMilliseconds(210));
        check(r.Current.Id == "sleep_idle", "flat2d entry transitions to stable sleeping loop");
        r.Advance(TimeSpan.FromSeconds(24));
        check(r.Current.Id == "sleep_idle", "flat2d random cannot break sleep");
        check(!r.PlayAction("missing"), "flat2d missing action returns failure");
        r = New(); r.PlayAction("wave"); r.Advance(TimeSpan.FromMilliseconds(210));
        check(r.PlayAction("wave") && r.Current.Id == "wave", "flat2d manual playback bypasses cooldown");
        role.ActionMap["happy"].Weight = 0;
        r = New(); r.Trigger("click"); r.Advance(TimeSpan.FromMilliseconds(210)); r.Trigger("click");
        check(r.Current.Id == "wave", "flat2d single eligible candidate may repeat");
        var path = Path.Combine(fixtureRoot, "flat2d-settings.json");
        File.WriteAllText(path, "{\"SchemaVersion\":1,\"UnknownFutureField\":42,\"AutomaticActions\":{\"ellen-flat2d\":false}}");
        var store = new SettingsStore(path); var settings = store.Load(); store.Save(settings);
        var loaded = store.Load();
        check(!loaded.AutomaticActions["ellen-flat2d"] && loaded.SelectedCharacter == "ellen-flat2d"
            && loaded.AdditionalFields!.ContainsKey("UnknownFutureField"), "flat2d toggle persists without losing legacy or unknown settings");
        var fresh = new SettingsStore(Path.Combine(fixtureRoot, Guid.NewGuid() + ".json")).Load();
        check(fresh.SelectedCharacter == "ellen-flat2d", "fresh installation defaults to approved 2D character");
        var packs = new Dictionary<string, CharacterDefinition> { ["ellen-flat2d"] = role, ["fixture-other"] = role };
        check(CharacterCatalog.ResolveSelection(packs, "missing") == "ellen-flat2d",
            "missing saved character falls back to approved 2D when additional characters exist");
        check(CharacterCatalog.ResolveSelection(packs, "fixture-other") == "fixture-other",
            "source build preserves an explicitly selected available character");
        packs.Remove("fixture-other");
        check(CharacterCatalog.ResolveSelection(packs, "fixture-other") == "ellen-flat2d",
            "2D-only portable package handles unavailable saved selection");
    }
}
