using System.Drawing.Drawing2D;
using ListenFlow.Core;

namespace ListenFlow.UI;

internal sealed class ComicQueuePanel : Control
{
    public sealed record QueueCardEntry(string Stub, string Title, string Subtitle, int Index, bool IsCurrent, bool IsReturn);

    private readonly List<HitCard> _hitCards = [];
    private string _currentTitle = "未命名文章";
    private string _currentSource = "本地";
    private int _currentChars;
    private PlaybackState _state = PlaybackState.Ready;
    private string? _returnTitle;
    private IReadOnlyList<(string Title, int Length)> _queued = [];
    private int _interactCount;
    private int _scrollOffset;
    private int _maxScroll;

    private RectangleF _addBtnRect;
    private RectangleF _manageBtnRect;
    private RectangleF _emptyAddRect;
    private string? _hoveredId;

    public event EventHandler? CurrentCardClicked;
    public event EventHandler<int>? QueuedItemPlayRequested;
    public event EventHandler<int>? QueuedItemRemoveRequested;
    public event EventHandler? RestoreReturnRequested;
    public event EventHandler? EnqueueClipboardRequested;
    public event EventHandler? ManageQueueRequested;

    public ComicQueuePanel()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
        BackColor = ComicTheme.Cream;
        TabStop = false;
    }

    public void UpdateQueue(
        string currentTitle,
        string currentSource,
        int currentChars,
        PlaybackState state,
        string? returnTitle,
        IReadOnlyList<(string Title, int Length)> queued)
    {
        _currentTitle = string.IsNullOrWhiteSpace(currentTitle) ? "未命名文章" : currentTitle;
        _currentSource = currentSource;
        _currentChars = Math.Max(0, currentChars);
        _state = state;
        _returnTitle = returnTitle;
        _queued = queued;
        Invalidate();
    }

    public void SetInteractCount(int count)
    {
        if (_interactCount == count) return;
        _interactCount = Math.Max(0, count);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        ComicTheme.ConfigureGraphics(g);
        var s = ComicTheme.Scale(this);
        ComicTheme.FillCreamHalftone(g, ClientRectangle, s);

        _hitCards.Clear();
        _emptyAddRect = RectangleF.Empty;

        // Header row: 待听队列 [N]   [★ 互动 N] [+]
        var headerH = 28f * s;
        using var headerFont = new Font("Microsoft YaHei UI", 10.2f, FontStyle.Bold);
        using var badgeFont = new Font("Microsoft YaHei UI", 8.2f, FontStyle.Bold);
        using var titleFont = new Font("Microsoft YaHei UI", 9.2f, FontStyle.Bold);
        using var subFont = new Font("Microsoft YaHei UI", 7.8f, FontStyle.Regular);
        using var tagFont = new Font("Microsoft YaHei UI", 7.5f, FontStyle.Bold);

        var titleW = g.MeasureString("待听队列", headerFont).Width;
        using (var inkBrush = new SolidBrush(ComicTheme.Ink))
        using (var sf = new StringFormat { Alignment = StringAlignment.Near, LineAlignment = StringAlignment.Center })
            g.DrawString("待听队列", headerFont, inkBrush, new RectangleF(0, 0, titleW + 4 * s, headerH), sf);

        var totalDisplay = (_currentChars > 0 ? 1 : 0) + _queued.Count + (_returnTitle is not null ? 1 : 0);
        var countText = totalDisplay.ToString();
        var pillW = Math.Max(24f * s, g.MeasureString(countText, badgeFont).Width + 12f * s);
        var pillRect = new RectangleF(titleW + 4f * s, (headerH - 20f * s) / 2f, pillW, 20f * s);
        ComicTheme.DrawPillBadge(g, pillRect, countText, badgeFont, ComicTheme.Ink, ComicTheme.White);

        // Right-side header controls: [⋯] [+] [★ 互动 N]
        var rightX = Width - 2f * s;
        var btnH = 22f * s;
        var btnY = (headerH - btnH) / 2f;

        _manageBtnRect = new RectangleF(rightX - 24f * s, btnY, 24f * s, btnH);
        ComicTheme.DrawPillBadge(g, _manageBtnRect, "⋯", badgeFont,
            _hoveredId == "manage" ? ComicTheme.Vermilion : ComicTheme.White,
            _hoveredId == "manage" ? ComicTheme.White : ComicTheme.Ink,
            ComicTheme.Ink, 1.6f * s);
        rightX = _manageBtnRect.Left - 5f * s;

        _addBtnRect = new RectangleF(rightX - 24f * s, btnY, 24f * s, btnH);
        ComicTheme.DrawPillBadge(g, _addBtnRect, "+", badgeFont,
            _hoveredId == "add" ? ComicTheme.Vermilion : ComicTheme.White,
            _hoveredId == "add" ? ComicTheme.White : ComicTheme.Ink,
            ComicTheme.Ink, 1.6f * s);
        rightX = _addBtnRect.Left - 6f * s;

        var interactText = $"★ 互动 {_interactCount}";
        var interactW = g.MeasureString(interactText, tagFont).Width + 14f * s;
        if (rightX - interactW > pillRect.Right + 6f * s)
        {
            var interactRect = new RectangleF(rightX - interactW, btnY, interactW, btnH);
            ComicTheme.DrawPillBadge(g, interactRect, interactText, tagFont, ComicTheme.White, ComicTheme.Vermilion, ComicTheme.Ink, 1.6f * s);
        }

        // Ticket list viewport
        var listTop = (int)Math.Ceiling(headerH + 8f * s);
        var listRect = new Rectangle(0, listTop, Width, Math.Max(0, Height - listTop));
        if (listRect.Height <= 8) return;

        var state = g.Save();
        g.SetClip(listRect);

        var cardW = Width - 7f * s;
        var y = listTop - _scrollOffset;
        var cardGap = 10f * s;

        // Card 0: Current article ticket
        var currentH = 54f * s;
        var currentRect = new RectangleF(1f * s, y, cardW, currentH);
        var activeStub = _state == PlaybackState.Speaking ? "▶读" : _state == PlaybackState.Paused ? "❚❚" : "待读";
        var activeTag = _state switch
        {
            PlaybackState.Speaking => "正在读",
            PlaybackState.Paused => "已暂停",
            PlaybackState.Completed => "已读完",
            PlaybackState.Error => "出错",
            _ => "当前"
        };
        DrawTicketCard(
            g, currentRect, s,
            stubText: activeStub,
            isActive: true,
            isReturn: false,
            hovered: _hoveredId == "current",
            title: _currentTitle,
            tagText: activeTag,
            subtitle: $"{_currentSource} · {_currentChars:N0} 字",
            titleFont, subFont, tagFont);
        _hitCards.Add(new HitCard("current", -1, currentRect));
        y += (int)Math.Ceiling(currentH + cardGap);

        // Optional return-to-interrupted card
        if (_returnTitle is not null)
        {
            var retH = 48f * s;
            var retRect = new RectangleF(1f * s, y, cardW, retH);
            DrawTicketCard(
                g, retRect, s,
                stubText: "返回",
                isActive: false,
                isReturn: true,
                hovered: _hoveredId == "return",
                title: _returnTitle,
                tagText: "插播前",
                subtitle: "点击返回原文继续",
                titleFont, subFont, tagFont);
            _hitCards.Add(new HitCard("return", -2, retRect));
            y += (int)Math.Ceiling(retH + cardGap);
        }

        // Queued items
        for (var i = 0; i < _queued.Count; i++)
        {
            var item = _queued[i];
            var itemH = 48f * s;
            var rect = new RectangleF(1f * s, y, cardW, itemH);
            var id = $"q:{i}";
            DrawTicketCard(
                g, rect, s,
                stubText: $"0{i + 1}",
                isActive: false,
                isReturn: false,
                hovered: _hoveredId == id,
                title: item.Title,
                tagText: null,
                subtitle: $"待听 · {item.Length:N0} 字",
                titleFont, subFont, tagFont);
            _hitCards.Add(new HitCard(id, i, rect));
            y += (int)Math.Ceiling(itemH + cardGap);
        }

        // If queue is empty, draw quick dashed add-from-clipboard prompt
        if (_queued.Count == 0 && _returnTitle is null)
        {
            var addH = 44f * s;
            _emptyAddRect = new RectangleF(1f * s, y, cardW, addH);
            using var path = ComicTheme.CreateRoundedRect(_emptyAddRect, 8f * s);
            using (var bg = new SolidBrush(_hoveredId == "empty-add" ? ComicTheme.PaperHover : Color.FromArgb(180, 251, 246, 236)))
                g.FillPath(bg, path);
            using (var pen = new Pen(_hoveredId == "empty-add" ? ComicTheme.Vermilion : ComicTheme.TextMuted, 1.8f * s) { DashPattern = [4f, 3f] })
                g.DrawPath(pen, path);
            using var fg = new SolidBrush(_hoveredId == "empty-add" ? ComicTheme.Vermilion : ComicTheme.TextMuted);
            using var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString("+ 剪贴板加入待听队列", badgeFont, fg, _emptyAddRect, sf);
            y += (int)Math.Ceiling(addH + cardGap);
        }

        var contentHeight = (y + _scrollOffset) - listTop;
        _maxScroll = Math.Max(0, contentHeight - listRect.Height);
        if (_scrollOffset > _maxScroll) _scrollOffset = _maxScroll;

        g.Restore(state);
    }

    private static void DrawTicketCard(
        Graphics g,
        RectangleF rect,
        float s,
        string stubText,
        bool isActive,
        bool isReturn,
        bool hovered,
        string title,
        string? tagText,
        string subtitle,
        Font titleFont,
        Font subFont,
        Font tagFont)
    {
        var r = 8f * s;
        var borderW = 2.2f * s;
        var shadowOff = (isActive ? 4.5f : 3.2f) * s;
        var shadowColor = isActive ? ComicTheme.Vermilion : isReturn ? ComicTheme.Mint : ComicTheme.Ink;
        var stubW = 36f * s;

        var shadowRect = new RectangleF(rect.X + shadowOff, rect.Y + shadowOff, rect.Width, rect.Height);
        using (var shPath = ComicTheme.CreateRoundedRect(shadowRect, r))
        using (var shBrush = new SolidBrush(shadowColor))
            g.FillPath(shBrush, shPath);

        using var cardPath = ComicTheme.CreateRoundedRect(rect, r);
        using (var fillBrush = new SolidBrush(hovered ? ComicTheme.PaperHover : ComicTheme.White))
            g.FillPath(fillBrush, cardPath);

        var stubColor = isActive
            ? ComicTheme.Vermilion
            : isReturn ? ComicTheme.Mint : ComicTheme.Cream;
        var stubFore = (isActive || isReturn) ? ComicTheme.White : ComicTheme.TextMuted;

        var state = g.Save();
        g.SetClip(cardPath, CombineMode.Intersect);
        using (var stubBrush = new SolidBrush(stubColor))
            g.FillRectangle(stubBrush, rect.X, rect.Y, stubW, rect.Height);
        g.Restore(state);

        using (var dashPen = new Pen(ComicTheme.Ink, 1.5f * s) { DashPattern = [2.5f, 2f] })
            g.DrawLine(dashPen, rect.X + stubW, rect.Y + 4 * s, rect.X + stubW, rect.Bottom - 4 * s);

        using (var borderPen = new Pen(ComicTheme.Ink, borderW) { LineJoin = LineJoin.Round })
            g.DrawPath(borderPen, cardPath);

        // Mini notches
        var nr = 5f * s;
        DrawMiniNotch(g, rect.X + stubW, rect.Top, nr, isTop: true, borderW);
        DrawMiniNotch(g, rect.X + stubW, rect.Bottom, nr, isTop: false, borderW);

        // Stub text
        using (var stubFont = new Font("Microsoft YaHei UI", stubText.Length > 2 ? 7.2f : 8.5f, FontStyle.Bold))
        using (var fg = new SolidBrush(stubFore))
        using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
            g.DrawString(stubText, stubFont, fg, new RectangleF(rect.X, rect.Y, stubW, rect.Height), sf);

        // Body text
        var textLeft = rect.X + stubW + 9f * s;
        var textW = Math.Max(24f, rect.Right - textLeft - 8f * s);
        var titleRect = new RectangleF(textLeft, rect.Y + 6f * s, textW, 19f * s);
        using (var tb = new SolidBrush(ComicTheme.Ink))
        using (var sf = new StringFormat { Alignment = StringAlignment.Near, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap })
            g.DrawString(title, titleFont, tb, titleRect, sf);

        var subY = rect.Y + 26f * s;
        var subX = textLeft;
        if (!string.IsNullOrEmpty(tagText))
        {
            var tagW = Math.Max(34f * s, g.MeasureString(tagText, tagFont).Width + 10f * s);
            var tagH = 16f * s;
            ComicTheme.DrawRoundedTag(g, new RectangleF(subX, subY + 1f * s, tagW, tagH), tagText, tagFont,
                isActive ? ComicTheme.Vermilion : ComicTheme.Mint, ComicTheme.White, 4.5f * s);
            subX += tagW + 5f * s;
        }
        using (var sb = new SolidBrush(ComicTheme.TextMuted))
        using (var sf = new StringFormat { Alignment = StringAlignment.Near, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap })
            g.DrawString(subtitle, subFont, sb, new RectangleF(subX, subY, Math.Max(10f, rect.Right - subX - 6f * s), 18f * s), sf);
    }

    private static void DrawMiniNotch(Graphics g, float cx, float cy, float r, bool isTop, float borderW)
    {
        var nr = new RectangleF(cx - r, cy - r, r * 2f, r * 2f);
        var state = g.Save();
        g.SetClip(isTop
            ? new RectangleF(nr.X - 2, cy - 1, nr.Width + 4, r + 3)
            : new RectangleF(nr.X - 2, cy - r - 2, nr.Width + 4, r + 3));
        using (var fill = new SolidBrush(ComicTheme.Cream))
            g.FillEllipse(fill, nr);
        using (var pen = new Pen(ComicTheme.Ink, borderW))
            g.DrawArc(pen, nr, isTop ? 0 : 180, 180);
        g.Restore(state);
    }

    private string? HitTest(Point p, out int queueIndex)
    {
        queueIndex = -1;
        if (_addBtnRect.Contains(p)) return "add";
        if (_manageBtnRect.Contains(p)) return "manage";
        if (!_emptyAddRect.IsEmpty && _emptyAddRect.Contains(p)) return "empty-add";
        foreach (var c in _hitCards)
        {
            if (c.Bounds.Contains(p))
            {
                queueIndex = c.QueueIndex;
                return c.Id;
            }
        }
        return null;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var hit = HitTest(e.Location, out _);
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
        var hit = HitTest(e.Location, out var idx);
        if (e.Button == MouseButtons.Right && idx >= 0)
        {
            var menu = new ContextMenuStrip();
            ComicTheme.StyleContextMenu(menu);
            menu.Items.Add("立即播放此项", null, (_, _) => QueuedItemPlayRequested?.Invoke(this, idx));
            menu.Items.Add("从待听队列移除", null, (_, _) => QueuedItemRemoveRequested?.Invoke(this, idx));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("打开待听队列管理…", null, (_, _) => ManageQueueRequested?.Invoke(this, EventArgs.Empty));
            ComicTheme.DisposeOnClose(menu, this);
            menu.Show(this, e.Location);
            return;
        }
        if (e.Button != MouseButtons.Left) return;
        switch (hit)
        {
            case "add":
            case "empty-add":
                EnqueueClipboardRequested?.Invoke(this, EventArgs.Empty);
                break;
            case "manage":
                ManageQueueRequested?.Invoke(this, EventArgs.Empty);
                break;
            case "current":
                CurrentCardClicked?.Invoke(this, EventArgs.Empty);
                break;
            case "return":
                RestoreReturnRequested?.Invoke(this, EventArgs.Empty);
                break;
            default:
                if (idx >= 0) QueuedItemPlayRequested?.Invoke(this, idx);
                break;
        }
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if (_maxScroll <= 0) return;
        _scrollOffset = Math.Clamp(_scrollOffset - Math.Sign(e.Delta) * 28, 0, _maxScroll);
        Invalidate();
    }

    private sealed record HitCard(string Id, int QueueIndex, RectangleF Bounds);
}
