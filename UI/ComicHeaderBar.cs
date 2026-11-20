using System.ComponentModel;
using System.Drawing.Drawing2D;

namespace ListenFlow.UI;

internal sealed class ComicHeaderBar : Control
{
    private readonly List<NavItem> _items = [];
    private string _activeNav = "listen";
    private string? _hoveredId;
    private string? _pressedId;
    private int _queueCount;
    private bool _settingsOpen;
    private bool _historyOpen;

    public event EventHandler? ListeningClicked;
    public event EventHandler? QueueClicked;
    public event EventHandler? HistoryClicked;
    public event EventHandler? OpenTxtClicked;
    public event EventHandler? PasteClipboardClicked;
    public event EventHandler? SettingsClicked;

    public ComicHeaderBar()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
        Dock = DockStyle.Top;
        Height = 54;
        BackColor = ComicTheme.Ink;
        TabStop = false;
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int QueueCount
    {
        get => _queueCount;
        set
        {
            if (_queueCount == value) return;
            _queueCount = Math.Max(0, value);
            Invalidate();
        }
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool SettingsOpen
    {
        get => _settingsOpen;
        set
        {
            if (_settingsOpen == value) return;
            _settingsOpen = value;
            Invalidate();
        }
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool HistoryOpen
    {
        get => _historyOpen;
        set
        {
            if (_historyOpen == value) return;
            _historyOpen = value;
            _activeNav = value ? "history" : "listen";
            Invalidate();
        }
    }

    public Rectangle GetItemBounds(string id) =>
        _items.FirstOrDefault(i => i.Id == id)?.Bounds ?? Rectangle.Empty;

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        ComicTheme.ConfigureGraphics(g);
        var s = ComicTheme.Scale(this);
        g.Clear(ComicTheme.Ink);

        _items.Clear();

        var padX = (int)Math.Round(22 * s);
        var cy = Height / 2f;
        var compact = Width < (int)(860 * s);

        using var logoFont = new Font("Microsoft YaHei UI", 16.5f, FontStyle.Bold | FontStyle.Italic);
        using var navFont = new Font("Microsoft YaHei UI", compact ? 9f : 10f, FontStyle.Bold);
        using var pillFont = new Font("Microsoft YaHei UI", 9.2f, FontStyle.Bold);

        // Brand: ListenFlow
        var listenSize = g.MeasureString("Listen", logoFont, PointF.Empty, StringFormat.GenericTypographic);
        var flowSize = g.MeasureString("Flow", logoFont, PointF.Empty, StringFormat.GenericTypographic);
        var logoY = cy - listenSize.Height / 2f - 1f * s;
        using (var creamBrush = new SolidBrush(ComicTheme.Cream))
        using (var verBrush = new SolidBrush(ComicTheme.Vermilion))
        {
            g.DrawString("Listen", logoFont, creamBrush, padX, logoY, StringFormat.GenericTypographic);
            g.DrawString("Flow", logoFont, verBrush, padX + listenSize.Width - 1.5f * s, logoY, StringFormat.GenericTypographic);
        }

        var x = padX + (int)Math.Ceiling(listenSize.Width + flowSize.Width + (compact ? 14 * s : 24 * s));
        var navH = (int)Math.Round(32 * s);
        var navY = (Height - navH) / 2;

        // Left Nav Tabs
        x = DrawNavTab(g, "listen", "正在听", navFont, x, navY, navH, s, _activeNav == "listen" && !_historyOpen);
        x += (int)Math.Round(6 * s);
        x = DrawNavTab(g, "queue", $"待听队列 {_queueCount}", navFont, x, navY, navH, s, _activeNav == "queue");
        x += (int)Math.Round(6 * s);
        DrawNavTab(g, "history", "最近文章", navFont, x, navY, navH, s, _historyOpen);

        // Right Action Controls (built right-to-left)
        var rightX = Width - padX;
        var pillH = (int)Math.Round(30 * s);
        var pillY = (Height - pillH) / 2;

        // ⚙ 设置 pill
        var setText = "⚙ 设置";
        var setW = (int)Math.Ceiling(g.MeasureString(setText, pillFont).Width + 24 * s);
        var setRect = new Rectangle(rightX - setW, pillY, setW, pillH);
        _items.Add(new NavItem("settings", setRect));
        DrawSettingsPill(g, setRect, setText, pillFont, s, _settingsOpen, _hoveredId == "settings", _pressedId == "settings");
        rightX = setRect.Left - (int)Math.Round(10 * s);

        // 粘贴剪贴板
        var pasteText = compact ? "剪贴板" : "粘贴剪贴板";
        var pasteW = (int)Math.Ceiling(g.MeasureString(pasteText, navFont).Width + 20 * s);
        var pasteRect = new Rectangle(rightX - pasteW, navY, pasteW, navH);
        if (pasteRect.Left > x + (int)(86 * s))
        {
            _items.Add(new NavItem("paste", pasteRect));
            DrawActionLink(g, pasteRect, pasteText, navFont, s, _hoveredId == "paste", _pressedId == "paste");
            rightX = pasteRect.Left - (int)Math.Round(6 * s);
        }

        // 打开 TXT
        var openText = compact ? "打开" : "打开 TXT";
        var openW = (int)Math.Ceiling(g.MeasureString(openText, navFont).Width + 20 * s);
        var openRect = new Rectangle(rightX - openW, navY, openW, navH);
        if (openRect.Left > x + (int)(86 * s))
        {
            _items.Add(new NavItem("open", openRect));
            DrawActionLink(g, openRect, openText, navFont, s, _hoveredId == "open", _pressedId == "open");
        }
    }

    private int DrawNavTab(Graphics g, string id, string text, Font font, int x, int y, int h, float s, bool active)
    {
        var textSize = g.MeasureString(text, font);
        var w = (int)Math.Ceiling(textSize.Width + 24 * s);
        var rect = new Rectangle(x, y, w, h);
        _items.Add(new NavItem(id, rect));

        var hovered = _hoveredId == id;
        if (active)
        {
            var skew = 7f * s;
            PointF[] poly =
            [
                new(rect.Left + skew, rect.Top),
                new(rect.Right, rect.Top),
                new(rect.Right - skew, rect.Bottom),
                new(rect.Left, rect.Bottom)
            ];
            using var brush = new SolidBrush(hovered ? ComicTheme.VermilionHover : ComicTheme.Vermilion);
            g.FillPolygon(brush, poly);
        }
        else if (hovered)
        {
            using var path = ComicTheme.CreateRoundedRect(rect, 6f * s);
            using var brush = new SolidBrush(Color.FromArgb(38, 255, 255, 255));
            g.FillPath(brush, path);
        }

        using var fg = new SolidBrush(ComicTheme.White);
        using var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap };
        g.DrawString(text, font, fg, rect, sf);
        return rect.Right;
    }

