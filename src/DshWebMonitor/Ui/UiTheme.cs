using AntdUI;
using Button = AntdUI.Button;
using Panel = AntdUI.Panel;

namespace DshWebMonitor.Ui;

internal static class UiTheme
{
    public static readonly Color WindowBackground = Color.FromArgb(244, 246, 249);
    public static readonly Color CardBackground = Color.White;
    public static readonly Color Border = Color.FromArgb(226, 230, 237);
    public static readonly Color TextPrimary = Color.FromArgb(31, 35, 41);
    public static readonly Color TextSecondary = Color.FromArgb(112, 118, 130);
    public static readonly Color Danger = Color.FromArgb(255, 77, 79);
    public static readonly Color Success = Color.FromArgb(82, 196, 26);
    public static readonly Color Warning = Color.FromArgb(250, 173, 20);
    public static readonly Color Muted = Color.FromArgb(140, 146, 158);
    public static readonly Color LogBackground = Color.White;
    public static readonly Color LogForeground = Color.FromArgb(31, 35, 40);

    public static Font CompactStatusFont { get; } = new("Microsoft YaHei UI", 15f, FontStyle.Bold);

    public static Font BodyFont { get; } = new("Microsoft YaHei UI", 10.5f);

    public static Font HintFont { get; } = new("Microsoft YaHei UI", 9f);

    public static Font StatNumberFont { get; } = new("Microsoft YaHei UI", 16f, FontStyle.Bold);

    public static Font MonoFont { get; } = new("Consolas", 9f);

    /// <summary>次要按钮（白底、描边）。</summary>
    public static Button CreateButton(string text) => new()
    {
        Text = text,
        Font = BodyFont,
        Height = 36,
        Width = TextWidth(text),
        Radius = 6,
        BorderWidth = 1,
        Type = TTypeMini.Default,
    };

    /// <summary>主按钮（实心主题色）。</summary>
    public static Button CreatePrimaryButton(string text) => new()
    {
        Text = text,
        Font = BodyFont,
        Height = 36,
        Width = TextWidth(text),
        Radius = 6,
        Type = TTypeMini.Primary,
    };

    /// <summary>危险操作按钮（红色文字 / 描边）。</summary>
    public static Button CreateDangerButton(string text) => new()
    {
        Text = text,
        Font = BodyFont,
        Height = 36,
        Width = TextWidth(text),
        Radius = 6,
        BorderWidth = 1,
        Type = TTypeMini.Error,
    };

    private static int TextWidth(string text) => Math.Max(80, TextRenderer.MeasureText(text, BodyFont).Width + 34);

    /// <summary>白底圆角卡片。</summary>
    public static Panel CreateCard() => new()
    {
        Back = CardBackground,
        Radius = 8,
        Shadow = 6,
        ShadowOpacity = 0.06f,
        BorderWidth = 1,
        BorderColor = Border,
    };
}




