using System.Text.Json;
using DesktopPet;
using DesktopPet.Tools;

internal static class ToolsChecks
{
    public static void Run(Action<bool, string> check)
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "tools-fixture", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "tools-state.json");
        var service = new ToolsService(new ToolsStore(path));
        check(!service.State.AutoHideBubble && service.State.BubbleSeconds == 20 && service.State.WakeOnImportant && !service.State.ShowTimerCard,
            "new presentation defaults");
        List<ToolNotice> notices = [];
        service.Notice += notices.Add;
        var now = new DateTime(2026, 9, 30, 9, 0, 0);
        service.Upsert(null, "会议", now.AddMinutes(1), RepeatRule.Once);
        var once = service.State.Reminders.Single();
        service.Tick(now, TimeSpan.Zero);
        check(notices.Count == 0, "reminder not delivered early");
        service.Tick(now.AddMinutes(1), TimeSpan.Zero);
        check(notices.Count == 1 && service.PendingCount == 1, "once reminder due and pending");
        service.Tick(now.AddMinutes(2), TimeSpan.Zero);
        check(notices.Count == 1, "no duplicate tick notification");
        service.Upsert(once.Id, "会议改标题", once.ScheduledLocal, once.Repeat);
        service.Tick(now.AddMinutes(2), TimeSpan.Zero);
        check(notices.Count == 1 && service.PendingCount == 1, "title-only edit preserves occurrence and avoids re-notifying");
        var restart = new ToolsService(new ToolsStore(path));
        List<ToolNotice> restartedNotices = [];
        restart.Notice += restartedNotices.Add;
        restart.Tick(now.AddMinutes(2), TimeSpan.Zero);
        check(restartedNotices.Count == 0 && restart.PendingCount == 1, "persisted occurrence prevents repeat after restart");
        restart.Tick(now.AddMinutes(2), TimeSpan.Zero, true);
        check(restartedNotices.Count == 1 && restartedNotices[0].ReminderId is null, "startup uses one pending summary");
        service.Snooze(once.Id, now.AddMinutes(2));
        service.Tick(now.AddMinutes(11), TimeSpan.Zero);
        check(notices.Count == 1, "snooze waits full ten minutes");
        service.Tick(now.AddMinutes(12), TimeSpan.Zero);
        check(notices.Count == 2 && once.SnoozeUntil is null, "snooze re-delivers once");
        service.Complete(once.Id);
        service.Tick(now.AddDays(10), TimeSpan.Zero);
        check(once.Completed && service.PendingCount == 0 && notices.Count == 2, "complete ends one-time reminder");

        service.Upsert(null, "每日记录", now, RepeatRule.Daily);
        service.Upsert(null, "每周例会", now, RepeatRule.Weekly);
        var daily = service.State.Reminders.Single(r => r.Repeat == RepeatRule.Daily);
        var weekly = service.State.Reminders.Single(r => r.Repeat == RepeatRule.Weekly);
        notices.Clear();
        service.Tick(now.AddDays(24).AddHours(2), TimeSpan.Zero, true);
        check(service.PendingCount == 2 && daily.PendingOccurrence == now.AddDays(24) &&
            weekly.PendingOccurrence == now.AddDays(21), "missed repeats retain latest occurrence only");
        check(notices.Count == 1 && notices[0].ReminderId is null, "multiple missed reminders use one summary");
        service.Complete(daily.Id);
        check(!daily.Completed && daily.PendingOccurrence is null &&
            service.NextOccurrence(daily, now.AddDays(24).AddHours(3)) == now.AddDays(25), "repeat completion preserves future schedule");
        service.Tick(now.AddDays(25), TimeSpan.Zero);
        check(daily.PendingOccurrence == now.AddDays(25), "daily schedule continues after complete");
        service.Snooze(daily.Id, now.AddDays(25).AddHours(23).AddMinutes(55));
        service.Tick(now.AddDays(26), TimeSpan.Zero);
        check(daily.PendingOccurrence == now.AddDays(26) && daily.SnoozeUntil is null, "next repeat supersedes old snooze");
        service.Upsert(daily.Id, "改名", now.AddDays(30), RepeatRule.Once);
        check(daily.Title == "改名" && daily.PendingOccurrence is null, "editing replaces pending schedule");
        service.Delete(weekly.Id);
        check(service.State.Reminders.All(r => r.Id != weekly.Id), "deleted reminder cannot fire");

        service.ConfigureFocus(1, 2);
        service.StartFocus();
        service.Tick(now, TimeSpan.FromSeconds(20));
        service.PauseFocus();
        var focus = service.State.Focus;
        check(focus.RemainingSeconds == 40 && !focus.Running, "focus pause saves remaining time");
        service.Tick(now.AddHours(1), TimeSpan.FromHours(1));
        check(focus.RemainingSeconds == 40, "paused focus unaffected by sleep elapsed");
        service.StartFocus();
        var focusRestart = new ToolsService(new ToolsStore(path));
        check(!focusRestart.State.Focus.Running && focusRestart.State.Focus.RemainingSeconds == 40, "active session restarts paused");
        notices.Clear();
        service.Tick(now, TimeSpan.FromSeconds(45));
        check(focus.AwaitingNext && !focus.Running && focus.RemainingSeconds == 0 && notices.Count == 1,
            "focus ends once without automatic phase start");
        service.Tick(now, TimeSpan.FromMinutes(10));
        check(notices.Count == 1, "completed focus not notified twice");
        service.StartFocus();
        check(focus.Stage == FocusStage.Break && focus.Running && focus.RemainingSeconds == 120, "manually start break");
        service.Tick(now, TimeSpan.FromSeconds(120));
        service.StartFocus();
        check(focus.Stage == FocusStage.Focus && focus.RemainingSeconds == 60, "manually start next focus");
        service.StopFocus();
        check(!focus.Running && !focus.AwaitingNext && focus.Stage == FocusStage.Focus && focus.RemainingSeconds == 60,
            "stop resets focus");

        var queue = new NoticeQueue(false);
        queue.Enqueue(new ToolNotice("旧提示"));
        queue.SetQuiet(true, 2, false);
        queue.Enqueue(new ToolNotice("安静期间到期"));
        check(queue.Take() is null, "quiet mode clears queue and suppresses new notices");
        queue.SetQuiet(false, 2, true);
        check(queue.Take()?.Text.Contains("2 条") == true && queue.Take() is null, "quiet off emits exactly one summary");
        queue.Enqueue(new ToolNotice("取消", once.Id));
        queue.RemoveStale(n => n.ReminderId != once.Id);
        check(queue.Take() is null, "cancelled reminder removed from notification queue");
        service.SetQuiet(true); service.SetSound(true);
        var preferences = new ToolsService(new ToolsStore(path));
        check(preferences.State.Quiet && preferences.State.SoundEnabled, "quiet and sound preferences persist");
        service.SetPresentation(true, 30, false, true);
        preferences = new ToolsService(new ToolsStore(path));
        check(preferences.State.AutoHideBubble && preferences.State.BubbleSeconds == 30 && !preferences.State.WakeOnImportant && preferences.State.ShowTimerCard &&
            preferences.State.Quiet && preferences.State.SoundEnabled && preferences.State.Reminders.Count == service.State.Reminders.Count,
            "presentation preferences persist without resetting data");
        var legacyPath = Path.Combine(directory,"legacy-tools.json");
        File.WriteAllText(legacyPath,"{\"Quiet\":true,\"SoundEnabled\":true,\"Focus\":{\"RemainingSeconds\":127}}");
        var legacy = new ToolsStore(legacyPath).Load();
        check(legacy.WakeOnImportant && !legacy.AutoHideBubble && !legacy.ShowTimerCard && legacy.BubbleSeconds == 20 &&
            legacy.Focus.RemainingSeconds == 127 && legacy.Quiet && legacy.SoundEnabled,"missing presentation fields keep prior state with defaults");

        foreach (var invalid in new[] { "{broken", "{\"SchemaVersion\":99}", "{\"Reminders\":null}",
            "{\"Focus\":{\"FocusMinutes\":0}}", "{\"Reminders\":[{\"Title\":\"x\",\"Repeat\":77}]}" })
        {
            var invalidPath = Path.Combine(directory, Guid.NewGuid() + ".json");
            File.WriteAllText(invalidPath, invalid);
            var broken = new ToolsService(new ToolsStore(invalidPath));
            broken.StartFocus(); broken.Upsert(null, "不可写", now, RepeatRule.Once);
            check(!broken.Available && broken.Error is not null && File.ReadAllText(invalidPath) == invalid,
                "invalid data preserved and feature disabled");
        }
        var blockedPath = Path.Combine(directory, "blocked.json");
        var blocked = new ToolsService(new ToolsStore(blockedPath));
        Directory.CreateDirectory(blockedPath);
        blocked.StartFocus();
        check(!blocked.Available && !blocked.State.Focus.Running, "save failure disables timing safely");
        var paths = new RuntimePaths(new StartupOptions(null, directory, null));
        var preview = new RuntimePaths(new StartupOptions(directory, null, null));
        check(paths.ToolsStatePath == path && preview.ToolsStatePath == Path.Combine(directory, ".runtime", "tools-state.json"),
            "tool data follows isolated and preview data roots");
        var batch = new ToolsService(new ToolsStore(Path.Combine(directory, "batch.json")));
        var batchQueue = new NoticeQueue(false);
        batch.Notice += batchQueue.Enqueue;
        batch.Upsert(null, "同一时刻 A", now, RepeatRule.Once);
        batch.Upsert(null, "同一时刻 B", now, RepeatRule.Once);
        batch.ConfigureFocus(1, 1); batch.StartFocus();
        batch.Tick(now, TimeSpan.FromMinutes(1));
        check(batchQueue.Take()?.ReminderId is not null && batchQueue.Take()?.ReminderId is not null &&
            batchQueue.Take()?.Text.Contains("专注结束") == true && batchQueue.Take() is null,
            "simultaneous reminders and focus completion are queued sequentially");
        Console.WriteLine("PASS: reminders, recurrence, snooze, catch-up, focus phases, persistence, quiet queue and invalid tool data");
    }
}
