namespace ListenFlow.UI;

internal sealed class ComicPaperPanel : Panel
{
    private readonly System.Windows.Forms.Timer _flashTimer = new() { Interval = 45 };
    private TextBox? _editor;
    private int _activeCharIndex = -1;
    private int _flashTicks;

    public ComicPaperPanel()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
        BackColor = ComicTheme.Cream;
        _flashTimer.Tick += (_, _) =>
        {
            _flashTicks--;
            if (_flashTicks <= 0) _flashTimer.Stop();
            Invalidate();
        };
    }

    public void AttachEditor(TextBox editor)
    {
        _editor = editor;
        editor.BorderStyle = BorderStyle.None;
        editor.BackColor = ComicTheme.Paper;
        editor.ForeColor = ComicTheme.TextPrimary;
        editor.HideSelection = false;
        Controls.Clear();
        Controls.Add(editor);
        LayoutEditor();
    }

    public void SetActiveSentence(int charIndex)
    {
        _activeCharIndex = charIndex;
        Invalidate();
    }

    public void FlashLocate()
    {
        _flashTicks = 18;
        _flashTimer.Start();
        Invalidate();
    }

    protected override void OnResize(EventArgs eventargs)
    {
        base.OnResize(eventargs);
        LayoutEditor();
    }

    private void LayoutEditor()
    {
        if (_editor is null) return;
        var s = ComicTheme.Scale(this);
        var shadow = (int)Math.Round(6 * s);
        var border = (int)Math.Round(3 * s);
        var padLeft = (int)Math.Round(24 * s) + border;
        var padTop = (int)Math.Round(18 * s) + border;
        var padRight = (int)Math.Round(18 * s) + border + shadow;
        var padBottom = (int)Math.Round(16 * s) + border + shadow;

        var w = Math.Max(60, Width - padLeft - padRight);
        var h = Math.Max(40, Height - padTop - padBottom);
        _editor.Bounds = new Rectangle(padLeft, padTop, w, h);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        ComicTheme.ConfigureGraphics(g);
        var s = ComicTheme.Scale(this);
        ComicTheme.FillCreamHalftone(g, ClientRectangle, s);

        var shadowOff = 6f * s;
        var borderW = 2.5f * s;
        var cardRect = new RectangleF(1f * s, 1f * s, Math.Max(20f, Width - shadowOff - 2f * s), Math.Max(20f, Height - shadowOff - 2f * s));

        var fill = _flashTicks > 0 && (_flashTicks % 4 >= 2) ? ComicTheme.Highlight : ComicTheme.Paper;
        ComicTheme.DrawHardShadowCard(g, cardRect, 8f * s, borderW, shadowOff, fill, ComicTheme.Ink, ComicTheme.Ink);

        // Draw subtle vermilion reading accent in the left margin aligned with current sentence line
        if (_editor is not null && _activeCharIndex >= 0 && _activeCharIndex < _editor.TextLength)
        {
            var pt = _editor.GetPositionFromCharIndex(_activeCharIndex);
            var barY = _editor.Top + pt.Y + 2f * s;
            var lineH = Math.Max(18f * s, _editor.Font.Height * 0.92f);
            if (barY >= _editor.Top - 2f * s && barY + lineH <= _editor.Bottom + 4f * s)
            {
                var barRect = new RectangleF(cardRect.X + 10f * s, barY, 4.5f * s, lineH);
                using var path = ComicTheme.CreateRoundedRect(barRect, 2.2f * s);
                using var brush = new SolidBrush(ComicTheme.Vermilion);
                g.FillPath(brush, path);
            }
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _flashTimer.Dispose();
        base.Dispose(disposing);
    }
}
