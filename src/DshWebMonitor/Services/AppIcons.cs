using System.Reflection;

namespace DshWebMonitor.Services;

/// <summary>程序与托盘图标，来自嵌入资源 logo.ico（由 logo.png 生成）。</summary>
internal static class AppIcons
{
    private const string ResourceName = "DshWebMonitor.logo.ico";

    private static readonly byte[] IcoBytes = LoadBytes();

    public static Icon CreateFormIcon()
    {
        using var stream = new MemoryStream(IcoBytes);
        return new Icon(stream);
    }

    public static Icon CreateTrayIcon()
    {
        using var stream = new MemoryStream(IcoBytes);
        return new Icon(stream, SystemInformation.SmallIconSize);
    }

    private static byte[] LoadBytes()
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream(ResourceName)
                           ?? throw new InvalidOperationException("缺少嵌入图标资源：" + ResourceName);
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }
}
