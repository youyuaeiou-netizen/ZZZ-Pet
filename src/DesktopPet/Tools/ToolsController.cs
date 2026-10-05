using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using W = System.Windows.Controls;

namespace DesktopPet.Tools;

public sealed class ToolsController : IDisposable
{
    private readonly Window _pet;
    private readonly ToolsService _service;
    private readonly NoticeQueue _notices;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private TimeSpan _lastTick;
    private ToolsWindow? _panel;
    private NoticeWindow? _popup;
    private TimeSpan _popupOpened;
    private bool _suspended;
    private bool _resumeFocus;
    private bool _disposed;
    private bool _errorShown;
    private CompanionWindow? _companion;
    private readonly Action<bool>? _attention;
    private TimeSpan _statusUntil;
    private bool _petHidden;
    public bool Quiet => _service.State.Quiet;
    public bool HasBubble => _popup?.IsVisible == true;

    public ToolsController(Window pet, string statePath, Action<bool>? attention = null)
    {
        _pet = pet;
        _attention = attention;
        _service = new ToolsService(new ToolsStore(statePath));
        _notices = new NoticeQueue(Quiet);
        _service.Notice += _notices.Enqueue;
        _service.Changed += OnChanged;
        _service.Feedback += OnFeedback;
        _timer.Tick += (_, _) => Tick();
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
        pet.Loaded += OnLoaded;
        pet.LocationChanged += OnPetMoved; pet.SizeChanged += OnPetSizeChanged;
        var menu = new W.ContextMenu();
        PixelFrame.Apply(menu);
        foreach (var (label, tab) in new[] { ("信息卡片", 0), ("日程提醒", 1), ("专注计时", 2) })
        {
            var item = new W.MenuItem { Header = label };
            item.Click += (_, _) => ShowPanel(tab);
            menu.Items.Add(item);
        }
        var quiet = new W.MenuItem { Header = "安静模式", IsCheckable = true };
        quiet.Click += (_, _) => ToggleQuiet();
        menu.Items.Add(quiet);
        menu.Opened += (_, _) => { quiet.IsChecked = Quiet; quiet.IsEnabled = _service.Available; };
        pet.ContextMenu = menu;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _service.Tick(DateTime.Now, TimeSpan.Zero, catchUp: true);
        QueueError();
        _lastTick = _clock.Elapsed;
        _timer.Start();
    }

    public void ShowPanel(int tab = 0)
    {
        if (_disposed) return;
        if (_panel is null)
        {
            _panel = new ToolsWindow(_service) { Owner = _petHidden ? null : _pet };
            _panel.Closed += (_, _) => _panel = null;
            _panel.Show();
        }
        _panel.SelectTab(tab);
        if (_panel.WindowState == WindowState.Minimized) _panel.WindowState = WindowState.Normal;
        WindowPlacement.Near(_panel, _pet);
        _panel.Activate();
    }

    public void ToggleQuiet() { _service.SetQuiet(!Quiet); if (Quiet) _service.Confirm("安静模式已开启。"); }

    public void SetPetHidden(bool hidden)
    {
        _petHidden = hidden;
        if (_panel is not null) _panel.Owner = hidden ? null : _pet;
        if (_popup is not null) _popup.Owner = hidden ? null : _pet;
        if (_companion is not null) _companion.Owner = hidden ? null : _pet;
    }

    private void OnChanged()
    {
        var wasQuiet = _notices.Quiet;
        _notices.SetQuiet(Quiet, _service.PendingCount, _service.State.Focus.AwaitingNext);
        _notices.RemoveStale(IsNoticeValid);
        if ((!wasQuiet && Quiet) || !_service.Available) _popup?.Close();
        QueueError();
        if (_popup?.Important == true)
        {
            if (!HasReady) _popup.Close(); else _popup.Refresh();
        }
        RefreshCompanion();
    }

    private void Tick()
    {
        var now = _clock.Elapsed;
        var elapsed = now - _lastTick;
        _lastTick = now;
        if (_suspended) return;
        // A long dispatcher stall is treated as catch-up, not a burst of overdue popups.
        if (elapsed.TotalSeconds > 5) _notices.Clear();
        _service.Tick(DateTime.Now, elapsed, catchUp: elapsed.TotalSeconds > 5);
        _companion?.Refresh();
        var notice = _notices.Take();
        while (notice is not null && !IsNoticeValid(notice)) notice = _notices.Take();
        // Drain this scheduler batch: one bubble and one optional character trigger.
        while (_notices.Take() is { } extra) { if ((notice is null || extra.Kind == NoticeKind.Error) && IsNoticeValid(extra)) notice = extra; }
        if (notice is not null && !Quiet)
        {
            if (notice.Kind != NoticeKind.Important) OnFeedback(notice);
            else if (_popup?.Important == true) { _popup.Refresh(); _popupOpened = now; }
            else { _popup?.Close(); ShowNotice(notice); }
            if (notice.Kind == NoticeKind.Important && _service.State.WakeOnImportant && HasReady) _attention?.Invoke(true);
            if (notice.Kind == NoticeKind.Important && _service.State.SoundEnabled) System.Media.SystemSounds.Asterisk.Play();
        }
        if (_popup is not null)
        {
            if (now >= _statusUntil) _popup.ClearStatus();
            var seconds = _popup.Notice.Kind == NoticeKind.Feedback ? 4 : _service.State.AutoHideBubble ? _service.State.BubbleSeconds : double.PositiveInfinity;
            if (!double.IsPositiveInfinity(seconds) && (now - _popupOpened).TotalSeconds >= seconds) _popup.Close();
            return;
        }
    }

