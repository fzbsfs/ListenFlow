using System.Drawing.Drawing2D;
using ListenFlow.Core;

namespace ListenFlow.UI;

internal sealed class ComicMascotControl : Control
{
    public enum MascotMood { Playing, Paused, Happy }

    private sealed class Particle
    {
        public string Symbol = "♪";
        public float X;
        public float Y;
        public float Vx;
        public float Vy;
        public float Age;
        public float MaxAge;
        public Color Color;
    }

    private static readonly (string Main, string Highlight, string Tail, string Sub)[] PatLines =
    [
        ("嘿嘿，被摸头会读得", "更卖力", "哦～", "继续陪你一起看！"),
        ("呀，别把", "呆毛", "压扁啦！", "耳机还稳稳戴着呢"),
        ("摸头收到！能量 ", "+100%", "", "今天也辛苦啦～"),
        ("嗯嗯，我在认真帮你", "盯着进度", "呢", "放心把眼睛歇一会儿")
    ];

    private static readonly (string Main, string Highlight, string Tail, string Sub)[] BodyLines =
    [
        ("需要我调整", "语速", "吗？", "在这里滚动滚轮就能调哦"),
        ("双击我可以快速", "播放 / 暂停", "！", "右键还有快捷菜单"),
        ("点我手里的书，能", "定位当前句", "", "不怕看丢位置～")
    ];

    private readonly Bitmap? _playingSprite;
    private readonly Bitmap? _pausedSprite;
    private readonly Bitmap? _happySprite;
    private readonly System.Windows.Forms.Timer _animTimer = new() { Interval = 33 };
    private readonly List<Particle> _particles = [];
    private readonly Random _rng = new();

    private MascotMood _baseMood = MascotMood.Paused;
    private MascotMood _currentMood = MascotMood.Paused;
    private long _tempHappyUntil;
    private long _bounceStart;
    private long _bubblePopStart;
    private long _lastAmbientNote;
    private float _elapsedSec;

    private string _bubPrefix = "准备好";
    private string _bubHighlight = "开读";
    private string _bubSuffix = "啦～";
    private string _bubSub = "点耳机播停 · 摸头彩蛋";

    private int _interactCount;
    private int _patStreak;
    private bool _hovered;
    private string? _hoverZone;

    private RectangleF _cardRect;
    private RectangleF _spriteRect;
    private RectangleF _bubbleRect;
    private RectangleF _headZone;
    private RectangleF _earLeftZone;
    private RectangleF _earRightZone;
    private RectangleF _bookZone;
    private RectangleF _bodyZone;

    public event EventHandler? PlayToggleRequested;
    public event EventHandler? LocateSentenceRequested;
    public event EventHandler<int>? RateDeltaRequested;
    public event EventHandler? FloatingControlsRequested;
    public event EventHandler? HideMascotRequested;
    public event EventHandler<int>? InteractCountChanged;

    public ComicMascotControl()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
        BackColor = ComicTheme.Cream;
        Height = 228;
        TabStop = false;

        _playingSprite = LoadEmbeddedBitmap("ListenFlow.Mascot.playing.png");
        _pausedSprite = LoadEmbeddedBitmap("ListenFlow.Mascot.paused.png");
        _happySprite = LoadEmbeddedBitmap("ListenFlow.Mascot.happy.png");

