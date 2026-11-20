using ListenFlow.AIReader;
using ListenFlow.Core;
using ListenFlow.Storage;

namespace ListenFlow;

internal sealed partial class MainForm
{
    private readonly List<string> _readingQueue = [];
    private readonly Button _queueButton = new();
    private (ReaderState Article, bool Resume)? _returnArticle;
    private bool _changingQueueDocument;
    private bool _queueAutoAdvance;

    private bool IsWebDocument => _documentId.StartsWith("web:", StringComparison.Ordinal);

    private void ConfigureReadingQueue(FlowLayoutPanel toolbar)
    {
        _queueButton.Text = "待听队列（0）";
        _queueButton.AutoSize = true;
        var menu = new ContextMenuStrip();
        menu.Items.Add("剪贴板加入待听", null, (_, _) => EnqueueClipboard());
        menu.Items.Add("查看和管理队列", null, (_, _) => ShowReadingQueue());
        menu.Items.Add("立即播放下一项", null, (_, _) => PlayNextQueuedReading());
        menu.Items.Add("返回插播前文章", null, (_, _) => RestoreInterruptedArticle());
        _queueButton.Click += (_, _) => menu.Show(_queueButton, new Point(0, _queueButton.Height));
        _queueButton.Disposed += (_, _) => menu.Dispose();
        toolbar.Controls.Add(_queueButton);
    }

    private string? ReadQueueClipboard(Func<string>? readText = null)
    {
        var result = DesktopClipboardConnector.Read(readText ?? Clipboard.GetText);
        if (!result.Success) _statusLabel.Text = result.Error;
        return result.Text;
    }

    private void EnqueueClipboard()
    {
        var text = ReadQueueClipboard();
        if (text is not null) EnqueueText(text);
    }