    private bool HasReady => _service.State.Focus.AwaitingNext || _service.State.Reminders.Any(r => r.PendingOccurrence is not null && r.SnoozeUntil is null);
    private void ShowNotice(ToolNotice notice)
    {
        _popup = new NoticeWindow(notice, _service, () => ShowPanel(notice.ReminderId is null ? 0 : 1)) { Owner = _petHidden ? null : _pet };
        _popup.Closed += (_, _) => { _popup = null; _attention?.Invoke(false); RefreshCompanion(); };
        _popup.Show();
        PositionAttached();
        _popupOpened = _clock.Elapsed;
    }
    private void OnFeedback(ToolNotice notice)
    {
        if (_disposed) return;
        if (_popup?.Important == true) { _popup.Status(notice.Text); _statusUntil = _clock.Elapsed + TimeSpan.FromSeconds(4); }
        else { _popup?.Close(); ShowNotice(notice); }
    }
    private void ReviewPending()
    {
        if (!HasReady) { ShowPanel(1); return; }
        _popup?.Close(); ShowNotice(new ToolNotice("待处理事项"));
    }
    private void RefreshCompanion()
    {
        if (_disposed) return;
        if (_service.Available && (_service.PendingCount > 0 || _service.State.Focus.AwaitingNext || _service.State.ShowTimerCard))
        {
            if (_companion is null) { _companion = new CompanionWindow(_service,ReviewPending) { Owner = _petHidden ? null : _pet }; _companion.Show(); }
            _companion.Refresh(); PositionAttached();
        }
        else { _companion?.Close(); _companion = null; }
    }
    private void PositionAttached()
    {
        if (_popup is not null) _popup.SetSide(WindowPlacement.Near(_popup,_pet));
        if (_companion is not null) WindowPlacement.Near(_companion, _pet, lower:true, avoid:_popup);
    }
    private void OnPetMoved(object? sender, EventArgs e) => PositionAttached();
    private void OnPetSizeChanged(object sender, SizeChangedEventArgs e) => PositionAttached();

    private void QueueError()
    {
        if (_service.Available || _errorShown) return;
        _errorShown = true;
        _notices.Enqueue(new ToolNotice(_service.Error!, Kind: NoticeKind.Error));
    }

    private bool IsNoticeValid(ToolNotice notice) => notice.ReminderId is not Guid id ||
        _service.State.Reminders.Any(r => r.Id == id && r.PendingOccurrence is not null && r.SnoozeUntil is null);

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (_disposed || _pet.Dispatcher.HasShutdownStarted) return;
        // Suspend is synchronous so the remaining time is saved before Windows sleeps.
        if (e.Mode == PowerModes.Suspend) _pet.Dispatcher.Invoke(Suspend);
        else if (e.Mode == PowerModes.Resume) _pet.Dispatcher.BeginInvoke(Resume);
    }

    private void Suspend()
    {
        if (_disposed || _suspended) return;
        var now = _clock.Elapsed;
        _service.Tick(DateTime.Now, now - _lastTick);
        _lastTick = now;
        _suspended = true;
        _resumeFocus = _service.State.Focus.Running;
        _service.PauseFocus();
        _popup?.Close();
    }

    private void Resume()
    {
        if (_disposed || !_suspended) return;
        _lastTick = _clock.Elapsed;
        _suspended = false;
        _notices.Clear();
        _service.Tick(DateTime.Now, TimeSpan.Zero, catchUp: true);
        if (_resumeFocus) _service.StartFocus();
        _resumeFocus = false;
    }

    private void OnDisplayChanged(object? sender, EventArgs e)
    {
        if (_disposed || _pet.Dispatcher.HasShutdownStarted) return;
        _pet.Dispatcher.BeginInvoke(() =>
        {
            if (_panel is not null) WindowPlacement.Near(_panel, _pet);
            PositionAttached();
        });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
        _pet.Loaded -= OnLoaded;
        _pet.LocationChanged -= OnPetMoved; _pet.SizeChanged -= OnPetSizeChanged;
        _service.Changed -= OnChanged;
        _service.Feedback -= OnFeedback;
        _service.Notice -= _notices.Enqueue;
        if (!_suspended && _service.State.Focus.Running)
            _service.Tick(DateTime.Now, _clock.Elapsed - _lastTick);
        _service.PauseFocus();
        _popup?.Close(); _companion?.Close(); _panel?.Close();
    }
}
