namespace DesktopPet.Tools;

// UI-independent policy: entering quiet mode drops queued popups, leaving emits one summary.
public sealed class NoticeQueue
{
    private readonly Queue<ToolNotice> _queue = new();
    public bool Quiet { get; private set; }
    public NoticeQueue(bool quiet) => Quiet = quiet;
    public void Enqueue(ToolNotice notice) { if (!Quiet) _queue.Enqueue(notice); }
    public ToolNotice? Take() => Quiet || _queue.Count == 0 ? null : _queue.Dequeue();
    public void Clear() => _queue.Clear();
    public void RemoveStale(Func<ToolNotice, bool> valid)
    {
        var kept = _queue.Where(valid).ToArray();
        _queue.Clear();
        foreach (var notice in kept) _queue.Enqueue(notice);
    }
    public void SetQuiet(bool quiet, int pending, bool focusEnded)
    {
        if (quiet == Quiet) return;
        Quiet = quiet;
        _queue.Clear();
        if (!quiet)
            _queue.Enqueue(new ToolNotice($"安静模式已关闭：{pending} 条待处理提醒" + (focusEnded ? "，计时阶段已结束。" : "。")));
    }
}
