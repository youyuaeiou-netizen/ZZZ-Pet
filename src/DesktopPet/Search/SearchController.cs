using System.Windows;
using DesktopPet.Tools;

namespace DesktopPet.Search;

internal sealed class SearchController : IDisposable
{
    private readonly Window _pet; private readonly SearchService _service; private readonly Action _beforeOpen;
    private SearchWindow? _window; private bool _hidden, _disposed;
    internal SearchController(Window pet, string settingsPath, Action beforeOpen)
    { _pet = pet; _service = new(settingsPath); _beforeOpen = beforeOpen; }
    internal void Show()
    {
        if (_disposed) return; _beforeOpen();
        if (_window is null || _window.IsClosing)
        {
            var created = new SearchWindow(_service) { Owner = _hidden ? null : _pet };
            _window = created;
            created.Closed += (_, _) => { if (ReferenceEquals(_window, created)) _window = null; };
            created.Show();
        }
        var window = _window;
        if (window is null || window.IsClosing) return;
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        WindowPlacement.Near(window, _pet);
        if (!window.IsClosing) window.Activate();
    }
    internal void SetPetHidden(bool hidden) { _hidden = hidden; if (_window is { IsClosing: false }) _window.Owner = hidden ? null : _pet; }
    public void Dispose() { if (_disposed) return; _disposed = true; _window?.Dismiss(); _service.Dispose(); }
}
