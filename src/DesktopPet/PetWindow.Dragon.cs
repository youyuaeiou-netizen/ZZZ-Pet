using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using DesktopPet.Behavior;
using DesktopPet.Character;
using DesktopPet.Tools;
using MenuItem = System.Windows.Controls.MenuItem;

namespace DesktopPet;

public partial class PetWindow
{
    private DragonFormRules? _dragon;
    private CharacterDefinition? _activeForm;
    private string? _shownDragonForm;
    private bool _feeding, _feedSeen;
    private bool _chibiFalling;
    private double _chibiFallSpeed, _chibiFallFloor;
    private FrameDefinition[] _chibiAirFrames = [];
    private DispatcherTimer? _dragonTimer;
    private readonly MenuItem _feedDragonMenu = new() { Header = "喂食" };
    private readonly List<MenuItem> _dragonFormMenus = [];
    private bool IsDragon => _characterId == "gpt-dragon"
        && _characters[_characterId].Forms is { } forms && forms.ContainsKey("human") && forms.ContainsKey("chibi");
    private CharacterDefinition CurrentCharacter => _activeForm ?? _characters[_characterId];

    private void InitializeDragon()
    {
        _activeForm = null; _shownDragonForm = null;
        if (!IsDragon) return;
        _settings.Dragon ??= new DragonState();
        _dragon ??= new DragonFormRules(_settings.Dragon);
        _dragon.Initialize(DateTimeOffset.UtcNow);
        _shownDragonForm = _settings.Dragon.Form;
        _activeForm = _characters[_characterId].Forms![_shownDragonForm];
    }

    private void SetupDragonMenus()
    {
        var roles = new MenuItem { Header = "角色" };
        foreach (var (id, definition) in _characters)
        {
            var role = new MenuItem { Header = definition.Name };
            role.Click += (_, _) => SelectCharacter(id); roles.Items.Add(role);
        }
        ContextMenu.Items.Add(roles);
        _feedDragonMenu.Click += (_, _) => FeedDragon();
        RebuildInteractionMenu();
        ContextMenu.Opened += (_, _) => UpdateDragonMenus();
        UpdateDragonMenus();
        _dragonTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _dragonTimer.Tick += (_, _) => UpdateDragon(); _dragonTimer.Start();
        _usage.SetBackgroundPolling(IsDragon);
        if (IsDragon) QueueSave();
    }

    private void UpdateDragonMenus()
    {
        _feedDragonMenu.IsEnabled = !_feeding;
        foreach (var item in _dragonFormMenus)
        {
            item.IsEnabled = !_feeding;
            if (item.Tag is string id) item.IsChecked = _shownDragonForm == id;
        }
    }

    private void AddDragonInteractionItems()
    {
        _dragonFormMenus.Clear();
        if (!IsDragon) return;
        _interactionMenu.Items.Add(_feedDragonMenu);
        foreach (var (id, label) in new[] { ("human", "龙娘变大（人物版）"), ("chibi", "龙娘变小（Q 版）") })
        {
            var item = new MenuItem { Header = label, Tag = id, IsCheckable = true };
            item.Click += (_, _) => SelectDragonForm(id);
            _dragonFormMenus.Add(item);
            _interactionMenu.Items.Add(item);
        }
        _interactionMenu.Items.Add(new Separator());
        UpdateDragonMenus();
    }

    private void SelectDragonForm(string form)
    {
        if (!IsDragon || _dragon is null || form is not ("human" or "chibi")) return;
        _chibiFalling = false;
        _feeding = _feedSeen = false;
        if (form == "human") _dragon.Enlarge(DateTimeOffset.UtcNow); else _dragon.MakeSmall();
        UpdateDragon(); QueueSave();
    }

    private void FeedDragon()
    {
        if (!IsDragon || _dragon is null || _feeding || _runner.IsDragging || _chibiFalling) return;
        if (!_runner.PlayAction("feed")) return;
        _interactionRevision++; _attentionSleep = null;
        _feeding = true; _feedSeen = false;
        UpdateDragonMenus();
    }

    private void DragonQuotaUpdated(CodexUsage? usage)
    {
        if (!IsDragon || _dragon is null) return;
        // Ignore auxiliary model buckets; only the account's main Codex windows participate.
        var bucket = usage?.Buckets.FirstOrDefault(b => b.Name.Equals("Codex", StringComparison.OrdinalIgnoreCase));
        if (bucket is null) _dragon.QuotaUnavailable();
        else _dragon.Observe(bucket.Primary?.Remaining, bucket.Secondary?.Remaining, usage!.RetrievedAt, DateTimeOffset.UtcNow);
        UpdateDragon(); QueueSave();
    }

