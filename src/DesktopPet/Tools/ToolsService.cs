namespace DesktopPet.Tools;

public sealed class ToolsService
{
    private readonly ToolsStore _store;
    private double _checkpointSeconds;
    public ToolsState State { get; }
    public bool Available => _store.Available;
    public string? Error => _store.Error;
    public int Revision { get; private set; }
    public event Action<ToolNotice>? Notice;
    public event Action? Changed;
    public event Action<ToolNotice>? Feedback;
    public void Confirm(string text, bool error = false) => Feedback?.Invoke(new ToolNotice(Available ? text : Error!, Kind: error || !Available ? NoticeKind.Error : NoticeKind.Feedback));
    public void SetPresentation(bool autoHide, int seconds, bool wake, bool timerCard)
    {
        if (!Available) return;
        if (seconds is not (10 or 20 or 30 or 60)) throw new ArgumentException("气泡时长请选择 10、20、30 或 60 秒。");
        State.AutoHideBubble = autoHide; State.BubbleSeconds = seconds;
        State.WakeOnImportant = wake; State.ShowTimerCard = timerCard;
        if (Persist()) Confirm("工具偏好已保存。");
    }
    public int PendingCount => State.Reminders.Count(r => r.PendingOccurrence is not null);

    public ToolsService(ToolsStore store)
    {
        _store = store;
        State = store.Load();
    }

    public static DateTime? LatestOccurrence(Reminder reminder, DateTime now)
    {
        if (reminder.Completed || reminder.ScheduledLocal > now) return null;
        var start = reminder.ScheduledLocal;
        if (reminder.Repeat == RepeatRule.Once) return start;
        var days = reminder.Repeat == RepeatRule.Daily ? 1 : 7;
        var intervals = (now.Ticks - start.Ticks) / TimeSpan.FromDays(days).Ticks;
        return start.AddDays(intervals * days);
    }

    public DateTime? NextOccurrence(Reminder reminder, DateTime now)
    {
        if (reminder.Completed) return null;
        if (reminder.SnoozeUntil is DateTime snooze) return snooze;
        if (reminder.ScheduledLocal > now) return reminder.ScheduledLocal;
        var latest = LatestOccurrence(reminder, now);
        if (latest is null) return null;
        if (reminder.LastOccurrence is null || latest > reminder.LastOccurrence) return latest;
        if (reminder.Repeat == RepeatRule.Once) return null;
        return latest.Value.AddDays(reminder.Repeat == RepeatRule.Daily ? 1 : 7);
    }

    public void Tick(DateTime now, TimeSpan elapsed, bool catchUp = false)
    {
        if (!Available) return;
        List<ToolNotice> notices = [];
        var changed = false;
        foreach (var reminder in State.Reminders)
        {
            var latest = LatestOccurrence(reminder, now);
            if (latest is not null && (reminder.LastOccurrence is null || latest > reminder.LastOccurrence))
            {
                reminder.LastOccurrence = reminder.PendingOccurrence = latest;
                reminder.SnoozeUntil = null;
                changed = true;
                notices.Add(new ToolNotice(reminder.Title, reminder.Id));
            }
            else if (reminder.SnoozeUntil is DateTime snooze && now >= snooze)
            {
                reminder.SnoozeUntil = null;
                changed = true;
                notices.Add(new ToolNotice(reminder.Title, reminder.Id));
            }
        }
        var focus = State.Focus;
        if (focus.Running)
        {
            focus.RemainingSeconds = Math.Max(0, focus.RemainingSeconds - Math.Max(0, elapsed.TotalSeconds));
            _checkpointSeconds += Math.Max(0, elapsed.TotalSeconds);
            if (focus.RemainingSeconds == 0)
            {
                focus.Running = false;
                focus.AwaitingNext = true;
                changed = true;
                notices.Add(new ToolNotice(focus.Stage == FocusStage.Focus ? "专注结束，休息一下吧。" : "休息结束，可以开始下一轮专注。"));
            }
        }
        if (changed || _checkpointSeconds >= 30)
        {
            if (!Persist()) return;
        }
        if (catchUp)
        {
            if (PendingCount > 0 || focus.AwaitingNext)
                Notice?.Invoke(new ToolNotice($"有 {PendingCount} 条待处理提醒" + (focus.AwaitingNext ? "，计时阶段已结束。" : "，点击查看。")));
        }
        else foreach (var notice in notices) Notice?.Invoke(notice);
    }

