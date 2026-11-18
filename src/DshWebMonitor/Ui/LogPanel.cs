using System.Runtime.InteropServices;
using System.Text;

namespace DshWebMonitor.Ui;

/// <summary>
/// 只读日志面板：任意线程的日志先入队，由 UI 线程按批追加；仅在用户原本位于底部时自动滚动，
/// 内存与界面都只保留最近 <see cref="AppConstants.MaxLogLines"/> 行（多行条目按真实 UI 行数计算）。
/// </summary>
internal sealed class LogPanel : RichTextBox
{
    private const int WmSetRedraw = 0x000B;
    private const int WmVscroll = 0x0115;
    private const int SbBottom = 7;
    private const int EmGetScrollPos = 0x04DD;
    private const int EmSetScrollPos = 0x04DE;

    private readonly List<LogEntry> _entries = new();
    private readonly List<int> _entryLineCounts = new();
    private readonly Queue<LogEntry> _pending = new();
    private readonly object _pendingLock = new();

    /// <summary>待刷新队列上限：只保留最近若干批，避免界面被模态框卡住时无界增长。</summary>
    private const int MaxPendingEntries = AppConstants.MaxLogLines * 4;
    private readonly ContextMenuStrip _menu;
    private int _scheduled;
    private int _uiLines;

    public LogPanel()
    {
        ReadOnly = true;
        Dock = DockStyle.Fill;
        BorderStyle = BorderStyle.None;
        BackColor = UiTheme.LogBackground;
        ForeColor = UiTheme.LogForeground;
        Font = new Font("Consolas", 10.5f);
        WordWrap = false;
        DetectUrls = false;
        HideSelection = false;
        ScrollBars = RichTextBoxScrollBars.Both;
        ShortcutsEnabled = true;
        _menu = CreateMenu();
        ContextMenuStrip = _menu;
    }

    /// <summary>内存缓冲中的行数（不会超过上限）。</summary>
    internal int BufferedLineCount => _entries.Count;

    /// <summary>控件文本当前行数，等于缓冲条目占用的真实 UI 行数。</summary>
    internal int UiLineCount => _uiLines;

    /// <summary>追加一行日志；可从任意线程调用，只会入队并按批调度一次刷新。</summary>
    public void Append(LogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        if (IsDisposed)
        {
            return;
        }

        lock (_pendingLock)
        {
            _pending.Enqueue(entry);

            // 界面被模态框卡住时后台仍会持续产出日志：队列按上限丢弃最旧条目，
            // 否则关闭对话框后要一次性追加一个任意大的批次。
            while (_pending.Count > MaxPendingEntries)
            {
                _pending.Dequeue();
            }
        }

        ScheduleFlush();
    }

    /// <summary>把缓冲区与控件内容全部清空（只在 UI 线程调用）。</summary>
    internal new void Clear() => ClearCore();

    private void ClearCore()
    {
        lock (_pendingLock)
        {
            _pending.Clear();
        }

        if (IsDisposed)
        {
            return;
        }

        _entries.Clear();
        _entryLineCounts.Clear();
        _uiLines = 0;
        Interlocked.Exchange(ref _scheduled, 0);

        try
        {
            var redraw = SuspendRedraw();
            try
            {
                base.Clear();
            }
            finally
            {
                ResumeRedraw(redraw);
            }
        }
        catch (ObjectDisposedException)
        {
            // 窗口已销毁，忽略。
        }
        catch (InvalidOperationException)
        {
            // 窗口正在销毁，忽略。
        }
    }

