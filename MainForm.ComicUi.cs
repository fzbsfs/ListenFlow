using ListenFlow.Core;
using ListenFlow.UI;

namespace ListenFlow;

internal sealed partial class MainForm
{
    private readonly ComicHeaderBar _headerBar = new();
    private readonly HeroTicketPlayer _heroPlayer = new();
    private readonly ComicPaperPanel _paperPanel = new();
    private readonly ComicQueuePanel _queuePanel = new();
    private readonly ComicMascotControl _mascot = new();
    private readonly ComicStatusStrip _statusStrip = new();
    private readonly HalftonePanel _contentHost = new();
    private readonly HalftonePanel _sidebarPanel = new();
    private readonly ComicPopoverPanel _settingsPopover = new();
    private readonly ComicPopoverPanel _historyPopover = new();
    private readonly ListBox _historyList = new();
    private readonly CheckBox _showMascotCheck = new();
    private bool _syncingHistoryList;

    private void BuildComicUi()
    {
        BackColor = ComicTheme.Cream;
        ForeColor = ComicTheme.TextPrimary;
        AutoScaleMode = AutoScaleMode.Dpi;

        // Wire hidden compatibility status label so any _statusLabel.Text assignment refreshes the comic status strip
        _statusLabel.TextChanged += (_, _) => RefreshComicStatus();

        // 1. Header Bar
        _headerBar.ListeningClicked += (_, _) => ClosePopovers();
        _headerBar.QueueClicked += (_, _) => { ClosePopovers(); ShowReadingQueue(); };
        _headerBar.HistoryClicked += (_, _) => ToggleHistoryPopover();
        _headerBar.OpenTxtClicked += (_, _) => { ClosePopovers(); OpenTextFile(); };
        _headerBar.PasteClipboardClicked += (_, _) => { ClosePopovers(); PasteClipboard(); };
        _headerBar.SettingsClicked += (_, _) => ToggleSettingsPopover();

        // 2. Hero Ticket Player
        _heroPlayer.PlayToggleClicked += (_, _) => TogglePlayback();
        _heroPlayer.PreviousClicked += (_, _) => MoveBySentence(-1);
        _heroPlayer.NextClicked += (_, _) => MoveBySentence(1);
        _heroPlayer.StopClicked += (_, _) => StopPlayback();
        _heroPlayer.RateDeltaRequested += (_, delta) => AdjustRateBy(delta);
        _heroPlayer.SeekRatioRequested += (_, ratio) => SeekByRatio(ratio);

        // 3. Editor inside Comic Paper Panel
        _editor.Multiline = true;
        _editor.AcceptsReturn = true;
        _editor.AcceptsTab = true;
        _editor.ScrollBars = ScrollBars.Vertical;
        _editor.WordWrap = true;
        _editor.Font = new Font("Microsoft YaHei UI", (float)Math.Clamp(_desktopPreferences.ReadingFontSize, 9, 24));
        _editor.TextChanged += EditorOnTextChanged;
        _editor.MouseWheel += (_, _) =>
        {
            if (_followReading.Checked) _followReading.Checked = false;
            _paperPanel.Invalidate();
        };
        _editor.MouseDown += (_, _) =>
        {
            ClosePopovers();
            if (_reader?.State is PlaybackState.Speaking or PlaybackState.Paused)
                _followReading.Checked = false;
        };
        ConfigureEditorReadingActions();
        if (_editor.ContextMenuStrip is { } editorMenu) ComicTheme.StyleContextMenu(editorMenu);
        _paperPanel.AttachEditor(_editor);
        _paperPanel.Dock = DockStyle.Fill;

        // 4. Right Sidebar: Ticket Queue + G2 Mascot
        _queuePanel.Dock = DockStyle.Fill;
        _queuePanel.CurrentCardClicked += (_, _) => TogglePlayback();
        _queuePanel.QueuedItemPlayRequested += (_, index) => PlayQueuedItemAt(index);
        _queuePanel.QueuedItemRemoveRequested += (_, index) => RemoveQueuedItemAt(index);
        _queuePanel.RestoreReturnRequested += (_, _) => RestoreInterruptedArticle();
        _queuePanel.EnqueueClipboardRequested += (_, _) => EnqueueClipboard();
        _queuePanel.ManageQueueRequested += (_, _) => ShowReadingQueue();

        _mascot.Dock = DockStyle.Bottom;
        _mascot.Visible = _desktopPreferences.ShowMascot;
        _mascot.PlayToggleRequested += (_, _) =>
        {
            TogglePlayback();
            var pct = GetCurrentPercent();
            _mascot.SyncPlaybackState(_reader?.State ?? PlaybackState.Ready, pct, (int)_rateBox.Value, fromUserToggle: true);
        };
        _mascot.LocateSentenceRequested += (_, _) => LocateCurrentSentenceFromMascot();
        _mascot.RateDeltaRequested += (_, delta) => AdjustRateBy(delta);
        _mascot.FloatingControlsRequested += (_, _) => ShowFloatingControls();
        _mascot.HideMascotRequested += (_, _) => SetMascotVisible(false);
        _mascot.InteractCountChanged += (_, count) => _queuePanel.SetInteractCount(count);

        _sidebarPanel.Dock = DockStyle.Right;
        _sidebarPanel.Padding = new Padding(16, 0, 0, 0);
        _sidebarPanel.Controls.Add(_queuePanel);
        _sidebarPanel.Controls.Add(_mascot);

        _contentHost.Dock = DockStyle.Fill;
        _contentHost.Padding = new Padding(24, 4, 24, 4);
        _contentHost.Controls.Add(_paperPanel);
        _contentHost.Controls.Add(_sidebarPanel);

        // 5. Status Strip
        _statusStrip.FollowToggleClicked += (_, _) => _followReading.Checked = !_followReading.Checked;
        _statusStrip.RepeatSentenceClicked += (_, _) => RepeatCurrentSentence();
        _statusStrip.FloatingClicked += (_, _) => ShowFloatingControls();
        _statusStrip.RestoreMascotClicked += (_, _) => SetMascotVisible(true);

        // Dock order matters in WinForms: Fill first, then Top/Bottom
        Controls.Add(_contentHost);
        Controls.Add(_heroPlayer);
        Controls.Add(_headerBar);
        Controls.Add(_statusStrip);

        // 6. Build Settings & History Popovers (live children of MainForm so all controls remain alive)
        BuildSettingsPopover();
        BuildHistoryPopover();

        // Keep compatibility hidden controls initialized
        _progressBar.Maximum = 1000;
        _playButton.Click += (_, _) => TogglePlayback();
        _stopButton.Click += (_, _) => StopPlayback();
        _previousButton.Click += (_, _) => MoveBySentence(-1);
        _nextButton.Click += (_, _) => MoveBySentence(1);

        Resize += (_, _) => ApplyResponsiveMetrics();
        DpiChanged += (_, _) => ApplyResponsiveMetrics();
        ApplyResponsiveMetrics();
    }

