using AntdUI;
using DshWebMonitor.Models;
using Panel = System.Windows.Forms.Panel;
using DshWebMonitor.Services;
using DshWebMonitor.Ui;

namespace DshWebMonitor;

/// <summary>
/// 插件管理页：安装 / 刷新 / 启用 / 停用 / 更新 / 删除；支持 stable / rc / beta / alpha / next / all 更新通道。
/// 控件启用状态只由 <see cref="AppState"/> 快照 + 当前选中行派生；读取进度统一写入可见状态行并通过
/// <see cref="StatusMessage"/> 转发给主窗体。
/// </summary>
internal sealed class PluginManagerView : UserControl
{
    private readonly MonitorController _controller;

    private readonly AntdUI.Table _grid = new();
    private readonly AntdUI.Label _status = new();
    private readonly AntdUI.Label _profileHint = new();
    private readonly AntdUI.Label _packageCaption = new();
    private readonly AntdUI.Label _channelCaption = new();
    private readonly AntdUI.Button _btnRefresh;
    private readonly AntdUI.Button _btnInstall;
    private readonly AntdUI.Input _txtPackage = new();
    private readonly AntdUI.Select _cmbChannel = new();
    private readonly AntdUI.Button _btnEnable;
    private readonly AntdUI.Button _btnDisable;
    private readonly AntdUI.Button _btnUpdate;
    private readonly AntdUI.Button _btnRemove;

    private AppState _state = new();
    private List<PluginInfo> _plugins = new();
    private PluginInfo? _selected;
    private UpdateChannel? _loadedChannel;
    private bool _loaded;
    private bool _refreshing;
    private bool _commandInFlight;
    private bool _syncingChannel;
    private bool _syncingSelection;
    private bool _refreshQueued;
    private bool _updateCheckIncomplete;
    private string? _lastLoadError;

    /// <summary>操作/错误提示，由主窗体底部状态栏显示。</summary>
    public event EventHandler<string>? StatusMessage;

    /// <summary>插件数据变化（刷新 / 操作完成）后通知主页概况。</summary>
    public event EventHandler? DataChanged;

    public PluginManagerView(MonitorController controller)
    {
        _controller = controller;
        Dock = DockStyle.Fill;
        BackColor = UiTheme.WindowBackground;

        _btnRefresh = UiTheme.CreateButton("刷新列表");
        _btnInstall = UiTheme.CreatePrimaryButton("安装");
        _btnEnable = UiTheme.CreateButton("启用");
        _btnDisable = UiTheme.CreateButton("停用");
        _btnUpdate = UiTheme.CreatePrimaryButton("更新");
        _btnRemove = UiTheme.CreateDangerButton("删除");

        _txtPackage.Font = UiTheme.BodyFont;
        _txtPackage.Height = 32;
        _txtPackage.PlaceholderText = "插件包名，例如 dsh-notify-me 或 dsh-notify-me@1.2.3";
        _txtPackage.AccessibleName = "插件包名，可为包名或“包名@版本”";
        _txtPackage.TabIndex = 0;

        _cmbChannel.Font = UiTheme.BodyFont;
        _cmbChannel.Height = 32;
        _cmbChannel.AccessibleName = "插件更新通道";
        _cmbChannel.TabIndex = 2;
        _cmbChannel.Items = new AntdUI.BaseCollection();
        foreach (var channel in UpdateChannels.Selectable)
        {
            _cmbChannel.Items.Add(UpdateChannels.ToLabel(channel));
        }

        _cmbChannel.SelectedIndex = ChannelIndex(_controller.PluginChannel);

        _btnInstall.TabIndex = 1;
        _btnRefresh.TabIndex = 3;
        _btnEnable.TabIndex = 5;
        _btnDisable.TabIndex = 6;
        _btnUpdate.TabIndex = 7;
        _btnRemove.TabIndex = 8;

        BuildUi();

        _btnRefresh.Click += OnRefreshRequested;
        _btnInstall.Click += OnInstallRequested;
        _btnEnable.Click += OnEnableRequested;
        _btnDisable.Click += OnDisableRequested;
        _btnUpdate.Click += OnUpdateRequested;
        _btnRemove.Click += OnRemoveRequested;
        _cmbChannel.SelectedIndexChanged += OnChannelChanged;
        _controller.StateChanged += OnControllerStateChanged;

        _state = _controller.State;
        ApplyAvailability();
    }

