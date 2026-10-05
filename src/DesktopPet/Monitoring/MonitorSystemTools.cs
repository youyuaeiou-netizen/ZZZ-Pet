using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using DesktopPet.Monitoring;

namespace DesktopPet;

internal sealed class MonitorSystemTools : IDisposable
{
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint SetThreadExecutionState(uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint FindWindow(string? cls, string? title);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint SendMessageTimeout(nint hwnd, uint message, nuint wparam, nint lparam, uint flags, uint timeout, out nuint result);
    private readonly DispatcherTimer _timer;
    public MonitorShutdownTimer Shutdown { get; }
    public bool PreventSleep { get; private set; }
    public string? Error { get; private set; }
    public event Action? Changed;
    public MonitorSystemTools()
    {
        Shutdown = new(() =>
        {
            try { StartSystem("shutdown.exe", ["/s", "/t", "0"]); }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { Error = "关机请求失败。"; }
        });
        _timer = new() { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => { Shutdown.Poll(Environment.TickCount64); Changed?.Invoke(); }; _timer.Start();
    }
    public bool SetPreventSleep(bool value)
    {
        if (SetThreadExecutionState(value ? 0x80000003u : 0x80000000u) == 0) return false;
        PreventSleep = value; Changed?.Invoke(); return true;
    }
    public static void StartSystem(string filename, string[]? args = null)
    {
        var info = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), filename))
        { UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in args ?? []) info.ArgumentList.Add(arg);
        using var process = Process.Start(info);
    }
    public static async Task TurnOffDisplay(nint owner)
    {
        await Task.Delay(500);
        SendMessageTimeout(owner, 0x112, 0xF170, 2, 2, 1000, out _);
    }
    public static async Task RestartExplorer()
    {
        // Target only this desktop's verified Explorer shell; never use taskkill or a process tree.
        var hwnd = FindWindow("Shell_TrayWnd", null); GetWindowThreadProcessId(hwnd, out var pid);
        if (hwnd == 0 || pid == 0) throw new InvalidOperationException("没有找到当前桌面的资源管理器。");
        using var shell = Process.GetProcessById((int)pid); using var own = Process.GetCurrentProcess();
        var executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
        if (shell.SessionId != own.SessionId || !string.Equals(shell.MainModule?.FileName, executable, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("资源管理器身份无法确认。");
        shell.Kill(false);
        using var timeout = new CancellationTokenSource(5000);
        try { await shell.WaitForExitAsync(timeout.Token); }
        finally { using var restored = Process.Start(new ProcessStartInfo(executable) { UseShellExecute = true }); }
    }
    public void Dispose()
    {
        _timer.Stop(); Shutdown.Cancel();
        if (PreventSleep) SetPreventSleep(false);
    }
}
