using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DesktopPet.Tools;
using W = System.Windows.Controls;
using Brushes = System.Windows.Media.Brushes;
using FontFamily = System.Windows.Media.FontFamily;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;

namespace DesktopPet.Search;

internal sealed class SearchWindow : Window
{
    private readonly SearchService _service;
    private readonly Action<SearchEntry, bool> _open;
    private readonly W.TextBox _query = new() { MaxLength = 256, FontSize = 17 };
    private readonly W.ComboBox _filter = new() { ItemsSource = new[] { "全部", "应用", "文件／目录" }, SelectedIndex = 0, Width = 114 };
    private readonly W.ComboBox _scope = new() { Width = 134, Margin = new Thickness(0, 0, 0, 8) };
    private readonly W.ListBox _results = new() { BorderThickness = new Thickness(0), Background = Brushes.Transparent,
        HorizontalContentAlignment = System.Windows.HorizontalAlignment.Stretch };
    private readonly W.TextBlock _status = ToolsWindow.Text("", 13);
    private readonly W.TextBlock _empty = ToolsWindow.Text("搜索文件或应用", 16);
    private readonly W.Button _launch;
    private readonly W.Button _reveal;
    private readonly W.CheckBox _bilingual = new() { Content = "中英同时检索", Margin = new Thickness(0, 0, 10, 8) };
    private readonly W.Button _alias;
    private readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(120) };
    private CancellationTokenSource? _request;
    private long _revision;
    private bool _closed, _closing, _dismissQueued, _dialogOpen, _busy, _updatingScope;
    internal bool IsClosing => _closing || _closed;

    internal SearchWindow(SearchService service, Action<SearchEntry, bool>? open = null)
    {
        _service = service; _open = open ?? SearchActions.Open;
        Title = "Q 宠 · 搜索"; Width = 600; Height = 470; MinWidth = 440; MinHeight = 340;
        Topmost = true; ShowInTaskbar = false; FontFamily = new FontFamily("Microsoft YaHei UI");
        Background = new LinearGradientBrush(Color.FromRgb(255, 240, 247), Color.FromRgb(233, 250, 243), 135);
        PixelFrame.Apply(this);
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/DesktopPet;component/Search/SearchTheme.xaml", UriKind.Relative) });
        System.Windows.Automation.AutomationProperties.SetName(_query, "搜索文件或应用");
        System.Windows.Automation.AutomationProperties.SetName(_filter, "搜索类型");
        System.Windows.Automation.AutomationProperties.SetName(_scope, "文件搜索范围");
        System.Windows.Automation.AutomationProperties.SetName(_results, "搜索结果");
        W.ScrollViewer.SetHorizontalScrollBarVisibility(_results, W.ScrollBarVisibility.Disabled);
        W.VirtualizingPanel.SetIsVirtualizing(_results, true);
        var grid = new W.Grid { Margin = new Thickness(16) };
        foreach (var height in new[] { GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto })
            grid.RowDefinitions.Add(new W.RowDefinition { Height = height });
        var input = new W.Grid { Margin = new Thickness(0, 0, 0, 12) };
        input.ColumnDefinitions.Add(new W.ColumnDefinition()); input.ColumnDefinitions.Add(new W.ColumnDefinition { Width = GridLength.Auto });
        _query.Margin = new Thickness(0, 0, 10, 0); input.Children.Add(_query); W.Grid.SetColumn(_filter, 1); input.Children.Add(_filter); grid.Children.Add(input);
        _bilingual.IsChecked = service.BilingualEnabled;
        var options = new W.Grid(); options.ColumnDefinitions.Add(new W.ColumnDefinition());
        options.ColumnDefinitions.Add(new W.ColumnDefinition { Width = GridLength.Auto }); options.ColumnDefinitions.Add(new W.ColumnDefinition { Width = GridLength.Auto });
        _bilingual.VerticalAlignment = VerticalAlignment.Center; options.Children.Add(_bilingual);
        var scopeLabel = ToolsWindow.Text("文件范围", 13); scopeLabel.VerticalAlignment = VerticalAlignment.Center; scopeLabel.Margin = new Thickness(0, 0, 8, 8);
        W.Grid.SetColumn(scopeLabel, 1); options.Children.Add(scopeLabel); W.Grid.SetColumn(_scope, 2); options.Children.Add(_scope);
        UpdateScopeOptions(); W.Grid.SetRow(options, 1); grid.Children.Add(options);
        var body = new W.Grid(); body.Children.Add(_results); _empty.HorizontalAlignment = System.Windows.HorizontalAlignment.Center;
        _empty.VerticalAlignment = VerticalAlignment.Center; body.Children.Add(_empty);
        W.Grid.SetRow(body, 2); grid.Children.Add(body);
        _status.Margin = new Thickness(0, 8, 0, 0); _status.TextWrapping = TextWrapping.NoWrap;
        _status.TextTrimming = TextTrimming.CharacterEllipsis;
        W.Grid.SetRow(_status, 3); grid.Children.Add(_status);
        var actions = new W.WrapPanel();
        _launch = ToolsWindow.Button("打开", () => OpenSelected(false)); _reveal = ToolsWindow.Button("定位", () => OpenSelected(true));
        actions.Children.Add(_launch); actions.Children.Add(_reveal);
        _alias = ToolsWindow.Button("别名", AddAlias); actions.Children.Add(_alias);
        actions.Children.Add(ToolsWindow.Button("添加目录", AddFolder)); actions.Children.Add(ToolsWindow.Button("刷新", Refresh));
        actions.Children.Add(ToolsWindow.Button("收起", Dismiss)); W.Grid.SetRow(actions, 4); grid.Children.Add(actions);
        Content = grid;
        _query.TextChanged += (_, _) => Schedule(); _filter.SelectionChanged += (_, _) => { _scope.IsEnabled = _filter.SelectedIndex != 1; Schedule(); };
        _scope.DropDownOpened += (_, _) => UpdateScopeOptions(); _scope.SelectionChanged += (_, _) => ChangeScope();
        _bilingual.Checked += (_, _) => ToggleBilingual(); _bilingual.Unchecked += (_, _) => ToggleBilingual();
        _debounce.Tick += (_, _) => { _debounce.Stop(); _ = RunQueryAsync(); };
        _results.SelectionChanged += (_, _) => UpdateActions();
        _results.MouseDoubleClick += (_, e) =>
        {
            if (W.ItemsControl.ContainerFromElement(_results, e.OriginalSource as DependencyObject) is W.ListBoxItem) OpenSelected(false);
        };
        PreviewKeyDown += Keys;
        Loaded += async (_, _) =>
        {
            _query.Focus();
            try { await _service.InitializeAsync(); }
            catch (Exception e) when (e is not OperationCanceledException) { SetStatus("索引初始化失败，请刷新"); }
        };
        Closing += (_, _) => { _closing = true; _debounce.Stop(); CancelQuery(); };
        Deactivated += (_, _) => QueueDismiss();
        Closed += (_, _) => { _closed = true; _revision++; _debounce.Stop(); CancelQuery(); };
        UpdateActions(); SetStatus("");
    }
    private void QueueDismiss()
    {
        if (IsClosing || Dispatcher.HasShutdownStarted || _dialogOpen || _busy || _dismissQueued) return;
        _dismissQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            _dismissQueued = false;
            if (!IsClosing && !_dialogOpen && !_busy && !IsActive) Dismiss();
        });
    }
    internal void Dismiss() { if (!IsClosing) Close(); }
    private void UpdateScopeOptions()
    {
        _updatingScope = true;
        try
        {
            var scopes = SearchScope.Available(_service.FileScope); _scope.ItemsSource = scopes;
            _scope.SelectedItem = scopes.Single(s => s.Key == _service.FileScope.Key);
        }
        finally { _updatingScope = false; }
    }
    private void ChangeScope()
    {
        if (IsClosing || _updatingScope || _scope.SelectedItem is not SearchScope scope) return;
        try { _service.SetFileScope(scope); Schedule(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        { UpdateScopeOptions(); SetStatus(e.Message); }
    }
    private void ToggleBilingual()
    {
        if (IsClosing || (_bilingual.IsChecked == true) == _service.BilingualEnabled) return;
        try { _service.SetBilingual(_bilingual.IsChecked == true); Schedule(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        { _bilingual.IsChecked = _service.BilingualEnabled; SetStatus(e.Message); }
    }
    private void AddAlias()
    {
        if (IsClosing || Selected is not { } entry) return;
        _dialogOpen = true;
        try
        {
            var dialog = new SearchAliasWindow(entry.Name) { Owner = this };
            if (dialog.ShowDialog() == true)
            {
                _service.AddAlias(entry, dialog.Alias);
                if (_service.BilingualEnabled) Schedule();
                else SetStatus("别名已保存，开启中英同时检索后生效");
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or System.Text.Json.JsonException) { SetStatus(e.Message); }
        finally { _dialogOpen = false; }
    }
    private void CancelQuery() { _request?.Cancel(); _request?.Dispose(); _request = null; }
    private void Schedule()
    {
        if (IsClosing) return;
        _revision++; CancelQuery(); _debounce.Stop();
        _results.Items.Clear(); UpdateActions();
        var hasQuery = _query.Text.Trim().Length > 0;
        _empty.Text = hasQuery ? "正在搜索…" : "搜索文件或应用"; _empty.Visibility = Visibility.Visible;
        SetStatus(""); if (hasQuery) _debounce.Start();
    }
    private async Task RunQueryAsync()
    {
        if (IsClosing) return;
        var version = _revision; var query = _query.Text; var filter = (SearchFilter)_filter.SelectedIndex;
        _request = new(); var token = _request.Token;
        var finished = false;
        try
        {
            var progress = new Progress<SearchReply>(partial =>
            {
                if (!finished && !_closed && version == _revision && !token.IsCancellationRequested) Render(partial);
            });
            var reply = await _service.QueryAsync(query, filter, token, progress);
            finished = true;
            if (_closed || version != _revision || token.IsCancellationRequested) return;
            Render(reply);
        }
        catch (OperationCanceledException) { }
        catch (Exception) { if (!_closed && version == _revision) { _empty.Text = "搜索暂不可用"; SetStatus("请刷新后重试"); } }
        finally { finished = true; }
    }
    internal void Render(SearchReply reply)
    {
        if (IsClosing) return;
        var selected = Selected?.Target;
        var targets = reply.Entries.Select(e => e.Target).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var item in _results.Items.OfType<W.ListBoxItem>().ToArray())
            if (!targets.Contains(((SearchEntry)item.Tag).Target)) _results.Items.Remove(item);
        foreach (var entry in reply.Entries)
        {
            var existing = _results.Items.OfType<W.ListBoxItem>().FirstOrDefault(item =>
                ((SearchEntry)item.Tag).Target.Equals(entry.Target, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                existing.Tag = entry;
                var existingRow = (W.StackPanel)existing.Content;
                ((W.TextBlock)existingRow.Children[0]).Text = entry.Name + (entry.MatchHint is null ? "" : "  · " + entry.MatchHint);
                continue;
            }
            var row = new W.StackPanel { Margin = new Thickness(8, 6, 8, 6) };
            var title = ToolsWindow.Text(entry.Name + (entry.MatchHint is null ? "" : "  · " + entry.MatchHint), 15); title.FontWeight = FontWeights.SemiBold; title.TextWrapping = TextWrapping.NoWrap; title.TextTrimming = TextTrimming.CharacterEllipsis;
            var path = ToolsWindow.Text(entry.Location, 13); path.TextWrapping = TextWrapping.NoWrap; path.TextTrimming = TextTrimming.CharacterEllipsis;
            row.Children.Add(title); row.Children.Add(path);
            var item = new W.ListBoxItem { Content = row, Tag = entry, ToolTip = entry.Target, Margin = new Thickness(0, 0, 0, 4),
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(entry.Kind == SearchKind.Application ? "#FFF0F7" : "#F0FAF5")) };
            System.Windows.Automation.AutomationProperties.SetName(item, entry.Category + " " + entry.Name + " " + entry.Location);
            _results.Items.Add(item);
        }
        if (_results.Items.Count > 0)
            _results.SelectedItem = _results.Items.OfType<W.ListBoxItem>().FirstOrDefault(item =>
                ((SearchEntry)item.Tag).Target.Equals(selected, StringComparison.OrdinalIgnoreCase)) ?? _results.Items[0];
        _empty.Text = reply.Complete ? "未找到匹配项" : "正在搜索磁盘…";
        _empty.Visibility = reply.Entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        var state = reply.Complete ? "检索完成" : reply.Entries.Count > 0 ? "已找到结果，正在补全" : "正在检索…";
        SetStatus($"{reply.Entries.Count} 项 · {state} · {_service.FileScope.Label}"
            + (reply.Status.Contains("结果不完整", StringComparison.Ordinal) ? " · 部分位置不可读取" : "")
            + (reply.Entries.Count >= 80 ? " · 显示前 80 项" : ""));
        _status.ToolTip = $"{reply.Milliseconds:0} ms · {reply.Status}";
        UpdateActions();
    }
    private SearchEntry? Selected => (_results.SelectedItem as W.ListBoxItem)?.Tag as SearchEntry;
    private void UpdateActions()
    {
        _launch.IsEnabled = Selected is not null;
        _alias.IsEnabled = Selected is not null;
        _reveal.IsEnabled = Selected is { } item && !item.Target.StartsWith("shell:AppsFolder\\", StringComparison.OrdinalIgnoreCase);
    }
    private void SetStatus(string text) { if (_closed) return; _status.Text = text; _status.ToolTip = text; _status.Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible; }
    private void OpenSelected(bool reveal)
    {
        if (Selected is not { } entry) return;
        try { _open(entry, reveal); Dismiss(); }
        catch (Exception e) when (e is IOException or System.ComponentModel.Win32Exception or UnauthorizedAccessException) { SetStatus(e.Message); }
    }
    private void Keys(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { e.Handled = true; Dismiss(); }
        else if (e.Key == Key.L && Keyboard.Modifiers == ModifierKeys.Control) { _query.Focus(); _query.SelectAll(); e.Handled = true; }
        else if (e.Key is Key.Down or Key.Up && _results.Items.Count > 0)
        {
            _results.SelectedIndex = Math.Clamp(_results.SelectedIndex + (e.Key == Key.Down ? 1 : -1), 0, _results.Items.Count - 1);
            _results.ScrollIntoView(_results.SelectedItem); e.Handled = true;
        }
        else if (e.Key == Key.Enter && (e.OriginalSource is W.TextBox || _results.IsKeyboardFocusWithin))
        { e.Handled = true; OpenSelected(Keyboard.Modifiers == ModifierKeys.Control); }
    }
    private async void AddFolder()
    {
        _dialogOpen = true;
        try
        {
            var picker = new Microsoft.Win32.OpenFolderDialog { Title = "添加搜索目录", Multiselect = false };
            if (picker.ShowDialog(this) != true) return;
            _busy = true; SetStatus("正在建立目录索引…");
            await _service.AddRootAsync(picker.FolderName);
            if (!_closed) { if (_query.Text.Trim().Length > 0) Schedule(); else SetStatus("目录已加入"); }
        }
        catch (Exception e) when (e is not OperationCanceledException) { SetStatus(e.Message); }
        finally { _busy = false; _dialogOpen = false; }
    }
    private async void Refresh()
    {
        if (_busy) return; _busy = true; SetStatus("正在刷新索引…");
        try { await _service.RefreshAsync(); if (!_closed) { if (_query.Text.Trim().Length > 0) Schedule(); else SetStatus("索引已刷新"); } }
        catch (Exception e) when (e is not OperationCanceledException) { SetStatus("刷新失败，请重试"); }
        finally { _busy = false; }
    }
}
