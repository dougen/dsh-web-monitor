namespace DshWebMonitor;

/// <summary>日志去向：两块日志面板互相独立。</summary>
internal enum LogTarget
{
    Server,
    Plugin,
}

internal enum LogLevel
{
    Output,
    Error,
    System,
    Command,
}

internal sealed record LogEntry(string Text, LogLevel Level);
