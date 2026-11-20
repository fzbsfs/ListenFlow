using System.Drawing.Drawing2D;
using ListenFlow.Core;

namespace ListenFlow.UI;

internal sealed class HeroTicketPlayer : Control
{
    private PlaybackState _state = PlaybackState.Ready;
    private string _title = "未命名文章";
    private string _sourceBadge = "本地";
    private bool _highlightBadge;
    private int _currentChars;
    private int _totalChars;
    private int _rate = 2;
    private bool _canRead;
    private bool _canStop;

    private RectangleF _prevRect;
    private RectangleF _playRect;
    private RectangleF _nextRect;
    private RectangleF _stopRect;
    private RectangleF _progressRect;
    private RectangleF _rateMinusRect;
    private RectangleF _ratePlusRect;
    private RectangleF _speedAreaRect;

    private string? _hoveredId;
    private string? _pressedId;
    private bool _draggingProgress;

    public event EventHandler? PreviousClicked;
    public event EventHandler? PlayToggleClicked;
    public event EventHandler? NextClicked;
    public event EventHandler? StopClicked;
    public event EventHandler<int>? RateDeltaRequested;
    public event EventHandler<double>? SeekRatioRequested;

    public HeroTicketPlayer()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
        Dock = DockStyle.Top;
        Height = 186;
        BackColor = ComicTheme.Cream;
        TabStop = false;
    }

    public void UpdateState(
        PlaybackState state,
        string title,
        string sourceBadge,
        bool highlightBadge,
        int currentChars,
        int totalChars,
        int rate,
        bool canRead,
        bool canStop)
    {
        _state = state;
        _title = string.IsNullOrWhiteSpace(title) ? "未命名文章" : title;
        _sourceBadge = sourceBadge;
        _highlightBadge = highlightBadge;
        _currentChars = Math.Max(0, currentChars);
        _totalChars = Math.Max(0, totalChars);
        _rate = rate;
        _canRead = canRead;
        _canStop = canStop;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        ComicTheme.ConfigureGraphics(g);
        var s = ComicTheme.Scale(this);
        var compact = Width < (int)(860 * s);

        // 1. Cream halftone canvas background
        ComicTheme.FillCreamHalftone(g, ClientRectangle, s);

        // 2. Signature vermilion jagged ribbon behind the ticket
        var ribTop = 34f * s;
        var ribH = 116f * s;
        float w = Width;
        PointF[] ribbon =
        [
            new(0, ribTop + ribH * 0.30f),
            new(w * 0.38f, ribTop),
            new(w * 0.64f, ribTop + ribH * 0.26f),
            new(w, ribTop + ribH * 0.04f),
            new(w, ribTop + ribH * 0.70f),
            new(w * 0.70f, ribTop + ribH),
            new(w * 0.40f, ribTop + ribH * 0.72f),
            new(0, ribTop + ribH * 0.96f)
        ];
        using (var ribBrush = new SolidBrush(ComicTheme.Vermilion))
            g.FillPolygon(ribBrush, ribbon);

        // 3. Ticket card geometry
        var padX = 24f * s;
        var cardTop = Height < 168f * s ? 10f * s : 16f * s;
        var cardH = Math.Max(118f * s, Height - cardTop * 2f - 4f * s);
        var shadowOff = 6.5f * s;
        var cardRect = new RectangleF(padX, cardTop, Math.Max(200f, Width - padX * 2f - shadowOff), cardH);
        var radius = 10f * s;
        var borderW = 2.5f * s;

        // Hard ink shadow
        var shadowRect = new RectangleF(cardRect.X + shadowOff, cardRect.Y + shadowOff, cardRect.Width, cardRect.Height);
        using (var shadowPath = ComicTheme.CreateRoundedRect(shadowRect, radius))
        using (var shadowBrush = new SolidBrush(ComicTheme.Ink))
            g.FillPath(shadowBrush, shadowPath);

        // White ticket body + left stub clipped inside ticket rounded path
        var stubW = (compact ? 44f : 54f) * s;
        var stubColor = _state switch
        {
            PlaybackState.Speaking => ComicTheme.Vermilion,
            PlaybackState.Completed => ComicTheme.Mint,
            PlaybackState.Error => ComicTheme.VermilionDark,
            PlaybackState.Paused or PlaybackState.Stopped => ComicTheme.InkMuted,
            _ => ComicTheme.Vermilion
        };
        var stubText = _state switch
        {
            PlaybackState.Speaking => "正在读",
            PlaybackState.Paused => "已暂停",
            PlaybackState.Stopped => "已停止",
            PlaybackState.Completed => "已读完",
            PlaybackState.Error => "出错了",
            _ => "待播放"
        };

        using (var cardPath = ComicTheme.CreateRoundedRect(cardRect, radius))
        {
            using var whiteBrush = new SolidBrush(ComicTheme.White);
            g.FillPath(whiteBrush, cardPath);

            var state = g.Save();
            g.SetClip(cardPath, CombineMode.Intersect);
            using var stubBrush = new SolidBrush(stubColor);
            g.FillRectangle(stubBrush, cardRect.X, cardRect.Y, stubW, cardRect.Height);
            g.Restore(state);

            // Dashed divider between stub and ticket body
            using var dashPen = new Pen(ComicTheme.Ink, 2f * s) { DashPattern = [3f, 2.5f] };
            g.DrawLine(dashPen, cardRect.X + stubW, cardRect.Y + 6f * s, cardRect.X + stubW, cardRect.Bottom - 6f * s);

            // Outer ink border
            using var borderPen = new Pen(ComicTheme.Ink, borderW) { LineJoin = LineJoin.Round };
            g.DrawPath(borderPen, cardPath);
        }

        // Top & bottom circular notches at the stub junction
        var notchR = 8f * s;
        var notchX = cardRect.X + stubW;
        DrawNotch(g, notchX, cardRect.Top, notchR, isTop: true, borderW, s);
        DrawNotch(g, notchX, cardRect.Bottom, notchR, isTop: false, borderW, s);

        // Vertical stub text
        DrawVerticalStubText(g, new RectangleF(cardRect.X, cardRect.Y + 8f * s, stubW, cardRect.Height - 16f * s), stubText, s);

        // 4. Transport buttons
        var bodyLeft = cardRect.X + stubW + (compact ? 14f : 22f) * s;
        var centerY = cardRect.Y + cardRect.Height / 2f;
        var smallD = (compact ? 34f : 40f) * s;
        var playD = (compact ? 54f : 64f) * s;
        var btnGap = (compact ? 9f : 14f) * s;

        var bx = bodyLeft;
        _prevRect = new RectangleF(bx, centerY - smallD / 2f, smallD, smallD);
        bx += smallD + btnGap;
        _playRect = new RectangleF(bx, centerY - playD / 2f, playD, playD);
        bx += playD + btnGap;
        _nextRect = new RectangleF(bx, centerY - smallD / 2f, smallD, smallD);
        bx += smallD + btnGap;
        _stopRect = new RectangleF(bx, centerY - smallD / 2f, smallD, smallD);

        DrawRoundTransportButton(g, "prev", _prevRect, isPrimary: false, enabled: _canRead, s);
        DrawRoundTransportButton(g, "play", _playRect, isPrimary: true, enabled: _canRead, s);
        DrawRoundTransportButton(g, "next", _nextRect, isPrimary: false, enabled: _canRead, s);
        DrawRoundTransportButton(g, "stop", _stopRect, isPrimary: false, enabled: _canStop, s);

        // 5. Right Speed Column
        var speedW = (compact ? 86f : 104f) * s;
        var speedLeft = cardRect.Right - speedW - (compact ? 8f : 16f) * s;
        _speedAreaRect = new RectangleF(speedLeft, cardRect.Y + 12f * s, speedW, cardRect.Height - 24f * s);

        using (var vDash = new Pen(ComicTheme.DashedLine, 2f * s) { DashPattern = [3f, 2.5f] })
            g.DrawLine(vDash, speedLeft - 8f * s, cardRect.Y + 18f * s, speedLeft - 8f * s, cardRect.Bottom - 18f * s);

        DrawSpeedSection(g, _speedAreaRect, s);

        // 6. Center Title + Progress Bar + Barcode Metadata
        var infoLeft = _stopRect.Right + (compact ? 16f : 26f) * s;
        var infoRight = speedLeft - (compact ? 16f : 26f) * s;
        var infoW = Math.Max(80f, infoRight - infoLeft);

        using var titleFont = new Font("Microsoft YaHei UI", compact ? 12.5f : 15f, FontStyle.Bold);
        using var tagFont = new Font("Microsoft YaHei UI", 8.5f, FontStyle.Bold);
        using var monoFont = new Font("Consolas", compact ? 8.2f : 9f, FontStyle.Bold);

        var titleRowY = cardRect.Y + 20f * s;
        var badgeW = Math.Max(46f * s, g.MeasureString(_sourceBadge, tagFont).Width + 16f * s);
        var badgeH = 22f * s;
        var maxTitleW = Math.Max(40f, infoW - badgeW - 10f * s);
        var measuredTitle = g.MeasureString(_title, titleFont, (int)maxTitleW);
        var titleRect = new RectangleF(infoLeft, titleRowY, Math.Min(maxTitleW, measuredTitle.Width + 6f * s), 28f * s);

        using (var titleBrush = new SolidBrush(ComicTheme.Ink))
        using (var sf = new StringFormat { Alignment = StringAlignment.Near, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap })
            g.DrawString(_title, titleFont, titleBrush, titleRect, sf);

        var badgeRect = new RectangleF(titleRect.Right + 6f * s, titleRowY + (titleRect.Height - badgeH) / 2f, badgeW, badgeH);
        ComicTheme.DrawRoundedTag(g, badgeRect, _sourceBadge, tagFont, _highlightBadge ? ComicTheme.Vermilion : ComicTheme.Mint, ComicTheme.White, 6f * s);

        // Progress Bar
        var progY = titleRowY + 38f * s;
        var progH = 15f * s;
        _progressRect = new RectangleF(infoLeft, progY, infoW, progH);
        DrawComicProgressBar(g, _progressRect, s);

        // Dashed horizontal divider
        var divY = progY + progH + 14f * s;
        using (var hDash = new Pen(ComicTheme.DashedLine, 1.5f * s) { DashPattern = [3f, 2.5f] })
            g.DrawLine(hDash, infoLeft, divY, infoRight, divY);

        // Barcode + Metadata
        var metaY = divY + 8f * s;
        var metaH = 18f * s;
        var showBarcode = infoW > 235f * s;
        var textX = infoLeft;
        if (showBarcode)
        {
            var barW = Math.Min(108f * s, infoW * 0.34f);
            ComicTheme.DrawBarcode(g, new RectangleF(infoLeft, metaY + 1f * s, barW, metaH - 2f * s), ComicTheme.Ink);
            textX = infoLeft + barW + 12f * s;
        }

        var pct = _totalChars == 0 ? 0 : (int)Math.Round(_currentChars * 100.0 / _totalChars);
        var noCode = _currentChars.ToString("D4");
        var metaText = $"No.{noCode} · {pct}% · {_currentChars:N0} / {_totalChars:N0} 字";
        using (var monoBrush = new SolidBrush(ComicTheme.Ink))
        using (var sf = new StringFormat { Alignment = StringAlignment.Near, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap })
            g.DrawString(metaText, monoFont, monoBrush, new RectangleF(textX, metaY, Math.Max(20f, infoRight - textX), metaH), sf);
    }

    private void DrawNotch(Graphics g, float cx, float cy, float r, bool isTop, float borderW, float s)
    {
        var rect = new RectangleF(cx - r, cy - r, r * 2f, r * 2f);
        var state = g.Save();
        // Clip to inside card vertically so notch cuts cleanly into card edge
        var clipRect = isTop
            ? new RectangleF(rect.X - 4 * s, cy - 1, rect.Width + 8 * s, r + 6 * s)
            : new RectangleF(rect.X - 4 * s, cy - r - 4 * s, rect.Width + 8 * s, r + 5 * s);
        g.SetClip(clipRect);
        using (var fill = new SolidBrush(ComicTheme.Vermilion)) // matches ribbon behind top/bottom of card
        {
            // Slightly blend with cream if outside ribbon
            g.FillEllipse(fill, rect);
        }
        // Re-fill with cream halftone inside notch so it looks punched through the ticket
        var notchPath = new GraphicsPath();
        notchPath.AddEllipse(rect);
        g.SetClip(notchPath, CombineMode.Intersect);
        ComicTheme.FillCreamHalftone(g, Rectangle.Ceiling(rect), s);
        g.Restore(state);

        using var pen = new Pen(ComicTheme.Ink, borderW);
        g.DrawArc(pen, rect, isTop ? 0 : 180, 180);
        notchPath.Dispose();
    }

    private static void DrawVerticalStubText(Graphics g, RectangleF rect, string text, float s)
    {
        using var font = new Font("Microsoft YaHei UI", 11.5f, FontStyle.Bold);
        using var brush = new SolidBrush(ComicTheme.White);
        using var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        var chars = text.ToCharArray();
        var step = Math.Min(24f * s, rect.Height / Math.Max(1, chars.Length));
        var startY = rect.Y + (rect.Height - step * chars.Length) / 2f;
        for (var i = 0; i < chars.Length; i++)
        {
            g.DrawString(chars[i].ToString(), font, brush, new RectangleF(rect.X, startY + i * step, rect.Width, step), sf);
        }
    }

    private void DrawRoundTransportButton(Graphics g, string id, RectangleF rect, bool isPrimary, bool enabled, float s)
    {
        var hovered = enabled && _hoveredId == id;
        var pressed = enabled && _pressedId == id;
        var shadow = (isPrimary ? 4f : 3f) * s;
        var shift = pressed ? shadow * 0.6f : 0f;
        var drawRect = new RectangleF(rect.X + shift, rect.Y + shift, rect.Width, rect.Height);
        var shadowRect = new RectangleF(rect.X + shadow, rect.Y + shadow, rect.Width, rect.Height);

        if (enabled)
        {
            using var shBrush = new SolidBrush(ComicTheme.Ink);
            g.FillEllipse(shBrush, shadowRect);
        }

        var fill = !enabled
            ? ComicTheme.DisabledFill
            : isPrimary
                ? (hovered ? ComicTheme.VermilionHover : ComicTheme.Vermilion)
                : (hovered ? ComicTheme.PaperHover : ComicTheme.White);
        using (var fillBrush = new SolidBrush(fill))
            g.FillEllipse(fillBrush, drawRect);

        var borderW = (isPrimary ? 3f : 2.5f) * s;
        using (var pen = new Pen(enabled ? ComicTheme.Ink : ComicTheme.DashedLine, borderW))
            g.DrawEllipse(pen, drawRect);

        var iconColor = !enabled ? ComicTheme.DisabledText : isPrimary ? ComicTheme.White : ComicTheme.Ink;
        DrawTransportGlyph(g, id, drawRect, iconColor, s);
    }

    private void DrawTransportGlyph(Graphics g, string id, RectangleF r, Color color, float s)
    {
        var cx = r.X + r.Width / 2f;
        var cy = r.Y + r.Height / 2f;
        using var brush = new SolidBrush(color);

        if (id == "play")
        {
            if (_state == PlaybackState.Speaking)
            {
                var barW = 5.5f * s;
                var barH = 20f * s;
                var gap = 5f * s;
                g.FillRectangle(brush, cx - gap / 2f - barW, cy - barH / 2f, barW, barH);
                g.FillRectangle(brush, cx + gap / 2f, cy - barH / 2f, barW, barH);
            }
            else
            {
                var triW = 16f * s;
                var triH = 20f * s;
                PointF[] tri =
                [
                    new(cx - triW * 0.38f, cy - triH / 2f),
                    new(cx + triW * 0.62f, cy),
                    new(cx - triW * 0.38f, cy + triH / 2f)
                ];
                g.FillPolygon(brush, tri);
            }
        }
        else if (id == "prev")
        {
            var h = 11f * s;
            var barW = 2.4f * s;
            var tw = 6.2f * s;
            g.FillRectangle(brush, cx - tw - barW + 1f * s, cy - h / 2f, barW, h);
            g.FillPolygon(brush, [new PointF(cx + 1f * s, cy - h / 2f), new PointF(cx - tw + 1f * s, cy), new PointF(cx + 1f * s, cy + h / 2f)]);
            g.FillPolygon(brush, [new PointF(cx + tw + 1f * s, cy - h / 2f), new PointF(cx + 1f * s, cy), new PointF(cx + tw + 1f * s, cy + h / 2f)]);
        }
        else if (id == "next")
        {
            var h = 11f * s;
            var barW = 2.4f * s;
            var tw = 6.2f * s;
            g.FillPolygon(brush, [new PointF(cx - tw - 1f * s, cy - h / 2f), new PointF(cx - 1f * s, cy), new PointF(cx - tw - 1f * s, cy + h / 2f)]);
            g.FillPolygon(brush, [new PointF(cx - 1f * s, cy - h / 2f), new PointF(cx + tw - 1f * s, cy), new PointF(cx - 1f * s, cy + h / 2f)]);
            g.FillRectangle(brush, cx + tw - 1f * s, cy - h / 2f, barW, h);
        }
        else if (id == "stop")
        {
            var sz = 10.5f * s;
            g.FillRectangle(brush, cx - sz / 2f, cy - sz / 2f, sz, sz);
        }
    }

    private void DrawComicProgressBar(Graphics g, RectangleF rect, float s)
    {
        var r = rect.Height / 2f;
        var ratio = _totalChars == 0 ? 0.0 : Math.Clamp((double)_currentChars / _totalChars, 0.0, 1.0);
        using var trackPath = ComicTheme.CreateRoundedRect(rect, r);
        using (var bg = new SolidBrush(ComicTheme.White))
            g.FillPath(bg, trackPath);

        if (ratio > 0.001)
        {
            var state = g.Save();
            g.SetClip(trackPath, CombineMode.Intersect);
            var fillW = Math.Max(6f * s, (float)(rect.Width * ratio));
            using var fillBrush = new SolidBrush(ComicTheme.Vermilion);
            g.FillRectangle(fillBrush, rect.X, rect.Y, fillW, rect.Height);
            if (fillW < rect.Width - 2f * s)
            {
                using var edgePen = new Pen(ComicTheme.Ink, 2.2f * s);
                g.DrawLine(edgePen, rect.X + fillW, rect.Y, rect.X + fillW, rect.Bottom);
            }
            g.Restore(state);
        }

        using var borderPen = new Pen(ComicTheme.Ink, 2.3f * s);
        g.DrawPath(borderPen, trackPath);
    }

    private void DrawSpeedSection(Graphics g, RectangleF area, float s)
    {
        using var labelFont = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold);
        using var numFont = new Font("Microsoft YaHei UI", 24f, FontStyle.Bold | FontStyle.Italic);
        using var btnFont = new Font("Microsoft YaHei UI", 10.5f, FontStyle.Bold);
        using var inkBrush = new SolidBrush(ComicTheme.Ink);
        using var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };

        var lblRect = new RectangleF(area.X, area.Y, area.Width, 20f * s);
        g.DrawString("语速", labelFont, inkBrush, lblRect, sf);

        var speedText = _rate > 0 ? $"+{_rate}" : _rate.ToString();
        var numRect = new RectangleF(area.X - 4f * s, area.Y + 20f * s, area.Width + 8f * s, 46f * s);
        g.DrawString(speedText, numFont, inkBrush, numRect, sf);

        var pillW = 34f * s;
        var pillH = 24f * s;
        var gap = 8f * s;
        var totalBtnW = pillW * 2f + gap;
        var startX = area.X + (area.Width - totalBtnW) / 2f;
        var btnY = area.Bottom - pillH - 2f * s;

        _rateMinusRect = new RectangleF(startX, btnY, pillW, pillH);
        _ratePlusRect = new RectangleF(startX + pillW + gap, btnY, pillW, pillH);

        DrawRatePill(g, "rate-", _rateMinusRect, "−", btnFont, _rate > -10, s);
        DrawRatePill(g, "rate+", _ratePlusRect, "+", btnFont, _rate < 10, s);
    }

    private void DrawRatePill(Graphics g, string id, RectangleF rect, string label, Font font, bool enabled, float s)
    {
        var hovered = enabled && _hoveredId == id;
        var pressed = enabled && _pressedId == id;
        var drawRect = pressed ? new RectangleF(rect.X + 1f * s, rect.Y + 1f * s, rect.Width, rect.Height) : rect;
        var back = !enabled ? ComicTheme.DashedLine : hovered ? ComicTheme.Vermilion : ComicTheme.Ink;
        ComicTheme.DrawPillBadge(g, drawRect, label, font, back, ComicTheme.White);
    }

    private string? HitTest(Point p)
    {
        if (_playRect.Contains(p)) return "play";
        if (_prevRect.Contains(p)) return "prev";
        if (_nextRect.Contains(p)) return "next";
        if (_stopRect.Contains(p)) return "stop";
        if (_rateMinusRect.Contains(p)) return "rate-";
        if (_ratePlusRect.Contains(p)) return "rate+";
        if (_progressRect.Contains(p)) return "progress";
        return null;
    }

    private bool IsInteractive(string? id) => id switch
    {
        "play" or "prev" or "next" => _canRead,
        "stop" => _canStop,
        "rate-" => _rate > -10,
        "rate+" => _rate < 10,
        "progress" => _totalChars > 0,
        _ => false
    };

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_draggingProgress && _totalChars > 0)
        {
            EmitSeek(e.X);
            return;
        }
        var hit = HitTest(e.Location);
        if (hit != _hoveredId)
        {
            _hoveredId = hit;
            Cursor = IsInteractive(hit) ? Cursors.Hand : Cursors.Default;
            Invalidate();
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (!_draggingProgress && (_hoveredId is not null || _pressedId is not null))
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
        var hit = HitTest(e.Location);
        if (!IsInteractive(hit)) return;
        if (hit == "progress")
        {
            _draggingProgress = true;
            Capture = true;
            EmitSeek(e.X);
            return;
        }
        _pressedId = hit;
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Left) return;
        if (_draggingProgress)
        {
            _draggingProgress = false;
            Capture = false;
            return;
        }
        var hit = HitTest(e.Location);
        var fire = hit is not null && hit == _pressedId && IsInteractive(hit) ? hit : null;
        _pressedId = null;
        Invalidate();
        switch (fire)
        {
            case "play": PlayToggleClicked?.Invoke(this, EventArgs.Empty); break;
            case "prev": PreviousClicked?.Invoke(this, EventArgs.Empty); break;
            case "next": NextClicked?.Invoke(this, EventArgs.Empty); break;
            case "stop": StopClicked?.Invoke(this, EventArgs.Empty); break;
            case "rate-": RateDeltaRequested?.Invoke(this, -1); break;
            case "rate+": RateDeltaRequested?.Invoke(this, 1); break;
        }
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if (_speedAreaRect.Contains(e.Location))
        {
            RateDeltaRequested?.Invoke(this, e.Delta > 0 ? 1 : -1);
        }
    }

    private void EmitSeek(int x)
    {
        if (_progressRect.Width <= 1f) return;
        var ratio = Math.Clamp((x - _progressRect.X) / _progressRect.Width, 0.0, 1.0);
        SeekRatioRequested?.Invoke(this, ratio);
    }
}