    private UpdateChannel CurrentChannel
    {
        get
        {
            var index = _cmbChannel.SelectedIndex;
            return index >= 0 && index < UpdateChannels.Selectable.Count
                ? UpdateChannels.Selectable[index]
                : UpdateChannel.Stable;
        }
    }

    private bool LocalBusy => _refreshing || _commandInFlight;

    /// <summary>供主页一键更新后刷新列表。</summary>
    public Task RefreshListAsync() => RefreshAsync();

    /// <summary>当前已加载的插件数据，供主页概况直接统计（避免重复调用 CLI）。</summary>
    public IReadOnlyList<PluginInfo> Plugins => _plugins;

    public bool HasLoaded => _loaded;

    /// <summary>最近一次加载是否有插件最新版本查询失败（此时「可更新」数量不完整，不能声称已全部最新）。</summary>
    public bool UpdateCheckIncomplete => _updateCheckIncomplete;

    /// <summary>最近一次加载插件列表的失败原因；null 表示最近一次加载成功（或尚未加载）。</summary>
    public string? LastLoadError => _lastLoadError;

    /// <summary>
    /// 加载插件列表与最新版本，只在启动时真正执行一次；之后切到本页直接复用已加载的数据，
    /// 不再重复联网检查。失败后（<c>_loaded</c> 为 false）下次进入页面可重试。
    /// </summary>
    public async Task<bool> EnsureLoadedAsync()
    {
        if (_loaded || IsDisposed || Disposing)
        {
            return _loaded;
        }

        if (_refreshing)
        {
            // 启动阶段的加载已经在这条路径上：不排队第二次检查，等它完成后界面自然拿到数据。
            return false;
        }

        _state = _controller.State;
        ApplyAvailability();
        return await RefreshAsync();
    }

    /// <summary>
    /// 主页切换更新通道时同步本页：更新下拉选中项，并在列表已加载且通道确实变化时刷新一次；
    /// 刷新由本方法直接发起，不依赖 SelectedIndexChanged，因此不会重复加载两次。
    /// </summary>
    internal void SyncChannel(UpdateChannel channel)
    {
        if (IsDisposed || Disposing)
        {
            return;
        }

        if (InvokeRequired)
        {
            TryRunOnUi(() => SyncChannel(channel));
            return;
        }

        _controller.PluginChannel = channel;
        _syncingChannel = true;
        try
        {
            var index = ChannelIndex(channel);
            if (_cmbChannel.SelectedIndex != index)
            {
                _cmbChannel.SelectedIndex = index;
            }
        }
        finally
        {
            _syncingChannel = false;
        }

        if (!_loaded || _loadedChannel == channel)
        {
            return;
        }

        _ = RefreshAfterChannelSyncAsync();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _controller.StateChanged -= OnControllerStateChanged;
        }