        _animTimer.Tick += OnAnimTick;
        _animTimer.Start();
    }

    public int InteractCount => _interactCount;

    public void SyncPlaybackState(PlaybackState state, int percent, int rate, bool fromUserToggle = false)
    {
        var prevBase = _baseMood;
        _baseMood = state == PlaybackState.Speaking ? MascotMood.Playing : MascotMood.Paused;

        if (state == PlaybackState.Completed)
        {
            SetTempHappy(3800);
            SetBubble("这篇", "读完 100%", " 啦！", "可以在队列继续听下一篇");
            SpawnParticles(["★", "✦", "♪", "♥", "★"], 6, 0.54f, 0.30f);
            return;
        }

        if (state == PlaybackState.Error)
        {
            _tempHappyUntil = 0;
            _currentMood = MascotMood.Paused;
            SetBubble("语音好像", "卡住", "了…", "去设置里检查下本地语音吧");
            Invalidate();
            return;
        }

        if (Environment.TickCount64 >= _tempHappyUntil)
            _currentMood = _baseMood;

        if (prevBase != _baseMood)
        {
            TriggerBounce();
            if (_baseMood == MascotMood.Playing)
            {
                SetBubble(fromUserToggle ? "耳机戴好，" : "正在读 ", fromUserToggle ? "继续读！" : $"{percent}%", "", "点我书本可闪亮定位当前句");
                SpawnParticles(["♪", "♫", "✦"], 3, 0.56f, 0.34f);
            }
            else if (state == PlaybackState.Paused)
            {
                SetBubble("先歇一会儿", "～", "", "随时点我耳机继续听");
                SpawnParticles(["z", "Z"], 2, 0.62f, 0.28f);
            }
            else if (state == PlaybackState.Stopped)
            {
                SetBubble("已停止", "休息中", "", "想听的时候再叫我～");
            }
        }
        else if (_baseMood == MascotMood.Playing && Environment.TickCount64 >= _tempHappyUntil)
        {
            SetBubbleQuiet("读到 ", $"{percent}%", " 啦～", $"语速 {(rate >= 0 ? "+" : "")}{rate} · 点耳机播停");
        }
        Invalidate();
    }

    public void NotifySpeedChanged(int rate)
    {
        if (rate >= 4)
        {
            SetTempHappy(1800);
            SetBubble("语速 ", $"+{rate}", " 冲刺模式！", "耳朵跟上节奏啦～");
            SpawnParticles(["⚡", "✦", "♪"], 4, 0.55f, 0.32f);
        }
        else if (rate <= -1)
        {
            SetBubble("调慢到 ", $"{rate}", " 啦", "慢慢听，不着急～");
        }
        else
        {
            var fmt = rate > 0 ? $"+{rate}" : rate.ToString();
            SetBubble("语速调到 ", fmt, "", "这个节奏刚刚好");
        }
    }

    public void NotifyArticleSwitched(string title)
    {
        var shortTitle = title.Length > 8 ? title[..8] + "…" : title;
        SetTempHappy(1600);
        SetBubble("切到《", shortTitle, "》", "马上陪你一起听～");
        SpawnParticles(["♪", "✦"], 3, 0.56f, 0.34f);
    }

    public void NotifySeek(int percent)
    {
        TriggerBounce();
        SetBubble("已跳到 ", $"{percent}%", "", "马上从这里接着读～");
        SpawnParticles(["♪", "✦"], 2, 0.56f, 0.36f);
    }

    public void DoHeadpat()
    {
        BumpInteract();
        _patStreak++;
        SetTempHappy(2400);
        if (_patStreak >= 3)
        {
            SetBubble("连摸 ", "3", " 次啦！好开心～", "解锁今日专属陪读 buff ✨");
            SpawnParticles(["♥", "★", "✦", "♥", "★"], 6, 0.54f, 0.22f);
            _patStreak = 0;
        }
        else
        {
            var line = PatLines[(_interactCount - 1) % PatLines.Length];
            SetBubble(line.Main, line.Highlight, line.Tail, line.Sub);
            SpawnParticles(["♥", "✦", "★"], 4, 0.54f, 0.24f);
        }
    }

    public void DoLocateFeedback(int percent)
    {
        BumpInteract();
        TriggerBounce();
        SetBubble("在这里！", $"{percent}%", "", "已帮你闪亮标出当前句");
        SpawnParticles(["📖", "✦"], 3, 0.54f, 0.58f);
    }

    private void DoBodyPoke()
    {
        BumpInteract();
        SetTempHappy(1800);
        var line = BodyLines[(_interactCount - 1) % BodyLines.Length];
        SetBubble(line.Main, line.Highlight, line.Tail, line.Sub);
        SpawnParticles(["✦", "♪"], 3, 0.56f, 0.44f);
    }

    private void BumpInteract()
    {
        _interactCount++;
        InteractCountChanged?.Invoke(this, _interactCount);
    }

    private void SetTempHappy(long durationMs)
    {
        _tempHappyUntil = Environment.TickCount64 + durationMs;
        _currentMood = MascotMood.Happy;
        TriggerBounce();
        Invalidate();
    }

    private void TriggerBounce()
    {
        _bounceStart = Environment.TickCount64;
    }

    private void SetBubble(string prefix, string highlight, string suffix, string sub)
    {
        _bubPrefix = prefix;
        _bubHighlight = highlight;
        _bubSuffix = suffix;
        _bubSub = sub;
        _bubblePopStart = Environment.TickCount64;
        Invalidate();
    }

    private void SetBubbleQuiet(string prefix, string highlight, string suffix, string sub)
    {
        if (_bubPrefix == prefix && _bubHighlight == highlight && _bubSuffix == suffix && _bubSub == sub) return;
        _bubPrefix = prefix;
        _bubHighlight = highlight;
        _bubSuffix = suffix;
        _bubSub = sub;
        Invalidate();
    }

    private void SpawnParticles(string[] symbols, int count, float normX, float normY)
    {
        Color[] colors = [ComicTheme.Vermilion, ComicTheme.Yellow, ComicTheme.Mint, ComicTheme.VermilionHover];
        for (var i = 0; i < count; i++)
        {
            _particles.Add(new Particle
            {
                Symbol = symbols[i % symbols.Length],
                X = Width * normX + (float)(_rng.NextDouble() - 0.5) * Width * 0.34f,
                Y = Height * normY + (float)(_rng.NextDouble() - 0.5) * Height * 0.14f,
                Vx = (float)(_rng.NextDouble() - 0.5) * 24f,
                Vy = -(26f + (float)_rng.NextDouble() * 20f),
                Age = -i * 0.06f,
                MaxAge = 1.15f + (float)_rng.NextDouble() * 0.25f,
                Color = colors[i % colors.Length]
            });
        }
    }

    private void OnAnimTick(object? sender, EventArgs e)
    {
        if (!Visible || Width <= 0 || Height <= 0) return;
        if (FindForm() is { WindowState: FormWindowState.Minimized }) return;

        var now = Environment.TickCount64;
        _elapsedSec += 0.033f;

        if (_tempHappyUntil > 0 && now >= _tempHappyUntil)
        {
            _tempHappyUntil = 0;
            _currentMood = _baseMood;
        }

        if (_baseMood == MascotMood.Playing && _currentMood == MascotMood.Playing && now - _lastAmbientNote > 2600)
        {
            _lastAmbientNote = now;
            SpawnParticles(["♪", "♫"], 1, 0.64f, 0.30f);
        }

        for (var i = _particles.Count - 1; i >= 0; i--)
        {
            var p = _particles[i];
            p.Age += 0.033f;
            if (p.Age > p.MaxAge)
            {
                _particles.RemoveAt(i);
                continue;
            }
            if (p.Age > 0)
            {
                p.X += p.Vx * 0.033f;
                p.Y += p.Vy * 0.033f;
            }
        }

        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        ComicTheme.ConfigureGraphics(g);
        var s = ComicTheme.Scale(this);
        ComicTheme.FillCreamHalftone(g, ClientRectangle, s);

        var shadowOff = 5f * s;
        var borderW = 2.5f * s;
        var radius = 10f * s;

        // Bottom vermilion G2 card (~57% of control height, leaving open upper-left zone for bubble + pop-out head)
        var cardH = Math.Min(132f * s, Height * 0.57f);
        var cardTop = Height - cardH - shadowOff - 2f * s;
        _cardRect = new RectangleF(2f * s, cardTop, Math.Max(40f, Width - shadowOff - 4f * s), cardH);

        var cardColor = _currentMood switch
        {
            MascotMood.Paused => ComicTheme.PausedCard,
            MascotMood.Happy => ComicTheme.VermilionHappy,
            _ => ComicTheme.Vermilion
        };

        // 1. Hard shadow + Vermilion card base + white halftone dots
        ComicTheme.DrawHardShadowCard(g, _cardRect, radius, borderW, shadowOff, cardColor, ComicTheme.Ink, ComicTheme.Ink);
        using (var cardPath = ComicTheme.CreateRoundedRect(_cardRect, radius))
            ComicTheme.FillWhiteHalftoneOverlay(g, cardPath, _cardRect, s);

        // 2. Compute mascot animation transform
        ComputeAnimTransform(s, out var dy, out var rotDeg, out var scaleX, out var scaleY);

        var sprite = _currentMood switch
        {
            MascotMood.Paused => _pausedSprite ?? _playingSprite,
            MascotMood.Happy => _happySprite ?? _playingSprite,
            _ => _playingSprite
        };

        if (sprite is not null)
        {
            // Scale cleanly so ahoge never clips top edge and shift gently right so top-left speech bubble stays off her face
            var maxH = Height - 8f * s;
            var drawW = Math.Min(_cardRect.Width * 0.94f, maxH * sprite.Width / sprite.Height);
            var drawH = drawW * sprite.Height / sprite.Width;
            var drawX = _cardRect.X + (_cardRect.Width - drawW) * 0.78f;
            var drawY = _cardRect.Bottom - drawH + 10f * s;
            _spriteRect = new RectangleF(drawX, drawY, drawW, drawH);

            // Update interactive hotspots relative to _spriteRect
            _headZone = new RectangleF(_spriteRect.X + drawW * 0.25f, _spriteRect.Y + drawH * 0.04f, drawW * 0.50f, drawH * 0.34f);
            _earLeftZone = new RectangleF(_spriteRect.X + drawW * 0.16f, _spriteRect.Y + drawH * 0.24f, drawW * 0.18f, drawH * 0.20f);
            _earRightZone = new RectangleF(_spriteRect.X + drawW * 0.64f, _spriteRect.Y + drawH * 0.22f, drawW * 0.20f, drawH * 0.20f);
            _bookZone = new RectangleF(_spriteRect.X + drawW * 0.44f, _spriteRect.Y + drawH * 0.58f, drawW * 0.36f, drawH * 0.34f);
            _bodyZone = new RectangleF(_spriteRect.X + drawW * 0.16f, _spriteRect.Y + drawH * 0.38f, drawW * 0.68f, drawH * 0.58f);

            // Dual-clip pop-out: inside card rounded interior UNION open area above card top!
            var innerCard = RectangleF.Inflate(_cardRect, -borderW * 0.5f, -borderW * 0.5f);
            using var innerPath = ComicTheme.CreateRoundedRect(innerCard, Math.Max(2f, radius - borderW * 0.5f));
            using var popRegion = new Region(innerPath);
            popRegion.Union(new RectangleF(0, 0, Width, _cardRect.Top + 6f * s));

            var save = g.Save();
            g.SetClip(popRegion, CombineMode.Intersect);

            var pivotX = _spriteRect.X + _spriteRect.Width / 2f;
            var pivotY = _cardRect.Bottom;
            g.TranslateTransform(pivotX, pivotY + dy);
            g.RotateTransform(rotDeg);
            g.ScaleTransform(scaleX, scaleY);
            g.TranslateTransform(-pivotX, -pivotY);
            g.DrawImage(sprite, _spriteRect);
            g.Restore(save);

            // Redraw bottom/side card ink border cleanly on top of clipped bottom sleeves
            using var cardPath = ComicTheme.CreateRoundedRect(_cardRect, radius);
            var borderSave = g.Save();
            g.SetClip(new RectangleF(0, _cardRect.Top + 14f * s, Width, Height));
            using var borderPen = new Pen(ComicTheme.Ink, borderW) { LineJoin = LineJoin.Round };
            g.DrawPath(borderPen, cardPath);
            g.Restore(borderSave);
        }

        // 3. Comic Speech Bubble (top-left, overlapping cleanly above the card!)
        DrawSpeechBubble(g, s);

        // 4. Floating Particles
        DrawParticles(g, s);

        // 5. Hover Zone Hint Pill
        if (_hovered)
            DrawZoneHint(g, s);
    }

    private void ComputeAnimTransform(float s, out float dy, out float rotDeg, out float scaleX, out float scaleY)
    {
        dy = 0f;
        rotDeg = 0f;
        scaleX = 1f;
        scaleY = 1f;

        if (_currentMood == MascotMood.Paused)
        {
            var phase = (float)Math.Sin(_elapsedSec * 1.65f);
            dy = phase * 1.2f * s;
            scaleY = 1f - phase * 0.008f;
        }
        else
        {
            var phase = (float)Math.Sin(_elapsedSec * 3.0f);
            dy = -Math.Abs(phase) * 2.6f * s;
            rotDeg = phase * 0.85f;
            scaleY = 1f + Math.Abs(phase) * 0.008f;
        }

        // Elastic bounce overlay (520ms)
        var elapsed = Environment.TickCount64 - _bounceStart;
        if (elapsed is >= 0 and < 520)
        {
            var t = elapsed / 520f;
            if (t < 0.25f)
            {
                var k = t / 0.25f;
                scaleX *= 1f + 0.06f * k;
                scaleY *= 1f - 0.07f * k;
                dy += 3.5f * s * k;
            }
            else if (t < 0.60f)
            {
                var k = (t - 0.25f) / 0.35f;
                scaleX *= 1.06f - 0.10f * k;
                scaleY *= 0.93f + 0.12f * k;
                dy += (3.5f - 11.5f * k) * s;
            }
            else
            {
                var k = (t - 0.60f) / 0.40f;
                scaleX *= 0.96f + 0.04f * k;
                scaleY *= 1.05f - 0.05f * k;
                dy += (-8.0f * (1f - k)) * s;
            }
        }
    }

    private void DrawSpeechBubble(Graphics g, float s)
    {
        var compact = Height < 168f * s;
        using var mainFont = new Font("Microsoft YaHei UI", compact ? 8.0f : 8.5f, FontStyle.Bold);
        using var subFont = new Font("Microsoft YaHei UI", 7.2f, FontStyle.Bold);

        var maxW = Math.Min(178f * s, Width * 0.74f);
        var padX = (compact ? 7f : 8.5f) * s;
        var padY = (compact ? 4.5f : 5.5f) * s;
        var fullMain = _bubPrefix + _bubHighlight + _bubSuffix;
        var mainSize = g.MeasureString(fullMain, mainFont, (int)(maxW - padX * 2f));
        var hasSub = !compact && !string.IsNullOrWhiteSpace(_bubSub);
        var subSize = hasSub ? g.MeasureString(_bubSub, subFont, (int)(maxW - padX * 2f)) : SizeF.Empty;

        var bubW = Math.Clamp(Math.Max(mainSize.Width, subSize.Width) + padX * 2f + 4f * s, 92f * s, maxW);
        var bubH = mainSize.Height + (hasSub ? subSize.Height + 1.5f * s : 0f) + padY * 2f;
        var bubY = Math.Max(2f * s, _cardRect.Top - bubH - (compact ? 9f : 11f) * s);
        _bubbleRect = new RectangleF(1f * s, bubY, bubW, bubH);

        // Pop animation
        var popScale = 1f;
        var popElapsed = Environment.TickCount64 - _bubblePopStart;
        if (popElapsed is >= 0 and < 240)
        {
            var t = popElapsed / 240f;
            popScale = 0.86f + 0.18f * (float)Math.Sin(t * Math.PI * 0.5) - (t > 0.6f ? 0.04f * ((t - 0.6f) / 0.4f) : 0f);
        }

        var save = g.Save();
        if (Math.Abs(popScale - 1f) > 0.005f)
        {
            var cx = _bubbleRect.X + _bubbleRect.Width / 2f;
            var cy = _bubbleRect.Bottom;
            g.TranslateTransform(cx, cy);
            g.ScaleTransform(popScale, popScale);
            g.TranslateTransform(-cx, -cy);
        }

        var r = 11f * s;
        var shOff = 3f * s;
        var borderW = 2.2f * s;
        ComicTheme.DrawHardShadowCard(g, _bubbleRect, r, borderW, shOff, ComicTheme.White, ComicTheme.Ink, ComicTheme.Ink);

        // Speech tail pointing down-right toward mascot
        var tailX = Math.Min(_bubbleRect.Right - 18f * s, _bubbleRect.X + _bubbleRect.Width * 0.72f);
        var tailY = _bubbleRect.Bottom - 1f * s;
        PointF[] tail =
        [
            new(tailX - 6f * s, tailY),
            new(tailX + 5f * s, tailY),
            new(tailX + 3f * s, tailY + 8.5f * s)
        ];
        using (var whiteBrush = new SolidBrush(ComicTheme.White))
            g.FillPolygon(whiteBrush, tail);
        using (var pen = new Pen(ComicTheme.Ink, borderW) { LineJoin = LineJoin.Round })
        {
            g.DrawLine(pen, tail[0], tail[2]);
            g.DrawLine(pen, tail[2], tail[1]);
        }

        // Render main text (single-line colored segments if it fits, otherwise wrapped cleanly)
        var textX = _bubbleRect.X + padX;
        var textY = _bubbleRect.Y + padY;
        var innerW = _bubbleRect.Width - padX * 2f;
        var oneLineW = g.MeasureString(fullMain, mainFont, PointF.Empty, StringFormat.GenericTypographic).Width;
        if (oneLineW <= innerW + 2f * s)
        {
            var curX = textX;
            curX = DrawSegment(g, _bubPrefix, mainFont, ComicTheme.Ink, curX, textY);
            curX = DrawSegment(g, _bubHighlight, mainFont, ComicTheme.Vermilion, curX, textY);
            DrawSegment(g, _bubSuffix, mainFont, ComicTheme.Ink, curX, textY);
        }
        else
        {
            using var inkBrush = new SolidBrush(ComicTheme.Ink);
            g.DrawString(fullMain, mainFont, inkBrush, new RectangleF(textX, textY, innerW, mainSize.Height + 2f * s));
        }

        if (hasSub)
        {
            using var subBrush = new SolidBrush(ComicTheme.TextMuted);
            g.DrawString(_bubSub, subFont, subBrush, new RectangleF(textX, textY + mainSize.Height + 1f * s, innerW, subSize.Height + 2f * s));
        }

        g.Restore(save);
    }

    private static float DrawSegment(Graphics g, string text, Font font, Color color, float x, float y)
    {
        if (string.IsNullOrEmpty(text)) return x;
        using var brush = new SolidBrush(color);
        g.DrawString(text, font, brush, x, y, StringFormat.GenericTypographic);
        return x + g.MeasureString(text, font, PointF.Empty, StringFormat.GenericTypographic).Width;
    }

    private void DrawParticles(Graphics g, float s)
    {
        if (_particles.Count == 0) return;
        using var font = new Font("Segoe UI Symbol", 11.5f, FontStyle.Bold);
        foreach (var p in _particles)
        {
            if (p.Age <= 0) continue;
            var t = Math.Clamp(p.Age / p.MaxAge, 0f, 1f);
            var alpha = t < 0.2f ? t / 0.2f : 1f - (t - 0.2f) / 0.8f;
            var a = (int)Math.Clamp(alpha * 255f, 0f, 255f);
            if (a <= 4) continue;
            using var brush = new SolidBrush(Color.FromArgb(a, p.Color));
            g.DrawString(p.Symbol, font, brush, p.X, p.Y);
        }
    }

    private void DrawZoneHint(Graphics g, float s)
    {
        var hint = _hoverZone switch
        {
            "head" => "✨ 点击摸摸头（触发开心互动）",
            "ear" => "🎧 点击耳机：快速播放 / 暂停",
            "book" => "📖 点击书本：闪亮定位当前句",
            "bubble" => "💬 点击切换看板娘小贴士",
            _ => "点耳机播停 · 摸头彩蛋 · 滚轮调速"
        };
        using var font = new Font("Microsoft YaHei UI", 7.6f, FontStyle.Bold);
        var w = Math.Min(_cardRect.Width - 10f * s, g.MeasureString(hint, font).Width + 16f * s);
        var h = 20f * s;
        var rect = new RectangleF(_cardRect.X + (_cardRect.Width - w) / 2f, _cardRect.Bottom - h - 6f * s, w, h);
        ComicTheme.DrawPillBadge(g, rect, hint, font, Color.FromArgb(224, 22, 22, 22), ComicTheme.White);
    }

    private string? HitZone(Point p)
    {
        if (_bubbleRect.Contains(p)) return "bubble";
        if (_earLeftZone.Contains(p) || _earRightZone.Contains(p)) return "ear";
        if (_headZone.Contains(p)) return "head";
        if (_bookZone.Contains(p)) return "book";
        if (_bodyZone.Contains(p) || _cardRect.Contains(p)) return "body";
        return null;
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        _hovered = true;
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var zone = HitZone(e.Location);
        if (zone != _hoverZone)
        {
            _hoverZone = zone;
            Cursor = zone is not null ? Cursors.Hand : Cursors.Default;
            Invalidate();
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hovered = false;
        _hoverZone = null;
        Cursor = Cursors.Default;
        Invalidate();
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (e.Button == MouseButtons.Right)
        {
            ShowMascotContextMenu(e.Location);
            return;
        }
        if (e.Button != MouseButtons.Left) return;
        var zone = HitZone(e.Location);
        switch (zone)
        {
            case "head":
                DoHeadpat();
                break;
            case "ear":
                BumpInteract();
                PlayToggleRequested?.Invoke(this, EventArgs.Empty);
                break;
            case "book":
                LocateSentenceRequested?.Invoke(this, EventArgs.Empty);
                break;
            case "bubble":
                BumpInteract();
                var line = BodyLines[_interactCount % BodyLines.Length];
                SetBubble(line.Main, line.Highlight, line.Tail, line.Sub);
                break;
            case "body":
                DoBodyPoke();
                break;
        }
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        if (e.Button == MouseButtons.Left && HitZone(e.Location) is not null)
        {
            PlayToggleRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        RateDeltaRequested?.Invoke(this, e.Delta > 0 ? 1 : -1);
    }

    private void ShowMascotContextMenu(Point pt)
    {
        var menu = new ContextMenuStrip();
        ComicTheme.StyleContextMenu(menu);
        menu.Items.Add(_baseMood == MascotMood.Playing ? "❚❚ 暂停朗读" : "▶ 开始 / 继续朗读", null, (_, _) => PlayToggleRequested?.Invoke(this, EventArgs.Empty));
        menu.Items.Add("✨ 摸摸头互动", null, (_, _) => DoHeadpat());
        menu.Items.Add("📖 定位并闪亮当前句", null, (_, _) => LocateSentenceRequested?.Invoke(this, EventArgs.Empty));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("🎛 打开悬浮控制条", null, (_, _) => FloatingControlsRequested?.Invoke(this, EventArgs.Empty));
        menu.Items.Add("🙈 暂时收起看板娘", null, (_, _) => HideMascotRequested?.Invoke(this, EventArgs.Empty));
        menu.Closed += (_, _) => menu.Dispose();
        menu.Show(this, pt);
    }

    private static Bitmap? LoadEmbeddedBitmap(string resourceName)
    {
        try
        {
            using var stream = typeof(ComicMascotControl).Assembly.GetManifestResourceStream(resourceName);
            if (stream is null) return null;
            using var temp = new Bitmap(stream);
            return new Bitmap(temp);
        }
        catch
        {
            return null;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _animTimer.Stop();
            _animTimer.Dispose();
            _playingSprite?.Dispose();
            _pausedSprite?.Dispose();
            _happySprite?.Dispose();
        }
        base.Dispose(disposing);
    }
}
