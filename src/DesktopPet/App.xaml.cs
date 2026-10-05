using System.IO;
using System.Windows;
using System.Diagnostics;
using DesktopPet.Character;

namespace DesktopPet;

public partial class App : System.Windows.Application
{
    private SingleInstanceGuard? _instance;
    private DiagnosticLog? _log;
    private bool _reportingFailure;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try { StartPet(StartupOptions.Parse(e.Args)); }
        catch (Exception exception) { ReportFailure(exception); }
    }

    private void StartPet(StartupOptions options)
    {
        var paths = new RuntimePaths(options);
        _instance = new SingleInstanceGuard(paths.DataDirectory);
        if (!_instance.IsPrimary)
        {
            _instance.NotifyPrimary();
            Shutdown();
            return;
        }
        _log = new DiagnosticLog(paths.LogDirectory);
        _log.Write("INFO", "启动");
        DispatcherUnhandledException += (_, args) =>
        {
            args.Handled = true;
            ReportFailure(args.Exception);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            _log.Write("ERROR", "未处理的进程异常", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
            _log.Write("ERROR", "未观察的后台任务异常", args.Exception);

        var settingsStore = new SettingsStore(paths.SettingsPath, warning => _log.Write("WARN", warning));
        var settings = settingsStore.Load();
        var catalog = CharacterCatalog.Load(paths.CharacterRoot, paths.UserCharacterRoot);
        foreach (var warning in catalog.Warnings) _log.Write("WARN", warning);
        var packs = catalog.Characters;
        if (packs.Count == 0)
            throw new InvalidDataException("没有可播放的角色。请完整解压发布包，不要只复制 DesktopPet.exe。");

        var preferred = options.Character ?? settings.SelectedCharacter;
        var selected = CharacterCatalog.ResolveSelection(packs, preferred);
        settings.SelectedCharacter = selected;
        var window = new PetWindow(packs, paths.CharacterRoot, selected, settings, settingsStore, paths);
        MainWindow = window;
        window.Show();
        _log.Write("INFO", $"角色已显示：{selected}");
        _instance.Listen(() =>
        {
            if (!Dispatcher.HasShutdownStarted)
                Dispatcher.BeginInvoke(() =>
                {
                    if (!Dispatcher.HasShutdownStarted)
                    {
                        window.NotifyAlreadyRunning();
                        _log.Write("INFO", "已处理重复启动");
                    }
                });
        });
        if (catalog.Warnings.Count > 0 || settingsStore.Warning is not null)
            window.NotifyWarning("部分角色或设置未加载，详情可从托盘“打开日志目录”查看。");
    }

    private void ReportFailure(Exception exception)
    {
        if (_reportingFailure) return;
        _reportingFailure = true;
        _log?.Write("ERROR", "运行失败", exception);
        var location = _log is null ? "" : $"\n\n日志位置：{_log.FilePath}";
        System.Windows.MessageBox.Show($"桌宠未能继续运行。\n{exception.Message}{location}\n\n请反馈版本号 {AppInfo.Version} 和错误信息。",
            AppInfo.Label, MessageBoxButton.OK, MessageBoxImage.Error);
        Shutdown(1);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _log?.Write("INFO", $"退出，代码 {e.ApplicationExitCode}");
        _instance?.Dispose();
        base.OnExit(e);
    }
}
