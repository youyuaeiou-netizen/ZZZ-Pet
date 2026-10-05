using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;

namespace DesktopPet.Tools;

internal sealed class UsageController : IDisposable
{
    private readonly Window _pet;
    private readonly Func<CancellationToken, Task<CodexUsage>> _read;
    private readonly Action _beforeShow;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMinutes(1) };
    private UsageBubble? _bubble;
    private CodexUsage? _usage;
    private DateTimeOffset _lastAttempt;
    private string? _error;
    private bool _busy, _hidden, _disposed;
    internal Window? BubbleWindow => _bubble;
    internal bool HasBubble => _bubble?.IsVisible == true;
    internal event Action? VisibilityChanged;
    internal UsageController(Window pet, Action beforeShow, Func<CancellationToken, Task<CodexUsage>>? read = null)
    {
        _pet = pet; _beforeShow = beforeShow; _read = read ?? CodexUsageClient.ReadAsync;
        _pet.LocationChanged += Moved; _pet.SizeChanged += Sized;
        _timer.Tick += (_, _) => { Render(); _ = RefreshAsync(false); };
    }
    internal void Show()
    {
        if (_disposed) return;
        _beforeShow();
        if (_bubble is null)
        {
            _bubble = new UsageBubble(() => _ = RefreshAsync(true), Dismiss) { Owner = _hidden ? null : _pet };
            _bubble.IsVisibleChanged += (_, _) => { if (HasBubble) _timer.Start(); else _timer.Stop(); VisibilityChanged?.Invoke(); };
            _bubble.SizeChanged += (_, _) => { if (HasBubble) _bubble.Position(_pet); };
        }
        Render(); _bubble.Show(); _bubble.Position(_pet); _ = RefreshAsync(false);
    }
    internal void Dismiss() => _bubble?.Hide();
    internal void SetPetHidden(bool hidden) { _hidden = hidden; if (hidden) Dismiss(); if (_bubble is not null) _bubble.Owner = hidden ? null : _pet; }
    private async Task RefreshAsync(bool force)
    {
        if (_disposed || _busy || !HasBubble || DateTimeOffset.UtcNow - _lastAttempt < TimeSpan.FromSeconds(force ? 10 : 300)) return;
        _busy = true; _lastAttempt = DateTimeOffset.UtcNow; _error = null; Render();
        try { _usage = await _read(_shutdown.Token); }
        catch (OperationCanceledException) { if (!_disposed) _error = "读取超时，请稍后刷新。"; }
        catch (FileNotFoundException) { _error = "请先安装 Codex，并使用 ChatGPT 账号登录。"; }
        catch (Exception e) when (e is IOException or InvalidOperationException or JsonException or System.ComponentModel.Win32Exception or UnauthorizedAccessException or ArgumentException)
        { _error = "读取失败，请确认 Codex 已登录，然后刷新。" + (_usage is null ? "" : "\n已有数字为上次读取结果。"); }
        finally { _busy = false; if (!_disposed) Render(); }
    }
    private void Render() { _bubble?.Render(_usage, _error, _busy); }
    private void Moved(object? sender, EventArgs e) { if (HasBubble) _bubble!.Position(_pet); }
    private void Sized(object sender, SizeChangedEventArgs e) => Moved(sender, e);
    public void Dispose()
    {
        if (_disposed) return; _disposed = true; _timer.Stop(); _shutdown.Cancel();
        _pet.LocationChanged -= Moved; _pet.SizeChanged -= Sized; _bubble?.Close(); _shutdown.Dispose();
    }
}
