using System.Drawing.Drawing2D;
using System.Reflection;

namespace DshWebMonitor.Ui;

/// <summary>npm 官方标识：嵌入的 npm logo，按 34×34 等比缩放绘制。</summary>
internal sealed class NpmMark : Control
{
    private const string ResourceName = "DshWebMonitor.npm-logo.png";
    private static readonly Image? Logo = LoadLogo();
    private static readonly Color NpmRed = Color.FromArgb(203, 56, 55);
    private static readonly Font FallbackFont = new("Consolas", 8.5f, FontStyle.Bold);

    public NpmMark()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint
            | ControlStyles.UserPaint
            | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.SupportsTransparentBackColor,
            true);
        BackColor = Color.Transparent;
        Size = new Size(34, 34);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        e.Graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        e.Graphics.CompositingQuality = CompositingQuality.HighQuality;

        var width = Math.Max(1, Width - 1);
        var height = Math.Max(1, Height - 1);

        if (Logo is not null)
        {
            // 官方 logo 本身是方形色块，直接等比绘制，避免小尺寸圆角裁剪产生锯齿。
            e.Graphics.DrawImage(Logo, new Rectangle(0, 0, width, height));
            return;
        }

        using var brush = new SolidBrush(NpmRed);
        e.Graphics.FillRectangle(brush, 0, 0, width, height);
        TextRenderer.DrawText(
            e.Graphics,
            "npm",
            FallbackFont,
            ClientRectangle,
            Color.White,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }

    private static Image? LoadLogo()
    {
        try
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName);
            if (stream is null)
            {
                return null;
            }

            using var image = Image.FromStream(stream);
            return new Bitmap(image);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
