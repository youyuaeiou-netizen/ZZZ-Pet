using DesktopPet.Character;

namespace DesktopPet.Behavior;

public sealed class ActionRunner
{
    private readonly CharacterDefinition _character;
    private readonly Random _random;
    private readonly Dictionary<string, double> _lastStarted = new(StringComparer.Ordinal);
    private double _nowMs;
    private double _actionElapsedMs;
    private double _lastInteractionMs;
    private double _nextRandomMs;
    private bool _idleTimeoutFired;
    private readonly Dictionary<string, string> _lastChosen = new(StringComparer.Ordinal);
    private ActionDefinition? _pendingAction;
    private bool _manualActive;

    public bool AutomaticActionsEnabled { get; set; } = true;
    public bool AutomaticActionsPaused { get; set; }

    public ActionDefinition Current { get; private set; }
    public bool IsDragging { get; private set; }
    public int Facing { get; private set; }
    public long Generation { get; private set; }
    public double NowMs => _nowMs;
    public string CurrentFramePath
    {
        get
        {
            var frames = Current.Frames;
            var total = frames.Sum(f => f.DurationMs);
            var position = Current.Loop ? _actionElapsedMs % total : Math.Min(_actionElapsedMs, total - 0.001);
            foreach (var frame in frames)
            {
                if (position < frame.DurationMs) return frame.FullPath;
                position -= frame.DurationMs;
            }
            return frames[^1].FullPath;
        }
    }

    public ActionRunner(CharacterDefinition character, Random? random = null)
    {
        _character = character;
        _random = random ?? new Random();
        Facing = string.Equals(character.DefaultFacing, "left", StringComparison.OrdinalIgnoreCase) ? -1 : 1;
        Current = character.ActionMap[character.DefaultActionId];
        _nextRandomMs = Math.Max(1, character.RandomIntervalMs);
        Trigger("app_start");
    }

    public bool Trigger(string trigger)
    {
        if (trigger == "notification" && !_character.Actions.Any(a => a.Weight > 0 && a.Triggers.Contains(trigger,StringComparer.Ordinal))) return false;
        if (trigger is "click" or "double_click" or "drag_start" or "notification")
        {
            _lastInteractionMs = _nowMs;
            _idleTimeoutFired = false;
            _nextRandomMs = _nowMs + Math.Max(1, _character.RandomIntervalMs);
        }
        if (trigger == "drag_start")
        {
            _pendingAction = null;
            _manualActive = false;
            IsDragging = true;
            // A drag always cancels the active action, even if it is otherwise uninterruptible.
            var dragged = Choose(trigger, ignoreCooldown: true);
            if (dragged is not null) Start(dragged);
            return true;
        }
        if (trigger == "drag_release")
        {
            if (!IsDragging) return false;
            IsDragging = false;
            if (_character.WakeActionId is not null)
            {
                _lastInteractionMs = _nowMs;
                _idleTimeoutFired = false;
                _nextRandomMs = _nowMs + Math.Max(1, _character.RandomIntervalMs);
            }
            var released = Choose(trigger, ignoreCooldown: true);
            if (released is not null) Start(released);
            return true;
        }
        if (IsDragging) return false;
        var chosen = Choose(trigger, ignoreCooldown: trigger is "click" or "double_click" or "notification");
        if (chosen is null) return false;
        if (_character.WakeActionId is not null && trigger is "click" or "double_click" or "notification")
            return RequestAction(chosen);
        if (trigger != "app_start" && ((trigger is not ("idle_timeout" or "click" or "double_click" or "notification") && chosen.Priority <= Current.Priority)
            || !Current.InterruptibleBy.Contains(trigger, StringComparer.Ordinal))) return false;
        Start(chosen);
        return true;
    }

