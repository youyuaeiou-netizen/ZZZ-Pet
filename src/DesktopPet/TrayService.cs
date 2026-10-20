namespace DesktopPet;

public sealed class TrayService : IDisposable
{
    private readonly System.Windows.Forms.NotifyIcon _icon;
    private readonly System.Drawing.Icon _image;
    private readonly System.Windows.Forms.ContextMenuStrip _menu;
    private readonly Func<IReadOnlyList<string>> _getCharacters;
    private readonly Func<string> _getSelectedCharacter;
    private readonly Func<double> _getScale;
    private readonly Func<bool> _getTopmost;
    private readonly Action<string> _selectCharacter;
    private readonly Action<double> _setScale;
    private readonly Action<bool> _setTopmost;
    private readonly Action _resetPosition;
    private readonly Action _exit;
    private readonly Action? _showAbout;
    private readonly Action? _openHelp;
    private readonly Action? _openUserCharacters;
    private readonly Action? _openLogs;
    private readonly Func<string, string>? _getCharacterName;
    private readonly Action? _showTools;
    private readonly Action? _showMonitor;
    private readonly Action? _showUsage;
    private readonly Action? _showSearch;
    private readonly Action? _toggleQuiet;
    private readonly Func<bool>? _getQuiet;
    private readonly Action? _togglePet;
    private readonly Func<bool>? _getPetHidden;
    private readonly Func<string?>? _getDragonForm;
    private readonly Action<string>? _selectDragonForm;
    private readonly Action? _feedDragon;

    public TrayService(Func<IReadOnlyList<string>> getCharacters, Func<string> getSelectedCharacter,
        Func<double> getScale, Func<bool> getTopmost, Action<string> selectCharacter,
        Action<double> setScale, Action<bool> setTopmost, Action resetPosition, Action exit,
        Action? showAbout = null, Action? openHelp = null, Action? openUserCharacters = null,
        Action? openLogs = null, Func<string, string>? getCharacterName = null,
        Action? showTools = null, Action? toggleQuiet = null, Func<bool>? getQuiet = null, Action? showMonitor = null,
        Action? togglePet = null, Func<bool>? getPetHidden = null, Action? showPet = null, Action? showUsage = null, Action? showSearch = null,
        Func<string?>? getDragonForm = null, Action<string>? selectDragonForm = null, Action? feedDragon = null)
    {
        _getCharacters = getCharacters;
        _getSelectedCharacter = getSelectedCharacter;
        _getScale = getScale;
        _getTopmost = getTopmost;
        _selectCharacter = selectCharacter;
        _setScale = setScale;
        _setTopmost = setTopmost;
        _resetPosition = resetPosition;
        _exit = exit;
        _showAbout = showAbout;
        _openHelp = openHelp;
        _openUserCharacters = openUserCharacters;
        _openLogs = openLogs;
        _getCharacterName = getCharacterName;
        _showTools = showTools;
        _showMonitor = showMonitor;
        _showUsage = showUsage;
        _showSearch = showSearch;
        _toggleQuiet = toggleQuiet;
        _getQuiet = getQuiet;
        _togglePet = togglePet;
        _getPetHidden = getPetHidden;
        _getDragonForm = getDragonForm; _selectDragonForm = selectDragonForm; _feedDragon = feedDragon;

        using var iconStream = typeof(TrayService).Assembly.GetManifestResourceStream("DesktopPet.AppIcon.ico")
            ?? throw new InvalidOperationException("应用图标资源缺失。");
        using var sourceIcon = new System.Drawing.Icon(iconStream, System.Windows.Forms.SystemInformation.SmallIconSize);
        _image = (System.Drawing.Icon)sourceIcon.Clone();
        _menu = new System.Windows.Forms.ContextMenuStrip();
        _menu.Opening += (_, _) => RebuildMenu();
        _icon = new System.Windows.Forms.NotifyIcon
        {
            Icon = _image,
            Text = AppInfo.Label,
            ContextMenuStrip = _menu,
            Visible = true
        };
        _icon.DoubleClick += (_, _) => showPet?.Invoke();
    }

