using System.Globalization;
using System.Windows;
using System.Windows.Threading;
using System.Windows.Markup;
using W = System.Windows.Controls;
using Media = System.Windows.Media;

namespace DesktopPet.Tools;

public sealed class ToolsWindow : Window
{
    private readonly ToolsService _service;
    private readonly DispatcherTimer _refresh = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly W.TextBlock _summary = Text("", 16);
    private readonly W.TextBlock _today = Text("", 12);
    private readonly W.TextBlock _pendingValue = Text("", 24);
    private readonly W.TextBlock _timerValue = Text("", 24);
    private readonly W.TextBlock _timerCaption = Text("专注", 12);
    private readonly W.TextBlock _focusStatus = Text("", 26);
    private readonly W.TextBlock _error = Text("");
    private readonly W.StackPanel _rows = new();
    private readonly W.TextBox _title = new() { MaxLength = 200 };
    private readonly W.DatePicker _date = new() { SelectedDate = DateTime.Now.AddHours(1).Date };
    private readonly W.TextBox _time = new() { Text = DateTime.Now.AddHours(1).ToString("HH:mm") };
    private readonly W.ComboBox _repeat = new() { ItemsSource = new[] { "一次性", "每天", "每周" }, SelectedIndex = 0 };
    private readonly W.TextBox _focusMinutes;
    private readonly W.TextBox _breakMinutes;
    private readonly W.CheckBox _quiet = new() { Content = "安静模式：暂停气泡与声音", Margin = new Thickness(0, 8, 0, 8) };
    private readonly W.CheckBox _sound = new() { Content = "启用提示音", Margin = new Thickness(0, 0, 0, 8) };
    private readonly W.CheckBox _autoHide = new() { Content = "重要气泡自动收起（否则保留到处理／收起）" };
    private readonly W.ComboBox _bubbleSeconds = new() { ItemsSource = new[] { 10, 20, 30, 60 }, SelectedItem = 20 };
    private readonly W.CheckBox _wake = new() { Content = "重要事项唤醒人物", IsChecked = true };
    private readonly W.CheckBox _timerCard = new() { Content = "显示人物旁的轻量计时卡片" };
    private readonly W.Button _start;
    private readonly W.Button _pause;
    private readonly W.TabControl _tabs = new();
    private Guid? _editing;
    private int _lastRevision = -1;
    private bool _updating;