    private void BuildSettingsPopover()
    {
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            AutoSize = false,
            BackColor = ComicTheme.Paper
        };

        var titleRow = new Label
        {
            Text = "⚙ 听读与语音设置",
            AutoSize = true,
            Font = new Font("Microsoft YaHei UI", 10.5f, FontStyle.Bold),
            ForeColor = ComicTheme.Ink,
            Margin = new Padding(0, 0, 0, 8)
        };
        layout.Controls.Add(titleRow);

        var voiceLbl = new Label
        {
            Text = "本地语音",
            AutoSize = true,
            Font = new Font("Microsoft YaHei UI", 8.8f, FontStyle.Bold),
            ForeColor = ComicTheme.TextMuted,
            Margin = new Padding(0, 2, 0, 4)
        };
        layout.Controls.Add(voiceLbl);

        _voiceBox.DropDownStyle = ComboBoxStyle.DropDownList;
        _voiceBox.Dock = DockStyle.Top;
        _voiceBox.Width = 370;
        _voiceBox.Font = new Font("Microsoft YaHei UI", 9.2f);
        _voiceBox.SelectedIndexChanged += VoiceSelectionChanged;
        _voiceBox.SelectionChangeCommitted += VoiceSelectionCommitted;
        layout.Controls.Add(_voiceBox);

