using AntdUI;
using DshWebMonitor.Models;
using DshWebMonitor.Services;
using DshWebMonitor.Ui;
using ContextMenuStrip = System.Windows.Forms.ContextMenuStrip;
using Panel = System.Windows.Forms.Panel;

namespace DshWebMonitor;

/// <summary>
/// 主窗体：主页 / 插件管理 / 日志三个 Tab，主页由状态卡、插件概况卡与 npm 卡组成，
/// 底部状态栏显示当前操作，任务栏通知区域提供与主窗体同步的五项菜单。
/// 控件启用状态、文案与加载态全部派生自同一份 <see cref="AppState"/> 快照；
/// 托盘图标是否存在以 <see cref="TrayIconHost"/> 的真实注册结果为准，不再凭完整性级别预判。
/// </summary>
internal sealed class MainForm : Form
{
    private const int DefaultClientWidth = 820;
    private const int DefaultClientHeight = 540;

    private static readonly Color AccentBlue = Color.FromArgb(9, 88, 217);

    private readonly MonitorController _controller;
    private readonly LogView _logView;
    private readonly PluginManagerView _pluginView;

    /// <summary>false 表示测试模式：不启动后台检测、不访问网络 / 进程、不弹模态框。</summary>
    private readonly bool _autoStart;

    private readonly StatusDot _statusDot = new();
    private readonly StatusDot _statusBarDot = new();
    private Panel _statusBar = null!;
    private readonly AntdUI.Label _lblServerStatus = new();
    private readonly AntdUI.Label _lblServerAddress = new();
    private readonly AntdUI.Label _lblServerPid = new();
    private readonly AntdUI.Tag _tagOwnership = new();
    private readonly AntdUI.Label _lblOperation = new();
    private readonly AntdUI.Label _lblVersion = new();
    private readonly AntdUI.Tag _tagInstallState = new();
    private readonly AntdUI.Tag _tagNpmVersionText = new();
    private readonly AntdUI.Tag _tagNewVersion = new();
    private AntdUI.Label _npmNameLabel = null!;
    private Panel _npmNameRow = null!;
    private readonly AntdUI.Label _lblNpmHint = new();
    private readonly AntdUI.Select _cmbNpmChannel = new();
    private readonly AntdUI.Tabs _tabs = new();

    private readonly AntdUI.Label _lblStatInstalled = new();
    private readonly AntdUI.Label _lblStatEnabled = new();
    private readonly AntdUI.Label _lblStatUpdatable = new();
    private AntdUI.Button _btnUpdateAll = null!;
    private readonly AntdUI.Label _lblBulkResult = new();
    private readonly AntdUI.Label _lblSummaryLoading = new();
    private AntdUI.Label _summaryProfile = null!;
    private FlowLayoutPanel _summaryStats = null!;
    private Panel _updateButtonHost = null!;

    private AntdUI.Panel _statusCard = null!;
    private AntdUI.Panel _summaryCard = null!;
    private AntdUI.Panel _npmCard = null!;
    private TableLayoutPanel _homeGrid = null!;

    private AntdUI.Button _btnStart = null!;
    private AntdUI.Button _btnStop = null!;
    private AntdUI.Button _btnRestart = null!;
    private AntdUI.Button _btnOpenWebUi = null!;
    private AntdUI.Button _btnInstall = null!;
    private AntdUI.Button _btnCheckNpmUpdates = null!;
    private AntdUI.Button _btnUpdateNpm = null!;

    private TrayIconHost? _trayHost;
    private ContextMenuStrip _trayMenu = null!;
    private ToolStripMenuItem _trayStart = null!;
    private ToolStripMenuItem _trayStop = null!;
    private ToolStripMenuItem _trayRestart = null!;
    private ToolStripMenuItem _trayOpenWebUi = null!;
    private ToolStripMenuItem _trayExit = null!;

    private string? _environmentHint;
    private bool _trayAvailable;
    private bool _trayRegistrationSettling;
    private bool _exiting;
    private bool _ready;
    private bool _startupStarted;
    private bool _resourcesDisposed;
    private bool _initializingChannel = true;
    private bool _summaryRefreshing;
    private bool _bulkUpdating;
    private bool _updatesChecked;
    private bool _updateCheckIncomplete;
    private bool _incompleteNoted;
    private int _updatableCount;

    internal MainForm()
        : this(new MonitorController())
    {
    }

    /// <param name="controller">唯一的编排者与状态持有者，由调用方负责生命周期（窗体关闭时释放）。</param>
    /// <param name="autoStart">false 为测试模式：不启动检测、不联网、不弹确认框。</param>
    /// <param name="trayAvailableOverride">仅测试使用：覆盖「是否尝试登记托盘图标」的预判。</param>
    internal MainForm(MonitorController controller, bool autoStart = true, bool? trayAvailableOverride = null)
    {
        _controller = controller ?? throw new ArgumentNullException(nameof(controller));
        _autoStart = autoStart;

        Text = "dsh Web Monitor";
        Icon = AppIcons.CreateFormIcon();
        Font = UiTheme.BodyFont;
        BackColor = UiTheme.WindowBackground;
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        MinimizeBox = true;
        ClientSize = new Size(DefaultClientWidth, DefaultClientHeight);
        StartPosition = FormStartPosition.CenterScreen;
        DoubleBuffered = true;

        _logView = new LogView();
        _pluginView = new PluginManagerView(_controller);
        _pluginView.DataChanged += OnPluginDataChanged;
        _pluginView.StatusMessage += OnPluginStatusMessage;

        BuildUi();
        BuildTray(trayAvailableOverride ?? TokenIntegrity.IsAtLeastMedium);

        if (!_trayAvailable)
        {
            // 托盘不可用时只能「关闭即退出」，这条提示同时出现在日志页与状态栏。
            _environmentHint ??= "系统托盘图标不可用：关闭窗口将退出程序（dsh web 运行中会先确认）";
            AppendSystemLog(_environmentHint);
        }

        _controller.StateChanged += OnStateChanged;
        _controller.ServerLog += OnServerLog;
        _controller.PluginLog += OnPluginLog;

        SizeChanged += (_, _) => RelayoutAll();
        VisibleChanged += (_, _) =>
        {
            if (Visible)
            {
                // 从托盘恢复后必须重新布局，避免隐藏期间的尺寸/DPI 变化留下越界坐标。
                RelayoutAll();
            }
        };

        ApplyState(_controller.State);
        _ready = true;
    }

    // ------------------------------------------------------------------ 测试可见的托盘状态

    /// <summary>托盘能力是否可用（预判通过且图标真实登记成功）。</summary>
    internal bool TrayAvailable => _trayAvailable;

    /// <summary>托盘图标当前是否真的在通知区域。</summary>
    internal bool TrayRegistered => _trayHost?.IsRegistered == true;

    // ------------------------------------------------------------------ 界面构建

    private UpdateChannel CurrentNpmChannel =>
        UpdateChannels.Selectable[Math.Max(0, _cmbNpmChannel.SelectedIndex)];

