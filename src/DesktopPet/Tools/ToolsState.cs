namespace DesktopPet.Tools;

public enum RepeatRule { Once, Daily, Weekly }
public enum FocusStage { Focus, Break }

public sealed class Reminder
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = "";
    public DateTime ScheduledLocal { get; set; }
    public RepeatRule Repeat { get; set; }
    public DateTime? LastOccurrence { get; set; }
    public DateTime? PendingOccurrence { get; set; }
    public DateTime? SnoozeUntil { get; set; }
    public bool Completed { get; set; }
}

public sealed class FocusState
{
    public int FocusMinutes { get; set; } = 25;
    public int BreakMinutes { get; set; } = 5;
    public FocusStage Stage { get; set; }
    public double RemainingSeconds { get; set; } = 25 * 60;
    public bool Running { get; set; }
    public bool AwaitingNext { get; set; }
}

public sealed class ToolsState
{
    public int SchemaVersion { get; set; } = 1;
    public List<Reminder> Reminders { get; set; } = [];
    public FocusState Focus { get; set; } = new();
    public bool Quiet { get; set; }
    public bool SoundEnabled { get; set; }
    public bool AutoHideBubble { get; set; }
    public int BubbleSeconds { get; set; } = 20;
    public bool WakeOnImportant { get; set; } = true;
    public bool ShowTimerCard { get; set; }
}

public enum NoticeKind { Important, Feedback, Error }
public sealed record ToolNotice(string Text, Guid? ReminderId = null, NoticeKind Kind = NoticeKind.Important);
