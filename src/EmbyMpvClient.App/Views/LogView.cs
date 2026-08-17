using System.Text;
using EmbyMpvClient.App.Composition;
using EmbyMpvClient.App.Controls;
using EmbyMpvClient.App.Theme;
using EmbyMpvClient.Diagnostics;

namespace EmbyMpvClient.App.Views;

/// <summary>
/// The in-app log. Reads the ring buffer the process is already filling rather than the log file,
/// so it works even when the disk sink failed — which is exactly when someone opens this page.
/// v1 had no diagnostics at all: an mpv launch that silently did nothing left no trace.
/// </summary>
public sealed class LogView : AppView
{
    private static readonly (string Label, LogLevel? Level)[] Levels =
    [
        ("全部", null),
        ("信息及以上", LogLevel.Info),
        ("警告及以上", LogLevel.Warn),
        ("仅错误", LogLevel.Error)
    ];

    private readonly RingBufferLogSink _buffer;

    private readonly DropDown _level = new();
    private readonly TextInput _filter = new() { Placeholder = "按关键字过滤…", Glyph = Glyphs.Search };
    private readonly ToggleSwitch _follow = new() { Text = "自动刷新" };
    private readonly FlatButton _copy = new() { Variant = ButtonVariant.Secondary, Glyph = Glyphs.Save, Text = "复制全部" };
    private readonly FlatButton _openFolder = new() { Variant = ButtonVariant.Secondary, Glyph = Glyphs.Folder, Text = "日志目录" };
    private readonly FlatButton _reload = new() { Variant = ButtonVariant.Ghost, Glyph = Glyphs.Refresh, Text = "刷新" };

    private readonly TextBlock _status = new("", Fonts.Small, Palette.TextFaint);
    private readonly Panel _frame = new() { BackColor = Palette.Border };
    private readonly TextBox _output = new();
    private readonly System.Windows.Forms.Timer _tick = new() { Interval = 1500 };

    private LogLevel? _minimum;
    private string _rendered = "";

    public LogView(IShell shell, RingBufferLogSink buffer) : base(shell)
    {
        HeaderTitle = "日志";
        _buffer = buffer;

        _level.Describe = value => value is LevelOption option ? option.Label : value.ToString() ?? "";
        _level.Fill(Levels.Select(level => (object)new LevelOption(level.Label, level.Level)).ToList());
        _level.SelectedIndexChanged += (_, _) =>
        {
            if (_level.SelectedItem is LevelOption option) _minimum = option.Level;
            Render();
        };

        _filter.Editor.TextChanged += (_, _) => Render();

        _follow.SetCheckedSilently(true);
        _follow.CheckedChanged += (_, _) =>
        {
            if (_follow.Checked) _tick.Start();
            else _tick.Stop();
        };

        _tick.Tick += (_, _) => Render();

        _copy.Click += (_, _) => CopyAll();
        _openFolder.Click += (_, _) => Reveal(Host.Paths.LogDirectory);
        _reload.Click += (_, _) => Render();

        _output.Multiline = true;
        _output.ReadOnly = true;
        _output.WordWrap = false;
        _output.ScrollBars = ScrollBars.Both;
        _output.BorderStyle = BorderStyle.None;
        _output.BackColor = Palette.Surface;
        _output.ForeColor = Palette.TextDim;
        _output.Font = Fonts.Mono;
        _output.MaxLength = 0;
        Win11.UseDarkControls(_output);
        _frame.Controls.Add(_output);

        foreach (var child in new Control[] { _level, _filter, _follow, _copy, _openFolder, _reload, _status, _frame })
            Controls.Add(child);
    }

    private sealed record LevelOption(string Label, LogLevel? Level);

    public override Task EnterAsync()
    {
        Render(scrollToEnd: true);
        if (_follow.Checked) _tick.Start();
        return Task.CompletedTask;
    }

    public override void Leave() => _tick.Stop();