        var voiceButtons = new FlowLayoutPanel { AutoSize = true, WrapContents = true, BackColor = ComicTheme.Paper, Margin = new Padding(0, 6, 0, 10) };
        _previewVoiceButton.Text = "试听";
        _previewVoiceButton.AutoSize = true;
        _previewVoiceButton.Click += (_, _) => PreviewSelectedVoice();
        ComicTheme.StyleComicButton(_previewVoiceButton, primary: true);
        voiceButtons.Controls.Add(_previewVoiceButton);

        _refreshVoicesButton.Text = "刷新语音";
        _refreshVoicesButton.AutoSize = true;
        _refreshVoicesButton.Click += (_, _) => RefreshVoices();
        ComicTheme.StyleComicButton(_refreshVoicesButton);
        voiceButtons.Controls.Add(_refreshVoicesButton);

        _voiceStatusButton.Text = "语音状态";
        _voiceStatusButton.AutoSize = true;
        _voiceStatusButton.Click += (_, _) => ShowVoiceStatus();
        ComicTheme.StyleComicButton(_voiceStatusButton);
        voiceButtons.Controls.Add(_voiceStatusButton);

        _speechSettingsButton.Text = "Windows 语音设置";
        _speechSettingsButton.AutoSize = true;
        _speechSettingsButton.Click += (_, _) => OpenSpeechSettings();
        ComicTheme.StyleComicButton(_speechSettingsButton);
        voiceButtons.Controls.Add(_speechSettingsButton);
        layout.Controls.Add(voiceButtons);