    private void UpdateDragon()
    {
        if (!IsDragon || _dragon is null) return;
        var now = DateTimeOffset.UtcNow;
        var protection = _dragon.State.ProtectedUntil;
        if (_feeding)
        {
            if (_runner.Current.Id == "feed") _feedSeen = true;
            else if (_feedSeen)
            {
                _feeding = _feedSeen = false;
                _dragon.Feed(now); QueueSave();
            }
            else if (_runner.Current.Id is not ("wake" or "sleep_enter"))
            {
                // Dragging or another interaction may cancel the queued feed while waking.
                _feeding = false; UpdateDragonMenus();
            }
        }
        _dragon.Evaluate(now);
        if (protection != _dragon.State.ProtectedUntil) QueueSave();
        if (_feeding || _gestures.IsPressed || _runner.IsDragging || _chibiFalling || _shownDragonForm == _dragon.State.Form) return;
        var form = _characters[_characterId].Forms![_dragon.State.Form];
        _activeForm = form; _shownDragonForm = _dragon.State.Form;
        _runner.SwitchForm(form);
        if (_attentionSleep is not null) _attentionSleep = form.ActionMap.GetValueOrDefault(_attentionSleep.Id);
        _displayedAction = null; ApplyViewport(); _player.Show(_runner); UpdateSleepIndicator();
        RebuildInteractionMenu(); UpdateDragonMenus(); QueueSave();
    }

    private bool IsChibi => IsDragon && _shownDragonForm == "chibi";

    private double ChibiFloor => SystemParameters.WorkArea.Bottom - EffectiveScale(CurrentCharacter);

    private string? ChibiAirFramePath
    {
        get
        {
            if (!_chibiFalling || _chibiAirFrames.Length == 0) return null;
            var scale=EffectiveScale(CurrentCharacter);
            var near=Math.Clamp(1-(_chibiFallFloor-_groundY)/(55*scale),0,1);
            var motion=near>0 ? .7+.3*near : .7*Math.Clamp(_chibiFallSpeed/(700*scale),0,1);
            return _chibiAirFrames[(int)Math.Round(motion*(_chibiAirFrames.Length-1))].FullPath;
        }
    }

    private void KeepChibiSuspended()
    {
        if (!IsChibi || !_runner.IsDragging) return;
        // Keep the feet within the work area without forcing an extra drop.
        _groundY = Math.Min(_groundY, ChibiFloor);
        Top = _groundY - _runner.Current.GetViewport(CurrentCharacter).AnchorY * EffectiveScale(CurrentCharacter);
    }

    private void BeginChibiFall()
    {
        if (!IsChibi || _runner.Current.Id != "release") return;
        _chibiFallFloor = Math.Min(ChibiFloor, _groundY + 48 * EffectiveScale(CurrentCharacter));
        _chibiFallSpeed = 0;
        _chibiAirFrames = _runner.Current.Frames.TakeWhile(f => System.IO.Path.GetFileName(f.Path).StartsWith("air",StringComparison.Ordinal)).ToArray();
        _chibiFalling = _groundY < _chibiFallFloor - 1;
        if (!_chibiFalling)
            _runner.Advance(TimeSpan.FromMilliseconds(_chibiAirFrames.Sum(f => f.DurationMs)));
    }

    private bool AdvanceChibiFall(TimeSpan elapsed)
    {
        if (!_chibiFalling) return false;
        _chibiFallFloor = Math.Min(_chibiFallFloor, ChibiFloor);
        var seconds = Math.Clamp(elapsed.TotalSeconds, 0, .1);
        var scale = EffectiveScale(CurrentCharacter);
        const double gravity = 2200;
        _groundY += _chibiFallSpeed * seconds + .5 * gravity * scale * seconds * seconds;
        _chibiFallSpeed += gravity * scale * seconds;
        if (_groundY >= _chibiFallFloor)
        {
            _groundY = _chibiFallFloor;
            _chibiFalling = false;
            // The first release frame is the airborne pose. Start impact only at contact.
            _runner.Advance(TimeSpan.FromMilliseconds(_chibiAirFrames.Sum(f=>f.DurationMs)));
            QueueSave();
        }
        Top = _groundY - _runner.Current.GetViewport(CurrentCharacter).AnchorY * scale;
        return true;
    }
}
