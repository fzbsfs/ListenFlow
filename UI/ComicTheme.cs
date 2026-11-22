using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace ListenFlow.UI;

internal static class ComicTheme
{
    public static readonly Color Vermilion = Color.FromArgb(242, 90, 44);
    public static readonly Color VermilionDark = Color.FromArgb(217, 67, 26);
    public static readonly Color VermilionHover = Color.FromArgb(255, 102, 54);
    public static readonly Color VermilionHappy = Color.FromArgb(255, 122, 61);
    public static readonly Color Ink = Color.FromArgb(22, 22, 22);
    public static readonly Color InkSoft = Color.FromArgb(45, 42, 39);
    public static readonly Color InkMuted = Color.FromArgb(58, 53, 50);
    public static readonly Color Cream = Color.FromArgb(243, 233, 213);
    public static readonly Color Paper = Color.FromArgb(251, 246, 236);
    public static readonly Color PaperHover = Color.FromArgb(255, 246, 229);
    public static readonly Color White = Color.White;
    public static readonly Color Mint = Color.FromArgb(37, 184, 134);
    public static readonly Color Yellow = Color.FromArgb(255, 210, 63);
    public static readonly Color Highlight = Color.FromArgb(255, 241, 168);
    public static readonly Color TextPrimary = Color.FromArgb(45, 38, 32);
    public static readonly Color TextMuted = Color.FromArgb(111, 101, 90);
    public static readonly Color DashedLine = Color.FromArgb(207, 195, 173);
    public static readonly Color PausedCard = Color.FromArgb(203, 188, 162);
    public static readonly Color DisabledFill = Color.FromArgb(236, 229, 216);
    public static readonly Color DisabledText = Color.FromArgb(156, 145, 132);
    public static readonly Color OverlayHighlight = Color.FromArgb(245, 130, 42);

    private static readonly Dictionary<int, Bitmap> CreamHalftoneTiles = new();
    private static readonly Dictionary<int, Bitmap> WhiteHalftoneTiles = new();

    public static float Scale(Control control) => Math.Max(1f, control.DeviceDpi / 96f);

    public static int Px(Control control, float logicalPx) => (int)Math.Round(logicalPx * Scale(control));

    public static float Pxf(Control control, float logicalPx) => logicalPx * Scale(control);

    public static void ConfigureGraphics(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
    }

    public static GraphicsPath CreateRoundedRect(RectangleF rect, float radius)
    {
        var path = new GraphicsPath();
        if (rect.Width <= 0 || rect.Height <= 0) return path;
        var r = Math.Min(radius, Math.Min(rect.Width, rect.Height) / 2f);
        if (r <= 0.5f)
        {
            path.AddRectangle(rect);
            return path;
        }
        var d = r * 2f;
        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    public static void FillCreamHalftone(Graphics g, Rectangle bounds, float scale, Point originOffset = default)
    {
        var step = Math.Max(7, (int)Math.Round(9f * scale));
        if (!CreamHalftoneTiles.TryGetValue(step, out var tile))
        {
            tile = new Bitmap(step, step);
            using var tg = Graphics.FromImage(tile);
            tg.SmoothingMode = SmoothingMode.AntiAlias;
            tg.Clear(Cream);
            var dotSize = Math.Max(2.2f, 2.5f * scale);
            var pos = (step - dotSize) / 2f;
            using var dotBrush = new SolidBrush(Color.FromArgb(38, 22, 22, 22));
            tg.FillEllipse(dotBrush, pos, pos, dotSize, dotSize);
            CreamHalftoneTiles[step] = tile;
        }
        using var brush = new TextureBrush(tile, WrapMode.Tile);
        if (originOffset != Point.Empty)
            brush.TranslateTransform(-originOffset.X, -originOffset.Y);
        g.FillRectangle(brush, bounds);
    }

    public static void FillWhiteHalftoneOverlay(Graphics g, GraphicsPath clipPath, RectangleF bounds, float scale)
    {
        var step = Math.Max(7, (int)Math.Round(9f * scale));
        if (!WhiteHalftoneTiles.TryGetValue(step, out var tile))
        {
            tile = new Bitmap(step, step);
            using var tg = Graphics.FromImage(tile);
            tg.SmoothingMode = SmoothingMode.AntiAlias;
            tg.Clear(Color.Transparent);
            var dotSize = Math.Max(2.2f, 2.5f * scale);
            var pos = (step - dotSize) / 2f;
            using var dotBrush = new SolidBrush(Color.FromArgb(72, 255, 255, 255));
            tg.FillEllipse(dotBrush, pos, pos, dotSize, dotSize);
            WhiteHalftoneTiles[step] = tile;
        }
        var state = g.Save();
        g.SetClip(clipPath, CombineMode.Intersect);
        using var brush = new TextureBrush(tile, WrapMode.Tile);
        g.FillRectangle(brush, bounds);
        g.Restore(state);
    }

    public static void DrawHardShadowCard(
        Graphics g,
        RectangleF rect,
        float radius,
        float borderWidth,
        float shadowOffset,
        Color fill,
        Color border,
        Color shadow)
    {
        if (shadowOffset > 0.1f)
        {
            var shadowRect = new RectangleF(rect.X + shadowOffset, rect.Y + shadowOffset, rect.Width, rect.Height);
            using var shadowPath = CreateRoundedRect(shadowRect, radius);
            using var shadowBrush = new SolidBrush(shadow);
            g.FillPath(shadowBrush, shadowPath);
        }

        using var cardPath = CreateRoundedRect(rect, radius);
        using var fillBrush = new SolidBrush(fill);
        g.FillPath(fillBrush, cardPath);
        if (borderWidth > 0.1f)
        {
            using var borderPen = new Pen(border, borderWidth) { LineJoin = LineJoin.Round };
            g.DrawPath(borderPen, cardPath);
        }
    }

    public static void DrawBarcode(Graphics g, RectangleF rect, Color color)
    {
        if (rect.Width < 12 || rect.Height < 4) return;
        float[] pattern = [2f, 2f, 1f, 2f, 3f, 2f, 1.5f, 1.5f, 2.5f, 2f, 1f, 2.5f];
        var unit = Math.Max(1f, rect.Height / 14f);
        var x = rect.X;
        var idx = 0;
        using var brush = new SolidBrush(color);
        while (x < rect.Right - unit)
        {
            var barW = pattern[idx % pattern.Length] * unit;
            var gapW = pattern[(idx + 1) % pattern.Length] * unit;
            var w = Math.Min(barW, rect.Right - x);
            if (w > 0.5f) g.FillRectangle(brush, x, rect.Y, w, rect.Height);
            x += barW + gapW;
            idx += 2;
        }
    }

    public static void DrawPillBadge(Graphics g, RectangleF rect, string text, Font font, Color back, Color fore, Color? border = null, float borderWidth = 0f)
    {
        using var path = CreateRoundedRect(rect, rect.Height / 2f);
        using var brush = new SolidBrush(back);
        g.FillPath(brush, path);
        if (border is { } b && borderWidth > 0f)
        {
            using var pen = new Pen(b, borderWidth);
            g.DrawPath(pen, path);
        }
        using var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
        using var fg = new SolidBrush(fore);
        g.DrawString(text, font, fg, rect, sf);
    }

    public static void DrawRoundedTag(Graphics g, RectangleF rect, string text, Font font, Color back, Color fore, float radius)
    {
        using var path = CreateRoundedRect(rect, radius);
        using var brush = new SolidBrush(back);
        g.FillPath(brush, path);
        using var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap };
        using var fg = new SolidBrush(fore);
        g.DrawString(text, font, fg, rect, sf);
    }

