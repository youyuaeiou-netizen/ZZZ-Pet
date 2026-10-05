using System.Text.Json;
using System.Text;
using System.Text.RegularExpressions;

namespace DesktopPet.Monitoring;

public sealed class MonitorLanguage
{
    public static readonly string[] Codes = ["zh", "zh-tw", "en", "ja", "ko", "fr", "de", "es", "ru"];
    public static readonly string[] Names = ["简体中文", "繁體中文", "English", "日本語", "한국어", "Français", "Deutsch", "Español", "Русский"];
    public string LanguageName(string displayLanguage, string language) => Text(displayLanguage, language switch
    {
        "zh" => "简体中文", "zh-tw" => "繁体中文", "en" => "英语", "ja" => "日语", "ko" => "韩语",
        "fr" => "法语", "de" => "德语", "es" => "西班牙语", "ru" => "俄语", _ => language
    });
    private readonly Dictionary<string, Dictionary<string, string>> _languages = [];
    private readonly Dictionary<string, string> _keysByChinese = [];
    private readonly Dictionary<string, Dictionary<string, string>> _pet = [];
    public MonitorLanguage(string directory)
    {
        foreach (var code in Codes)
        {
            try
            {
                using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, code + ".json")));
                var values = new Dictionary<string, string>(); Flatten(json.RootElement, "", values); _languages[code] = values;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException) { }
        }
        if (_languages.TryGetValue("zh", out var chinese)) foreach (var item in chinese) _keysByChinese.TryAdd(item.Value, item.Key);
        try
        {
            var additions = JsonSerializer.Deserialize<Dictionary<string, string[]>>(File.ReadAllText(Path.Combine(directory, "pet.json")));
            string[] order = ["en", "zh-tw", "ja", "ko", "fr", "de", "es", "ru"];
            if (additions is not null) foreach (var p in additions)
                if (p.Value is { Length: 8 }) for (var i = 0; i < order.Length; i++)
                {
                    if (!ValidTranslation(p.Key, p.Value[i])) continue;
                    if (!_pet.ContainsKey(order[i])) _pet[order[i]] = []; _pet[order[i]][p.Key] = p.Value[i];
                }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { }
    }
    private static bool ValidTranslation(string template, string? translation)
    {
        if (string.IsNullOrWhiteSpace(translation)) return false;
        try
        {
            CompositeFormat.Parse(template); CompositeFormat.Parse(translation);
            static IEnumerable<string> Arguments(string text) => Regex.Matches(text, @"(?<!\{)\{(\d+)(?:[^{}]*)\}(?!\})")
                .Select(m => m.Groups[1].Value).Distinct().Order();
            return Arguments(template).SequenceEqual(Arguments(translation));
        }
        catch (FormatException) { return false; }
    }
    private static void Flatten(JsonElement element, string prefix, Dictionary<string, string> values)
    {
        foreach (var p in element.EnumerateObject())
        {
            var key = prefix + p.Name;
            if (p.Value.ValueKind == JsonValueKind.Object) Flatten(p.Value, key + ".", values);
            else if (p.Value.ValueKind == JsonValueKind.String) values[key] = p.Value.GetString()!;
        }
    }
    public string Key(string code, string key, string fallback) => _languages.GetValueOrDefault(code)?.GetValueOrDefault(key)
        ?? _languages.GetValueOrDefault("en")?.GetValueOrDefault(key) ?? fallback;
    public string Metric(string code, MonitorMetric metric) => Key(code, "Items." + metric.Id, metric.Name);
    public string Text(string code, string chinese)
    {
        if (code == "zh") return chinese;
        if (_pet.GetValueOrDefault(code)?.GetValueOrDefault(chinese) is string own) return own;
        if (_keysByChinese.TryGetValue(chinese, out var key)) return Key(code, key, chinese);
        if (Owned.TryGetValue(chinese, out var pair)) return pair.Key is null ? pair.English : Key(code, pair.Key, pair.English);
        if (_pet.GetValueOrDefault("en")?.GetValueOrDefault(chinese) is string english) return english;
        foreach (var separator in new[] { "\n", "；", "：" })
            if (chinese.Contains(separator))
                return string.Join(separator == "\n" || code == "zh-tw" ? separator : separator == "：" ? ": " : " · ", chinese.Split(separator).Select(line => Text(code, line)));
        return chinese;
    }
    public string Format(string code, string template, params object[] values) => string.Format(System.Globalization.CultureInfo.GetCultureInfo(Codes.Contains(code) ? code : "en"), Text(code, template), values);
    // Retain compatibility fallbacks when a resource is absent. Hardware names and user input stay intact.
    private static readonly Dictionary<string, (string? Key, string English)> Owned = new()
    {
        ["系统监控"] = (null, "System monitor"), ["监控"] = (null, "Monitor"), ["显示"] = (null, "Display"),
        ["进阶功能"] = (null, "Advanced"), ["系统监控 · 进阶功能"] = (null, "System monitor · Advanced"),
        ["启用系统监控"] = (null, "Enable system monitoring"), ["提醒设置"] = (null, "Alert settings"),
        ["高温提醒（部分开启）"] = (null, "Temperature alerts (partially enabled)"), ["等待帧数据"] = (null, "Waiting for frames"),
        ["FPS 正在启动"] = (null, "Starting FPS capture"), ["FPS 需要管理员权限"] = (null, "FPS requires administrator permission"),
        ["FPS 采集不可用"] = (null, "FPS capture unavailable"),
        ["系统工具与内存"] = (null, "System tools and memory"), ["外观与布局"] = (null, "Appearance and layout"),
        ["启动设置"] = (null, "Startup settings"), ["维护与诊断"] = (null, "Maintenance and diagnostics"),
        ["任务栏显示"] = (null, "Taskbar display"), ["双列布局"] = (null, "Two-column layout"),
        ["分类颜色阈值（不改变提醒规则）"] = (null, "Category color thresholds (alert rules unchanged)"),
        ["使用所选分类的独立颜色阈值"] = (null, "Use independent color thresholds for this category"),
        ["警告 / 严重（Temp 为 ℃，NetKBps 为 MB/s，其余为 %）"] = (null, "Warning / Critical (Temp: °C, NetKBps: MB/s, other categories: %)"),
        ["保存分类颜色阈值"] = (null, "Save category color thresholds"),
        ["硬件驱动状态"] = (null, "Hardware driver status"), ["刷新驱动状态"] = (null, "Refresh driver status"),
        ["打开 PawnIO 官方下载页"] = (null, "Open official PawnIO download page"),
        ["未找到已登记的 PawnIO；部分温度／电压可能不可用。"] = (null, "No registered PawnIO installation found; some temperatures or voltages may be unavailable."),
        ["无法读取已安装驱动状态。"] = (null, "Unable to read installed driver status."),
        ["Pet 不自动更改共享驱动。安装可能需要管理员权限及重启；已有 ObsUI 硬件服务优先复用。仅在确认需要缺失传感器时安装官方驱动。"] = (null, "Pet does not change shared drivers automatically. Installation may require administrator access and a restart. Existing ObsUI monitoring is preferred. Install the official driver only when needed for missing sensors."),
        ["颜色使用 #RRGGBB。布局尺寸为 0–120，字号为 8–32。温度、内存、显存等独立阈值可随主题 JSON 导入导出。"] = (null, "Use #RRGGBB colors, layout sizes 0–120 and font sizes 8–32. Independent temperature, memory and VRAM thresholds can be imported and exported in theme JSON."),
        ["局域网访问需要系统允许网络连接。仅分享给信任的设备；链接包含本次运行的访问凭证。Pet 不修改防火墙。"] = (null, "LAN access requires the system to allow network connections. Share only with trusted devices; the link contains this run's access token. Pet does not change the firewall."),
        ["请启用局域网网页监控并选择已监听的地址。"] = (null, "Enable LAN monitoring and choose a listening address."),
        ["访问链接已复制；本次退出后失效。"] = (null, "Link copied; it expires when Pet exits."), ["剪贴板暂不可用。"] = (null, "Clipboard temporarily unavailable."),
        ["临时文件由 Windows 存储设置预览和确认删除；Pet 不遍历删除其他程序的缓存。"] = (null, "Preview and confirm temporary file removal in Windows Storage settings. Pet does not traverse or delete other programs' caches."),
        ["将关闭当前资源管理器窗口并短暂刷新任务栏。正在进行的文件操作可能中断，请先保存。继续？"] = (null, "Current Explorer windows will close and the taskbar will refresh briefly. File operations may be interrupted. Save your work first. Continue?"),
        ["将在 {0} 分钟后请求关机。请保存所有程序的工作；可在这里取消，退出 Pet 会取消本次计划。继续？"] = (null, "Request shutdown in {0} minutes. Save work in all programs. You can cancel here; exiting Pet cancels this schedule. Continue?"),
        ["请输入 1–1440 分钟。"] = (null, "Enter 1–1440 minutes."), ["无法更改本次睡眠请求。"] = (null, "Unable to change this session's sleep request."),
        ["关机请求失败。"] = (null, "Shutdown request failed."), ["资源管理器重启失败。"] = (null, "Explorer restart failed."), ["系统工具无法启动。"] = (null, "Unable to start system tool."),
        ["基础采集运行中；未安装或修改驱动"] = (null, "Basic sampling running; no drivers installed or changed"),
        ["运行中 · 只读复用现有 8085 采集服务"] = (null, "Running · read-only reuse of the existing 8085 provider"),
        ["运行中 · LibreHardwareMonitor 0.9.6；缺失传感器显示不可用"] = (null, "Running · LibreHardwareMonitor 0.9.6; missing sensors show unavailable"),
        ["基础采集可用；等待现有硬件服务恢复，不重复打开驱动"] = (null, "Basic sampling available; waiting for existing hardware provider recovery"),
        ["实时监控"] = ("Menu.MonitorItemDisplay", "Monitor"), ["气泡"] = (null, "Bubble"),
        ["高温提醒"] = ("Menu.AlertTemp", "Temperature alerts"), ["历史"] = ("Menu.MonitorHistory", "History"),
        ["扩展工具"] = ("Menu.More", "Tools"), ["插件"] = ("Menu.Plugins", "Plugins"), ["外观"] = ("Menu.Appearance", "Appearance"),
        ["艾莲布 · 系统监控"] = (null, "Ellen · System monitor"), ["艾莲布 · 电脑状态"] = (null, "Ellen · PC status"),
        ["电脑状态"] = (null, "PC status"),
        ["日期"] = (null, "Date"), ["时间缩放"] = (null, "Zoom"), ["暂无可用趋势数据"] = (null, "No trend data available"),
        ["功能概览"] = (null, "Overview"), ["扩展设置"] = (null, "Extended settings"),
        ["按需扩展，让日常监控保持轻盈。"] = (null, "Customize the extras while keeping everyday monitoring simple."),
        ["连接你需要的数据源，独立管理每个实例。"] = (null, "Connect data sources and manage each instance."),
        ["常用系统操作、Pet 内存整理与本次关机计划。"] = (null, "System actions, Pet memory and this session's shutdown schedule."),
        ["调整气泡与独立窗，收藏自己的显示主题。"] = (null, "Customize the bubble, monitor window and display themes."),
        ["管理当前用户登录后的 Pet 启动方式。"] = (null, "Manage Pet startup for the current user."),
        ["查看采集状态、选择设备与检查整包更新。"] = (null, "Inspect sampling, choose devices and check package updates."),
        ["开关即时生效\n设置随输入完成保存"] = (null, "Switches apply immediately\nSettings save after editing"),
        ["主题配色"] = (null, "Theme colors"), ["字体与尺寸"] = (null, "Typography and sizing"),
        ["数值颜色阈值"] = (null, "Value color thresholds"), ["分组、进度条与动画"] = (null, "Groups, bars and animation"),
        ["收藏主题"] = (null, "Saved themes"), ["运行状态"] = (null, "Runtime status"),
        ["采集设备与刷新"] = (null, "Devices and sampling"), ["独立监控窗"] = (null, "Monitor window"),
        ["显卡"] = ("Menu.GpuSource", "GPU"), ["网卡"] = ("Menu.NetworkSource", "Network"), ["磁盘"] = ("Menu.DiskSource", "Disk"),
        ["自动选择"] = ("Menu.Auto", "Auto"), ["数据气泡顺序"] = (null, "Bubble item order"), ["上移"] = (null, "Move up"),
        ["下移"] = (null, "Move down"), ["移除"] = ("Menu.PluginRemoveTarget", "Remove"), ["监控设置"] = ("Menu.SettingsPanel", "Settings"),
        ["收起"] = (null, "Close"), ["打开监控"] = (null, "Open monitor"), ["暂停提醒 1 小时"] = (null, "Pause alerts for 1 hour"),
        ["本次高温不再提示"] = (null, "Dismiss this episode"), ["CPU 使用率"] = ("Items.CPU.Load", "CPU usage"),
        ["GPU 使用率"] = ("Items.GPU.Load", "GPU usage"), ["内存使用率"] = ("Items.MEM.Load", "Memory usage"),
        ["CPU 温度"] = ("Items.CPU.Temp", "CPU temperature"), ["GPU 核心温度"] = ("Items.GPU.Temp", "GPU core temperature"),
        ["上传速度"] = ("Items.NET.Up", "Upload"), ["下载速度"] = ("Items.NET.Down", "Download"), ["指标"] = ("Menu.MonitorItem", "Metric"),
        ["磁盘读取"] = ("Items.DISK.Read", "Disk read"), ["磁盘写入"] = ("Items.DISK.Write", "Disk write"),
        ["不可用"] = (null, "Unavailable"), ["已断开"] = (null, "Disconnected"), ["数据延迟"] = (null, "Data delayed"), ["未启用"] = (null, "Disabled"),
        ["正在启动监控…"] = (null, "Starting monitor…"), ["当前会话"] = (null, "Current session"),
        ["趋势与历史"] = ("Menu.MonitorHistory", "Trends and history"), ["刷新日期"] = (null, "Refresh dates"),
        ["导出所选日期 CSV"] = (null, "Export selected date as CSV"), ["日期（当前会话实时更新）"] = (null, "Date (live current session)"),
        ["记录历史（每 10 秒，单日日志最多 32 MB）"] = (null, "Record every 10 seconds (32 MB per day maximum)"),
        ["Pet 监控外观"] = ("Menu.Appearance", "Monitor appearance"), ["背景色（#RRGGBB）"] = ("Menu.BackgroundColor", "Background (#RRGGBB)"),
        ["文字色（#RRGGBB）"] = ("Menu.LabelColor", "Text (#RRGGBB)"), ["正常数值色"] = ("Menu.ValueSafeColor", "Normal color"),
        ["警告数值色"] = ("Menu.ValueWarnColor", "Warning color"), ["严重数值色"] = ("Menu.ValueCritColor", "Critical color"),
        ["字体名称"] = ("Menu.TaskbarFont", "Font"), ["字号（8–32）"] = ("Menu.TaskbarFontSize", "Font size (8–32)"),
        ["行间距（0–30）"] = (null, "Row spacing (0–30)"), ["面板宽度（240–900）"] = ("Menu.Width", "Width (240–900)"),
        ["界面缩放（0.5–2.5）"] = ("Menu.Scale", "Scale (0.5–2.5)"), ["气泡透明度（0.1–1）"] = ("Menu.Opacity", "Opacity (0.1–1)"),
        ["气泡使用双列布局"] = (null, "Use two columns"), ["显示独立监控窗"] = ("Menu.MainFormSettings", "Show monitor window"),
        ["独立监控窗鼠标穿透"] = ("Menu.ClickThrough", "Monitor window click-through"), ["独立监控窗靠边隐藏"] = ("Menu.AutoHide", "Monitor window edge hiding"),
        ["应用主题"] = ("Menu.Apply", "Apply theme"), ["导入主题 JSON"] = (null, "Import theme JSON"), ["导出主题 JSON"] = (null, "Export theme JSON"),
        ["以管理员权限重新启动监控"] = (null, "Restart monitor as administrator"), ["自定义插件"] = ("Menu.Plugins", "Custom plugins"),
        ["启用联网插件"] = (null, "Enable network plugins"), ["添加插件实例"] = ("Menu.PluginCreateCopy", "Add plugin instance"),
        ["导入 LiteMonitor JSON 模板"] = (null, "Import LiteMonitor JSON template"), ["CPU 高温提醒"] = (null, "CPU temperature alerts"),
        ["GPU 高温提醒"] = (null, "GPU temperature alerts"), ["阈值（℃，30–130）"] = ("Menu.AlertThreshold", "Threshold (°C, 30–130)"),
        ["保存 CPU 阈值"] = (null, "Save CPU threshold"), ["保存 GPU 阈值"] = (null, "Save GPU threshold"),
        ["勾选要在人物对话气泡中展示的指标"] = (null, "Select metrics to display in the pet bubble"),
        ["部分温度等传感器需要管理员权限；取消授权仍可使用基础监控。"] = (null, "Some sensors require administrator access. Basic monitoring remains available if permission is cancelled."),
        ["单击宠物打开。鼠标在宠物或气泡内保持显示，离开后 5 秒收起。人物动作继续运行。"] = (null, "Click the pet to open. Hover over the pet or bubble to keep it visible; it closes 5 seconds after leaving. Animations continue."),
        ["连续超温 10 秒提醒一次；低于阈值 5℃并保持 60 秒后，才允许再次提醒。持续高温、断线和重启不会反复提醒。"] = (null, "Alert once after 10 seconds above threshold. Rearm after 60 seconds at least 5°C below it. Continuous heat, disconnects and restarts do not repeat alerts."),
        ["安静模式不自动弹出，也不会在解除后补发旧提醒。CPU 使用封装温度或最高有效核心温度；GPU 使用核心温度。"] = (null, "Quiet mode suppresses popups without replay. CPU uses package temperature or the highest valid core; GPU uses core temperature."),
        ["日志仅保存监控运行期间的数据。断线与缺失值显示为断点；损坏行跳过并标记，原文件保留。"] = (null, "History covers active monitoring only. Missing data creates gaps; corrupt lines are skipped and counted without changing the file."),
        ["放大最近时间段（1–10 倍）；悬停图表查看时间和值"] = (null, "Zoom the latest period (1–10×); hover to inspect time and value"),
        ["独立窗可拖动。靠边后移开鼠标自动收起，返回边缘展开；穿透时从宠物或托盘进入设置关闭。宠物与提醒气泡保持可操作。"] = (null, "Drag the monitor window. Edge hiding collapses it after leaving; hover at the edge to restore. Disable click-through from pet or tray settings. Pet and alert controls remain usable."),
        ["网页与实用工具"] = (null, "Web display and tools"), ["启用本机网页监控（默认关闭）"] = ("Menu.WebServer", "Enable local web display"),
        ["刷新间隔（毫秒）"] = ("Menu.Refresh", "Refresh interval (ms)"),
        ["启用 FPS 呈现帧率采集（默认关闭，需要管理员权限）"] = (null, "Enable presented FPS capture (off by default; administrator required)"),
        ["FPS 进程 ID（留空自动选择最高活跃帧率）"] = (null, "FPS process ID (empty: fastest active application)"),
        ["保存 FPS 进程"] = (null, "Save FPS process"), ["呈现帧率"] = ("Items.FPS", "Presented FPS"),
        ["定时整理 Pet 自身内存（默认关闭）"] = (null, "Trim Pet memory periodically (off by default)"), ["整理间隔（分钟，5–1440）"] = (null, "Trim interval (minutes, 5–1440)"),
        ["保存定时整理"] = (null, "Save trim schedule"), ["Pet 整包更新"] = (null, "Whole Pet package updates"),
        ["默认不联网检查。填写可信的 HTTPS 更新清单，手动检查；仅接受完整 Pet x64 包，内置 LiteMonitor 不独立更新。"] = (null, "No automatic network checks. Configure a trusted HTTPS feed and check manually. Only whole Pet x64 packages are accepted."),
        ["保存更新来源"] = (null, "Save update source"), ["检查 Pet 更新"] = ("Menu.CheckUpdate", "Check Pet updates"), ["打开整包下载"] = (null, "Open package download"),
        ["安装或替换整包前先退出 Pet；保留用户数据。下载后核对清单提供的 SHA-256。不会自动执行下载文件或替换监控组件。"] = (null, "Exit Pet before replacing its package; retain user data and verify the provided SHA-256. Downloads are not automatically executed."),
        ["负载警告阈值（%）"] = (null, "Load warning threshold (%)"), ["负载严重阈值（%）"] = (null, "Load critical threshold (%)"),
        ["网络／磁盘速率警告阈值（MB/s）"] = (null, "Network/disk warning rate (MB/s)"), ["网络／磁盘速率严重阈值（MB/s）"] = (null, "Network/disk critical rate (MB/s)"),
        ["在任务栏空白处显示监控（默认关闭）"] = ("Menu.TaskbarShow", "Show monitor in blank taskbar space"),
        ["任务栏指标标识（逗号分隔，最多 12 项）"] = (null, "Taskbar metric IDs (comma separated, up to 12)"),
        ["任务栏显示器"] = ("Menu.TaskbarMonitor", "Taskbar monitor"),
        ["任务栏显示始终鼠标穿透，不遮挡点击。空间不足或无法确认空白区时暂停显示，不挤压现有程序按钮。"] = (null, "Taskbar display is always click-through. It pauses when blank space cannot be verified or is too small; app buttons are unchanged."),
        ["任务栏显示使用 Pet 自有窗口；不注入 Explorer，也不改变其他程序的任务栏布局。"] = (null, "Taskbar display uses a Pet-owned window without injecting Explorer or changing other app layouts."),
        ["端口（不占用 8085、5173）"] = (null, "Port (8085 and 5173 reserved)"), ["应用网页设置"] = (null, "Apply web settings"),
        ["打开网页监控"] = ("Menu.OpenWeb", "Open web display"), ["网络测速"] = ("Menu.SpeedTest", "Network speed test"),
        ["下载测试 URL（最多读取 10 MB，点击后才联网）"] = (null, "Download URL (up to 10 MB, starts on click)"),
        ["上传测试 URL（POST 1 MB，点击后才联网）"] = (null, "Upload URL (POST 1 MB, starts on click)"),
        ["测试下载速度"] = (null, "Test download speed"), ["测试上传速度"] = (null, "Test upload speed"),
        ["停止测速"] = (null, "Stop test"), ["整理 Pet 自身内存"] = (null, "Trim Pet memory"), ["测速中…"] = (null, "Testing…"),
        ["网页监控未运行"] = (null, "Web display stopped"), ["网页监控运行中 · 仅监听 127.0.0.1"] = (null, "Web display running · 127.0.0.1 only"),
        ["当前用户登录时启动 Pet（不创建管理员计划任务）"] = (null, "Start Pet on user login (no elevated scheduled task)"),
        ["更新统一使用完整 Pet 安装包或 ZIP。此内置 LiteMonitor 固定为 1.3.6，不会独立自动替换。"] = (null, "Update the complete Pet installer or ZIP. The integrated LiteMonitor source stays pinned to 1.3.6."),
        ["插件仅在 Pet 普通权限进程中联网运行；默认关闭。输入配置使用当前 Windows 用户的 DPAPI 加密，不写入监控日志。"] = (null, "Plugins run with ordinary Pet permissions and are off by default. Inputs use Windows user encryption and are excluded from monitoring logs."),
        ["编辑输入"] = (null, "Edit inputs"), ["移除实例"] = (null, "Remove instance"), ["监控目标"] = ("Menu.PluginTargetTitle", "Target"),
        ["移除此目标"] = ("Menu.PluginRemoveTarget", "Remove target"), ["添加目标"] = ("Menu.PluginAddTarget", "Add target"),
        ["启用此实例"] = (null, "Enable instance"), ["保存"] = ("Menu.Apply", "Save"), ["尚未选择展示指标。"] = (null, "No metrics selected."),
        ["这次高温只提醒一次，恢复后才会再次提醒。"] = (null, "One alert per episode. Alerts rearm after recovery."),
        ["主题已应用。"] = (null, "Theme applied."), ["主题已导入。"] = (null, "Theme imported."), ["主题已导出。"] = (null, "Theme exported."),
        ["历史已导出。"] = (null, "History exported."), ["自启动设置已更新。"] = (null, "Startup preference updated."),
        ["颜色无效，请使用 #RRGGBB。"] = (null, "Invalid color; use #RRGGBB."), ["缩放或透明度范围无效。"] = (null, "Scale or opacity is outside the allowed range."),
        ["字体、间距、宽度或阈值颜色无效。"] = (null, "Invalid font, spacing, width or threshold colors."),
        ["主题不兼容或参数无效；原主题文件保留。"] = (null, "Incompatible or invalid theme; original file retained."),
        ["主题导出失败。"] = (null, "Theme export failed."), ["历史目录暂不可读。"] = (null, "History directory unavailable."),
        ["历史读取失败，原文件保留。"] = (null, "History read failed; original file retained."), ["导出失败，原始历史未改变。"] = (null, "Export failed; original history unchanged."),
        ["请输入 30–130 之间的温度。"] = (null, "Enter a temperature from 30 to 130°C."), ["阈值已保存。"] = (null, "Threshold saved."),
        ["请输入 1024–65535 范围的独立端口。"] = (null, "Choose an unused port from 1024 to 65535."), ["请先启用监控和网页服务。"] = (null, "Enable monitoring and web display first."),
        ["无法打开默认浏览器。"] = (null, "Unable to open the default browser."), ["当前权限无法整理工作集。"] = (null, "Working set trim unavailable."),
        ["Pet 的工作集已整理；没有清理 ObsUI 或其他程序。"] = (null, "Pet working set trimmed; ObsUI and other processes unchanged."),
        ["请输入有效 HTTP/HTTPS 地址。"] = (null, "Enter a valid HTTP/HTTPS URL."), ["测速已停止或超时。"] = (null, "Test cancelled or timed out."),
        ["测速失败，检查地址与网络。"] = (null, "Download test failed; check the URL and network."), ["上传测试失败，检查地址与网络。"] = (null, "Upload test failed; check the URL and network."),
    };
}
