namespace ListenFlow.UI;

internal sealed class ComicPopoverPanel : Panel
{
    public ComicPopoverPanel()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
        BackColor = ComicTheme.Paper;
        Padding = new Padding(16, 14, 20, 18);
        Visible = false;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        ComicTheme.ConfigureGraphics(g);
        var s = ComicTheme.Scale(this);
        ComicTheme.FillCreamHalftone(g, ClientRectangle, s);

        var shOff = 6f * s;
        var borderW = 2.5f * s;
        var rect = new RectangleF(1f * s, 1f * s, Math.Max(20f, Width - shOff - 2f * s), Math.Max(20f, Height - shOff - 2f * s));
        ComicTheme.DrawHardShadowCard(g, rect, 10f * s, borderW, shOff, ComicTheme.Paper, ComicTheme.Ink, ComicTheme.Ink);
    }
}

internal sealed class HalftonePanel : Panel
{
    public HalftonePanel()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
        BackColor = ComicTheme.Cream;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var s = ComicTheme.Scale(this);
        ComicTheme.FillCreamHalftone(e.Graphics, ClientRectangle, s);
    }
}