    public void Advance(TimeSpan elapsed)
    {
        var ms = Math.Max(0, elapsed.TotalMilliseconds);
        _nowMs += ms;
        if (IsDragging && _character.WakeActionId is null) return;
        _actionElapsedMs += ms;
        if (IsDragging) return;

        var frameDuration = Current.Frames.Sum(f => f.DurationMs);
        if ((Current.DurationMs is int duration && _actionElapsedMs >= duration)
            || (!Current.Loop && _actionElapsedMs >= frameDuration))
        {
            Complete();
        }

        if (_nowMs >= _nextRandomMs)
        {
            _nextRandomMs = _nowMs + Math.Max(1, _character.RandomIntervalMs);
            if (AutomaticActionsEnabled && !AutomaticActionsPaused && !_manualActive
                && Current.Id == _character.DefaultActionId) Trigger("random");
        }
        if (AutomaticActionsPaused) _lastInteractionMs = _nowMs;
        if (!_manualActive && !AutomaticActionsPaused && !_idleTimeoutFired && _character.SleepThresholdMs > 0
            && _nowMs - _lastInteractionMs >= _character.SleepThresholdMs)
        {
            _idleTimeoutFired = true;
            Trigger("idle_timeout");
        }
    }

    public void Flip() => Facing *= -1;
    public bool PlayAction(string id) => _character.ActionMap.TryGetValue(id, out var action) && RequestAction(action);

    public bool PlayRandomAction()
    {
        var action = Choose("random", ignoreCooldown: true);
        return action is not null && RequestAction(action);
    }

    private static bool IsSleeping(ActionDefinition action) => action.ShowSleepIndicator
        || action.Triggers.Contains("idle_timeout", StringComparer.Ordinal);

    private bool RequestAction(ActionDefinition action)
    {
        if (IsDragging) return false;
        _lastInteractionMs = _nowMs;
        _idleTimeoutFired = false;
        _nextRandomMs = _nowMs + Math.Max(1, _character.RandomIntervalMs);
        if (_character.WakeActionId is { } wakeId && _character.ActionMap.TryGetValue(wakeId, out var wake))
        {
            if (Current.Id == wakeId && action.Id != wakeId)
            {
                _pendingAction = action;
                return true;
            }
            if (IsSleeping(Current) && !IsSleeping(action) && action.Id != wakeId)
            {
                _pendingAction = action;
                _manualActive = true;
                Start(wake);
                return true;
            }
            if (_manualActive && !Current.Loop && !IsSleeping(action) && !IsSleeping(Current))
            {
                _pendingAction = action;
                return true;
            }
        }
        _pendingAction = null;
        _manualActive = true;
        Start(action);
        return true;
    }
    public void RestoreSleepingPose(ActionDefinition pose)
    {
        if (!IsDragging && pose.ShowSleepIndicator && _character.Actions.Contains(pose)) Start(pose);
    }

    private ActionDefinition? Choose(string trigger, bool ignoreCooldown = false)
    {
        var candidates = _character.Actions.Where(a => a.Triggers.Contains(trigger, StringComparer.Ordinal)
            && a.Weight > 0 && (ignoreCooldown || !_lastStarted.TryGetValue(a.Id, out var last)
                || _nowMs - last >= a.CooldownMs)).ToArray();
        if (candidates.Length == 0) return null;
        var highest = candidates.Max(a => a.Priority);
        candidates = candidates.Where(a => a.Priority == highest).ToArray();
        if (_character.AvoidRepeatedActions && candidates.Length > 1
            && _lastChosen.TryGetValue(trigger, out var last))
            candidates = candidates.Where(a => a.Id != last).ToArray();
        var roll = _random.Next(candidates.Sum(a => a.Weight));
        foreach (var action in candidates)
        {
            roll -= action.Weight;
            if (roll < 0)
            {
                _lastChosen[trigger] = action.Id;
                return action;
            }
        }
        return candidates[^1];
    }

    private void Complete()
    {
        if (_pendingAction is { } pending)
        {
            _pendingAction = null;
            Start(pending);
            return;
        }
        var next = Current.NextActionId;
        if (next is not null && _character.ActionMap.TryGetValue(next, out var action))
        {
            Start(action);
            return;
        }
        var completion = Choose("animation_complete");
        Start(completion ?? _character.ActionMap[_character.DefaultActionId]);
    }

    private void Start(ActionDefinition action)
    {
        Current = action;
        if (action.Id == _character.DefaultActionId) _manualActive = false;
        _actionElapsedMs = 0;
        _lastStarted[action.Id] = _nowMs;
        Generation++;
    }
}