    private void RebuildMenu()
    {
        var oldItems = _menu.Items.Cast<System.Windows.Forms.ToolStripItem>().ToArray();
        _menu.Items.Clear();
        foreach (var oldItem in oldItems) oldItem.Dispose();
        AddOptional(_getPetHidden?.Invoke() == true ? "显示 Q 宠" : "隐藏 Q 宠", _togglePet);
        var tools = new System.Windows.Forms.ToolStripMenuItem("轻工具");
        void Tool(string label, Action? action)
        {
            if (action is null) return;
            var item = new System.Windows.Forms.ToolStripMenuItem(label);
            item.Click += (_, _) => action(); tools.DropDownItems.Add(item);
        }
        Tool("信息／提醒／专注", _showTools);
        Tool("系统监控", _showMonitor);
        Tool("搜索文件／应用", _showSearch);
        Tool("ChatGPT 额度", _showUsage);
        _menu.Items.Add(tools);
        if (_toggleQuiet is not null)
        {
            var quiet = new System.Windows.Forms.ToolStripMenuItem("安静模式") { Checked = _getQuiet?.Invoke() == true };
            quiet.Click += (_, _) => _toggleQuiet();
            tools.DropDownItems.Add(quiet);
        }
        if (_showTools is not null) _menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        var characters = new System.Windows.Forms.ToolStripMenuItem("角色");
        foreach (var id in _getCharacters())
        {
            var item = new System.Windows.Forms.ToolStripMenuItem(_getCharacterName?.Invoke(id) ?? id)
            {
                Checked = id == _getSelectedCharacter(),
                CheckOnClick = false
            };
            item.Click += (_, _) => _selectCharacter(id);
            characters.DropDownItems.Add(item);
        }
        _menu.Items.Add(characters);
        if (_getDragonForm?.Invoke() is { } form)
        {
            var interactions = new System.Windows.Forms.ToolStripMenuItem("互动动作");
            if (_feedDragon is not null)
            {
                var feed = new System.Windows.Forms.ToolStripMenuItem("喂食");
                feed.Click += (_, _) => _feedDragon();
                interactions.DropDownItems.Add(feed);
            }
            foreach (var (id, label) in new[] { ("human", "龙娘变大（人物版）"), ("chibi", "龙娘变小（Q 版）") })
            {
                var item = new System.Windows.Forms.ToolStripMenuItem(label) { Checked = form == id };
                item.Click += (_, _) => _selectDragonForm?.Invoke(id);
                interactions.DropDownItems.Add(item);
            }
            _menu.Items.Add(interactions);
        }
        _menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());

        var scale = Math.Clamp(_getScale(), 0.5, 2.5);
        var scaleLabel = new System.Windows.Forms.ToolStripMenuItem($"缩放：{scale:P0}") { Enabled = false };
        _menu.Items.Add(scaleLabel);
        var smaller = new System.Windows.Forms.ToolStripMenuItem("缩小");
        smaller.Click += (_, _) => _setScale(Math.Max(0.5, Math.Round(scale - 0.1, 1)));
        var larger = new System.Windows.Forms.ToolStripMenuItem("放大");
        larger.Click += (_, _) => _setScale(Math.Min(2.5, Math.Round(scale + 0.1, 1)));
        _menu.Items.Add(smaller);
        _menu.Items.Add(larger);

        var topmost = new System.Windows.Forms.ToolStripMenuItem("始终置顶") { Checked = _getTopmost() };
        topmost.Click += (_, _) => _setTopmost(!topmost.Checked);
        _menu.Items.Add(topmost);
        var reset = new System.Windows.Forms.ToolStripMenuItem("重置位置");
        reset.Click += (_, _) => _resetPosition();
        _menu.Items.Add(reset);
        _menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        AddOptional("使用与更新说明", _openHelp);
        AddOptional("打开用户角色目录", _openUserCharacters);
        AddOptional("打开日志目录", _openLogs);
        AddOptional("关于 DesktopPet", _showAbout);
        var exit = new System.Windows.Forms.ToolStripMenuItem("退出");
        exit.Click += (_, _) => _exit();
        _menu.Items.Add(exit);
    }

    private void AddOptional(string label, Action? callback)
    {
        if (callback is null) return;
        var item = new System.Windows.Forms.ToolStripMenuItem(label);
        item.Click += (_, _) => callback();
        _menu.Items.Add(item);
    }

    public void Notify(string message) => _icon.ShowBalloonTip(2500, AppInfo.Label, message,
        System.Windows.Forms.ToolTipIcon.Info);

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _image.Dispose();
        _menu.Dispose();
    }
}
