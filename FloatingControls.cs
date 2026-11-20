using ListenFlow.Core;
using ListenFlow.Storage;
using ListenFlow.UI;

namespace ListenFlow;

internal sealed class FloatingControls : Form
{
    private readonly ToolTip _tips = new();
    private readonly Button _toggle = new();
    private readonly Label _state = new();
    private readonly Label _source = new();
    private readonly Button _previous = new();
    private readonly Button _next = new();
    private readonly DesktopPreferences _preferences;
    private bool _allowClose;
    private Point _dragStart;

    public FloatingControls(Action toggle, Action stop, Action slower, Action faster, Action showMain, Action readingActions,
        Action previous, Action next, DesktopPreferences preferences)
    {
        _preferences = preferences;
        Text = "ListenFlow · 朗读控制";
        FormBorderStyle = FormBorderStyle.FixedToolWindow;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold);
        BackColor = ComicTheme.Cream;
        ClientSize = new Size(388, 120);
        var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1024, 768);
        Location = new Point(Math.Max(area.Left, area.Right - Width - 24), Math.Max(area.Top, area.Bottom - Height - 24));
        if (preferences.FloatingLeft is int left && preferences.FloatingTop is int top)
            Location = new Point(left, top);
        Shown += (_, _) => KeepOnScreen();
        ResizeEnd += (_, _) => SavePosition();
        var row = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 54, Padding = new Padding(10, 9, 0, 0), WrapContents = false, BackColor = ComicTheme.Cream };
        Controls.Add(row);
        Add(row, _previous, "‹", "上一句；暂停时定位不会提前发声", previous);
        Add(row, _toggle, "▶", "播放或暂停", toggle);
        Add(row, _next, "›", "下一句；暂停时定位不会提前发声", next);
        Add(row, new Button(), "■", "停止朗读", stop);
        Add(row, new Button(), "−", "降低语速", slower);
        Add(row, new Button(), "+", "提高语速", faster);
        Add(row, new Button(), "⋯", "更多朗读操作，包括剪贴板、插播、待听队列和重听", readingActions);
        Add(row, new Button(), "▣", "打开主窗口", showMain);
        _state.Dock = DockStyle.Bottom;
        _state.Height = 32;
        _state.TextAlign = ContentAlignment.MiddleLeft;
        _state.Padding = new Padding(12, 0, 0, 0);
        _state.BackColor = ComicTheme.Ink;
        _state.ForeColor = ComicTheme.Cream;
        _state.Text = "准备就绪";
        Controls.Add(_state);
        _state.MouseDown += (_, e) => { if (e.Button == MouseButtons.Left) _dragStart = e.Location; };
        _state.MouseMove += (_, e) => { if (e.Button == MouseButtons.Left) Location = new Point(Left + e.X - _dragStart.X, Top + e.Y - _dragStart.Y); };
        _state.MouseUp += (_, _) => SavePosition();
        _tips.SetToolTip(_state, "拖动此处移动控制条");
        _source.Dock = DockStyle.Fill;
        _source.AutoEllipsis = true;
        _source.TextAlign = ContentAlignment.MiddleLeft;
        _source.Padding = new Padding(12, 0, 12, 0);
        _source.BackColor = ComicTheme.Paper;
        _source.ForeColor = ComicTheme.Ink;
        Controls.Add(_source);
        _source.BringToFront();
        FormClosing += (_, e) =>
        {
            SavePosition();
            if (!_allowClose && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); }
        };
    }

    private void Add(Control parent, Button button, string text, string hint, Action action)
    {
        var isPrimary = ReferenceEquals(button, _toggle);
        button.Text = text;
        button.Size = new Size(40, 34);
        button.Margin = new Padding(0, 0, 6, 0);
        button.FlatStyle = FlatStyle.Flat;
        button.UseVisualStyleBackColor = false;
        button.FlatAppearance.BorderSize = 2;
        button.FlatAppearance.BorderColor = ComicTheme.Ink;
        button.FlatAppearance.MouseOverBackColor = isPrimary ? ComicTheme.VermilionHover : ComicTheme.PaperHover;
        button.FlatAppearance.MouseDownBackColor = isPrimary ? ComicTheme.VermilionDark : ComicTheme.Cream;
        button.BackColor = isPrimary ? ComicTheme.Vermilion : ComicTheme.White;
        button.ForeColor = isPrimary ? ComicTheme.White : ComicTheme.Ink;
        button.Font = new Font("Segoe UI Symbol", 11F, FontStyle.Bold);
        button.Cursor = Cursors.Hand;
        button.AccessibleName = hint;
        _tips.SetToolTip(button, hint);
        button.Click += (_, _) => action();
        parent.Controls.Add(button);
    }

    public void SetState(PlaybackState state, int rate, string title, bool canRead)
    {
        _toggle.Enabled = _previous.Enabled = _next.Enabled = canRead;
        _source.Text = "当前文章 · " + title;
        _tips.SetToolTip(_source, title);
        _toggle.Text = state == PlaybackState.Speaking ? "Ⅱ" : "▶";
        _tips.SetToolTip(_toggle, state == PlaybackState.Speaking ? "暂停朗读" : "播放或继续朗读");
        _state.Text = $"{(state == PlaybackState.Speaking ? "●  正在朗读" : state == PlaybackState.Paused ? "●  已暂停" : state == PlaybackState.Error ? "朗读出错" : state == PlaybackState.Completed ? "朗读完成" : state == PlaybackState.Ready ? "准备就绪" : "已停止")}     语速 {rate:+0;-0;0}";
    }

    private void KeepOnScreen()
    {
        var area = Screen.FromRectangle(Bounds).WorkingArea;
        Location = new Point(Math.Clamp(Left, area.Left, Math.Max(area.Left, area.Right - Width)),
            Math.Clamp(Top, area.Top, Math.Max(area.Top, area.Bottom - Height)));
    }

    private void SavePosition()
    {
        KeepOnScreen();
        _preferences.FloatingLeft = Left;
        _preferences.FloatingTop = Top;
        _preferences.Save();
    }

    public void CloseForExit() { _allowClose = true; Close(); }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _tips.Dispose();
        base.Dispose(disposing);
    }
}
