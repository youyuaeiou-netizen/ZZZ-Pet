using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using DesktopPet.Behavior;
using DesktopPet.Character;
using DesktopPet.Tools;
using DesktopPet.Search;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Point = System.Windows.Point;

namespace DesktopPet;

public partial class PetWindow : Window
{
    [DllImport("user32.dll")] private static extern uint GetDoubleClickTime();
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);

    private ActionRunner _runner;
    private SpritePlayer _player;
    private readonly DispatcherTimer _animationTimer;
    private readonly DispatcherTimer _saveTimer;
    private PointerGestures _gestures;
    private readonly Dictionary<string, CharacterDefinition> _characters;
    private readonly string _characterRoot;
    private readonly UserSettings _settings;
    private readonly SettingsStore _settingsStore;
    private readonly TrayService _tray;
    private readonly ToolsController _tools;
    private readonly MonitorController _monitor;
    private readonly OutsideClickDismissal _outsideClick;
    private readonly UsageController _usage;
    private readonly SearchController _search;
    private string _characterId;
    private bool _readyForPersistence;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private TimeSpan _lastTick;
    private Point _pressLocal;
    private long _boundaryGeneration = -1;
    private ActionDefinition? _displayedAction;
    private double _groundX;
    private double _groundY;
    private ActionDefinition? _attentionSleep;
    private bool _attentionPending;
    private bool _attentionActive;
    private long _interactionRevision;
    private long _attentionRevision;
    private readonly System.Windows.Controls.MenuItem _interactionMenu = new() { Header = "互动动作" };

    public PetWindow(IReadOnlyDictionary<string, CharacterDefinition> characters, string characterRoot,
        string selectedCharacter, UserSettings settings, SettingsStore settingsStore, RuntimePaths? paths = null)
    {
        InitializeComponent();
        NoticeWindow.NonActivating(this);
        _characters = characters.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        _characterRoot = characterRoot;
        _settings = settings;
        _settings.AutomaticActions ??= new(StringComparer.OrdinalIgnoreCase);
        _settingsStore = settingsStore;
        _characterId = selectedCharacter;
        var character = _characters[selectedCharacter];
        _settings.Scale = ClampScale(_settings.Scale);
        _runner = new ActionRunner(character);
        _runner.AutomaticActionsEnabled = !_settings.AutomaticActions.TryGetValue(_characterId, out var automatic) || automatic;
        var scale = EffectiveScale(character);
        var viewport = _runner.Current.GetViewport(character);
        var work = SystemParameters.WorkArea;
        _groundX = settings.WindowLeft is double savedLeft
            ? savedLeft + character.AnchorX * scale
            : work.Left + (work.Width - viewport.Width * scale) / 2 + viewport.AnchorX * scale;
        _groundY = settings.WindowTop is double savedTop
            ? savedTop + character.AnchorY * scale
            : work.Bottom - viewport.Height * scale + viewport.AnchorY * scale;
        Topmost = settings.AlwaysOnTop;
        ShowActivated = !settings.PetHidden;
        ApplyViewport();

        _player = new SpritePlayer(SpriteImage);
        _player.Show(_runner);
        UpdateSleepIndicator();
        UpdateDebugTitle();
        _gestures = CreateGestures();
        _animationTimer = new DispatcherTimer(DispatcherPriority.Render)
            { Interval = TimeSpan.FromMilliseconds(16) };
        _animationTimer.Tick += OnTick;
        _animationTimer.Start();
        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); SaveSettings(); };
        _tools = new ToolsController(this, paths?.ToolsStatePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DesktopPet", "tools-state.json"), Attention);
        _monitor = new MonitorController(this, _tools, paths?.MonitorDirectory ?? Path.Combine(
            settingsStore.DataDirectory, "monitor"));
        _usage = new UsageController(this, () => { _monitor.DismissBubble(); ContextMenu.IsOpen = false; });
        _outsideClick = new OutsideClickDismissal(Dispatcher, TransientSurfaces, DismissTransientSurfaces);
        _search = new SearchController(this, paths?.SearchSettingsPath ?? Path.Combine(settingsStore.DataDirectory, "search-settings.json"), DismissTransientSurfaces);
        _usage.VisibilityChanged += UpdateOutsideClick;
        _monitor.BubbleVisibilityChanged += UpdateOutsideClick;
        ContextMenu.Opened += (_, _) => UpdateOutsideClick();
        ContextMenu.Closed += (_, _) => UpdateOutsideClick();
        _tray = new TrayService(() => _characters.Keys.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
            () => _characterId, () => _settings.Scale, () => Topmost,
            SelectCharacter, SetScale, SetTopmost, ResetPosition, Close,
            showAbout: () => System.Windows.MessageBox.Show($"{AppInfo.Label}\n朋友试用版 · Windows x64\n\n更新方式：退出旧版，完整解压新版后启动。\n角色和设置保存在用户数据目录。",
                "关于 DesktopPet", MessageBoxButton.OK, MessageBoxImage.Information),
            openHelp: () => OpenDocument(Path.Combine(AppContext.BaseDirectory,
                File.Exists(Path.Combine(AppContext.BaseDirectory, "朋友试用说明.txt")) ? "朋友试用说明.txt" : "朋友试用说明.md")),
            openUserCharacters: paths is null ? null : () => OpenDirectory(paths.UserCharacterRoot),
            openLogs: paths is null ? null : () => OpenDirectory(paths.LogDirectory),
            getCharacterName: id => string.IsNullOrWhiteSpace(_characters[id].Name) ? id : _characters[id].Name,
            showTools: () => _tools.ShowPanel(), toggleQuiet: _tools.ToggleQuiet, getQuiet: () => _tools.Quiet,
            showMonitor: _monitor.ShowPanel,
            togglePet: () => SetPetHidden(!_settings.PetHidden), getPetHidden: () => _settings.PetHidden,
            showPet: () => SetPetHidden(false), showUsage: _usage.Show, showSearch: _search.Show);
        var search = new System.Windows.Controls.MenuItem { Header = "搜索文件／应用" };
        search.Click += (_, _) => _search.Show(); ContextMenu.Items.Add(search);
        var usage = new System.Windows.Controls.MenuItem { Header = "ChatGPT 额度" };
        usage.Click += (_, _) => _usage.Show(); ContextMenu.Items.Add(usage);
        var hidePet = new System.Windows.Controls.MenuItem { Header = "隐藏 Q 宠" };
        hidePet.Click += (_, _) => SetPetHidden(true);
        ContextMenu.Items.Add(hidePet);
        ContextMenu.Items.Add(_interactionMenu);
        RebuildInteractionMenu();
        MouseLeftButtonDown += OnLeftDown;
        MouseMove += OnMouseMove;
        MouseLeftButtonUp += OnLeftUp;
        LocationChanged += (_, _) =>
        {
            if (_readyForPersistence && _gestures.IsDragging) QueueSave();
        };
        Loaded += (_, _) => { _readyForPersistence = true; ApplyPetVisibility(); };
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        Closed += OnClosed;
    }

    private IEnumerable<FrameworkElement> TransientSurfaces()
    {
        yield return this;
        if (ContextMenu.IsOpen) yield return ContextMenu;
        if (_monitor.BubbleWindow is { IsVisible: true } bubble) yield return bubble;
        if (_usage.BubbleWindow is { IsVisible: true } usage) yield return usage;
    }
    private void UpdateOutsideClick() => _outsideClick.Watch(ContextMenu.IsOpen || _monitor.HasBubble || _usage.HasBubble);
    private void DismissTransientSurfaces()
    {
        ContextMenu.IsOpen = false;
        _monitor.DismissBubble();
        _usage.Dismiss();
        UpdateOutsideClick();
    }

    private void Attention(bool open)
    {
        if (open)
        {
            if (_attentionActive)
            {
                _attentionPending = true;
                if (!_gestures.IsPressed && !_runner.IsDragging) WakeForAttention();
                return;
            }
            _attentionActive = true; _attentionRevision = _interactionRevision;
            _attentionSleep = _runner.Current.ShowSleepIndicator ? _runner.Current : null;
            _attentionPending = true;
            if (!_gestures.IsPressed && !_runner.IsDragging) WakeForAttention();
        }
        else
        {
            _attentionPending = false;
            if (_attentionActive && _attentionRevision == _interactionRevision && _attentionSleep is not null)
                _runner.RestoreSleepingPose(_attentionSleep);
            _attentionActive = false; _attentionSleep = null;
        }
    }
    private void WakeForAttention()
    {
        _attentionPending = false;
        _runner.Trigger("notification");
        ApplyViewport(); _player.Show(_runner); UpdateSleepIndicator();
    }

    private void OnTick(object? sender, EventArgs e)
    {
        var now = _clock.Elapsed;
        var elapsed = now - _lastTick;
        _lastTick = now;
        if (elapsed > TimeSpan.FromMilliseconds(100)) elapsed = TimeSpan.FromMilliseconds(100);

        var move = _runner.Current.Move;
        if (!_gestures.IsPressed && move is not null)
        {
            var work = SystemParameters.WorkArea;
            var result = Movement.Step(Left, Top, Width, Height,
                new Bounds(work.Left, work.Top, work.Right, work.Bottom),
                move, _runner.Facing, elapsed);
            Left = result.Left;
            Top = result.Top;
            UpdateGroundFromWindow();
            if (result.BoundaryHit && _boundaryGeneration != _runner.Generation)
            {
                _boundaryGeneration = _runner.Generation;
                if (move.FlipAtBoundary) _runner.Flip();
                _runner.Trigger("movement_boundary");
            }
            if (!result.BoundaryHit) _boundaryGeneration = -1;
        }
        _runner.AutomaticActionsPaused = ContextMenu.IsOpen || _gestures.IsPressed;
        _runner.Advance(elapsed);
        var gesture = _gestures.Advance(_clock.Elapsed.TotalMilliseconds);
        if (gesture is not null) HandleGesture(gesture);
        if (_attentionPending && !_gestures.IsPressed && !_runner.IsDragging) WakeForAttention();
        ApplyViewport();
        _player.Show(_runner);
        UpdateSleepIndicator();
        UpdateDebugTitle();
    }

    private void OnLeftDown(object sender, MouseButtonEventArgs e)
    {
        _interactionRevision++;
        _pressLocal = e.GetPosition(this);
        var screen = PointToScreen(_pressLocal);
        var nowMs = _clock.Elapsed.TotalMilliseconds;
        var dueGesture = _gestures.Advance(nowMs);
        if (dueGesture is not null) HandleGesture(dueGesture);
        _gestures.Press(new PointerPosition(screen.X, screen.Y), nowMs);
        CaptureMouse();
        e.Handled = true;
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        var point = e.GetPosition(this);
        var screen = PointToScreen(point);
        var gesture = _gestures.Move(new PointerPosition(screen.X, screen.Y));
        if (gesture is not null) HandleGesture(gesture);
        ApplyViewport();
        if (_gestures.IsDragging)
        {
            Left += point.X - _pressLocal.X;
            Top += point.Y - _pressLocal.Y;
            UpdateGroundFromWindow();
        }
    }

    private void OnLeftUp(object sender, MouseButtonEventArgs e)
    {
        ReleaseMouseCapture();
        var screen = PointToScreen(e.GetPosition(this));
        var gesture = _gestures.Release(new PointerPosition(screen.X, screen.Y), _clock.Elapsed.TotalMilliseconds);
        if (gesture is not null) HandleGesture(gesture);
        if (gesture == "drag_release") SaveSettings();
        if (gesture == "drag_release") _interactionRevision++;
        ApplyViewport();
        _player.Show(_runner);
        UpdateSleepIndicator();
        UpdateDebugTitle();
        e.Handled = true;
    }

    private PointerGestures CreateGestures() => new((int)GetDoubleClickTime(),
        GetSystemMetrics(36), GetSystemMetrics(37), GetSystemMetrics(68), GetSystemMetrics(69),
        immediateClick: _characters[_characterId].ImmediateClick
            ?? _characters[_characterId].Actions.Any(a => a.ShowSleepIndicator));

    private void HandleGesture(string gesture)
    {
        _runner.Trigger(gesture);
        if (gesture == "click") { _usage.Dismiss(); _monitor.Click(); }
        if (gesture == "drag_start") _monitor.Drag(true);
        if (gesture == "drag_release") _monitor.Drag(false);
    }

    private void RebuildInteractionMenu()
    {
        _interactionMenu.Items.Clear();
        var actions = _characters[_characterId].Actions;
        foreach (var group in actions.Where(a => !string.IsNullOrWhiteSpace(InteractionLabel(a)))
            .GroupBy(a => string.IsNullOrWhiteSpace(a.Category)
                ? (a.ShowSleepIndicator || a.Triggers.Contains("idle_timeout") || a.Id == "wake" ? "休息" : a.Id == "wave" ? "回应" : "日常")
                : a.Category))
        {
            var category = new System.Windows.Controls.MenuItem { Header = group.Key };
            foreach (var action in group)
            {
                var item = new System.Windows.Controls.MenuItem { Header = InteractionLabel(action) };
                item.Click += (_, _) => PlayInteraction(action.Id);
                category.Items.Add(item);
            }
            _interactionMenu.Items.Add(category);
        }
        var random = new System.Windows.Controls.MenuItem
        {
            Header = "随机动作", IsEnabled = actions.Any(a => a.Weight > 0 && a.Triggers.Contains("random"))
        };
        random.Click += (_, _) => PlayInteraction(null);
        _interactionMenu.Items.Add(random);
        var automatic = new System.Windows.Controls.MenuItem
        {
            Header = "自动小动作", IsCheckable = true, IsChecked = _runner.AutomaticActionsEnabled
        };
        automatic.Click += (_, _) =>
        {
            _runner.AutomaticActionsEnabled = automatic.IsChecked;
            _settings.AutomaticActions[_characterId] = automatic.IsChecked;
            SaveSettings();
        };
        _interactionMenu.Items.Add(automatic);
    }

    private static string? InteractionLabel(ActionDefinition action) => action.DisplayName ?? action.Id switch
    {
        "awake_idle" => "待机", "sleep_stand" => "准备入睡", "sleep_sit" => "坐睡",
        "wake" => "醒来", "wave" => "打招呼", _ => null
    };

    private void PlayInteraction(string? actionId)
    {
        _interactionRevision++;
        var played = actionId is null ? _runner.PlayRandomAction() : _runner.PlayAction(actionId);
        if (!played)
            new DiagnosticLog(Path.Combine(_settingsStore.DataDirectory, "logs"))
                .Write("WARN", $"Interaction unavailable: {_characterId}/{actionId ?? "random"}");
        ApplyViewport();
        _player.Show(_runner);
        UpdateSleepIndicator();
    }

    public void NotifyAlreadyRunning()
    {
        SetPetHidden(false);
        _tray.Notify("桌宠已经在运行，可从此托盘图标操作或退出。");
    }

    internal void SetPetHidden(bool hidden)
    {
        _settings.PetHidden = hidden;
        ApplyPetVisibility();
        SaveSettings();
    }

    private void ApplyPetVisibility()
    {
        // Detach independent tools before hiding their owner so monitoring and reminders stay visible.
        _monitor.SetPetHidden(_settings.PetHidden);
        _tools.SetPetHidden(_settings.PetHidden);
        _usage.SetPetHidden(_settings.PetHidden);
        _search.SetPetHidden(_settings.PetHidden);
        if (_settings.PetHidden)
        {
            ContextMenu.IsOpen = false;
            ReleaseMouseCapture();
            if (_runner.IsDragging) _runner.Trigger("drag_release");
            _gestures = CreateGestures();
            _monitor.Drag(false);
            _animationTimer.Stop();
            Hide();
        }
        else
        {
            _lastTick = _clock.Elapsed;
            EnsureVisibleOnVirtualScreen();
            if (!IsVisible) Show();
            _animationTimer.Start();
        }
    }

    public void NotifyWarning(string message) => _tray.Notify(message);

    private static void OpenDirectory(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            System.Windows.MessageBox.Show($"无法打开目录：{exception.Message}\n{path}", "DesktopPet");
        }
    }

    private static void OpenDocument(string path)
    {
        if (!File.Exists(path))
        {
            System.Windows.MessageBox.Show("说明文件未找到，请查看发布包中的朋友试用说明.md。", "DesktopPet");
            return;
        }
        try
        {
            var start = new ProcessStartInfo("notepad.exe") { UseShellExecute = false };
            start.ArgumentList.Add(path);
            Process.Start(start);
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            System.Windows.MessageBox.Show($"无法打开说明：{exception.Message}\n{path}", "DesktopPet");
        }
    }

    private void UpdateSleepIndicator()
    {
        var show = _runner.Current.ShowSleepIndicator;
        SleepIndicator.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show) return;
        var scale = EffectiveScale(_characters[_characterId]);
        var phase = (_runner.NowMs % 1800) / 1800.0;
        SleepIndicator.Text = phase < 1.0 / 3 ? "Z" : phase < 2.0 / 3 ? "ZZ" : "ZZZ";
        SleepIndicator.FontSize = 18 * scale;
        SleepIndicator.Margin = new Thickness(Width * 0.76, (12 + (1 - phase) * 14) * scale, 0, 0);
        SleepIndicator.Opacity = phase < 0.15 ? phase / 0.15
            : phase > 0.65 ? (1 - phase) / 0.35 : 1;
    }

    private static double ClampScale(double scale) => Math.Clamp(double.IsFinite(scale) ? scale : 1, 0.5, 2.5);

    private double EffectiveScale(CharacterDefinition character) => character.DefaultScale * _settings.Scale;

    private void UpdateGroundFromWindow()
    {
        var character = _characters[_characterId];
        var viewport = _runner.Current.GetViewport(character);
        var scale = EffectiveScale(character);
        _groundX = Left + viewport.AnchorX * scale;
        _groundY = Top + viewport.AnchorY * scale;
    }

    private void ApplyViewport()
    {
        if (ReferenceEquals(_displayedAction, _runner.Current)) return;
        var character = _characters[_characterId];
        var viewport = _runner.Current.GetViewport(character);
        // WPF rounds the shown window to physical pixels. Reassigning the
        // same logical viewport on each pose change alters that rounded size.
        if (_displayedAction is not null && _displayedAction.GetViewport(character) == viewport)
        {
            _displayedAction = _runner.Current;
            return;
        }
        var scale = EffectiveScale(character);
        Width = viewport.Width * scale;
        Height = viewport.Height * scale;
        Left = _groundX - viewport.AnchorX * scale;
        Top = _groundY - viewport.AnchorY * scale;
        EnsureVisibleOnVirtualScreen();
        _displayedAction = _runner.Current;
    }

    private void SelectCharacter(string id)
    {
        _interactionRevision++; _attentionSleep = null;
        if (!_characters.TryGetValue(id, out var character)) return;
        _characterId = id;
        _settings.SelectedCharacter = id;
        _runner = new ActionRunner(character);
        _runner.AutomaticActionsEnabled = !_settings.AutomaticActions.TryGetValue(id, out var automatic) || automatic;
        _player = new SpritePlayer(SpriteImage);
        _displayedAction = null;
        ApplyViewport();
        _gestures = CreateGestures();
        _player.Show(_runner);
        UpdateSleepIndicator();
        UpdateDebugTitle();
        RebuildInteractionMenu();
        QueueSave();
    }

    private void SetScale(double scale)
    {
        _settings.Scale = ClampScale(scale);
        _displayedAction = null;
        ApplyViewport();
        QueueSave();
    }

    private void SetTopmost(bool enabled)
    {
        Topmost = enabled;
        _settings.AlwaysOnTop = enabled;
        QueueSave();
    }

    private void ResetPosition()
    {
        var work = SystemParameters.WorkArea;
        var character = _characters[_characterId];
        var viewport = _runner.Current.GetViewport(character);
        var scale = EffectiveScale(character);
        _groundX = work.Left + (work.Width - viewport.Width * scale) / 2 + viewport.AnchorX * scale;
        _groundY = work.Bottom - viewport.Height * scale + viewport.AnchorY * scale;
        _displayedAction = null;
        ApplyViewport();
        QueueSave();
    }

    private void EnsureVisibleOnVirtualScreen()
    {
        var left = SystemParameters.VirtualScreenLeft;
        var top = SystemParameters.VirtualScreenTop;
        var right = left + SystemParameters.VirtualScreenWidth;
        var bottom = top + SystemParameters.VirtualScreenHeight;
        Left = Math.Clamp(Left, left, Math.Max(left, right - Width));
        Top = Math.Clamp(Top, top, Math.Max(top, bottom - Height));
        UpdateGroundFromWindow();
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        if (Dispatcher.HasShutdownStarted) return;
        Dispatcher.BeginInvoke(() =>
        {
            EnsureVisibleOnVirtualScreen();
            QueueSave();
        });
    }

    private void QueueSave()
    {
        if (!_readyForPersistence) return;
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void SaveSettings()
    {
        var character = _characters[_characterId];
        var scale = EffectiveScale(character);
        _settings.WindowLeft = _groundX - character.AnchorX * scale;
        _settings.WindowTop = _groundY - character.AnchorY * scale;
        _settings.SelectedCharacter = _characterId;
        _settings.AlwaysOnTop = Topmost;
        _settingsStore.Save(_settings);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _monitor.BubbleVisibilityChanged -= UpdateOutsideClick;
        _usage.VisibilityChanged -= UpdateOutsideClick;
        _outsideClick.Dispose();
        _search.Dispose();
        _usage.Dispose();
        _monitor.Dispose();
        _tools.Dispose();
        _animationTimer.Stop();
        _saveTimer.Stop();
        SaveSettings();
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        _tray.Dispose();
    }

    [Conditional("DEBUG")]
    private void UpdateDebugTitle()
    {
        var title = $"DesktopPet [{_runner.Current.Id}]";
        if (Title != title) Title = title;
    }
}