    public void Upsert(Guid? id, string title, DateTime scheduledLocal, RepeatRule repeat)
    {
        if (!Available) return;
        title = title.Trim();
        if (title.Length is < 1 or > 200 || scheduledLocal == default || !Enum.IsDefined(repeat))
            throw new ArgumentException("标题须为 1–200 字，日期和重复方式须有效。");
        var reminder = id is Guid value ? State.Reminders.Single(r => r.Id == value) : new Reminder();
        if (id is null) State.Reminders.Add(reminder);
        var scheduleChanged = id is null || reminder.ScheduledLocal != scheduledLocal || reminder.Repeat != repeat;
        reminder.Title = title;
        reminder.ScheduledLocal = DateTime.SpecifyKind(scheduledLocal, DateTimeKind.Unspecified);
        reminder.Repeat = repeat;
        if (scheduleChanged)
        {
            reminder.Completed = false;
            reminder.LastOccurrence = reminder.PendingOccurrence = reminder.SnoozeUntil = null;
        }
        Persist();
    }

    public void Delete(Guid id)
    {
        if (!Available) return;
        State.Reminders.RemoveAll(r => r.Id == id);
        Persist();
    }

    public void Complete(Guid id)
    {
        if (!Available) return;
        var reminder = State.Reminders.FirstOrDefault(r => r.Id == id);
        if (reminder?.PendingOccurrence is null) return;
        reminder.PendingOccurrence = reminder.SnoozeUntil = null;
        if (reminder.Repeat == RepeatRule.Once) reminder.Completed = true;
        Persist();
    }

    public void Snooze(Guid id, DateTime now)
    {
        if (!Available) return;
        var reminder = State.Reminders.FirstOrDefault(r => r.Id == id);
        if (reminder?.PendingOccurrence is null) return;
        reminder.SnoozeUntil = now.AddMinutes(10);
        Persist();
    }

    public void SetQuiet(bool quiet)
    {
        if (!Available || State.Quiet == quiet) return;
        State.Quiet = quiet;
        Persist();
    }

    public void SetSound(bool enabled)
    {
        if (!Available) return;
        State.SoundEnabled = enabled;
        Persist();
    }

    public void ConfigureFocus(int focusMinutes, int breakMinutes)
    {
        if (!Available) return;
        if (focusMinutes is < 1 or > 240 || breakMinutes is < 1 or > 240)
            throw new ArgumentException("专注和休息时长须为 1–240 分钟。");
        if (State.Focus.Running || State.Focus.AwaitingNext)
            throw new InvalidOperationException("请先结束当前计时再修改时长。");
        State.Focus.FocusMinutes = focusMinutes;
        State.Focus.BreakMinutes = breakMinutes;
        State.Focus.RemainingSeconds = (State.Focus.Stage == FocusStage.Focus ? focusMinutes : breakMinutes) * 60;
        Persist();
    }

    public void StartFocus()
    {
        if (!Available) return;
        var focus = State.Focus;
        if (focus.AwaitingNext)
        {
            focus.Stage = focus.Stage == FocusStage.Focus ? FocusStage.Break : FocusStage.Focus;
            focus.RemainingSeconds = (focus.Stage == FocusStage.Focus ? focus.FocusMinutes : focus.BreakMinutes) * 60;
            focus.AwaitingNext = false;
        }
        focus.Running = true;
        Persist();
    }

    public void PauseFocus()
    {
        if (!Available) return;
        State.Focus.Running = false;
        Persist();
    }

    public void StopFocus()
    {
        if (!Available) return;
        State.Focus.Stage = FocusStage.Focus;
        State.Focus.Running = State.Focus.AwaitingNext = false;
        State.Focus.RemainingSeconds = State.Focus.FocusMinutes * 60;
        Persist();
    }

    public bool Persist()
    {
        _checkpointSeconds = 0;
        var saved = _store.Save(State);
        if (!saved) State.Focus.Running = false;
        Revision++;
        Changed?.Invoke();
        return saved;
    }
}