    /// <summary>
    /// 在 UI 线程一次性处理当前排队的全部日志；测试可在没有消息循环时同步调用。
    /// </summary>
    internal void FlushPending()
    {
        if (InvokeRequired)
        {
            try
            {
                BeginInvoke(new Action(FlushPending));
            }
            catch (InvalidOperationException)
            {
                // 窗口正在销毁，丢弃该批。
            }

            return;
        }

        Interlocked.Exchange(ref _scheduled, 0);

        if (IsDisposed)
        {
            lock (_pendingLock)
            {
                _pending.Clear();
            }

            return;
        }

        List<LogEntry>? batch = null;
        lock (_pendingLock)
        {
            if (_pending.Count > 0)
            {
                batch = new List<LogEntry>(_pending.Count);
                while (_pending.Count > 0)
                {
                    batch.Add(_pending.Dequeue());
                }
            }
        }

        if (batch is null)
        {
            return;
        }

        try
        {
            FlushCore(batch);
        }
        catch (ObjectDisposedException)
        {
            // 窗口已销毁：丢弃本批，不向上抛。
        }
        catch (InvalidOperationException)
        {
            // 窗口正在销毁：丢弃本批，不向上抛。
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        if (HasPending)
        {
            ScheduleFlush();
        }
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        // 已投递但还没执行的刷新会随句柄一起丢失，清掉调度标记，句柄重建后重新调度。
        Interlocked.Exchange(ref _scheduled, 0);
        base.OnHandleDestroyed(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            // 右键菜单只赋给了 ContextMenuStrip 属性，控件不会替我们释放它。
            _menu.Dispose();
        }

        base.Dispose(disposing);
    }

    private bool HasPending
    {
        get
        {
            lock (_pendingLock)
            {
                return _pending.Count > 0;
            }
        }
    }

    private void ScheduleFlush()
    {
        if (!IsHandleCreated || IsDisposed)
        {
            // 句柄还没创建：日志留在队列里，等 OnHandleCreated / FlushPending 再处理。
            return;
        }

        if (Interlocked.Exchange(ref _scheduled, 1) != 0)
        {
            return;
        }

        try
        {
            BeginInvoke(new Action(FlushPending));
        }
        catch (InvalidOperationException)
        {
            Interlocked.Exchange(ref _scheduled, 0);
        }
    }

    private void FlushCore(List<LogEntry> batch)
    {
        var scrollToEnd = IsAtBottom();

        var oldCount = _entries.Count;
        var oldUiLines = _uiLines;
        var totalLines = oldUiLines;
        foreach (var entry in batch)
        {
            var lineCount = CountLines(entry.Text);
            _entries.Add(entry);
            _entryLineCounts.Add(lineCount);
            totalLines += lineCount;
        }

        // 缓冲超过上限时按条目整条淘汰，控件文本同步按被淘汰条目的真实行数裁剪。
        var exceed = totalLines - AppConstants.MaxLogLines;
        var evictCount = 0;
        var evictedLines = 0;
        while (evictCount < _entries.Count - 1 && evictedLines < exceed)
        {
            evictedLines += _entryLineCounts[evictCount];
            evictCount++;
        }

        // 被淘汰的旧条目已存在于控件文本里，需要从文本头部一次性删掉它们的行；
        // 本批新增的条目还没进控件，只按剩余条目的下标追加。
        var evictedOldCount = Math.Min(evictCount, oldCount);
        var evictedBatchCount = evictCount - evictedOldCount;
        var uiLinesToDelete = 0;
        for (var index = 0; index < evictedOldCount; index++)
        {
            uiLinesToDelete += _entryLineCounts[index];
        }

        if (evictCount > 0)
        {
            _entries.RemoveRange(0, evictCount);
            _entryLineCounts.RemoveRange(0, evictCount);
            totalLines -= evictedLines;
        }

        var selectionStart = SelectionStart;
        var selectionLength = SelectionLength;
        var appendFrom = evictedBatchCount;
        var scrollBefore = CaptureScrollPosition();
        var lineHeight = MeasureLineHeight();
        var rebuild = false;

        var redraw = SuspendRedraw();
        SuspendLayout();
        try
        {
            if (uiLinesToDelete > 0)
            {
                var deleted = DeleteLeadingLines(uiLinesToDelete);
                if (deleted < 0)
                {
                    rebuild = true;
                }
                else
                {
                    selectionStart = Math.Max(0, selectionStart - deleted);
                }
            }

            if (rebuild)
            {
                RebuildText();
            }
            else
            {
                AppendBatch(batch, appendFrom);
            }

            _uiLines = totalLines;

            // 恢复用户选择后修正视口：在底部时滚到底，上翻阅读时按被裁掉的行数补偿滚动偏移。
            RestoreSelection(selectionStart, selectionLength);
            if (scrollToEnd)
            {
                ScrollToBottom();
            }
            else
            {
                RestoreScrollPosition(scrollBefore, uiLinesToDelete * lineHeight);
            }
        }
        finally
        {
            ResumeLayout();
            ResumeRedraw(redraw);
        }
    }

    private Point CaptureScrollPosition()
    {
        if (!IsHandleCreated)
        {
            return Point.Empty;
        }

        var point = new NativePoint();
        try
        {
            SendMessage(Handle, EmGetScrollPos, IntPtr.Zero, ref point);
        }
        catch (ObjectDisposedException)
        {
            return Point.Empty;
        }
        catch (InvalidOperationException)
        {
            return Point.Empty;
        }

        return new Point(point.X, point.Y);
    }

    /// <summary>文本整体上移 <paramref name="removedPixels"/> 像素后，保持原视口内容不变。</summary>
    private void RestoreScrollPosition(Point position, int removedPixels)
    {
        if (!IsHandleCreated)
        {
            return;
        }

        var point = new NativePoint
        {
            X = position.X,
            Y = Math.Max(0, position.Y - Math.Max(0, removedPixels)),
        };
        try
        {
            SendMessage(Handle, EmSetScrollPos, IntPtr.Zero, ref point);
        }
        catch (ObjectDisposedException)
        {
            // 窗口已销毁。
        }
        catch (InvalidOperationException)
        {
            // 窗口正在销毁。
        }
    }

    /// <summary>单行像素高度；裁剪掉的行数乘以它即可得到滚动偏移的补偿量。</summary>
    private int MeasureLineHeight()
    {
        var first = GetFirstCharIndexFromLine(0);
        var second = GetFirstCharIndexFromLine(1);
        if (first >= 0 && second > first)
        {
            var delta = GetPositionFromCharIndex(second).Y - GetPositionFromCharIndex(first).Y;
            if (delta > 0)
            {
                return delta;
            }
        }

        return Math.Max(1, Font.Height);
    }

    private void ScrollToBottom()
    {
        try
        {
            SendMessage(Handle, WmVscroll, new IntPtr(SbBottom), IntPtr.Zero);
            Invalidate();
        }
        catch (ObjectDisposedException)
        {
            // 窗口已销毁。
        }
        catch (InvalidOperationException)
        {
            // 窗口正在销毁。
        }
    }

    private static int CountLines(string text)
    {
        var lines = 1;
        foreach (var ch in text)
        {
            if (ch == '\n')
            {
                lines++;
            }
        }

        return lines;
    }

    private void AppendBatch(List<LogEntry> batch, int startIndex)
    {
        if (startIndex >= batch.Count)
        {
            return;
        }

        var builder = new StringBuilder();
        var runColor = Color.Empty;
        var hasRun = false;
        for (var index = startIndex; index < batch.Count; index++)
        {
            var entry = batch[index];
            var color = ColorFor(entry.Level);
            if (hasRun && color != runColor)
            {
                AppendRun(builder, runColor);
                builder.Clear();
            }

            runColor = color;
            hasRun = true;
            builder.Append(entry.Text).Append(Environment.NewLine);
        }

        if (hasRun && builder.Length > 0)
        {
            AppendRun(builder, runColor);
        }
    }

    private void AppendRun(StringBuilder builder, Color color)
    {
        SelectionStart = TextLength;
        SelectionLength = 0;
        SelectionColor = color;
        AppendText(builder.ToString());
    }

    private void RebuildText()
    {
        base.Clear();
        AppendBatch(_entries, 0);
    }

    /// <summary>
    /// 删除控件文本开头的指定行。只读 RichTextBox 会忽略删除操作，因此裁剪期间临时关闭 ReadOnly。
    /// 返回实际删除的字符数；返回 -1 表示无法按行裁剪，调用方应整段重建。
    /// </summary>
    private int DeleteLeadingLines(int lineCount)
    {
        var cut = GetFirstCharIndexFromLine(lineCount);
        if (cut <= 0)
        {
            return -1;
        }

        var lengthBefore = TextLength;
        var readOnly = ReadOnly;
        ReadOnly = false;
        try
        {
            Select(0, cut);
            SelectedText = string.Empty;
        }
        finally
        {
            ReadOnly = readOnly;
        }

        var deleted = lengthBefore - TextLength;
        return deleted > 0 ? deleted : -1;
    }

    private void RestoreSelection(int start, int length)
    {
        var textLength = TextLength;
        var newStart = Math.Min(Math.Max(0, start), textLength);
        var newEnd = Math.Min(Math.Max(0, start + length), textLength);
        SelectionStart = newStart;
        SelectionLength = Math.Max(0, newEnd - newStart);
    }

    /// <summary>用户是否正停在日志底部；是才自动滚动，上翻阅读时不打扰。</summary>
    private bool IsAtBottom()
    {
        if (!IsHandleCreated || IsDisposed || TextLength == 0)
        {
            return true;
        }

        var lastIndex = TextLength - 1;
        var position = GetPositionFromCharIndex(lastIndex);
        if (position.Y < 0)
        {
            // 取不到坐标时按「在底部」处理，保证新日志仍能自动出现。
            return true;
        }

        return position.Y + Font.Height <= ClientSize.Height;
    }

    private bool SuspendRedraw()
    {
        try
        {
            SendMessage(Handle, WmSetRedraw, IntPtr.Zero, IntPtr.Zero);
            return true;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private void ResumeRedraw(bool suspended)
    {
        if (!suspended || IsDisposed)
        {
            return;
        }

        try
        {
            SendMessage(Handle, WmSetRedraw, new IntPtr(1), IntPtr.Zero);
            Invalidate();
        }
        catch (ObjectDisposedException)
        {
            // 窗口已销毁。
        }
        catch (InvalidOperationException)
        {
            // 窗口正在销毁。
        }
    }

    private ContextMenuStrip CreateMenu()
    {
        var menu = new ContextMenuStrip
        {
            Font = UiTheme.BodyFont,
        };
        var copy = new ToolStripMenuItem("复制全部");
        copy.Click += (_, _) => CopyAll();
        var clear = new ToolStripMenuItem("清空");
        clear.Click += (_, _) => Clear();
        menu.Items.Add(copy);
        menu.Items.Add(clear);
        menu.Opening += (_, _) =>
        {
            copy.Enabled = TextLength > 0;
            clear.Enabled = BufferedLineCount > 0 || HasPending;
        };
        return menu;
    }

    private void CopyAll()
    {
        if (TextLength == 0)
        {
            return;
        }

        try
        {
            Clipboard.SetText(Text);
        }
        catch (ExternalException)
        {
            // 剪贴板被其它进程占用：静默失败即可。
        }
    }

    private static Color ColorFor(LogLevel level) => level switch
    {
        LogLevel.Error => Color.FromArgb(207, 19, 34),
        LogLevel.System => Color.FromArgb(56, 158, 13),
        LogLevel.Command => Color.FromArgb(9, 88, 217),
        _ => UiTheme.LogForeground,
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, ref NativePoint lParam);
}