    private void EnqueueText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            _statusLabel.Text = "没有可加入待听队列的文字。";
            return;
        }
        if (_readingQueue.Count >= 20)
        {
            _statusLabel.Text = "待听队列最多保存 20 项，请先听完或移除部分内容。";
            return;
        }
        if (_readingQueue.Contains(text, StringComparer.Ordinal))
        {
            _statusLabel.Text = "这段文字已经在待听队列中。";
            return;
        }
        _readingQueue.Add(text);
        UpdateQueueCaption();
        _statusLabel.Text = IsWebDocument
            ? "已加入待听。网页朗读结束后，请从待听队列手动播放。"
            : "已加入待听；本地文章自然读完后接着播放。停止会中止自动连读。";
    }

    private void UpdateQueueCaption()
    {
        _queueButton.Text = $"待听队列（{_readingQueue.Count}）";
        RefreshComicQueue();
    }

    private void PlayQueuedItemAt(int index)
    {
        if (index < 0 || index >= _readingQueue.Count) return;
        if (!PrepareQueueTransition()) return;
        var text = _readingQueue[index];
        if (StartQueuedText(text))
            _readingQueue.RemoveAt(index);
        UpdateQueueCaption();
    }

    private void RemoveQueuedItemAt(int index)
    {
        if (index < 0 || index >= _readingQueue.Count) return;
        _readingQueue.RemoveAt(index);
        UpdateQueueCaption();
        _statusLabel.Text = "已从待听队列移除选中项。";
    }

    private static string QueueTitle(string text)
    {
        var line = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? "剪贴板文章";
        var end = Math.Min(24, line.Length);
        if (end < line.Length && char.IsHighSurrogate(line[end - 1])) end--;
        return line[..end] + (end < line.Length ? "…" : "");
    }

    private bool PrepareQueueTransition()
    {
        if (_reader is null || !StopVoicePreview()) return false;
        _reader.Stop();
        if (_reader.State != PlaybackState.Stopped)
        {
            _statusLabel.Text = _reader.ErrorMessage ?? "未能确认原声音已停止，保留待听内容。";
            return false;
        }
        return true;
    }

    private void InterruptWithClipboard(Func<string>? readText = null)
    {
        var text = ReadQueueClipboard(readText);
        if (text is not null) InterruptWithText(text);
    }

    private void InterruptWithText(string text)
    {
        if (IsWebDocument)
        {
            _statusLabel.Text = "网页会话暂不支持自动插播恢复，请使用待听队列保存内容。";
            return;
        }
        if (_returnArticle is not null)
        {
            _statusLabel.Text = "正在插播，请先返回原文；新内容可以加入待听队列。";
            return;
        }
        if (string.IsNullOrWhiteSpace(text) || _reader is null) return;
        // 编辑后正文与控制器尚未同步，旧正文的字符位置不能用于新正文。
        var recoveryPosition = _documentNeedsSync ? 0 : _reader.GetRecoveryPosition();
        var previous = ReaderState.Create(_articleTitle, _editor.Text, recoveryPosition,
            _filePath, _speechService?.SelectedVoiceName ?? string.Empty, (int)_rateBox.Value, _documentId);
        var resume = _reader.State == PlaybackState.Speaking;
        if (!PrepareQueueTransition()) return;
        if (!string.IsNullOrWhiteSpace(previous.Text)) _returnArticle = (previous, resume);
        StartQueuedText(text);
    }

    private bool StartQueuedText(string text)
    {
        _changingQueueDocument = true;
        try
        {
            LocalPlaybackStarting?.Invoke(this, EventArgs.Empty);
            SetDocument(text, null, QueueTitle(text));
            StartPlayback();
            var started = _reader?.State is PlaybackState.Speaking or PlaybackState.Completed;
            if (!started && _returnArticle is not null)
            {
                _queueAutoAdvance = false;
                _statusLabel.Text = (_reader?.ErrorMessage ?? "插播未能启动。") + " 可从待听队列菜单返回插播前文章。";
            }
            return started;
        }
        finally { _changingQueueDocument = false; }
    }

    private void PlayNextQueuedReading()
    {
        if (_readingQueue.Count == 0) { _statusLabel.Text = "待听队列为空。"; return; }
        if (!PrepareQueueTransition()) return;
        var text = _readingQueue[0];
        if (StartQueuedText(text))
            _readingQueue.RemoveAt(0);
        UpdateQueueCaption();
    }

    private void RestoreInterruptedArticle()
    {
        if (_returnArticle is not { } saved) { _statusLabel.Text = "没有等待恢复的插播前文章。"; return; }
        if (!PrepareQueueTransition()) return;
        _changingQueueDocument = true;
        try
        {
            var article = saved.Article;
            SetDocument(article.Text, article.FilePath, article.Title, article.DocumentId, article.CharacterPosition);
            _rateBox.Value = Math.Clamp(article.Rate, -10, 10);
            if (saved.Resume)
            {
                StartPlayback();
                if (_reader?.State is PlaybackState.Speaking or PlaybackState.Completed)
                    _returnArticle = null;
                else
                {
                    _queueAutoAdvance = false;
                    _statusLabel.Text = (_reader?.ErrorMessage ?? "原文未能继续朗读。") + " 已保留返回位置，可从待听队列菜单重试。";
                }
            }
            else
            {
                _returnArticle = null;
                _queueAutoAdvance = false;
                _statusLabel.Text = "已返回插播前文章，保持安静；点击播放继续。";
            }
        }
        finally { _changingQueueDocument = false; }
    }

    private void ScheduleNextReading(ReaderChangedEventArgs args)
    {
        if (args.State == PlaybackState.Error) _queueAutoAdvance = false;
        if (args.State != PlaybackState.Completed || !_queueAutoAdvance || IsWebDocument || !IsHandleCreated) return;
        var generation = args.Generation;
        var document = _documentId;
        BeginInvoke((Action)(() =>
        {
            if (_closing || !_queueAutoAdvance || _documentId != document ||
                _reader?.State != PlaybackState.Completed || _reader.Generation != generation) return;
            if (_returnArticle is not null) RestoreInterruptedArticle();
            else if (_readingQueue.Count > 0) PlayNextQueuedReading();
        }));
    }

    private void ShowReadingQueue()
    {
        using var dialog = new Form { Text = "待听队列 · 退出程序后清空", StartPosition = FormStartPosition.CenterParent,
            Size = new Size(540, 360), MinimizeBox = false, MaximizeBox = false, Font = Font,
            BackColor = ListenFlow.UI.ComicTheme.Paper, ForeColor = ListenFlow.UI.ComicTheme.TextPrimary };
        var list = new ListBox
        {
            Dock = DockStyle.Fill,
            BackColor = ListenFlow.UI.ComicTheme.White,
            ForeColor = ListenFlow.UI.ComicTheme.Ink,
            BorderStyle = BorderStyle.FixedSingle,
            Font = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold)
        };
        void RefreshItems()
        {
            var selectedText = (list.SelectedItem as QueueListItem)?.Text;
            list.Items.Clear();
            foreach (var text in _readingQueue)
            {
                var index = list.Items.Add(new QueueListItem(text));
                if (string.Equals(text, selectedText, StringComparison.Ordinal)) list.SelectedIndex = index;
            }
            UpdateQueueCaption();
        }
        var actions = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, BackColor = ListenFlow.UI.ComicTheme.Paper, Padding = new Padding(8, 8, 8, 8) };
        var removeBtn = MakeButton("移除选中", (_, _) =>
        {
            if (list.SelectedItem is QueueListItem item) _readingQueue.Remove(item.Text);
            RefreshItems();
        });
        ListenFlow.UI.ComicTheme.StyleComicButton(removeBtn);
        actions.Controls.Add(removeBtn);
        var playNextBtn = MakeButton("播放下一项", (_, _) => { PlayNextQueuedReading(); dialog.Close(); });
        ListenFlow.UI.ComicTheme.StyleComicButton(playNextBtn, primary: true);
        actions.Controls.Add(playNextBtn);
        // 队列可能在对话框打开期间自动推进，操作前保持列表与当前队列一致。
        using var refresh = new System.Windows.Forms.Timer { Interval = 500 };
        refresh.Tick += (_, _) =>
        {
            if (!list.Items.Cast<QueueListItem>().Select(item => item.Text).SequenceEqual(_readingQueue))
                RefreshItems();
        };
        dialog.Controls.Add(list);
        dialog.Controls.Add(actions);
        RefreshItems();
        refresh.Start();
        dialog.ShowDialog(this);
    }

    private sealed record QueueListItem(string Text)
    {
        public override string ToString() => $"{QueueTitle(Text)} · {Text.Length:N0} 字";
    }
}