    private void BuildUi()
    {
        _tabs.Dock = DockStyle.Fill;
        _tabs.Font = UiTheme.BodyFont;
        _tabs.Gap = 12;
        _tabs.Type = TabType.Line;
        _tabs.SelectedIndexChanged += OnTabChanged;

        var statusPage = new AntdUI.TabPage
        {
            Text = "主页",
            BackColor = UiTheme.WindowBackground,
            Padding = new Padding(20, 16, 20, 16),
        };
        BuildHomePage(statusPage);

        var pluginsPage = new AntdUI.TabPage
        {
            Text = "插件管理",
            BackColor = UiTheme.WindowBackground,
            Padding = new Padding(20, 16, 20, 16),
        };
        pluginsPage.Controls.Add(_pluginView);

        var logsPage = new AntdUI.TabPage
        {
            Text = "日志",
            BackColor = UiTheme.WindowBackground,
            Padding = new Padding(20, 16, 20, 16),
        };
        logsPage.Controls.Add(_logView);

        _tabs.Pages.Add(statusPage);
        _tabs.Pages.Add(pluginsPage);
        _tabs.Pages.Add(logsPage);

        Controls.Add(_tabs);
        Controls.Add(BuildStatusBar());
    }

    private void BuildHomePage(AntdUI.TabPage page)
    {
        _homeGrid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 2,
            BackColor = UiTheme.WindowBackground,
            Padding = new Padding(0),
        };
        _homeGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 64f));
        _homeGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 36f));
        _homeGrid.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        _homeGrid.RowStyles.Add(new RowStyle(SizeType.Absolute, S(128)));

        _statusCard = BuildStatusCard();
        _statusCard.Margin = new Padding(0, 0, S(12), S(12));
        _homeGrid.Controls.Add(_statusCard, 0, 0);

        _summaryCard = BuildSummaryCard();
        _summaryCard.Margin = new Padding(0, 0, 0, S(12));
        _homeGrid.Controls.Add(_summaryCard, 1, 0);

        _npmCard = BuildNpmCard();
        _npmCard.Margin = new Padding(0);
        _homeGrid.Controls.Add(_npmCard, 0, 1);
        _homeGrid.SetColumnSpan(_npmCard, 2);

        page.Controls.Add(_homeGrid);
    }

    private AntdUI.Panel BuildStatusCard()
    {
        var card = UiTheme.CreateCard();
        card.Dock = DockStyle.Fill;

        _statusDot.Location = new Point(S(20), S(26));
        _statusDot.Size = new Size(S(10), S(10));

        _lblServerStatus.AutoSize = true;
        _lblServerStatus.Font = UiTheme.CompactStatusFont;
        _lblServerStatus.ForeColor = UiTheme.TextPrimary;
        _lblServerStatus.Location = new Point(S(36), S(16));

        _tagOwnership.Text = "由本程序启动";
        _tagOwnership.Type = TTypeMini.Success;
        _tagOwnership.Font = UiTheme.HintFont;
        _tagOwnership.AutoSize = true;
        _tagOwnership.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _tagOwnership.Location = new Point(S(330), S(16));

        _lblServerAddress.Text = $"127.0.0.1:{_controller.Port}";
        _lblServerAddress.AutoSize = true;
        _lblServerAddress.Font = UiTheme.MonoFont;
        _lblServerAddress.ForeColor = UiTheme.TextSecondary;

        var addressSeparator = new AntdUI.Label
        {
            Text = "·",
            AutoSize = true,
            Font = UiTheme.HintFont,
            ForeColor = UiTheme.Muted,
        };

        _lblServerPid.Text = "未监听";
        _lblServerPid.AutoSize = true;
        _lblServerPid.Font = UiTheme.MonoFont;
        _lblServerPid.ForeColor = UiTheme.TextSecondary;

        var addressFlow = new FlowLayoutPanel
        {
            AutoSize = true,
            WrapContents = false,
            BackColor = Color.Transparent,
            Location = new Point(S(36), S(58)),
        };
        addressFlow.Controls.Add(_lblServerAddress);
        addressFlow.Controls.Add(addressSeparator);
        addressFlow.Controls.Add(_lblServerPid);

        var actionsHost = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = S(64),
            BackColor = Color.Transparent,
        };
        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            WrapContents = false,
            BackColor = Color.Transparent,
            Padding = new Padding(S(18), S(8), S(18), S(12)),
        };

        _btnStart = UiTheme.CreatePrimaryButton("启动");
        _btnStop = UiTheme.CreateDangerButton("停止");
        _btnRestart = UiTheme.CreateButton("重新启动");
        _btnOpenWebUi = UiTheme.CreatePrimaryButton("打开 Web UI");

        _btnStart.Click += OnStartRequested;
        _btnStop.Click += OnStopRequested;
        _btnRestart.Click += OnRestartRequested;
        _btnOpenWebUi.Click += OnOpenWebUiRequested;

        actions.Controls.Add(_btnStart);
        actions.Controls.Add(_btnStop);
        actions.Controls.Add(_btnRestart);
        actions.Controls.Add(_btnOpenWebUi);
        actionsHost.Controls.Add(actions);

        card.Controls.Add(_statusDot);
        card.Controls.Add(_lblServerStatus);
        card.Controls.Add(_tagOwnership);
        card.Controls.Add(addressFlow);
        card.Controls.Add(actionsHost);

        card.SizeChanged += (_, _) => PositionOwnershipTag();
        return card;
    }

    private void PositionOwnershipTag()
    {
        if (_statusCard is null || _statusCard.IsDisposed)
        {
            return;
        }

        var width = _statusCard.ClientSize.Width;
        if (width <= 0 || !_tagOwnership.Visible)
        {
            return;
        }

        _tagOwnership.Location = new Point(
            Math.Max(S(20), width - _tagOwnership.Width - S(20)),
            S(16));
    }

    private AntdUI.Panel BuildSummaryCard()
    {
        var card = UiTheme.CreateCard();
        card.Dock = DockStyle.Fill;

        var title = new AntdUI.Label
        {
            Text = "插件概况",
            AutoSize = true,
            Font = new Font("Microsoft YaHei UI", 10.5f, FontStyle.Bold),
            ForeColor = UiTheme.TextPrimary,
            Location = new Point(S(18), S(16)),
        };

        _summaryProfile = new AntdUI.Label
        {
            Text = $"profile：{AppConstants.PluginProfile}",
            AutoSize = true,
            Font = UiTheme.HintFont,
            ForeColor = UiTheme.TextSecondary,
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
            Location = new Point(S(150), S(18)),
        };

        _summaryStats = new FlowLayoutPanel
        {
            AutoSize = false,
            Size = new Size(S(248), S(36)),
            WrapContents = false,
            BackColor = Color.Transparent,
            Location = new Point(S(18), S(54)),
        };
        _summaryStats.Controls.Add(BuildStat(_lblStatInstalled, "已安装"));
        _summaryStats.Controls.Add(BuildStatSeparator());
        _summaryStats.Controls.Add(BuildStat(_lblStatEnabled, "已启用"));
        _summaryStats.Controls.Add(BuildStatSeparator());
        _summaryStats.Controls.Add(BuildStat(_lblStatUpdatable, "可更新"));
        _lblStatUpdatable.ForeColor = AccentBlue;

        _btnUpdateAll = UiTheme.CreateButton("一键更新");
        _btnUpdateAll.Font = UiTheme.BodyFont;
        _btnUpdateAll.Height = S(36);
        _btnUpdateAll.Width = S(104);
        _btnUpdateAll.BackColor = Color.White;
        _btnUpdateAll.ForeColor = UiTheme.TextPrimary;
        _btnUpdateAll.BorderWidth = 1;
        _btnUpdateAll.DefaultBorderColor = UiTheme.Border;
        _btnUpdateAll.Location = new Point(0, 0);
        _btnUpdateAll.Visible = false;
        _btnUpdateAll.Click += OnUpdateAllRequested;

        // 加载提示与「一键更新」按钮占同一位置，互斥显示（不允许两张内容叠画在一起）。
        _lblSummaryLoading.AutoSize = false;
        _lblSummaryLoading.Font = UiTheme.HintFont;
        _lblSummaryLoading.ForeColor = UiTheme.TextSecondary;
        _lblSummaryLoading.BackColor = Color.White;
        _lblSummaryLoading.TextAlign = ContentAlignment.MiddleLeft;
        _lblSummaryLoading.AutoEllipsis = true;
        _lblSummaryLoading.Location = new Point(S(18), S(112));
        _lblSummaryLoading.Size = new Size(S(260), S(22));
        _lblSummaryLoading.Text = "正在读取插件信息…";
        _lblSummaryLoading.Visible = false;

        _updateButtonHost = new Panel
        {
            BackColor = Color.Transparent,
            Location = new Point(S(18), S(112)),
            Size = new Size(S(200), S(36)),
            Visible = false,
        };
        _updateButtonHost.Controls.Add(_btnUpdateAll);

        _lblBulkResult.AutoSize = false;
        _lblBulkResult.Font = UiTheme.HintFont;
        _lblBulkResult.ForeColor = UiTheme.TextSecondary;
        _lblBulkResult.BackColor = Color.White;
        _lblBulkResult.AutoEllipsis = true;
        _lblBulkResult.Location = new Point(S(18), S(158));
        _lblBulkResult.Size = new Size(S(200), S(48));

        card.Controls.Add(title);
        card.Controls.Add(_summaryProfile);
        card.Controls.Add(_summaryStats);
        card.Controls.Add(_lblSummaryLoading);
        card.Controls.Add(_updateButtonHost);
        card.Controls.Add(_lblBulkResult);

        card.SizeChanged += (_, _) => LayoutSummaryCard();
        return card;
    }

    private FlowLayoutPanel BuildStat(AntdUI.Label number, string labelText)
    {
        number.Text = "—";
        number.AutoSize = true;
        number.Font = UiTheme.StatNumberFont;
        number.ForeColor = UiTheme.TextPrimary;
        number.Margin = new Padding(0, 0, S(4), 0);

        var label = new AntdUI.Label
        {
            Text = labelText,
            AutoSize = true,
            Font = UiTheme.HintFont,
            ForeColor = UiTheme.TextSecondary,
            Margin = new Padding(0, S(6), 0, 0),
        };

        var group = new FlowLayoutPanel
        {
            AutoSize = false,
            Width = S(74),
            Height = S(32),
            WrapContents = false,
            BackColor = Color.Transparent,
            Margin = new Padding(0),
        };
        group.Controls.Add(number);
        group.Controls.Add(label);
        return group;
    }

    private Panel BuildStatSeparator() => new()
    {
        Width = S(1),
        Height = S(20),
        BackColor = UiTheme.Border,
        Margin = new Padding(S(6), S(7), S(6), 0),
    };

    private AntdUI.Panel BuildNpmCard()
    {
        var card = UiTheme.CreateCard();
        card.Dock = DockStyle.Fill;

        var mark = new NpmMark
        {
            Location = new Point(S(20), S(30)),
            Size = new Size(S(34), S(34)),
        };
        _npmNameLabel = new AntdUI.Label
        {
            Text = AppConstants.NpmPackage,
            AutoSize = true,
            Font = new Font("Consolas", 10f, FontStyle.Bold),
            ForeColor = UiTheme.TextPrimary,
            Location = new Point(0, S(2)),
        };

        _tagInstallState.Text = "检测中…";
        _tagInstallState.Type = TTypeMini.Default;
        _tagInstallState.Font = UiTheme.HintFont;
        _tagInstallState.AutoSize = true;

        _tagNpmVersionText.Text = "v—";
        _tagNpmVersionText.Type = TTypeMini.Info;
        _tagNpmVersionText.Font = UiTheme.HintFont;
        _tagNpmVersionText.AutoSize = true;

        _tagNewVersion.Type = TTypeMini.Warn;
        _tagNewVersion.Font = UiTheme.HintFont;
        _tagNewVersion.AutoSize = true;
        _tagNewVersion.Visible = false;

        _npmNameRow = new Panel
        {
            BackColor = Color.Transparent,
            Location = new Point(S(66), S(20)),
            Size = new Size(S(320), S(48)),
        };
        _npmNameRow.Controls.Add(_npmNameLabel);
        _npmNameRow.Controls.Add(_tagInstallState);
        _npmNameRow.Controls.Add(_tagNpmVersionText);
        _npmNameRow.Controls.Add(_tagNewVersion);

        _cmbNpmChannel.Font = UiTheme.BodyFont;
        _cmbNpmChannel.AutoSize = false;
        _cmbNpmChannel.Height = S(32);
        _cmbNpmChannel.Width = S(104);
        _cmbNpmChannel.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _cmbNpmChannel.Items = new AntdUI.BaseCollection();
        foreach (var channel in UpdateChannels.Selectable)
        {
            _cmbNpmChannel.Items.Add(UpdateChannels.ToLabel(channel));
        }

        _cmbNpmChannel.SelectedIndex =
            Math.Max(0, UpdateChannels.Selectable.ToList().IndexOf(_controller.PluginChannel));
        _initializingChannel = false;
        _cmbNpmChannel.SelectedIndexChanged += OnNpmChannelChanged;

        _btnInstall = UiTheme.CreatePrimaryButton("下载安装");
        _btnCheckNpmUpdates = UiTheme.CreateButton("检查更新");
        _btnUpdateNpm = UiTheme.CreatePrimaryButton("更新");
        _btnInstall.Click += OnInstallNpmRequested;
        _btnCheckNpmUpdates.Click += OnCheckNpmUpdatesRequested;
        _btnUpdateNpm.Click += OnUpdateNpmRequested;

        var hintHost = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = S(34),
            BackColor = Color.Transparent,
        };
        hintHost.Paint += (_, e) =>
        {
            using var pen = new Pen(UiTheme.Border);
            e.Graphics.DrawLine(pen, 0, 0, hintHost.ClientSize.Width, 0);
        };
        hintHost.SizeChanged += (_, _) =>
            _lblNpmHint.Width = Math.Max(S(200), hintHost.ClientSize.Width - S(40));

        _lblNpmHint.AutoSize = false;
        _lblNpmHint.Font = UiTheme.HintFont;
        _lblNpmHint.ForeColor = UiTheme.TextSecondary;
        _lblNpmHint.TextAlign = ContentAlignment.MiddleLeft;
        _lblNpmHint.AutoEllipsis = true;
        _lblNpmHint.Location = new Point(S(20), S(8));
        _lblNpmHint.Size = new Size(S(700), S(20));
        _lblNpmHint.Text = "尚未检查更新";
        hintHost.Controls.Add(_lblNpmHint);

        card.Controls.Add(mark);
        card.Controls.Add(_npmNameRow);
        card.Controls.Add(_cmbNpmChannel);
        card.Controls.Add(_btnInstall);
        card.Controls.Add(_btnCheckNpmUpdates);
        card.Controls.Add(_btnUpdateNpm);
        card.Controls.Add(hintHost);

        card.SizeChanged += (_, _) =>
        {
            LayoutNpmNameRow();
            PositionNpmRightSide();
        };

        return card;
    }

    /// <summary>包名单独占一行，版本标签放到包名下方，避免和右侧通道 / 按钮挤在同一行被裁切。</summary>
    private void LayoutNpmNameRow()
    {
        if (_npmNameRow is null || _npmNameLabel is null || _npmNameRow.IsDisposed)
        {
            return;
        }

        var nameWidth = TextRenderer.MeasureText(_npmNameLabel.Text, _npmNameLabel.Font).Width;
        _npmNameLabel.Location = new Point(0, 0);

        var tagTop = S(24);
        var x = 0;
        _tagInstallState.Location = new Point(x, tagTop);
        x = _tagInstallState.Width + S(4);

        if (_tagNpmVersionText.Visible)
        {
            _tagNpmVersionText.Location = new Point(x, tagTop);
            x += _tagNpmVersionText.Width + S(4);
        }

        if (_tagNewVersion.Visible)
        {
            _tagNewVersion.Location = new Point(x, tagTop);
            x += _tagNewVersion.Width;
        }

        // 标签行同样要完整容纳：右侧默认从卡片右边排开，宽度上限避免压到通道下拉。
        _npmNameRow.Width = Math.Min(Math.Max(nameWidth, x) + S(4), S(452));
    }

    /// <summary>
    /// 按卡片当前宽度把 npm 卡片右侧的下拉与按钮从右向左排开。
    /// 卡片尚未完成布局（未显示 / 宽度为 0）时直接返回：
    /// 收起到托盘期间的状态刷新不能把按钮排到负坐标，恢复显示后由 VisibleChanged 重新布局。
    /// </summary>
    private void PositionNpmRightSide()
    {
        if (_npmCard is null || _npmCard.IsDisposed)
        {
            return;
        }

        if (!_npmCard.Visible)
        {
            return;
        }

        var width = _npmCard.ClientSize.Width;
        if (width <= 0)
        {
            return;
        }

        PositionNpmRightSide(width);
    }

    private void PositionNpmRightSide(int cardWidth)
    {
        var gap = S(8);
        var leftLimit = S(20);
        var x = cardWidth - S(20);
        var rowTop = S(29);

        void Place(AntdUI.Button button, int y)
        {
            if (!button.Visible)
            {
                return;
            }

            x -= button.Width;
            button.Location = new Point(Math.Max(leftLimit, x), y);
            x -= gap;
        }

        Place(_btnUpdateNpm, rowTop);
        Place(_btnCheckNpmUpdates, rowTop);
        Place(_btnInstall, rowTop);

        _cmbNpmChannel.Location = new Point(Math.Max(leftLimit, x - _cmbNpmChannel.Width), S(31));
    }

    private Panel BuildStatusBar()
    {
        var panel = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = S(30),
            BackColor = UiTheme.CardBackground,
        };
        panel.Paint += (_, e) =>
        {
            using var pen = new Pen(UiTheme.Border);
            e.Graphics.DrawLine(pen, 0, 0, panel.ClientSize.Width, 0);
        };

        _statusBarDot.Location = new Point(S(20), S(12));
        _statusBarDot.Size = new Size(S(6), S(6));

        _lblOperation.AutoSize = false;
        _lblOperation.Font = UiTheme.HintFont;
        _lblOperation.ForeColor = UiTheme.TextSecondary;
        _lblOperation.TextAlign = ContentAlignment.MiddleLeft;
        _lblOperation.Location = new Point(S(32), S(4));
        _lblOperation.Size = new Size(S(640), S(22));
        _lblOperation.Text = "就绪";

        _lblVersion.AutoSize = true;
        _lblVersion.Font = new Font("Consolas", 8.5f);
        _lblVersion.ForeColor = UiTheme.TextSecondary;
        _lblVersion.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        var appVersion = typeof(MainForm).Assembly.GetName().Version;
        _lblVersion.Text = appVersion is { } version
            ? $"v{version.Major}.{version.Minor}.{Math.Max(0, version.Build)}"
            : "v1.0.1";

        panel.Controls.Add(_statusBarDot);
        panel.Controls.Add(_lblOperation);
        panel.Controls.Add(_lblVersion);
        panel.SizeChanged += (_, _) => PositionStatusBarItems();
        _statusBar = panel;
        return panel;
    }

    private void BuildTray(bool trayAllowed)
    {
        _trayMenu = new ContextMenuStrip();

        _trayStart = new ToolStripMenuItem("启动");
        _trayStop = new ToolStripMenuItem("停止");
        _trayRestart = new ToolStripMenuItem("重新启动");
        _trayOpenWebUi = new ToolStripMenuItem("打开 Web UI");
        _trayExit = new ToolStripMenuItem("退出");

        _trayStart.Click += OnStartRequested;
        _trayStop.Click += OnStopRequested;
        _trayRestart.Click += OnRestartRequested;
        _trayOpenWebUi.Click += OnOpenWebUiRequested;
        _trayExit.Click += OnExitRequested;

        _trayMenu.Items.AddRange(new ToolStripItem[]
        {
            _trayStart,
            _trayStop,
            _trayRestart,
            _trayOpenWebUi,
            new ToolStripSeparator(),
            _trayExit,
        });

        if (!trayAllowed)
        {
            _trayAvailable = false;
            _environmentHint = "低完整性进程：Windows 会拒绝登记托盘图标，关闭窗口将退出程序（dsh web 运行中会先确认）";
            return;
        }

        _trayHost = new TrayIconHost(AppIcons.CreateTrayIcon(), Text, _trayMenu);
        _trayHost.Activated += RestoreFromTray;
        _trayHost.RegistrationChanged += OnTrayRegistrationChanged;

        // 完整性级别只是预判；TryRegister 的真实结果才是托盘可用性的最终依据。
        _trayRegistrationSettling = true;
        try
        {
            _trayAvailable = _trayHost.TryRegister();
        }
        finally
        {
            _trayRegistrationSettling = false;
        }

        if (!_trayAvailable)
        {
            _environmentHint = "系统托盘图标注册失败：关闭窗口将退出程序（dsh web 运行中会先确认）";
        }
    }

    // ------------------------------------------------------------------ 布局（DPI 与窗口可见性）

    /// <summary>逻辑像素 → 当前 DPI 的设备像素，避免 96 DPI 常量在缩放后错位。</summary>
    private int S(int logicalPixels) => LogicalToDeviceUnits(logicalPixels);

    private void RelayoutAll()
    {
        if (!_ready || IsDisposed)
        {
            return;
        }

        var npmRowHeight = S(128);
        if (Math.Abs(_homeGrid.RowStyles[1].Height - npmRowHeight) > 0.5f)
        {
            _homeGrid.RowStyles[1].Height = npmRowHeight;
            _homeGrid.PerformLayout();
        }

        // 状态栏高度按当前 DPI 收口，避免 Docked 面板在非 96 DPI 下仍用逻辑常量。
        _statusBar.Height = S(30);

        PositionOwnershipTag();
        LayoutNpmNameRow();
        LayoutSummaryCard();
        PositionStatusBarItems();

        if (_npmCard is not null && !_npmCard.IsDisposed && _npmCard.ClientSize.Width > 0)
        {
            PositionNpmRightSide(_npmCard.ClientSize.Width);
        }
    }

    private void LayoutSummaryCard()
    {
        if (_summaryCard is null || _summaryCard.IsDisposed)
        {
            return;
        }

        var width = _summaryCard.ClientSize.Width;
        if (width <= 0)
        {
            return;
        }

        var contentWidth = Math.Max(S(140), width - S(36));
        _summaryProfile.Location = new Point(
            Math.Max(S(90), width - _summaryProfile.Width - S(18)),
            S(18));
        _summaryStats.Size = new Size(contentWidth, S(36));

        _lblSummaryLoading.Location = new Point(S(18), S(112));
        _lblSummaryLoading.Size = new Size(contentWidth, S(22));

        _updateButtonHost.Location = new Point(S(18), S(112));
        _updateButtonHost.Size = new Size(contentWidth, S(36));

        _lblBulkResult.Location = new Point(S(18), S(158));
        _lblBulkResult.Size = new Size(contentWidth, S(48));
    }

    private void PositionStatusBarItems()
    {
        if (_statusBar is null || _statusBar.IsDisposed)
        {
            return;
        }

        var width = _statusBar.ClientSize.Width;
        if (width <= 0)
        {
            return;
        }

        _lblVersion.Location = new Point(
            Math.Max(S(600), width - _lblVersion.Width - S(20)),
            S(7));
    }

    // ------------------------------------------------------------------ 状态渲染

    private void OnStateChanged(AppState state)
    {
        if (IsDisposed)
        {
            return;
        }

        if (InvokeRequired)
        {
            try
            {
                BeginInvoke(new Action<AppState>(OnStateChanged), state);
            }
            catch (InvalidOperationException)
            {
                // 窗口正在销毁。
            }

            return;
        }

        ApplyState(state);
    }

    /// <summary>
    /// 把同一份状态快照同时渲染到主窗体控件与托盘菜单；
    /// 两处都不做独立的启用判断，加载态只读取 <see cref="AppState.Busy"/> 枚举。
    /// </summary>
    private void ApplyState(AppState state)
    {
        if (IsDisposed)
        {
            return;
        }

        var color = DotColorFor(state);
        _statusDot.DotColor = color;
        _statusBarDot.DotColor = color;

        _lblServerStatus.Text = state.ServerStatusText;

        var ownedByApp = _controller.IsServerOwnedByApp;
        _tagOwnership.Visible = state.Server is ServerStatus.Running or ServerStatus.Starting or ServerStatus.PortConflict;
        _tagOwnership.Text = state.Server == ServerStatus.PortConflict
            ? "外部进程占用"
            : ownedByApp
                ? "由本程序启动"
                : "外部启动";
        _tagOwnership.Type = ownedByApp && state.Server != ServerStatus.PortConflict
            ? TTypeMini.Success
            : TTypeMini.Default;

        _lblServerAddress.Text = state.Server == ServerStatus.Stopped
            ? $"端口 {state.Port} 空闲"
            : $"127.0.0.1:{state.Port}";
        _lblServerPid.Text = state.ListenerPid is { } pid ? $"PID {pid}" : "未监听";

        _tagInstallState.Text = state.InstallText;
        _tagInstallState.Type = state.Install == InstallStatus.Installed
            ? TTypeMini.Success
            : TTypeMini.Default;

        _tagNpmVersionText.Text = state.InstalledVersion is { Length: > 0 } installedVersion
            ? $"v{installedVersion}"
            : "v—";
        _tagNpmVersionText.Visible = state.Install == InstallStatus.Installed;

        // 有新版本时用醒目的橙色标签单独标出目标版本，不再只靠底部一行提示。
        var latestVersion = state.LatestVersion;
        _tagNewVersion.Visible = state.HasUpdate && latestVersion is { Length: > 0 };
        if (_tagNewVersion.Visible)
        {
            _tagNewVersion.Text = $"可更新 v{latestVersion}";
        }

        LayoutNpmNameRow();

        var busy = state.Busy;
        _lblNpmHint.ForeColor = state.UpdateCheckError is { Length: > 0 } ? UiTheme.Danger : UiTheme.TextSecondary;
        _lblNpmHint.Text = busy switch
        {
            BusyKind.CheckNpmUpdates => "正在检查更新…",
            BusyKind.UpdateNpmPackage => "正在更新 dsh…",
            BusyKind.InstallNpmPackage => "正在安装 dsh…",
            _ => state.UpdateHintText,
        };

        _btnStart.Enabled = state.CanStart;
        _btnStart.Type = state.CanStart ? TTypeMini.Primary : TTypeMini.Default;
        _btnStop.Enabled = state.CanStop;
        _btnRestart.Enabled = state.CanRestart;
        _btnOpenWebUi.Enabled = state.CanOpenWebUi;
        _btnOpenWebUi.Type = state.CanOpenWebUi ? TTypeMini.Primary : TTypeMini.Default;

        // 「检查更新」在检测中 / 状态未知时也保留（禁用），只有确定未安装时才换成「下载安装」，
        // 避免启动瞬间或 npm 暂时不可用时按钮整块消失。
        var definitelyNotInstalled = state.Install == InstallStatus.NotInstalled;
        _btnInstall.Visible = definitelyNotInstalled;
        _btnInstall.Enabled = state.CanInstallPackage;
        _btnInstall.Loading = busy == BusyKind.InstallNpmPackage;
        _btnCheckNpmUpdates.Visible = !definitelyNotInstalled;
        _btnCheckNpmUpdates.Enabled = state.CanCheckNpmUpdates;
        _btnCheckNpmUpdates.Loading = busy == BusyKind.CheckNpmUpdates;

        // 更新按钮默认隐藏，只有检查更新并发现新版本后才出现；目标版本由橙色标签和提示行给出。
        _btnUpdateNpm.Text = "更新";
        _btnUpdateNpm.Width = Math.Max(S(80), TextRenderer.MeasureText(_btnUpdateNpm.Text, _btnUpdateNpm.Font).Width + S(34));
        _btnUpdateNpm.Visible = state.HasUpdate;
        _btnUpdateNpm.Enabled = state.CanUpdatePackage;
        _btnUpdateNpm.Loading = busy == BusyKind.UpdateNpmPackage;

        _cmbNpmChannel.Enabled = !state.IsBusy;

        _trayStart.Enabled = state.CanStart;
        _trayStop.Enabled = state.CanStop;
        _trayRestart.Enabled = state.CanRestart;
        _trayOpenWebUi.Enabled = state.CanOpenWebUi;

        _trayHost?.UpdateTooltip($"dsh Web Monitor · {state.ServerStatusText}");

        UpdateBulkButton();
        UpdateOperationText();
        PositionOwnershipTag();
        PositionNpmRightSide();
    }

    private static Color DotColorFor(AppState state) => state.Server switch
    {
        ServerStatus.Running => UiTheme.Success,
        ServerStatus.Starting or ServerStatus.Stopping => UiTheme.Warning,
        ServerStatus.PortConflict or ServerStatus.StartFailed => UiTheme.Danger,
        ServerStatus.Stopped => UiTheme.Muted,
        _ => Color.Silver,
    };

    private void UpdateOperationText()
    {
        if (IsDisposed)
        {
            return;
        }

        var state = _controller.State;
        string text;
        if (state.BusyOperation is { Length: > 0 } busyText)
        {
            text = busyText;
        }
        else if (state.IsBusy)
        {
            text = state.ServerStatusText;
        }
        else if (_tabs.SelectedIndex == 1)
        {
            text = GetPluginStatusText();
        }
        else if (_environmentHint is { } hint)
        {
            text = hint;
        }
        else
        {
            text = state.Install switch
            {
                InstallStatus.NotInstalled => $"{AppConstants.NpmPackage} 未安装，请先安装",
                InstallStatus.Unknown => "无法读取 npm 全局包状态，请确认 npm 可用后重试",
                _ => "就绪",
            };
        }

        SetStatus(text);
    }

    private string GetPluginStatusText()
    {
        if (!_pluginView.HasLoaded)
        {
            return _pluginView.LastLoadError is { Length: > 0 } error
                ? $"插件列表读取失败：{error}；可在插件页点「刷新列表」重试"
                : "正在读取插件信息…";
        }

        var plugins = _pluginView.Plugins;
        if (plugins.Count == 0)
        {
            return "尚无插件";
        }

        var enabled = plugins.Count(plugin => plugin.IsEnabled);
        return $"共 {plugins.Count} 个插件，{enabled} 个已启用 · 通道 {UpdateChannels.ToLabel(_controller.PluginChannel)}";
    }

    // ------------------------------------------------------------------ 主页概况 / 一键更新

    private async void OnTabChanged(object? sender, IntEventArgs e)
    {
        try
        {
            if (!_ready)
            {
                return;
            }

            UpdateOperationText();
            if (!_autoStart)
            {
                // 测试模式：不加载插件、不访问网络。
                return;
            }

            if (e.Value == 1)
            {
                await _pluginView.EnsureLoadedAsync();
                UpdateOperationText();
                return;
            }

            if (e.Value != 0)
            {
                return;
            }

            // 插件页改过通道时，主页下拉要跟着回到同一个通道（M-18：两处口径一致）。
            SyncHomeChannelFromController();
            if (_pluginView.HasLoaded)
            {
                UpdateSummaryFromView();
            }
            else
            {
                await RefreshHomeSummaryAsync();
            }
        }
        catch (Exception ex)
        {
            ReportFailure("切换页面", ex);
        }
    }

    private void UpdateSummaryFromView()
    {
        if (IsDisposed)
        {
            return;
        }

        var plugins = _pluginView.Plugins;
        if (plugins.Count == 0 && !_pluginView.HasLoaded)
        {
            return;
        }

        _updateCheckIncomplete = _pluginView.UpdateCheckIncomplete;
        ApplySummary(
            new PluginSummary(
                plugins.Count,
                plugins.Count(plugin => plugin.IsEnabled),
                plugins.Count(plugin => plugin.HasUpdate)),
            updatesChecked: true);

        if (_updateCheckIncomplete)
        {
            SetBulkResult("部分插件最新版本查询失败，可更新数量可能不准确，详见日志页", UiTheme.Danger);
            _incompleteNoted = true;
        }
        else if (_incompleteNoted)
        {
            SetBulkResult(string.Empty, UiTheme.TextSecondary);
            _incompleteNoted = false;
        }
    }

    private void OnPluginDataChanged(object? sender, EventArgs e)
    {
        if (IsDisposed)
        {
            return;
        }

        if (InvokeRequired)
        {
            BeginInvokeSafe(() => OnPluginDataChanged(sender, e));
            return;
        }

        UpdateSummaryFromView();
        UpdateOperationText();
    }

    private void OnPluginStatusMessage(object? sender, string message)
    {
        if (IsDisposed || _tabs.SelectedIndex != 1)
        {
            return;
        }

        SetStatus(message);
    }

    private async Task RefreshHomeSummaryAsync()
    {
        if (_summaryRefreshing || _bulkUpdating || IsDisposed || !_autoStart)
        {
            return;
        }

        if (_pluginView.HasLoaded)
        {
            // 启动时的检查已经完成：直接复用插件页的数据，不重复调用 CLI / npm。
            UpdateSummaryFromView();
            return;
        }

        _summaryRefreshing = true;
        ShowSummaryLoading("正在读取插件信息…");
        try
        {
            // 启动阶段完成唯一一次插件更新检查（列表 + 按通道解析最新版本）；
            // 之后点击「插件管理」直接复用这份结果，不再触发第二轮检查。
            var loaded = await _pluginView.EnsureLoadedAsync();
            if (IsDisposed)
            {
                return;
            }

            if (loaded)
            {
                UpdateSummaryFromView();
            }
            else
            {
                ShowSummaryUnavailable();
            }
        }
        catch (Exception ex)
        {
            ShowSummaryUnavailable();
            ReportFailure("读取插件概况", ex);
        }
        finally
        {
            _summaryRefreshing = false;
            HideSummaryLoading();
        }
    }

    private void ShowSummaryLoading(string message)
    {
        if (IsDisposed)
        {
            return;
        }

        _lblSummaryLoading.Text = message;
        _lblSummaryLoading.Visible = true;
        _updateButtonHost.Visible = false;
    }

    private void HideSummaryLoading()
    {
        if (IsDisposed)
        {
            return;
        }

        _lblSummaryLoading.Visible = false;
        _updateButtonHost.Visible = true;
    }

    private void ShowSummaryUnavailable()
    {
        if (IsDisposed)
        {
            return;
        }

        _lblStatInstalled.Text = "—";
        _lblStatEnabled.Text = "—";
        _lblStatUpdatable.Text = "—";
        _lblStatUpdatable.ForeColor = UiTheme.TextSecondary;
        _updatableCount = 0;
        _updatesChecked = false;
        _updateCheckIncomplete = false;
        _btnUpdateAll.Visible = true;
        UpdateBulkButton();
    }

    /// <param name="updatesChecked">
    /// false 表示「可更新」数量尚未经过通道检查（启动阶段的已知值为 0），
    /// 此时按钮禁用并显示「待检查更新」，不谎称已全部最新。
    /// </param>
    private void ApplySummary(PluginSummary summary, bool updatesChecked)
    {
        _lblStatInstalled.Text = summary.Installed.ToString();
        _lblStatEnabled.Text = summary.Enabled.ToString();
        _lblStatUpdatable.Text = summary.Updatable.ToString();
        _lblStatUpdatable.ForeColor = summary.Updatable > 0 ? AccentBlue : UiTheme.TextSecondary;
        _updatableCount = summary.Updatable;
        _updatesChecked = updatesChecked;
        _btnUpdateAll.Visible = true;
        UpdateBulkButton();
    }

    private void UpdateBulkButton()
    {
        if (_btnUpdateAll is null)
        {
            return;
        }

        _btnUpdateAll.Loading = _bulkUpdating;
        _btnUpdateAll.Text = _bulkUpdating
            ? "更新中…"
            : _updateCheckIncomplete
                ? "检查不完整"
                : !_updatesChecked
                    ? "待检查更新"
                    : _updatableCount > 0
                        ? "一键更新"
                        : "已全部最新";
        _btnUpdateAll.Enabled = !_bulkUpdating
                                && !_updateCheckIncomplete
                                && _updatesChecked
                                && _updatableCount > 0
                                && _controller.State.CanRunPluginCommand;
    }

    private void SetBulkResult(string text, Color color)
    {
        _lblBulkResult.ForeColor = color;
        _lblBulkResult.Text = text;
    }

    private async void OnUpdateAllRequested(object? sender, EventArgs e)
    {
        try
        {
            if (_bulkUpdating)
            {
                return;
            }

            if (!_updatesChecked)
            {
                SetBulkResult("请先切到「插件管理」页刷新列表，确认可更新项后再执行一键更新。", UiTheme.TextSecondary);
                return;
            }

            if (_updatableCount == 0)
            {
                SetBulkResult("没有可更新的插件", UiTheme.TextSecondary);
                return;
            }

            _bulkUpdating = true;
            UpdateBulkButton();
            SetBulkResult("正在更新可更新的插件…", UiTheme.TextSecondary);

            PluginBulkUpdateResult result = default;
            try
            {
                result = await _controller.UpdateAllPluginsAsync();
            }
            finally
            {
                _bulkUpdating = false;
            }

            if (IsDisposed || Disposing)
            {
                return;
            }

            if (result.Updated + result.Failed == 0)
            {
                SetBulkResult("没有可更新的插件", UiTheme.TextSecondary);
            }
            else if (result.Failed == 0)
            {
                SetBulkResult($"已更新 {result.Updated} 个插件，重启 dsh web 后生效", UiTheme.Success);
            }
            else
            {
                SetBulkResult($"已更新 {result.Updated} 个，失败 {result.Failed} 个，详见日志页", UiTheme.Danger);
            }

            // 插件页刷新完成后直接复用其数据刷新主页统计，不再单独联网查询一遍。
            await _pluginView.RefreshListAsync();
            if (IsDisposed || Disposing)
            {
                return;
            }

            UpdateSummaryFromView();
        }
        catch (Exception ex)
        {
            _bulkUpdating = false;
            if (IsDisposed || Disposing)
            {
                return;
            }

            UpdateBulkButton();
            SetBulkResult("一键更新失败，请查看日志页了解详情。", UiTheme.Danger);
            ReportFailure("一键更新", ex);
        }
    }

    // ------------------------------------------------------------------ 日志与提示

    private void OnServerLog(LogEntry entry) => AppendLog(entry);

    private void OnPluginLog(LogEntry entry) => AppendLog(entry);

    private void AppendLog(LogEntry entry)
    {
        if (IsDisposed)
        {
            return;
        }

        _logView.AppendEntry(entry);
    }

    private void AppendSystemLog(string text) => AppendLog(new LogEntry(text, LogLevel.System));

    /// <summary>
    /// 失败提示统一写日志页 + 状态栏（带「请查看日志页」指引），
    /// 不弹裸 ex.Message 对话框；窗口已隐藏时也没有 Owner 失效的问题。
    /// </summary>
    private void ReportFailure(string actionName, Exception exception)
    {
        var detail = string.IsNullOrWhiteSpace(exception.Message)
            ? exception.GetType().Name
            : exception.Message;
        AppendLog(new LogEntry($"「{actionName}」失败：{detail}", LogLevel.Error));
        SetStatus($"「{actionName}」失败：{detail}。请查看日志页了解详情。");
    }

    private void SetStatus(string text)
    {
        if (IsDisposed)
        {
            return;
        }

        if (InvokeRequired)
        {
            BeginInvokeSafe(() => SetStatus(text));
            return;
        }

        _lblOperation.Text = text;
    }

    private void BeginInvokeSafe(Action action)
    {
        if (IsDisposed)
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
            return;
        }

        try
        {
            BeginInvoke(action);
        }
        catch (InvalidOperationException)
        {
            // 窗口正在销毁。
        }
    }

    // ------------------------------------------------------------------ 窗体生命周期

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        RelayoutAll();

        if (!_autoStart || _startupStarted)
        {
            return;
        }

        _startupStarted = true;
        _ = StartStartupAsync();
    }

    private async Task StartStartupAsync()
    {
        try
        {
            // 启动检测与插件概况并行：概况只读已知信息，不查 dist-tags。
            var serverTask = _controller.StartAsync();
            var summaryTask = RefreshHomeSummaryAsync();
            await Task.WhenAll(serverTask, summaryTask);
        }
        catch (Exception ex)
        {
            ReportFailure("启动检测", ex);
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // 系统关机 / 任务管理器结束进程：一律放行，不阻止系统关闭。
        if (e.CloseReason is CloseReason.WindowsShutDown or CloseReason.TaskManagerClosing)
        {
            base.OnFormClosing(e);
            return;
        }

        if (_exiting || e.CloseReason != CloseReason.UserClosing)
        {
            base.OnFormClosing(e);
            return;
        }

        // 关闭即收起到托盘：前提是通知区域里真的有图标（真实注册结果，不是预判）。
        if (TryHideToTray())
        {
            e.Cancel = true;
            base.OnFormClosing(e);
            return;
        }

        // 无法收起到托盘：关闭即退出，退出前确认（退出会结束由本程序启动的 dsh web）。
        if (ConfirmExit())
        {
            _exiting = true;
        }
        else
        {
            e.Cancel = true;
        }

        base.OnFormClosing(e);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        DisposeOwnedResources();
        base.OnFormClosed(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            DisposeOwnedResources();
        }

        base.Dispose(disposing);
    }

    private void DisposeOwnedResources()
    {
        if (_resourcesDisposed)
        {
            return;
        }

        _resourcesDisposed = true;

        _controller.StateChanged -= OnStateChanged;
        _controller.ServerLog -= OnServerLog;
        _controller.PluginLog -= OnPluginLog;
        _pluginView.DataChanged -= OnPluginDataChanged;
        _pluginView.StatusMessage -= OnPluginStatusMessage;

        if (_trayHost is not null)
        {
            _trayHost.Activated -= RestoreFromTray;
            _trayHost.RegistrationChanged -= OnTrayRegistrationChanged;
            // 图标、菜单与隐藏窗口都由 TrayIconHost 自己释放（含 NIM_DELETE）。
            _trayHost.Dispose();
            _trayHost = null;
        }
        else if (_trayMenu is not null)
        {
            _trayMenu.Dispose();
        }

        _controller.Dispose();
    }

    /// <summary>
    /// 收起到托盘。隐藏前复核图标是否真的存在：复核不通过时按真实注册结果降级为「关闭即退出」，
    /// 绝不允许出现「窗口已隐藏、托盘却没有图标」的不可恢复状态。
    /// </summary>
    private bool TryHideToTray()
    {
        if (_trayHost is null || !_trayAvailable)
        {
            return false;
        }

        // 复核 / 补登记期间屏蔽 RegistrationChanged 的自动降级，由本方法按真实结果统一处理，
        // 避免把「首次登记失败」误报成「Explorer 重启导致图标丢失」。
        bool registered;
        _trayRegistrationSettling = true;
        try
        {
            registered = _trayHost.Recheck() || _trayHost.TryRegister();
        }
        finally
        {
            _trayRegistrationSettling = false;
        }

        if (!registered)
        {
            DegradeTray("无法在通知区域登记托盘图标");
            return false;
        }

        Hide();
        return true;
    }

    /// <summary>托盘不可用后的降级：关闭窗口退出；窗口若已隐藏则立即恢复显示。</summary>
    private void DegradeTray(string reason)
    {
        var wasAvailable = _trayAvailable;
        _trayAvailable = false;
        _environmentHint = reason + "：关闭窗口将退出程序（dsh web 运行中会先确认）";

        if (wasAvailable)
        {
            AppendSystemLog(reason + "。托盘功能已停用，此后关闭窗口将退出程序（dsh web 运行中会先确认）。");
        }

        if (!Visible)
        {
            RestoreFromTray();
        }

        UpdateOperationText();
    }

    /// <summary>从托盘恢复：先恢复普通窗口状态，再显示并激活。</summary>
    private void RestoreFromTray()
    {
        if (IsDisposed)
        {
            return;
        }

        WindowState = FormWindowState.Normal;
        Show();
        Activate();
        BringToFront();
        RelayoutAll();
    }

    private void OnTrayRegistrationChanged(bool registered)
    {
        if (_trayRegistrationSettling || IsDisposed)
        {
            return;
        }

        if (registered)
        {
            if (_trayAvailable)
            {
                return;
            }

            _trayAvailable = true;
            _environmentHint = null;
            AppendSystemLog("系统托盘图标已重新登记，关闭窗口恢复为收起到托盘。");
            UpdateOperationText();
            return;
        }

        // Explorer 重启或通知区域重置后重新登记失败：图标已经丢了，不能再把窗口藏起来。
        DegradeTray("系统托盘图标已丢失（Explorer 重启或通知区域重置）");
    }

    /// <summary>
    /// 退出确认：按 dsh web 是否由本程序启动区分文案（退出即结束由本程序启动的 dsh web）。
    /// 窗口隐藏时对话框不指定 Owner，避免弹在被隐藏窗体后面。
    /// </summary>
    private bool ConfirmExit()
    {
        // 测试模式不弹模态框，否则无人点击会阻塞整轮测试。
        if (!_autoStart)
        {
            return true;
        }

        var state = _controller.State;
        // Stopping 也要确认：此时 PID 仍可能属于本程序启动的进程树，直接退出会一并结束它。
        if (state.Server is not (ServerStatus.Running or ServerStatus.Starting or ServerStatus.Stopping))
        {
            return true;
        }

        var pid = state.ListenerPid is { } listenerPid ? $"PID {listenerPid}" : null;
        var owned = _controller.IsServerOwnedByApp;
        var message = owned
            ? pid is null
                ? "退出监控器会结束由本程序启动的 dsh web，确定退出吗？"
                : $"退出监控器会结束由本程序启动的 dsh web（{pid}），确定退出吗？"
            : pid is null
                ? "dsh web 正在运行（由外部启动），退出监控器不会结束它，确定退出吗？"
                : $"dsh web 正在运行（{pid}，由外部启动），退出监控器不会结束它，确定退出吗？";

        var answer = Visible
            ? MessageBox.Show(
                this,
                message,
                "确认退出",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2)
            : MessageBox.Show(
                message,
                "确认退出",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);
        return answer == DialogResult.Yes;
    }

    private void ExitApplication()
    {
        if (!ConfirmExit())
        {
            return;
        }

        _exiting = true;
        Close();
    }

    // ------------------------------------------------------------------ 操作入口
    // RunUiOperationAsync 内部已捕获并上报异常，入口方法不需要再包一层 try/catch。

    private async void OnStartRequested(object? sender, EventArgs e) =>
        await RunUiOperationAsync("启动 dsh web", _controller.StartServerAsync);

    private async void OnStopRequested(object? sender, EventArgs e) =>
        await RunUiOperationAsync("停止 dsh web", _controller.StopServerAsync);

    private async void OnRestartRequested(object? sender, EventArgs e) =>
        await RunUiOperationAsync("重新启动 dsh web", _controller.RestartServerAsync);

    private void OnOpenWebUiRequested(object? sender, EventArgs e) => _controller.OpenWebUi();

    private void OnExitRequested(object? sender, EventArgs e) => ExitApplication();

    private async void OnInstallNpmRequested(object? sender, EventArgs e) =>
        await RunUiOperationAsync("安装 dsh", _controller.InstallPackageAsync);

    private async void OnCheckNpmUpdatesRequested(object? sender, EventArgs e) =>
        await RunUiOperationAsync("检查更新", () => _controller.CheckNpmUpdatesAsync(CurrentNpmChannel));

    private async void OnUpdateNpmRequested(object? sender, EventArgs e)
    {
        var target = _controller.State.LatestVersion;
        if (string.IsNullOrWhiteSpace(target))
        {
            SetStatus("没有可用的目标版本：请先点击「检查更新」。");
            return;
        }

        await RunUiOperationAsync("更新 dsh", () => _controller.UpdatePackageAsync(target));
    }

    /// <summary>把主页通道下拉同步回 controller 的当前通道（插件页可能改过它），不触发重新检查。</summary>
    private void SyncHomeChannelFromController()
    {
        var index = UpdateChannels.Selectable.ToList().IndexOf(_controller.PluginChannel);
        if (index < 0)
        {
            index = 0;
        }

        if (_cmbNpmChannel.SelectedIndex == index)
        {
            return;
        }

        var wasInitializing = _initializingChannel;
        _initializingChannel = true;
        try
        {
            _cmbNpmChannel.SelectedIndex = index;
        }
        finally
        {
            _initializingChannel = wasInitializing;
        }
    }

    /// <summary>
    /// 主页更新通道变化：写回 controller 并同步插件页，保证两处「可更新」统计口径一致。
    /// </summary>
    private async void OnNpmChannelChanged(object? sender, EventArgs e)
    {
        if (_initializingChannel)
        {
            return;
        }

        var channel = CurrentNpmChannel;
        if (channel == _controller.PluginChannel)
        {
            // 程序化同步回同一个通道时不再触发联网检查。
            return;
        }

        _controller.PluginChannel = channel;
        _pluginView.SyncChannel(channel);

        if (_controller.State.Install != InstallStatus.Installed)
        {
            return;
        }

        await RunUiOperationAsync(
            $"按通道 {UpdateChannels.ToLabel(channel)} 检查更新",
            () => _controller.CheckNpmUpdatesAsync(channel));
    }

    private async Task RunUiOperationAsync(string actionName, Func<Task> operation)
    {
        try
        {
            await operation();
        }
        catch (Exception ex)
        {
            ReportFailure(actionName, ex);
        }
    }
}