    private static void DrawActionLink(Graphics g, Rectangle rect, string text, Font font, float s, bool hovered, bool pressed)
    {
        if (hovered)
        {
            using var path = ComicTheme.CreateRoundedRect(rect, 6f * s);
            using var brush = new SolidBrush(Color.FromArgb(pressed ? 55 : 32, 255, 255, 255));
            g.FillPath(brush, path);
        }
        using var fg = new SolidBrush(hovered ? ComicTheme.Cream : ComicTheme.White);
        using var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap };
        g.DrawString(text, font, fg, rect, sf);
    }

    private static void DrawSettingsPill(Graphics g, Rectangle rect, string text, Font font, float s, bool open, bool hovered, bool pressed)
    {
        var drawRect = pressed ? new Rectangle(rect.X + 1, rect.Y + 1, rect.Width, rect.Height) : rect;
        var fill = open
            ? ComicTheme.Vermilion
            : hovered ? Color.FromArgb(40, 255, 255, 255) : Color.Transparent;
        var border = open ? ComicTheme.Vermilion : ComicTheme.White;
        ComicTheme.DrawPillBadge(g, drawRect, text, font, fill, ComicTheme.White, border, 1.8f * s);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var hit = _items.FirstOrDefault(i => i.Bounds.Contains(e.Location))?.Id;
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
        if (_hoveredId is not null || _pressedId is not null)
        {
            _hoveredId = null;
            _pressedId = null;
            Cursor = Cursors.Default;
            Invalidate();
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        _pressedId = _items.FirstOrDefault(i => i.Bounds.Contains(e.Location))?.Id;
        if (_pressedId is not null) Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Left) return;
        var hit = _items.FirstOrDefault(i => i.Bounds.Contains(e.Location))?.Id;
        var fire = hit is not null && hit == _pressedId ? hit : null;
        _pressedId = null;
        Invalidate();
        switch (fire)
        {
            case "listen":
                _activeNav = "listen";
                ListeningClicked?.Invoke(this, EventArgs.Empty);
                break;
            case "queue":
                QueueClicked?.Invoke(this, EventArgs.Empty);
                break;
            case "history":
                HistoryClicked?.Invoke(this, EventArgs.Empty);
                break;
            case "open":
                OpenTxtClicked?.Invoke(this, EventArgs.Empty);
                break;
            case "paste":
                PasteClipboardClicked?.Invoke(this, EventArgs.Empty);
                break;
            case "settings":
                SettingsClicked?.Invoke(this, EventArgs.Empty);
                break;
        }
    }

    private sealed record NavItem(string Id, Rectangle Bounds);
}
