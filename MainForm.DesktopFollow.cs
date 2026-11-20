using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using ListenFlow.AIReader;
using ListenFlow.Core;
using ListenFlow.Diagnostics;

namespace ListenFlow;

internal sealed partial class MainForm
{
    private const string DesktopFollowPrefix = "desktop-follow:";
    private bool IsDesktopFollowDocument => _documentId.StartsWith(DesktopFollowPrefix, StringComparison.Ordinal);
    private DesktopFollowSource? _desktopFollow;
    private System.Windows.Forms.Timer? _desktopFollowTimer;
    private FollowOverlay? _desktopFollowOverlay;
    private TextRange _desktopFollowSentence;
    private long _desktopFollowGeneration;
    private DesktopFollowSource? _desktopFollowBusySource;
    private DesktopFollowSource? _desktopFollowSeekingSource;
    private long _desktopFollowLastPaint;
    private bool _desktopFollowCapturing;
    private bool _desktopFollowPainted;
    private bool _desktopFollowClosed;
    private nint _desktopFollowHotkeyWindow;
    private DesktopFollowMenu? _desktopFollowMenu;

    private void InitializeDesktopFollow()
    {
        _desktopFollowMenu = new DesktopFollowMenu(this,
            (window, point, text, wholeReply, isCurrent) => StartDesktopFollowAsync(window, point, text, wholeReply, isCurrent),
            CopyDesktopFollowSelection, IsDesktopFollowFailure,
            (window, point, isCurrent) => _ = SeekDesktopFollowAsync(window, point, isCurrent));
        if (_desktopFollowMenu.Register()) AppLog.WriteEvent("desktop-follow-menu-ready");
        else ReportDesktopFollow("无法启用桌面右键朗读菜单。", "menu-unavailable");

        if (DesktopFollowNative.Register(Handle))
        {
            _desktopFollowHotkeyWindow = Handle;
            AppLog.WriteEvent("desktop-follow-hotkey-ready");
        }
        else ReportDesktopFollow("无法注册 Ctrl+Alt+F9；快捷键可能已被占用。",
            "hotkey-unavailable:" + Marshal.GetLastPInvokeError());
    }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == DesktopFollowNative.HotkeyMessage && message.WParam == DesktopFollowNative.HotkeyId)
        {
            _ = StartDesktopFollowAsync(DesktopFollowNative.Foreground);
            return;
        }
        base.WndProc(ref message);
    }

    private async Task StartDesktopFollowAsync(nint window, Point? hit = null, string? expectedText = null,
        bool wholeReply = false, Func<bool>? isCurrent = null)
    {
        if (_desktopFollowCapturing || _desktopFollowClosed) return;
        _desktopFollowCapturing = true;
        var requestedDocument = _documentId;
        var requestedGeneration = _reader?.Generation;
        var requestedState = _reader?.State;
        try
        {
            var source = await Task.Run(() => DesktopFollowSource.Capture(window, hit, wholeReply));
            if (_desktopFollowClosed || isCurrent?.Invoke() == false || DesktopFollowNative.Foreground != window ||
                requestedDocument != _documentId || requestedGeneration != _reader?.Generation || requestedState != _reader?.State) return;
            if (source is null || (expectedText is not null && source.Text != expectedText))
            {
                ReportDesktopFollow("请在 Codex 或 Claude Code 的回复正文中选中文字（最多 20,000 字符）。", "selection-unavailable");
                return;
            }
            var voice = _speechService?.Voices.FirstOrDefault(voice => voice.Enabled && !voice.IsKnownOnline &&
                voice.Name.StartsWith("Microsoft Xiaoxiao", StringComparison.OrdinalIgnoreCase));
            if (_reader is null || voice is null)
            {
                ReportDesktopFollow("未找到可用的本机 Microsoft Xiaoxiao 语音。", "voice-unavailable");
                return;
            }
            if (_returnArticle is not null)
            {
                ReportDesktopFollow("请先返回插播前文章，再启动桌面跟读。", "interruption-active");
                return;
            }
            if (!PrepareQueueTransition()) return;
            SaveStateIfDirty(updateHistory: true);
            if (_stateDirty && !IsDesktopFollowDocument) return;
            _reader.SelectVoice(voice.Name);
            LocalPlaybackStarting?.Invoke(this, EventArgs.Empty);
            SetDocument(source.Text, null, source.Name + " · 临时跟读",
                DesktopFollowPrefix + Guid.NewGuid().ToString("N"), archiveCurrent: false, deferSave: true);
            _historyBox.SelectedIndex = -1;
            _initializingVoices = true;
            try { SelectVoiceByName(voice.Name); }
            finally { _initializingVoices = false; }
            _desktopFollow = source;
            _desktopFollowPainted = false;
            _desktopFollowGeneration = -1;
            AppLog.WriteEvent("desktop-follow-start", new Dictionary<string, object?>
            {
                ["source"] = source.Name, ["textLength"] = source.Text.Length, ["voiceName"] = voice.Name
            });
            StartPlayback();
            if (wholeReply && _reader.State == PlaybackState.Speaking)
                await Task.Run(() =>
                {
                    if (isCurrent?.Invoke() == false || DesktopFollowNative.Foreground != window) return;
                    try { source.SelectInWindow(); }
                    catch (Exception error) when (IsDesktopFollowFailure(error)) { }
                });
        }
        catch (Exception error) when (IsDesktopFollowFailure(error))
        {
            ReportDesktopFollow("无法从当前窗口开始跟读。请重新选中文本后重试。", error.GetType().Name);
        }
        finally { _desktopFollowCapturing = false; }
    }

    private async Task SeekDesktopFollowAsync(nint window, Point point, Func<bool> isCurrent)
    {
        if (_desktopFollow is not { } source || source.Window != window || !isCurrent() ||
            ReferenceEquals(_desktopFollowSeekingSource, source) ||
            _reader is not { State: PlaybackState.Speaking or PlaybackState.Paused } reader) return;
        var generation = reader.Generation;
        var state = reader.State;
        int? offset;
        _desktopFollowSeekingSource = source;
        try { offset = await Task.Run(() => source.ResolveClick(point)); }
        catch (Exception error) when (IsDesktopFollowFailure(error)) { return; }
        finally { if (ReferenceEquals(_desktopFollowSeekingSource, source)) _desktopFollowSeekingSource = null; }
        if (offset is not { } position || !ReferenceEquals(_desktopFollow, source) || !isCurrent() ||
            DesktopFollowNative.Foreground != window || generation != reader.Generation || state != reader.State) return;
        reader.SeekTo(position);
        if (reader.State == PlaybackState.Paused)
        {
            _desktopFollowSentence = new TextChunker().FindSentence(source.Text, position);
            _desktopFollowGeneration = reader.Generation;
            _ = RefreshDesktopFollowAsync();
        }
        AppLog.WriteEvent("desktop-follow-click-seek", new Dictionary<string, object?>
        {
            ["source"] = source.Name, ["position"] = position
        });
    }

    private void UpdateDesktopFollow(ReaderChangedEventArgs args)
    {
        if (_desktopFollow is null) return;
        if (args.State is not (PlaybackState.Speaking or PlaybackState.Paused))
        {
            if (args.State == PlaybackState.Error) ClearDesktopFollow();
            _desktopFollowSentence = default;
            _desktopFollowGeneration = -1;
            _desktopFollowTimer?.Stop();
            _desktopFollowOverlay?.Hide();
            return;
        }
        var sentence = args.SpokenRange.Length > 0 ? args.Sentence : default;
        if (_desktopFollowSentence == sentence && _desktopFollowGeneration == args.Generation) return;
        _desktopFollowSentence = sentence;
        _desktopFollowGeneration = args.Generation;
        _desktopFollowOverlay?.Hide();
        if (_desktopFollowTimer is null)
        {
            _desktopFollowTimer = new System.Windows.Forms.Timer { Interval = 150 };
            _desktopFollowTimer.Tick += (_, _) => _ = RefreshDesktopFollowAsync();
        }
        _desktopFollowTimer.Start();
        _ = RefreshDesktopFollowAsync();
    }

    private async Task RefreshDesktopFollowAsync()
    {
        var source = _desktopFollow;
        if (source is null) return;
        if (DesktopFollowNative.Foreground != source.Window || _desktopFollowSentence.Length == 0)
        {
            _desktopFollowOverlay?.Hide();
            return;
        }
        var started = Environment.TickCount64;
        var bounds = DesktopFollowNative.ClientBounds(source.Window);
        if (started - _desktopFollowLastPaint > 325) _desktopFollowOverlay?.Hide();
        if (ReferenceEquals(_desktopFollowBusySource, source)) return;
        var sentence = _desktopFollowSentence;
        var generation = _desktopFollowGeneration;
        var rectangles = Array.Empty<Rectangle>();
        _desktopFollowBusySource = source;
        try { rectangles = await Task.Run(() => source.Locate(sentence)); }
        catch (Exception error) when (IsDesktopFollowFailure(error)) { /* A stale UIA range is not a valid paint target. */ }
        finally { if (ReferenceEquals(_desktopFollowBusySource, source)) _desktopFollowBusySource = null; }
        if (!ReferenceEquals(_desktopFollow, source) || generation != _desktopFollowGeneration || sentence != _desktopFollowSentence) return;
        if (DesktopFollowNative.Foreground != source.Window || rectangles.Length == 0 ||
            Environment.TickCount64 - started > 325 || bounds != DesktopFollowNative.ClientBounds(source.Window))
        {
            _desktopFollowOverlay?.Hide();
            return;
        }
        _desktopFollowOverlay ??= new FollowOverlay();
        _desktopFollowOverlay.PaintRanges(rectangles);
        _desktopFollowLastPaint = Environment.TickCount64;
        if (_desktopFollowPainted) return;
        _desktopFollowPainted = true;
        AppLog.WriteEvent("desktop-follow-first-highlight", new Dictionary<string, object?>
        {
            ["source"] = source.Name, ["rectangleCount"] = rectangles.Length, ["sentenceLength"] = sentence.Length
        });
    }

    private void ClearDesktopFollow()
    {
        _desktopFollow = null;
        _desktopFollowSentence = default;
        _desktopFollowTimer?.Stop();
        _desktopFollowOverlay?.Hide();
    }

    private void CopyDesktopFollowSelection(string text)
    {
        try { Clipboard.SetDataObject(new DataObject(DataFormats.UnicodeText, text), true, 1, 0); }
        catch (ExternalException)
        {
            ReportDesktopFollow("暂时无法访问剪贴板，请重试。", "clipboard-unavailable");
        }
    }

    private void CloseDesktopFollow()
    {
        _desktopFollowClosed = true;
        _desktopFollowMenu?.Dispose();
        _desktopFollowMenu = null;
        ClearDesktopFollow();
        if (_desktopFollowHotkeyWindow != 0) DesktopFollowNative.Unregister(_desktopFollowHotkeyWindow);
        _desktopFollowHotkeyWindow = 0;
        _desktopFollowTimer?.Dispose();
        _desktopFollowTimer = null;
        _desktopFollowOverlay?.Dispose();
        _desktopFollowOverlay = null;
    }

    private void ReportDesktopFollow(string message, string reason)
    {
        _statusLabel.Text = message;
        _tray?.ShowBalloonTip(3000, "ListenFlow 桌面跟读", message, ToolTipIcon.Info);
        AppLog.WriteEvent("desktop-follow-unavailable", new Dictionary<string, object?> { ["reason"] = reason });
    }

    private static bool IsDesktopFollowFailure(Exception error) => error is
        InvalidOperationException or ArgumentException or COMException or Win32Exception or ElementNotAvailableException;

    private sealed class FollowOverlay : Form
    {
        private Rectangle[] _rectangles = [];

        public FollowOverlay()
        {
            Text = "ListenFlow · 原窗口高亮";
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            AutoScaleMode = AutoScaleMode.None;
            TopMost = true;
            BackColor = ListenFlow.UI.ComicTheme.OverlayHighlight;
            Opacity = 0.26;
        }

        protected override bool ShowWithoutActivation => true;
        protected override CreateParams CreateParams
        {
            get
            {
                var parameters = base.CreateParams;
                parameters.ExStyle |= 0x08000000 | 0x00000080 | 0x00000020; // no activate, tool window, input transparent
                return parameters;
            }
        }

        public void PaintRanges(Rectangle[] rectangles)
        {
            if (!_rectangles.AsSpan().SequenceEqual(rectangles))
            {
                var bounds = rectangles.Aggregate(Rectangle.Union);
                var region = new Region();
                region.MakeEmpty();
                foreach (var rect in rectangles)
                    region.Union(new Rectangle(rect.X - bounds.X, rect.Y - bounds.Y, rect.Width, rect.Height));
                Region = region;
                Bounds = bounds;
                _rectangles = rectangles;
            }
            if (!Visible) Show();
        }
    }
}
