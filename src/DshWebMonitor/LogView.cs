using AntdUI;
using DshWebMonitor.Ui;
using Panel = System.Windows.Forms.Panel;

namespace DshWebMonitor;

/// <summary>日志页：单一输出面板，dsh web 与插件 / npm 命令输出混合显示。</summary>
internal sealed class LogView : UserControl
{
    private readonly LogPanel _panel = new();

    public LogView()
    {
        Dock = DockStyle.Fill;
        BackColor = UiTheme.WindowBackground;
        Padding = new Padding(0);

        var card = UiTheme.CreateCard();
        card.Dock = DockStyle.Fill;

        var inner = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.White,
            Padding = new Padding(10),
        };
        _panel.Dock = DockStyle.Fill;
        inner.Controls.Add(_panel);
        card.Controls.Add(inner);
        Controls.Add(card);

        // 预创建句柄：Tab 未激活时后台线程的日志也要能通过 Invoke/BeginInvoke 封送。
        _ = Handle;
        _ = _panel.Handle;
    }

    public void AppendEntry(LogEntry entry) => _panel.Append(entry);
}