    public override Task RefreshAsync()
    {
        Render(scrollToEnd: true);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Rebuilds the text only when it actually changed: this runs on a timer, and assigning
    /// <see cref="TextBox.Text"/> resets the scroll position and the user's selection.
    /// </summary>
    private void Render(bool scrollToEnd = false)
    {
        var entries = _buffer.Snapshot();
        var needle = _filter.Text.Trim();

        var text = new StringBuilder();
        var shown = 0;

        foreach (var entry in entries)
        {
            if (_minimum is { } minimum && entry.Level < minimum) continue;

            var line = entry.ToString();
            if (needle.Length > 0 && !line.Contains(needle, StringComparison.OrdinalIgnoreCase)) continue;

            text.Append(line).Append("\r\n");
            shown++;
        }

        var body = text.ToString();
        var changed = body != _rendered;

        if (changed)
        {
            _rendered = body;

            // Only follow the tail when the caret is already at the end, so reading scrollback is
            // not interrupted every 1.5 seconds.
            var atEnd = _output.TextLength == 0 || _output.SelectionStart >= _output.TextLength - 1;
            _output.Text = body;

            if (scrollToEnd || atEnd)
            {
                _output.SelectionStart = _output.TextLength;
                _output.SelectionLength = 0;
                _output.ScrollToCaret();
            }
        }

        var warnings = entries.Count(entry => entry.Level == LogLevel.Warn);
        var errors = entries.Count(entry => entry.Level == LogLevel.Error);
        _status.Text = $"缓冲区 {entries.Count} 条 · 显示 {shown} 条 · 警告 {warnings} · 错误 {errors} · 完整日志见 {Host.Paths.LogDirectory}";
        _status.ForeColor = errors > 0 ? Palette.Danger : warnings > 0 ? Palette.Warning : Palette.TextFaint;
    }

    private void CopyAll()
    {
        if (_rendered.Length == 0)
        {
            Shell.Notify("没有可复制的日志", ToastKind.Info);
            return;
        }

        try
        {
            // A header makes a pasted log usable in a bug report without further explanation.
            Clipboard.SetText($"{AppInfo.TitleWithVersion}{Environment.NewLine}{Environment.OSVersion}{Environment.NewLine}{Environment.NewLine}{_rendered}");
            Shell.Notify($"已复制 {_rendered.Split('\n').Length - 1} 行日志", ToastKind.Success);
        }
        catch (Exception error)
        {
            // The clipboard belongs to whatever window currently owns it; failing is normal.
            Log.Warn("ui", "复制日志失败", error);
            Shell.Notify("复制失败，剪贴板被其他程序占用", ToastKind.Warning);
        }
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        LayoutPage();
    }

    private void LayoutPage()
    {
        var padding = Dpi.Scale(this, 16);
        var gap = Dpi.Scale(this, 8);
        var rowHeight = Dpi.Scale(this, 30);

        var x = padding;
        _level.SetBounds(x, padding + Dpi.Scale(this, 2), Dpi.Scale(this, 150), rowHeight - Dpi.Scale(this, 4));
        x = _level.Right + gap;

        _filter.SetBounds(x, padding, Dpi.Scale(this, 220), rowHeight);
        x = _filter.Right + gap;

        _follow.SetBounds(x, padding, Dpi.Scale(this, 120), rowHeight);
        x = _follow.Right + gap;

        foreach (var button in new[] { _reload, _copy, _openFolder })
        {
            button.AutoSizeToContent(minimumWidth: Dpi.Scale(this, 86));
            button.SetBounds(x, padding, button.Width, rowHeight);
            x = button.Right + gap;
        }

        var statusTop = padding + rowHeight + Dpi.Scale(this, 6);
        _status.SetBounds(padding, statusTop, Math.Max(Dpi.Scale(this, 100), Width - padding * 2), Dpi.Scale(this, 18));

        var bodyTop = _status.Bottom + Dpi.Scale(this, 8);
        _frame.SetBounds(
            padding,
            bodyTop,
            Math.Max(Dpi.Scale(this, 120), Width - padding * 2),
            Math.Max(Dpi.Scale(this, 80), Height - bodyTop - padding));
        _output.SetBounds(1, 1, Math.Max(10, _frame.Width - 2), Math.Max(10, _frame.Height - 2));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _tick.Dispose();
        base.Dispose(disposing);
    }
}