        // Reading preferences row: Font size + Rate
        var prefRow = new FlowLayoutPanel { AutoSize = true, WrapContents = true, BackColor = ComicTheme.Paper, Margin = new Padding(0, 2, 0, 8) };
        prefRow.Controls.Add(new Label { Text = "正文字号：", AutoSize = true, Margin = new Padding(0, 5, 2, 0), ForeColor = ComicTheme.Ink, Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold) });
        _fontSizeBox.Minimum = 9;
        _fontSizeBox.Maximum = 24;
        _fontSizeBox.Width = 56;
        _fontSizeBox.AccessibleName = "正文字号";
        _fontSizeBox.Value = Math.Clamp(_desktopPreferences.ReadingFontSize, 9, 24);
        _fontSizeBox.ValueChanged += (_, _) =>
        {
            var oldFont = _editor.Font;
            _editor.Font = new Font("Microsoft YaHei UI", (float)_fontSizeBox.Value);
            oldFont.Dispose();
            _desktopPreferences.ReadingFontSize = (int)_fontSizeBox.Value;
            _desktopPreferences.Save();
            if (_followReading.Checked) ScrollToCurrentSentence();
            RefreshComicStatus();
        };
        prefRow.Controls.Add(_fontSizeBox);

        prefRow.Controls.Add(new Label { Text = "语速（-10~+10）：", AutoSize = true, Margin = new Padding(14, 5, 2, 0), ForeColor = ComicTheme.Ink, Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold) });
        _rateBox.Minimum = -10;
        _rateBox.Maximum = 10;
        _rateBox.Value = 2;
        _rateBox.Width = 56;
        _rateBox.ValueChanged += (_, _) =>
        {
            ApplyPlaybackRate();
            MarkStateDirty();
            RefreshComicHero();
            RefreshComicStatus();
        };
        prefRow.Controls.Add(_rateBox);
        layout.Controls.Add(prefRow);

        // Toggles row
        var toggleRow = new FlowLayoutPanel { AutoSize = true, WrapContents = true, BackColor = ComicTheme.Paper, Margin = new Padding(0, 0, 0, 10) };
        _followReading.Text = "跟随朗读高亮";
        _followReading.AutoSize = true;
        _followReading.ForeColor = ComicTheme.Ink;
        _followReading.Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold);
        _followReading.Checked = _desktopPreferences.FollowReading;
        _followReading.CheckedChanged += (_, _) =>
        {
            _desktopPreferences.FollowReading = _followReading.Checked;
            _desktopPreferences.Save();
            if (_followReading.Checked) ScrollToCurrentSentence();
            RefreshComicStatus();
        };
        toggleRow.Controls.Add(_followReading);

        _showMascotCheck.Text = "显示交互看板娘（G2）";
        _showMascotCheck.AutoSize = true;
        _showMascotCheck.ForeColor = ComicTheme.Ink;
        _showMascotCheck.Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold);
        _showMascotCheck.Margin = new Padding(14, 3, 0, 0);
        _showMascotCheck.Checked = _desktopPreferences.ShowMascot;
        _showMascotCheck.CheckedChanged += (_, _) => SetMascotVisible(_showMascotCheck.Checked);
        toggleRow.Controls.Add(_showMascotCheck);
        layout.Controls.Add(toggleRow);

        // Quick actions row
        var quickRow = new FlowLayoutPanel { AutoSize = true, WrapContents = true, BackColor = ComicTheme.Paper, Margin = new Padding(0, 2, 0, 0) };
        AddPopoverButton(quickRow, "朗读剪贴板", () => { ClosePopovers(); StartClipboardReading(Clipboard.GetText); });
        AddPopoverButton(quickRow, "插播剪贴板", () => { ClosePopovers(); InterruptWithClipboard(); });
        AddPopoverButton(quickRow, "悬浮控制条", () => { ClosePopovers(); ShowFloatingControls(); });
        AddPopoverButton(quickRow, "清空正文", () => { ClosePopovers(); ClearText(); });
        layout.Controls.Add(quickRow);

        _settingsPopover.Controls.Add(layout);
        Controls.Add(_settingsPopover);
    }

    private static void AddPopoverButton(FlowLayoutPanel panel, string text, Action action)
    {
        var btn = new Button { Text = text, AutoSize = true, Padding = new Padding(6, 3, 6, 3), Margin = new Padding(0, 0, 6, 6) };
        ComicTheme.StyleComicButton(btn);
        btn.Click += (_, _) => action();
        panel.Controls.Add(btn);
    }

    private void BuildHistoryPopover()
    {
        _historyBox.DropDownStyle = ComboBoxStyle.DropDownList;
        _historyBox.Visible = false;
        _historyBox.SelectedIndexChanged += HistorySelectionChanged;
        _historyPopover.Controls.Add(_historyBox);

        var header = new Label
        {
            Text = "最近文章（点击切换并恢复进度）",
            Dock = DockStyle.Top,
            Height = 26,
            Font = new Font("Microsoft YaHei UI", 9.6f, FontStyle.Bold),
            ForeColor = ComicTheme.Ink,
            BackColor = ComicTheme.Paper
        };

        _historyList.Dock = DockStyle.Fill;
        _historyList.BorderStyle = BorderStyle.FixedSingle;
        _historyList.BackColor = ComicTheme.White;
        _historyList.ForeColor = ComicTheme.Ink;
        _historyList.Font = new Font("Microsoft YaHei UI", 9.2f, FontStyle.Bold);
        _historyList.ItemHeight = 28;
        _historyList.SelectedIndexChanged += (_, _) =>
        {
            if (_syncingHistoryList || _historyList.SelectedIndex < 0) return;
            var idx = _historyList.SelectedIndex;
            ClosePopovers();
            if (idx < _historyBox.Items.Count)
                _historyBox.SelectedIndex = idx;
        };

        _historyPopover.Controls.Add(_historyList);
        _historyPopover.Controls.Add(header);
        Controls.Add(_historyPopover);
    }

    private void ApplyResponsiveMetrics()
    {
        var s = ComicTheme.Scale(this);
        var compact = ClientSize.Width < (int)(860 * s);
        var shortWin = ClientSize.Height < (int)(620 * s);

        _headerBar.Height = (int)Math.Round(54 * s);
        _heroPlayer.Height = (int)Math.Round((shortWin ? 150 : 180) * s);
        _statusStrip.Height = (int)Math.Round(34 * s);

        var padX = (int)Math.Round((compact ? 14 : 24) * s);
        var padY = (int)Math.Round(4 * s);
        _contentHost.Padding = new Padding(padX, padY, padX, padY);

        _sidebarPanel.Width = (int)Math.Round((compact ? 228 : 262) * s);
        _sidebarPanel.Padding = new Padding((int)Math.Round((compact ? 12 : 18) * s), 0, 0, 0);

        var availContentH = Math.Max(180, ClientSize.Height - _headerBar.Height - _heroPlayer.Height - _statusStrip.Height - padY * 2);
        var targetMascotH = shortWin
            ? Math.Clamp((int)Math.Round(availContentH * 0.50f), (int)Math.Round(132 * s), (int)Math.Round(186 * s))
            : Math.Clamp((int)Math.Round(availContentH * 0.48f), (int)Math.Round(172 * s), (int)Math.Round(206 * s));
        _mascot.Height = targetMascotH;

        PositionPopovers();
        PerformLayout();
    }

    private void PositionPopovers()
    {
        var s = ComicTheme.Scale(this);
        var setW = Math.Min(ClientSize.Width - (int)(32 * s), (int)Math.Round(440 * s));
        var setH = (int)Math.Round(305 * s);
        _settingsPopover.Bounds = new Rectangle(
            Math.Max((int)(16 * s), ClientSize.Width - setW - (int)(20 * s)),
            _headerBar.Bottom + (int)(4 * s),
            setW,
            setH);

        var histAnchor = _headerBar.GetItemBounds("history");
        var histW = Math.Min(ClientSize.Width - (int)(32 * s), (int)Math.Round(360 * s));
        var histH = (int)Math.Round(270 * s);
        var histX = histAnchor.IsEmpty ? (int)(180 * s) : Math.Clamp(histAnchor.Left, (int)(16 * s), Math.Max((int)(16 * s), ClientSize.Width - histW - (int)(16 * s)));
        _historyPopover.Bounds = new Rectangle(histX, _headerBar.Bottom + (int)(4 * s), histW, histH);
    }

    private void ToggleSettingsPopover()
    {
        var next = !_settingsPopover.Visible;
        ClosePopovers();
        if (!next) return;
        PositionPopovers();
        _settingsPopover.Visible = true;
        _settingsPopover.BringToFront();
        _headerBar.SettingsOpen = true;
    }

    private void ToggleHistoryPopover()
    {
        var next = !_historyPopover.Visible;
        ClosePopovers();
        if (!next) return;
        SyncHistoryList();
        PositionPopovers();
        _historyPopover.Visible = true;
        _historyPopover.BringToFront();
        _headerBar.HistoryOpen = true;
    }

    private bool ClosePopovers()
    {
        var closed = _settingsPopover.Visible || _historyPopover.Visible;
        _settingsPopover.Visible = false;
        _historyPopover.Visible = false;
        _headerBar.SettingsOpen = false;
        _headerBar.HistoryOpen = false;
        return closed;
    }

    private void SyncHistoryList()
    {
        _syncingHistoryList = true;
        try
        {
            _historyList.Items.Clear();
            foreach (var item in _historyBox.Items)
                _historyList.Items.Add(item is HistoryItem hi ? hi.DisplayText : item.ToString() ?? string.Empty);
            if (_historyBox.SelectedIndex >= 0 && _historyBox.SelectedIndex < _historyList.Items.Count)
                _historyList.SelectedIndex = _historyBox.SelectedIndex;
            _historyList.Enabled = _historyBox.Enabled;
        }
        finally { _syncingHistoryList = false; }
    }

    private void SetMascotVisible(bool visible)
    {
        _mascot.Visible = visible;
        if (_showMascotCheck.Checked != visible) _showMascotCheck.Checked = visible;
        if (_desktopPreferences.ShowMascot != visible)
        {
            _desktopPreferences.ShowMascot = visible;
            _desktopPreferences.Save();
        }
        RefreshComicStatus();
    }

    private void AdjustRateBy(int delta)
    {
        var next = Math.Clamp((int)_rateBox.Value + delta, (int)_rateBox.Minimum, (int)_rateBox.Maximum);
        if (next == (int)_rateBox.Value) return;
        _rateBox.Value = next;
        _mascot.NotifySpeedChanged(next);
    }

    private void SeekByRatio(double ratio)
    {
        if (_reader is null || _editor.TextLength == 0) return;
        EnsureDocumentSynchronized();
        var offset = Math.Clamp((int)Math.Round(ratio * _editor.TextLength), 0, Math.Max(0, _editor.TextLength - 1));
        if (offset > 0 && offset < _editor.TextLength && char.IsLowSurrogate(_editor.Text[offset]) &&
            char.IsHighSurrogate(_editor.Text[offset - 1])) offset--;
        if (_reader.State is PlaybackState.Speaking or PlaybackState.Paused)
            _reader.SeekTo(offset);
        else
            _reader.SetDocument(_editor.Text, offset);
        UpdateProgress(offset);
        MarkStateDirty();
        SaveStateIfDirty(updateHistory: true);
        _mascot.NotifySeek(GetCurrentPercent());
    }

    private void LocateCurrentSentenceFromMascot()
    {
        _followReading.Checked = true;
        ScrollToCurrentSentence();
        _paperPanel.FlashLocate();
        _mascot.DoLocateFeedback(GetCurrentPercent());
    }

    private int GetCurrentPercent(int? positionOverride = null)
    {
        var total = _editor.TextLength;
        var current = Math.Clamp(positionOverride ?? (_documentNeedsSync ? 0 : _reader?.Position ?? 0), 0, total);
        return total == 0 ? 0 : (int)Math.Round(current * 100.0 / total);
    }

    private (string Badge, bool Highlight) GetSourceBadge()
    {
        if (IsDesktopFollowDocument)
        {
            if (_articleTitle.Contains("Codex", StringComparison.OrdinalIgnoreCase)) return ("Codex", true);
            if (_articleTitle.Contains("Claude", StringComparison.OrdinalIgnoreCase)) return ("Claude", true);
            return ("原窗口", true);
        }
        if (IsWebDocument) return ("网页", false);
        if (!string.IsNullOrWhiteSpace(_filePath)) return ("TXT", false);
        if (_articleTitle.Contains("剪贴板", StringComparison.Ordinal)) return ("剪贴板", false);
        return ("本地", false);
    }

    private void RefreshComicUi(int? positionOverride = null)
    {
        RefreshComicHero(positionOverride);
        RefreshComicQueue();
        RefreshComicStatus();
    }

    private void RefreshComicHero(int? positionOverride = null)
    {
        var total = _editor.TextLength;
        var current = Math.Clamp(positionOverride ?? (_documentNeedsSync ? 0 : _reader?.Position ?? 0), 0, total);
        var state = _reader?.State ?? PlaybackState.Ready;
        var hasText = !string.IsNullOrWhiteSpace(_editor.Text);
        var (badge, highlight) = GetSourceBadge();

        _heroPlayer.UpdateState(
            state: state,
            title: _articleTitle,
            sourceBadge: badge,
            highlightBadge: highlight,
            currentChars: current,
            totalChars: total,
            rate: (int)_rateBox.Value,
            canRead: _reader is not null && hasText,
            canStop: state is PlaybackState.Speaking or PlaybackState.Paused);
    }

    private void RefreshComicQueue()
    {
        var (badge, _) = GetSourceBadge();
        var items = _readingQueue.Select(t => (QueueTitle(t), t.Length)).ToArray();
        _queuePanel.UpdateQueue(
            currentTitle: _articleTitle,
            currentSource: badge,
            currentChars: _editor.TextLength,
            state: _reader?.State ?? PlaybackState.Ready,
            returnTitle: _returnArticle?.Article.Title,
            queued: items);
        _headerBar.QueueCount = _readingQueue.Count;
    }

    private void RefreshComicStatus()
    {
        var voiceName = (_voiceBox.SelectedItem as VoiceItem)?.Name ?? _speechService?.SelectedVoiceName ?? "本机语音";
        _statusStrip.UpdateStatus(
            voiceName: voiceName,
            followReading: _followReading.Checked,
            fontSize: (int)_fontSizeBox.Value,
            statusText: _statusLabel.Text,
            mascotHidden: !_mascot.Visible);
    }
}