    public static void StyleComicButton(Button button, bool primary = false)
    {
        button.FlatStyle = FlatStyle.Flat;
        button.UseVisualStyleBackColor = false;
        button.Cursor = Cursors.Hand;
        button.FlatAppearance.BorderSize = 2;
        button.FlatAppearance.BorderColor = Ink;
        button.BackColor = primary ? Vermilion : White;
        button.ForeColor = primary ? White : Ink;
        button.FlatAppearance.MouseOverBackColor = primary ? VermilionHover : PaperHover;
        button.FlatAppearance.MouseDownBackColor = primary ? VermilionDark : Cream;
        button.Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold);
    }

    public static void StyleContextMenu(ContextMenuStrip menu)
    {
        menu.BackColor = Paper;
        menu.ForeColor = Ink;
        menu.ShowImageMargin = false;
        menu.Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold);
        menu.Renderer = new ComicToolStripRenderer();
    }

    public static void DisposeOnClose(ContextMenuStrip menu, Control owner)
    {
        EventHandler? onOwnerDisposed = null;
        onOwnerDisposed = (_, _) =>
        {
            if (!menu.IsDisposed) menu.Dispose();
        };
        owner.Disposed += onOwnerDisposed;
        menu.Disposed += (_, _) => owner.Disposed -= onOwnerDisposed;

        var scheduled = false;
        menu.Closed += (_, _) =>
        {
            if (scheduled || menu.IsDisposed) return;
            scheduled = true;

            void DisposeDeferred()
            {
                if (!menu.IsDisposed) menu.Dispose();
            }

            if (!owner.IsDisposed && owner.IsHandleCreated)
            {
                try
                {
                    owner.BeginInvoke((Action)DisposeDeferred);
                    return;
                }
                catch (ObjectDisposedException) { }
                catch (InvalidOperationException) { }
            }

            if (!menu.IsDisposed && menu.IsHandleCreated)
            {
                try
                {
                    menu.BeginInvoke((Action)DisposeDeferred);
                    return;
                }
                catch (ObjectDisposedException) { }
                catch (InvalidOperationException) { }
            }

            DisposeDeferred();
        };
    }

    private sealed class ComicToolStripRenderer : ToolStripProfessionalRenderer
    {
        public ComicToolStripRenderer() : base(new ComicColorTable()) { }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            using var pen = new Pen(Ink, 2f);
            e.Graphics.DrawRectangle(pen, 1, 1, e.AffectedBounds.Width - 2, e.AffectedBounds.Height - 2);
        }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            var rect = new Rectangle(3, 1, e.Item.Width - 6, e.Item.Height - 2);
            if (e.Item.Selected && e.Item.Enabled)
            {
                using var brush = new SolidBrush(Vermilion);
                using var path = CreateRoundedRect(rect, 5f);
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                e.Graphics.FillPath(brush, path);
            }
            else
            {
                using var brush = new SolidBrush(Paper);
                e.Graphics.FillRectangle(brush, rect);
            }
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = !e.Item.Enabled ? DisabledText : e.Item.Selected ? White : Ink;
            base.OnRenderItemText(e);
        }
    }

    private sealed class ComicColorTable : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => Paper;
        public override Color MenuBorder => Ink;
        public override Color SeparatorDark => DashedLine;
        public override Color SeparatorLight => Paper;
    }
}