        base.Dispose(disposing);
    }

    private static int ChannelIndex(UpdateChannel channel)
    {
        for (var index = 0; index < UpdateChannels.Selectable.Count; index++)
        {
            if (UpdateChannels.Selectable[index] == channel)
            {
                return index;
            }
        }

        return 0;
    }

    private void BuildUi()
    {
        var toolbar = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 56,
            Padding = new Padding(14, 8, 14, 8),
            BackColor = UiTheme.WindowBackground,
            ColumnCount = 6,
            RowCount = 1,
        };
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 128f));
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        toolbar.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));

        ConfigureCaption(_packageCaption, "插件包名");
        _packageCaption.Margin = new Padding(0, 0, 6, 0);
        toolbar.Controls.Add(_packageCaption, 0, 0);

        _txtPackage.Dock = DockStyle.Fill;
        _txtPackage.Margin = new Padding(0, 4, 10, 4);
        toolbar.Controls.Add(_txtPackage, 1, 0);

        _btnInstall.Anchor = AnchorStyles.Left;
        _btnInstall.Margin = new Padding(0, 0, 10, 0);
        toolbar.Controls.Add(_btnInstall, 2, 0);

        ConfigureCaption(_channelCaption, "更新通道");
        _channelCaption.Margin = new Padding(0, 0, 6, 0);
        toolbar.Controls.Add(_channelCaption, 3, 0);

        _cmbChannel.Dock = DockStyle.Fill;
        _cmbChannel.Margin = new Padding(0, 4, 10, 4);
        toolbar.Controls.Add(_cmbChannel, 4, 0);

        _btnRefresh.Anchor = AnchorStyles.Left;
        _btnRefresh.Margin = new Padding(0);
        toolbar.Controls.Add(_btnRefresh, 5, 0);

        ConfigureGrid();
        var gridContainer = new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(14, 6, 14, 6),
            BackColor = UiTheme.WindowBackground,
        };
        gridContainer.Controls.Add(_grid);

        var bottom = new TableLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 78,
            Padding = new Padding(14, 2, 14, 8),
            BackColor = UiTheme.WindowBackground,
            ColumnCount = 1,
            RowCount = 2,
        };
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        bottom.RowStyles.Add(new RowStyle(SizeType.Absolute, 24f));
        bottom.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));

        _status.AutoSize = false;
        _status.Dock = DockStyle.Fill;
        _status.Font = UiTheme.HintFont;
        _status.ForeColor = UiTheme.TextSecondary;
        _status.TextAlign = ContentAlignment.MiddleLeft;
        _status.AutoEllipsis = true;
        _status.TabStop = false;
        _status.AccessibleName = "插件操作状态";
        _status.Text = "尚未加载插件列表，切换到本页后自动加载。";
        bottom.Controls.Add(_status, 0, 0);

        var actionRow = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = UiTheme.WindowBackground,
            ColumnCount = 2,
            RowCount = 1,
        };
        actionRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        actionRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        actionRow.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));

        _profileHint.AutoSize = false;
        _profileHint.Dock = DockStyle.Fill;
        _profileHint.Font = UiTheme.HintFont;
        _profileHint.ForeColor = UiTheme.TextSecondary;
        _profileHint.TextAlign = ContentAlignment.MiddleLeft;
        _profileHint.AutoEllipsis = true;
        _profileHint.TabStop = false;
        _profileHint.AccessibleName = "插件 profile 目录";
        _profileHint.Text = $"profile：{_controller.ProfileDirectory}";
        actionRow.Controls.Add(_profileHint, 0, 0);

        var buttons = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Anchor = AnchorStyles.Right,
            Margin = new Padding(0),
            BackColor = Color.Transparent,
        };
        _btnRemove.Margin = new Padding(6, 0, 0, 0);
        _btnUpdate.Margin = new Padding(6, 0, 0, 0);
        _btnDisable.Margin = new Padding(6, 0, 0, 0);
        _btnEnable.Margin = new Padding(0);
        buttons.Controls.Add(_btnRemove);
        buttons.Controls.Add(_btnUpdate);
        buttons.Controls.Add(_btnDisable);
        buttons.Controls.Add(_btnEnable);
        actionRow.Controls.Add(buttons, 1, 0);

        bottom.Controls.Add(actionRow, 0, 1);

        Controls.Add(gridContainer);
        Controls.Add(bottom);
        Controls.Add(toolbar);
    }

    private static void ConfigureCaption(AntdUI.Label label, string text)
    {
        label.Text = text;
        label.AutoSize = true;
        label.Font = UiTheme.HintFont;
        label.ForeColor = UiTheme.TextSecondary;
        label.Anchor = AnchorStyles.Left;
        label.TabStop = false;
    }

    private void ConfigureGrid()
    {
        _grid.Dock = DockStyle.Fill;
        _grid.Font = UiTheme.BodyFont;
        _grid.BackColor = Color.White;
        _grid.Radius = 8;
        _grid.Bordered = false;
        _grid.AutoSizeColumnsMode = ColumnsMode.Fill;
        _grid.Gap = 0;
        _grid.RowHeight = 40;
        _grid.RowHeightHeader = 40;
        _grid.MultipleRows = false;
        _grid.LostFocusClearSelection = false;
        _grid.AccessibleName = "已安装插件列表";
        _grid.TabIndex = 4;
        _grid.Columns = new ColumnCollection
        {
            new Column("Name", "插件"),
            new Column("Current", "当前版本") { Width = "128", Align = ColumnAlign.Center },
            new Column("Latest", "最新版本") { Width = "128", Align = ColumnAlign.Center },
            new Column("State", "状态")
            {
                Width = "110",
                Align = ColumnAlign.Center,
                Render = (_, record, _) => record is PluginRow row
                    ? row.Plugin.IsEnabled
                        ? new CellTag("已启用", TTypeMini.Success)
                        : new CellTag("已停用")
                    : null,
            },
        };
        _grid.CellClick += OnCellClick;
        _grid.SelectIndexChanged += (_, _) => SyncSelectionFromTable();
    }

    // ------------------------------------------------------------------ 数据刷新

    private async Task<bool> RefreshAsync()
    {
        if (IsDisposed || Disposing)
        {
            return false;
        }

        if (_refreshing)
        {
            // 正在刷新时再次请求（例如连续切通道）：当前刷新结束后补一次，避免展示旧通道数据。
            _refreshQueued = true;
            return false;
        }

        if (!_state.CanRunPluginCommand)
        {
            ShowStatus(BusyHint("刷新插件列表"));
            return false;
        }

        var channel = CurrentChannel;
        var selectedName = _selected?.Name;
        var scrollTop = _grid.ScrollBar.ValueY;

        _refreshing = true;
        ApplyAvailability();
        var succeeded = false;
        var plugins = Array.Empty<PluginInfo>();
        try
        {
            ShowStatus($"正在读取插件信息（通道 {UpdateChannels.ToLabel(channel)}）…");
            var loaded = await _controller.LoadPluginInfoAsync(channel, ShowStatus);
            if (IsDisposed || Disposing)
            {
                return false;
            }

            plugins = loaded as PluginInfo[] ?? loaded.ToArray();
            var failedChecks = _controller.LastPluginUpdateCheckFailures;
            _updateCheckIncomplete = failedChecks > 0;
            BindPlugins(plugins, selectedName, scrollTop);
            // 只有成功拿到结果才算加载完成：失败时下次进入页面还能重试。
            _loaded = true;
            _loadedChannel = channel;
            _lastLoadError = null;
            succeeded = true;
            ShowStatus(
                $"共 {plugins.Length} 个插件，{plugins.Count(plugin => plugin.IsEnabled)} 个已启用"
                + $"（通道 {UpdateChannels.ToLabel(channel)}）"
                + (failedChecks > 0 ? $"；{failedChecks} 个最新版本查询失败，详见日志页" : string.Empty));
        }
        catch (Exception ex)
        {
            _lastLoadError = string.IsNullOrWhiteSpace(ex.Message) ? "未知错误" : ex.Message.Trim();
            if (!IsDisposed && !Disposing)
            {
                ReportFailure("读取插件信息", ex);
            }
        }
        finally
        {
            _refreshing = false;
            if (!IsDisposed && !Disposing)
            {
                ApplyAvailability();
            }
        }

        if (IsDisposed || Disposing)
        {
            return succeeded;
        }

        DataChanged?.Invoke(this, EventArgs.Empty);

        if (_refreshQueued)
        {
            _refreshQueued = false;
            if (!_state.CanRunPluginCommand)
            {
                ApplyAvailability();
                return succeeded;
            }

            await RefreshAsync();
        }

        return succeeded;
    }

    private void BindPlugins(IReadOnlyList<PluginInfo> plugins, string? selectedName, int scrollTop)
    {
        _plugins = plugins.ToList();
        var rows = _plugins.Select(plugin => new PluginRow(plugin)).ToList();
        // DataSource 绑定会清空选中；随后按插件名恢复，而不是延迟回调里强制清空。
        _grid.DataSource = rows;

        var index = selectedName is null
            ? -1
            : _plugins.FindIndex(plugin =>
                string.Equals(plugin.Name, selectedName, StringComparison.OrdinalIgnoreCase));

        if (index < 0)
        {
            _selected = null;
            _grid.SelectedIndex = -1;
            PostUi(() => RestoreViewport(-1, scrollTop));
            ApplyAvailability();
            return;
        }

        _selected = _plugins[index];
        _syncingSelection = true;
        try
        {
            _grid.SelectedIndex = index + 1; // AntdUI.Table.SelectedIndex 从 1 开始
        }
        finally
        {
            _syncingSelection = false;
        }

        PostUi(() => RestoreViewport(index, scrollTop));
        ApplyAvailability();
    }

    /// <summary>布局完成后恢复滚动位置，并保证之前选中的行仍在可视区域内。</summary>
    private void RestoreViewport(int selectedIndex, int scrollTop)
    {
        if (IsDisposed || Disposing)
        {
            return;
        }

        try
        {
            _grid.Refresh();
            var scrollBar = _grid.ScrollBar;
            scrollBar.ValueY = Math.Clamp(scrollTop, 0, Math.Max(0, scrollBar.MaxY));
            if (selectedIndex >= 0)
            {
                _grid.ScrollLine(selectedIndex, false);
            }
        }
        catch (ObjectDisposedException)
        {
        }
        catch (InvalidOperationException)
        {
        }
    }

    // ------------------------------------------------------------------ 选中与启用状态

    private void OnCellClick(object? sender, TableClickEventArgs e)
    {
        // 只有点到真实行才更新选中；表头 / 空白区域的点击交给 SelectIndexChanged 处理。
        if (e.Record is PluginRow row)
        {
            _selected = row.Plugin;
            ApplyAvailability();
        }
    }

    private void SyncSelectionFromTable()
    {
        if (_syncingSelection)
        {
            return;
        }

        var selected = _grid.SelectedsReal();
        _selected = selected.Length > 0 ? (selected[0] as PluginRow)?.Plugin : null;
        ApplyAvailability();
    }

    /// <summary>
    /// 唯一的控件启用派生点：AppState 快照（CanRunPluginCommand / IsBusy）+ 当前选中行 + 本页进行中的异步任务。
    /// </summary>
    private void ApplyAvailability()
    {
        if (IsDisposed || Disposing)
        {
            return;
        }

        var state = _state;
        var localBusy = LocalBusy;
        var canRunCommand = state.CanRunPluginCommand && !localBusy;
        var plugin = _selected;
        var actionable = canRunCommand && plugin is not null;

        _btnRefresh.Enabled = canRunCommand;
        _btnInstall.Enabled = canRunCommand;
        _btnEnable.Enabled = actionable && !plugin!.IsEnabled;
        _btnDisable.Enabled = actionable && plugin!.IsEnabled;
        _btnUpdate.Enabled = actionable && plugin!.HasUpdate;
        _btnRemove.Enabled = actionable;

        // 全局忙碌时输入框与通道下拉也必须禁用，避免在写操作窗口内发起新的 CLI 调用。
        _txtPackage.Enabled = !state.IsBusy && !localBusy;
        _cmbChannel.Enabled = !state.IsBusy && !localBusy;
    }

    private void OnControllerStateChanged(AppState state)
    {
        if (IsDisposed || Disposing)
        {
            return;
        }

        if (InvokeRequired)
        {
            // StateChanged 可能来自后台线程，先封送到 UI 线程再改控件；窗口销毁时 BeginInvoke 抛出即忽略。
            TryRunOnUi(() => OnControllerStateChanged(state));
            return;
        }

        _state = state;
        ApplyAvailability();
    }

    // ------------------------------------------------------------------ 事件处理

    private async void OnChannelChanged(object? sender, EventArgs e)
    {
        try
        {
            if (_syncingChannel)
            {
                return;
            }

            var channel = CurrentChannel;
            _controller.PluginChannel = channel;
            if (!_loaded)
            {
                // 尚未加载：切到本页时 EnsureLoadedAsync 会按新通道加载，无需立即刷新。
                return;
            }

            if (_loadedChannel == channel && !_refreshing)
            {
                return;
            }

            await RefreshAsync();
        }
        catch (Exception ex)
        {
            ReportFailure("切换更新通道", ex);
        }
    }

    private async void OnRefreshRequested(object? sender, EventArgs e)
    {
        try
        {
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            ReportFailure("刷新插件列表", ex);
        }
    }

    private async void OnInstallRequested(object? sender, EventArgs e)
    {
        try
        {
            if (_commandInFlight || _refreshing)
            {
                return;
            }

            if (!_state.CanRunPluginCommand)
            {
                ShowStatus(BusyHint("安装插件"));
                return;
            }

            if (!PluginService.TryParsePackageSpec(_txtPackage.Text, out var package, out var pinnedVersion))
            {
                ShowStatus(
                    "安装失败：包名或版本格式不合法。请输入 npm 包名（例如 dsh-notify-me），"
                    + "或“包名@版本”（例如 dsh-notify-me@1.2.3）。");
                return;
            }

            // 在第一次 await 之前同步占位：版本解析可能耗时数秒，若等到解析返回后才置标志，
            // 期间重复点击会再次通过 _commandInFlight 检查，两个安装流程互相改写按钮状态。
            _commandInFlight = true;
            ApplyAvailability();
            try
            {
                var channel = CurrentChannel;
                string? version = pinnedVersion;
                ChannelTarget? target = null;
                if (version is null)
                {
                    ShowStatus($"正在解析 {package} 在通道 {UpdateChannels.ToLabel(channel)} 下的目标版本…");
                    target = await _controller.ResolveChannelTargetAsync(package, channel);
                    if (IsDisposed || Disposing)
                    {
                        return;
                    }

                    version = target?.Version;
                }

                var installed = false;
                string? failure = null;
                try
                {
                    ShowStatus($"正在安装 {package}{(version is null ? string.Empty : $" v{version}")}…");
                    // 传给控制器的必须是纯包名 + 独立版本号，profile bundles 只会写入纯包名。
                    installed = await _controller.InstallPluginAsync(package, version);
                }
                catch (Exception ex)
                {
                    failure = ex.Message;
                }

                if (IsDisposed || Disposing)
                {
                    return;
                }

                await RefreshAsync();

                if (!installed)
                {
                    ReportFailure("安装插件", null, failure ?? $"插件 {package} 未安装成功");
                    return;
                }

                var versionLabel = pinnedVersion is not null
                    ? $"指定版本 v{pinnedVersion}"
                    : target is not null
                        ? $"通道 {UpdateChannels.ToLabel(channel)}，v{target.Version}"
                        : "未解析到通道版本，按 latest 安装";
                _txtPackage.Text = string.Empty;
                ShowStatus($"{package}：安装完成（{versionLabel}；重新启动 dsh web 后生效）");
            }
            finally
            {
                _commandInFlight = false;
                ApplyAvailability();
            }
        }
        catch (Exception ex)
        {
            ReportFailure("安装插件", ex);
        }
    }

    private async void OnEnableRequested(object? sender, EventArgs e)
    {
        try
        {
            await RunOperationAsync("启用", plugin => _controller.SetPluginEnabledAsync(plugin.Name, true));
        }
        catch (Exception ex)
        {
            ReportFailure("启用插件", ex);
        }
    }

    private async void OnDisableRequested(object? sender, EventArgs e)
    {
        try
        {
            await RunOperationAsync("停用", plugin => _controller.SetPluginEnabledAsync(plugin.Name, false));
        }
        catch (Exception ex)
        {
            ReportFailure("停用插件", ex);
        }
    }

    private async void OnUpdateRequested(object? sender, EventArgs e)
    {
        try
        {
            if (_commandInFlight || _refreshing)
            {
                return;
            }

            var before = _selected;
            if (before is null)
            {
                ShowStatus("请先在列表中选中一个插件，再执行更新操作");
                return;
            }

            if (!before.HasUpdate)
            {
                ShowStatus(before.LatestVersion is null
                    ? $"{before.Name}：未获取到当前通道的目标版本，请刷新列表后重试"
                    : $"{before.Name}：已是当前通道的最新版本 {before.Version}");
                return;
            }

            var succeeded = await RunOperationAsync(
                "更新",
                plugin => _controller.UpdatePluginAsync(plugin.Name, plugin.LatestVersion));
            if (!succeeded)
            {
                return;
            }

            var after = _plugins.FirstOrDefault(plugin =>
                string.Equals(plugin.Name, before.Name, StringComparison.OrdinalIgnoreCase));
            if (after is null)
            {
                return;
            }

            ShowStatus(string.Equals(after.Version, before.Version, StringComparison.OrdinalIgnoreCase)
                ? $"{before.Name}：更新命令已执行，但版本仍为 {after.Version}，请查看日志页确认"
                : $"{before.Name}：已更新 {before.Version} → {after.Version}（重新启动 dsh web 后生效）");
        }
        catch (Exception ex)
        {
            ReportFailure("更新插件", ex);
        }
    }

    private async void OnRemoveRequested(object? sender, EventArgs e)
    {
        try
        {
            await RunOperationAsync("删除", plugin => _controller.RemovePluginAsync(plugin.Name));
        }
        catch (Exception ex)
        {
            ReportFailure("删除插件", ex);
        }
    }

    private async Task<bool> RunOperationAsync(string actionName, Func<PluginInfo, Task<bool>> operation)
    {
        if (IsDisposed || Disposing)
        {
            return false;
        }

        if (_commandInFlight || _refreshing)
        {
            return false;
        }

        if (!_state.CanRunPluginCommand)
        {
            ShowStatus(BusyHint($"{actionName}插件"));
            return false;
        }

        var plugin = _selected;
        if (plugin is null)
        {
            ShowStatus($"请先在列表中选中一个插件，再执行{actionName}操作");
            return false;
        }

        _commandInFlight = true;
        ApplyAvailability();
        var succeeded = false;
        string? failure = null;
        try
        {
            ShowStatus($"正在{actionName} {plugin.Name}…");
            succeeded = await operation(plugin);
        }
        catch (Exception ex)
        {
            failure = ex.Message;
        }
        finally
        {
            _commandInFlight = false;
            ApplyAvailability();
        }

        await RefreshAsync();

        if (succeeded)
        {
            ShowStatus($"{plugin.Name}：{actionName}完成（重新启动 dsh web 后生效）");
        }
        else
        {
            ReportFailure(
                $"{actionName}插件",
                null,
                failure ?? $"插件 {plugin.Name} {actionName}未成功，请查看日志页确认原因");
        }

        return succeeded;
    }

    // ------------------------------------------------------------------ 状态提示

    private async Task RefreshAfterChannelSyncAsync()
    {
        try
        {
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            ReportFailure("读取插件信息", ex);
        }
    }

    private void ShowStatus(string message)
    {
        if (IsDisposed || Disposing)
        {
            return;
        }

        if (InvokeRequired)
        {
            // 读取进度回调可能来自后台线程，同样需要封送。
            TryRunOnUi(() => ShowStatus(message));
            return;
        }

        _status.Text = message;
        StatusMessage?.Invoke(this, message);
    }

    private void ReportFailure(string action, Exception? error = null, string? reason = null)
    {
        var detail = string.IsNullOrWhiteSpace(reason) ? error?.Message : reason;
        var message = string.IsNullOrWhiteSpace(detail)
            ? $"{action}失败：原因未知。请查看日志页获取详细信息"
            : $"{action}失败：{detail.Trim()}。请查看日志页获取详细信息";
        ShowStatus(message);
    }

    private string BusyHint(string action) =>
        string.IsNullOrWhiteSpace(_state.BusyOperation)
            ? $"当前有其它操作正在进行，无法{action}，请稍后再试"
            : $"{_state.BusyOperation}，暂时无法{action}，请稍后再试";

    private void PostUi(Action action)
    {
        if (IsDisposed || Disposing || !IsHandleCreated)
        {
            return;
        }

        try
        {
            BeginInvoke(action);
        }
        catch (ObjectDisposedException)
        {
        }
        catch (InvalidOperationException)
        {
            // 窗口销毁过程中不再排队。
        }
    }

    private void TryRunOnUi(Action action)
    {
        if (IsDisposed || Disposing)
        {
            return;
        }

        if (!InvokeRequired)
        {
            action();
            return;
        }

        if (!IsHandleCreated)
        {
            // 句柄尚未创建：丢弃回调，进入页面时用控制器最新快照刷新。
            return;
        }

        try
        {
            BeginInvoke(action);
        }
        catch (ObjectDisposedException)
        {
        }
        catch (InvalidOperationException)
        {
            // 窗口销毁过程中丢弃回调。
        }
    }

    /// <summary>表格行数据：列 Key 绑定同名属性，Plugin 保留原始信息。</summary>
    private sealed class PluginRow
    {
        public PluginRow(PluginInfo plugin)
        {
            Plugin = plugin;
            Name = plugin.Name;
            Current = plugin.Version;
            Latest = plugin.LatestVersion is null
                ? "-"
                : plugin.HasUpdate
                    ? plugin.LatestVersion
                    : string.Empty;
            State = plugin.IsEnabled ? "已启用" : "已停用";
        }

        public PluginInfo Plugin { get; }

        public string Name { get; }

        public string Current { get; }

        public string Latest { get; }

        public string State { get; }
    }
}
