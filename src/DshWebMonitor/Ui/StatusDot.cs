using System.ComponentModel;

namespace DshWebMonitor.Ui;

/// <summary>状态圆点，颜色由状态机快照决定。</summary>
internal sealed class StatusDot : Control
{
    private Color _dotColor = Color.Gray;

    public StatusDot()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint
            | ControlStyles.UserPaint
            | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.SupportsTransparentBackColor,
            true);
        BackColor = Color.Transparent;
        Size = new Size(14, 14);
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color DotColor
    {
        get => _dotColor;
        set
        {
            if (_dotColor == value)
            {
                return;
            }

            _dotColor = value;
            Invalidate();
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using var brush = new SolidBrush(_dotColor);
        e.Graphics.FillEllipse(brush, 1, 1, Width - 3, Height - 3);
    }
}