    public ToolsWindow(ToolsService service)
    {
        _service = service;
        Title = "艾莲布 · 轻工具";
        Width = 520; Height = 720; MinWidth = 420; MinHeight = 400;
        ShowInTaskbar = false;
        Background = new Media.LinearGradientBrush(Brush("#FFF0F7").Color, Brush("#E9FAF3").Color, 135);
        PixelFrame.Apply(this);
        FontFamily = new Media.FontFamily("Microsoft YaHei UI");
        Language = XmlLanguage.GetLanguage("zh-CN");
        FontSize = 13;
        UseLayoutRounding = true;
        var root = new W.DockPanel();
        var header = new W.StackPanel();
        var heading = new W.StackPanel { Orientation = W.Orientation.Horizontal };
        var heart = new PixelHeart { Margin = new Thickness(0), HorizontalAlignment = System.Windows.HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        heading.Children.Add(new W.Border { Child = heart, Width = 48, Height = 48, CornerRadius = new CornerRadius(16),
            Background = Brush("#C9F4E7"), Margin = new Thickness(0, 0, 12, 0) });
        var titles = new W.StackPanel();
        var title = Text("艾莲布的陪伴小站", 21); title.FontWeight = FontWeights.SemiBold; title.Margin = new Thickness(0, 0, 0, 3);
        titles.Children.Add(title);
        heading.Children.Add(titles);
        header.Children.Add(heading);
        _error.Foreground = Media.Brushes.Firebrick;
        header.Children.Add(_error);
        W.DockPanel.SetDock(header, W.Dock.Top); root.Children.Add(header);
        _tabs.Margin = new Thickness(0, 16, 0, 0);

        var overview = new W.StackPanel { Margin = new Thickness(2) };
        var daily = new W.StackPanel(); daily.Children.Add(_today);
        var greeting = Text("今天也要闪闪发光 ✦", 18); greeting.FontWeight = FontWeights.SemiBold; daily.Children.Add(greeting);
        _summary.FontSize = 12; _summary.Foreground = Brush("#8C729D"); daily.Children.Add(_summary);
        var stats = new W.Grid(); stats.ColumnDefinitions.Add(new W.ColumnDefinition()); stats.ColumnDefinitions.Add(new W.ColumnDefinition());
        var pending = new W.StackPanel(); pending.Children.Add(Text("待处理", 12)); pending.Children.Add(_pendingValue);
        var timer = new W.StackPanel(); timer.Children.Add(_timerCaption); timer.Children.Add(_timerValue);
        var pendingCard = Card(pending, "#CCFFFFFF"); pendingCard.Margin = new Thickness(0, 8, 6, 0); stats.Children.Add(pendingCard);
        var timerCard = Card(timer, "#CCFFFFFF"); timerCard.Margin = new Thickness(6, 8, 0, 0); W.Grid.SetColumn(timerCard, 1); stats.Children.Add(timerCard);
        daily.Children.Add(stats);
        var dailyCard = Card(daily); dailyCard.Background = new Media.LinearGradientBrush(Brush("#FDE5F2").Color, Brush("#DFF8EF").Color, 25); overview.Children.Add(dailyCard);
        var sounds = new W.StackPanel(); sounds.Children.Add(Text("提示与声音", 15)); sounds.Children.Add(_quiet); sounds.Children.Add(_sound);
        overview.Children.Add(Card(sounds));
        var preferences = new W.StackPanel(); preferences.Children.Add(Text("气泡与陪伴偏好", 15)); preferences.Children.Add(_autoHide);
        AddField(preferences, "自动收起时长（秒）", _bubbleSeconds); preferences.Children.Add(_wake); preferences.Children.Add(_timerCard);
        overview.Children.Add(Card(preferences));
        var shortcuts = new W.WrapPanel(); shortcuts.Children.Add(Button("查看提醒", () => _tabs.SelectedIndex = 1));
        shortcuts.Children.Add(Button("开始专注", () => _tabs.SelectedIndex = 2)); overview.Children.Add(shortcuts);
        AddTab("信息", overview);

        var reminders = new W.StackPanel { Margin = new Thickness(2) };
        var editor = new W.StackPanel(); editor.Children.Add(Text("创建或编辑提醒", 16));
        AddField(editor, "标题", _title);
        var schedule = new W.Grid();
        schedule.ColumnDefinitions.Add(new W.ColumnDefinition());
        schedule.ColumnDefinitions.Add(new W.ColumnDefinition());
        var timeField = new W.StackPanel { Margin = new Thickness(0, 0, 6, 0) };
        AddField(timeField, "时间（HH:mm）", _time); schedule.Children.Add(timeField);
        var repeatField = new W.StackPanel { Margin = new Thickness(6, 0, 0, 0) };
        AddField(repeatField, "重复", _repeat); W.Grid.SetColumn(repeatField, 1); schedule.Children.Add(repeatField);
        AddField(editor, "日期", _date); editor.Children.Add(schedule);
        var editorButtons = new W.WrapPanel();
        editorButtons.Children.Add(Button("保存提醒", SaveReminder));
        editorButtons.Children.Add(Button("取消编辑", ResetEditor));
        editor.Children.Add(editorButtons); reminders.Children.Add(Card(editor));
        reminders.Children.Add(Text("提醒列表 · 待处理事项优先", 16));
        reminders.Children.Add(_rows);
        AddTab("提醒", reminders);

        var focus = new W.StackPanel { Margin = new Thickness(2) };
        var focusHero = new W.StackPanel(); focusHero.Children.Add(Text("给自己一点专注时间 ✦", 15));
        _focusStatus.FontSize = 30; _focusStatus.FontWeight = FontWeights.SemiBold; focusHero.Children.Add(_focusStatus);
        focus.Children.Add(Card(focusHero, "#E1F7EE"));
        var timing = new W.StackPanel(); timing.Children.Add(Text("专注节奏", 15));
        _focusMinutes = new W.TextBox { Text = service.State.Focus.FocusMinutes.ToString(CultureInfo.InvariantCulture) };
        _breakMinutes = new W.TextBox { Text = service.State.Focus.BreakMinutes.ToString(CultureInfo.InvariantCulture) };
        AddField(timing, "专注（1–240 分钟）", _focusMinutes);
        AddField(timing, "休息（1–240 分钟）", _breakMinutes);
        timing.Children.Add(Button("应用时长", () => Run(() =>
        {
            if (!int.TryParse(_focusMinutes.Text, out var work) || !int.TryParse(_breakMinutes.Text, out var rest))
                throw new ArgumentException("请输入整数分钟。");
            service.ConfigureFocus(work, rest);
            service.Confirm("计时时长已更新。");
        })));
        focus.Children.Add(Card(timing));
        var controls = new W.WrapPanel();
        _start = Button("开始", () => { service.StartFocus(); service.Confirm("计时已开始。"); });
        _pause = Button("暂停", () => { service.PauseFocus(); service.Confirm("计时已暂停。"); });
        controls.Children.Add(_start); controls.Children.Add(_pause);
        controls.Children.Add(Button("结束并重置", () => { service.StopFocus(); service.Confirm("计时已结束并重置。"); }));
        focus.Children.Add(controls);
        AddTab("专注", focus);
        root.Children.Add(_tabs); Content = new W.Border { Name = "ToolsSurface", Child = root, Margin = new Thickness(14), Padding = new Thickness(20),
            CornerRadius = new CornerRadius(22), Background = Brush("#F7FFFCF9"), BorderBrush = Brush("#E5D4ED"), BorderThickness = new Thickness(1) };
        _quiet.Click += (_, _) => { if (!_updating) service.SetQuiet(_quiet.IsChecked == true); };
        _sound.Click += (_, _) => { if (!_updating) service.SetSound(_sound.IsChecked == true); };
        void Preferences() { if (!_updating) service.SetPresentation(_autoHide.IsChecked == true,
            _bubbleSeconds.SelectedItem is int seconds ? seconds : 20, _wake.IsChecked == true, _timerCard.IsChecked == true); }
        _autoHide.Click += (_, _) => Preferences(); _wake.Click += (_, _) => Preferences();
        _timerCard.Click += (_, _) => Preferences(); _bubbleSeconds.SelectionChanged += (_, _) => Preferences();
        service.Changed += Refresh;
        _refresh.Tick += (_, _) => Refresh();
        Loaded += (_, _) => { Refresh(); _refresh.Start(); };
        Closed += (_, _) => { _refresh.Stop(); service.Changed -= Refresh; };
    }

    public void SelectTab(int index) { _tabs.SelectedIndex = index; }

    private void AddTab(string title, W.StackPanel content) => _tabs.Items.Add(new W.TabItem
    {
        Header = title,
        Content = new W.ScrollViewer { Content = content, FontWeight = FontWeights.Normal, VerticalScrollBarVisibility = W.ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = W.ScrollBarVisibility.Disabled }
    });

    internal static W.TextBlock Text(string value, double size = 13) => new()
    {
        Text = value, FontSize = size, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 8)
    };

