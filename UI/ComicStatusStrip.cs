namespace ListenFlow.UI;

internal sealed class ComicStatusStrip : Control
{
    private readonly List<ActionChip> _chips = [];
    private string _voiceName = "本机语音";
    private bool _followReading = true;
    private int _fontSize = 13;
    private string _statusText = "准备就绪。";
    private bool _mascotHidden;
    private string? _hoveredId;

    public event EventHandler? FollowToggleClicked;
    public event EventHandler? RepeatSentenceClicked;
    public event EventHandler? FloatingClicked;
    public event EventHandler? RestoreMascotClicked;

    public ComicStatusStrip()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
        Dock = DockStyle.Bottom;
        Height = 34;
        BackColor = ComicTheme.Cream;
        TabStop = false;
    }

    public void UpdateStatus(string voiceName, bool followReading, int fontSize, string statusText, bool mascotHidden)
    {
        _voiceName = string.IsNullOrWhiteSpace(voiceName) ? "未选择语音" : voiceName;
        _followReading = followReading;
        _fontSize = fontSize;
        _statusText = statusText;
        _mascotHidden = mascotHidden;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        ComicTheme.ConfigureGraphics(g);
        var s = ComicTheme.Scale(this);
        ComicTheme.FillCreamHalftone(g, ClientRectangle, s);

        _chips.Clear();

        var padX = 24f * s;
        var chipH = 22f * s;
        var chipY = (Height - chipH) / 2f;
        var rightX = Width - padX;

        using var chipFont = new Font("Microsoft YaHei UI", 8f, FontStyle.Bold);
        using var infoFont = new Font("Microsoft YaHei UI", 8.4f, FontStyle.Regular);

        if (_mascotHidden)
            rightX = DrawChip(g, "mascot", "唤回看板娘", chipFont, rightX, chipY, chipH, s, active: true);
        rightX = DrawChip(g, "floating", "悬浮条", chipFont, rightX, chipY, chipH, s, active: false);
        rightX = DrawChip(g, "repeat", "重听本句", chipFont, rightX, chipY, chipH, s, active: false);
        rightX = DrawChip(g, "follow", _followReading ? "✓ 跟随朗读" : "跟随朗读", chipFont, rightX, chipY, chipH, s, active: _followReading);

        var infoText = $"本地语音：{_voiceName} · 字号 {_fontSize} · {_statusText}";
        var infoRect = new RectangleF(padX, 0, Math.Max(40f, rightX - padX - 10f * s), Height);
        using var fg = new SolidBrush(ComicTheme.TextMuted);
        using var sf = new StringFormat { Alignment = StringAlignment.Near, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
        g.DrawString(infoText, infoFont, fg, infoRect, sf);
    }

    private float DrawChip(Graphics g, string id, string text, Font font, float rightX, float y, float h, float s, bool active)
    {
        var w = g.MeasureString(text, font).Width + 16f * s;
        var rect = new RectangleF(rightX - w, y, w, h);
        _chips.Add(new ActionChip(id, rect));
        var hovered = _hoveredId == id;
        var back = active
            ? (hovered ? ComicTheme.VermilionHover : ComicTheme.Ink)
            : (hovered ? ComicTheme.PaperHover : ComicTheme.White);
        var fore = active ? ComicTheme.White : ComicTheme.Ink;
        ComicTheme.DrawPillBadge(g, rect, text, font, back, fore, ComicTheme.Ink, 1.5f * s);
        return rect.Left - 7f * s;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var hit = _chips.FirstOrDefault(c => c.Bounds.Contains(e.Location))?.Id;
        if (hit != _hoveredId)
        {
            _hoveredId = hit;
            Cursor = hit is not null ? Cursors.Hand : Cursors.Default;
            Invalidate();
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hoveredId is not null)
        {
            _hoveredId = null;
            Cursor = Cursors.Default;
            Invalidate();
        }
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (e.Button != MouseButtons.Left) return;
        var hit = _chips.FirstOrDefault(c => c.Bounds.Contains(e.Location))?.Id;
        switch (hit)
        {
            case "follow": FollowToggleClicked?.Invoke(this, EventArgs.Empty); break;
            case "repeat": RepeatSentenceClicked?.Invoke(this, EventArgs.Empty); break;
            case "floating": FloatingClicked?.Invoke(this, EventArgs.Empty); break;
            case "mascot": RestoreMascotClicked?.Invoke(this, EventArgs.Empty); break;
        }
    }

    private sealed record ActionChip(string Id, RectangleF Bounds);
}