    private static Media.SolidColorBrush Brush(string color) => new((Media.Color)Media.ColorConverter.ConvertFromString(color));
    private static W.Border Card(UIElement child, string background = "#FFFFFF") => new() { Child = child, Background = Brush(background),
        CornerRadius = new CornerRadius(16), Padding = new Thickness(16), BorderBrush = Brush("#EBDCEC"), BorderThickness = new Thickness(1),
        Margin = new Thickness(0, 0, 0, 12) };

    internal static W.Button Button(string title, Action action)
    {
        var button = new W.Button { Content = title, Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(0, 4, 6, 4) };
        button.Click += (_, _) => action();
        return button;
    }

    private static void AddField(W.StackPanel parent, string label, UIElement input)
    {
        parent.Children.Add(Text(label)); parent.Children.Add(input);
    }

    private void Run(Action action)
    {
        try { action(); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        { _service.Confirm(ex.Message, error: true); }
    }

    private void SaveReminder() => Run(() =>
    {
        if (_date.SelectedDate is not DateTime date ||
            !TimeSpan.TryParseExact(_time.Text.Trim(), @"hh\:mm", CultureInfo.InvariantCulture, out var time) ||
            time.TotalHours >= 24)
            throw new ArgumentException("请选择日期，时间使用 HH:mm 格式，例如 09:30。");
        if (_editing is null && date.Date + time <= DateTime.Now)
            throw new ArgumentException("新提醒请选择未来时间。");
        _service.Upsert(_editing, _title.Text, date.Date + time, (RepeatRule)_repeat.SelectedIndex);
        if (_service.Available) { ResetEditor(); _service.Confirm("提醒已保存。"); }
    });

    private void ResetEditor()
    {
        _editing = null; _title.Text = "";
        _date.SelectedDate = DateTime.Now.AddHours(1).Date;
        _time.Text = DateTime.Now.AddHours(1).ToString("HH:mm");
        _repeat.SelectedIndex = 0;
    }

    private void Refresh()
    {
        _updating = true;
        var state = _service.State;
        _quiet.IsChecked = state.Quiet; _sound.IsChecked = state.SoundEnabled;
        _autoHide.IsChecked = state.AutoHideBubble; _bubbleSeconds.SelectedItem = state.BubbleSeconds;
        _bubbleSeconds.IsEnabled = state.AutoHideBubble;
        _wake.IsChecked = state.WakeOnImportant; _timerCard.IsChecked = state.ShowTimerCard;
        _error.Text = _service.Error ?? "";
        _error.Visibility = string.IsNullOrEmpty(_error.Text) ? Visibility.Collapsed : Visibility.Visible;
        _tabs.IsEnabled = _service.Available;
        var next = state.Reminders.Select(r => (Reminder: r, Time: _service.NextOccurrence(r, DateTime.Now)))
            .Where(r => r.Time is not null).OrderBy(r => r.Time).FirstOrDefault();
        var f = state.Focus;
        var seconds = (int)Math.Ceiling(f.RemainingSeconds);
        var timer = $"{seconds / 60:00}:{seconds % 60:00}";
        var stage = f.Stage == FocusStage.Focus ? "专注" : "休息";
        _today.Text = DateTime.Now.ToString("yyyy年M月d日 dddd", CultureInfo.GetCultureInfo("zh-CN"));
        _summary.Text = "下一条：" + (next.Reminder is null ? "暂无，今天轻轻松松" : $"{next.Reminder.Title}\n{next.Time:MM-dd HH:mm}");
        _pendingValue.Text = $"{_service.PendingCount} 条"; _timerCaption.Text = stage; _timerValue.Text = timer;
        _focusStatus.Text = $"{stage} · {timer}\n" + (f.AwaitingNext ? "阶段已结束" : f.Running ? "正在计时" : "已暂停／待开始");
        _start.Content = f.AwaitingNext ? (f.Stage == FocusStage.Focus ? "开始休息" : "开始下一轮专注") : "开始／继续";
        _start.IsEnabled = !f.Running;
        _pause.IsEnabled = f.Running;
        _focusMinutes.IsEnabled = _breakMinutes.IsEnabled = !f.Running && !f.AwaitingNext;
        if (_lastRevision != _service.Revision)
        {
            _lastRevision = _service.Revision;
            RenderReminders();
        }
        _updating = false;
    }

    private void RenderReminders()
    {
        _rows.Children.Clear();
        if (_service.State.Reminders.Count == 0) _rows.Children.Add(Text("还没有提醒，创建第一条吧。"));
        foreach (var reminder in _service.State.Reminders.OrderBy(r => r.PendingOccurrence is null).ThenBy(r => r.ScheduledLocal))
        {
            var card = new W.StackPanel { Margin = new Thickness(10) };
            card.Children.Add(Text(reminder.Title, 15));
            var status = reminder.Completed ? "已完成" : reminder.SnoozeUntil is DateTime snooze ? $"稍后提醒 {snooze:MM-dd HH:mm}" :
                reminder.PendingOccurrence is not null ? "待处理" : "待提醒";
            card.Children.Add(Text($"{reminder.ScheduledLocal:yyyy-MM-dd HH:mm} · {new[] { "一次性", "每天", "每周" }[(int)reminder.Repeat]} · {status}"));
            var actions = new W.WrapPanel();
            if (reminder.PendingOccurrence is not null)
            {
                actions.Children.Add(Button("完成", () => { _service.Complete(reminder.Id); _service.Confirm("本次提醒已完成。"); }));
                actions.Children.Add(Button("10 分钟后提醒", () => { _service.Snooze(reminder.Id, DateTime.Now); _service.Confirm("10 分钟后再提醒。"); }));
            }
            actions.Children.Add(Button("编辑", () =>
            {
                _editing = reminder.Id; _title.Text = reminder.Title;
                _date.SelectedDate = reminder.ScheduledLocal.Date;
                _time.Text = reminder.ScheduledLocal.ToString("HH:mm");
                _repeat.SelectedIndex = (int)reminder.Repeat;
                _title.Focus();
            }));
            actions.Children.Add(Button("删除", () =>
            {
                _service.Delete(reminder.Id);
                _service.Confirm("提醒已删除。");
                if (_editing == reminder.Id) ResetEditor();
            }));
            card.Children.Add(actions);
            card.Margin = new Thickness(0); _rows.Children.Add(Card(card, "#FFF0F7"));
        }
    }
}
