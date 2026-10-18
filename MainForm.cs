using System.Text;
using ListenFlow.Core;
using ListenFlow.Speech;
using ListenFlow.Storage;

namespace ListenFlow;

internal sealed class MainForm : Form
{
    private static readonly string StatePath = Path.Combine(AppPaths.DataDirectory, "progress.json");

    private readonly TextBox _editor = new();
    private readonly ComboBox _voiceBox = new();
    private readonly ComboBox _historyBox = new();
    private readonly NumericUpDown _rateBox = new();
    private readonly Label _statusLabel = new();
    private readonly Label _progressLabel = new();
    private readonly ProgressBar _progressBar = new();
    private readonly Button _playButton = new();
    private readonly Button _pauseButton = new();
    private readonly Button _resumeButton = new();
    private readonly Button _stopButton = new();
    private readonly Button _previousButton = new();
    private readonly Button _nextButton = new();
    private readonly ProgressStore _progressStore = new(StatePath);
    private readonly RecentArticleStore _recentStore = new(Path.Combine(AppPaths.DataDirectory, "history"));
    private readonly System.Windows.Forms.Timer _saveTimer = new() { Interval = 1000 };

    private SapiSpeechService? _speechService;
    private ReaderController? _reader;
    private ReaderState? _restoredState;
    private string? _filePath;
    private string _documentId = Guid.NewGuid().ToString("N");
    private string _articleTitle = "未命名文章";
    private bool _loading = true;
    private bool _initializingVoices;
    private bool _initializingHistory;
    private bool _stateDirty;
    private bool _documentNeedsSync;
    private bool _closing;

    public MainForm()
    {
        Text = "ListenFlow · 本地听读助手";
        Width = 1080;
        Height = 740;
        MinimumSize = new Size(780, 520);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Microsoft YaHei UI", 9F);
        KeyPreview = true;

        BuildUi();
        LoadSavedState();
        InitializeSpeech();

        _saveTimer.Tick += (_, _) => SaveStateIfDirty();
        _saveTimer.Start();
        _loading = false;
        RefreshHistory();
        UpdateProgress();
        UpdateControls();

        KeyDown += MainForm_KeyDown;
        FormClosing += MainForm_FormClosing;
    }

    private void BuildUi()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            Padding = new Padding(10)
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Controls.Add(root);

        var toolbar = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            WrapContents = true,
            Padding = new Padding(0, 0, 0, 8)
        };
        toolbar.Controls.Add(MakeButton("打开 TXT", (_, _) => OpenTextFile()));
        toolbar.Controls.Add(MakeButton("粘贴剪贴板", (_, _) => PasteClipboard()));
        toolbar.Controls.Add(MakeButton("清空", (_, _) => ClearText()));
        toolbar.Controls.Add(new Label
        {
            Text = "最近文章：",
            AutoSize = true,
            Margin = new Padding(12, 8, 0, 0)
        });
        _historyBox.DropDownStyle = ComboBoxStyle.DropDownList;
        _historyBox.Width = 210;
        _historyBox.SelectedIndexChanged += HistorySelectionChanged;
        toolbar.Controls.Add(_historyBox);

        _previousButton.Text = "后退一句";
        _previousButton.AutoSize = true;
        _previousButton.Click += (_, _) => MoveBySentence(-1);
        toolbar.Controls.Add(_previousButton);

        _playButton.Text = "播放";
        _playButton.AutoSize = true;
        _playButton.Click += (_, _) => StartPlayback();
        toolbar.Controls.Add(_playButton);

        _pauseButton.Text = "暂停";
        _pauseButton.AutoSize = true;
        _pauseButton.Click += (_, _) => PausePlayback();
        toolbar.Controls.Add(_pauseButton);

        _resumeButton.Text = "继续";
        _resumeButton.AutoSize = true;
        _resumeButton.Click += (_, _) => ResumePlayback();
        toolbar.Controls.Add(_resumeButton);

        _stopButton.Text = "停止";
        _stopButton.AutoSize = true;
        _stopButton.Click += (_, _) => StopPlayback();
        toolbar.Controls.Add(_stopButton);

        _nextButton.Text = "前进一句";
        _nextButton.AutoSize = true;
        _nextButton.Click += (_, _) => MoveBySentence(1);
        toolbar.Controls.Add(_nextButton);
        root.Controls.Add(toolbar, 0, 0);

        _editor.Dock = DockStyle.Fill;
        _editor.Multiline = true;
        _editor.AcceptsReturn = true;
        _editor.AcceptsTab = true;
        _editor.ScrollBars = ScrollBars.Both;
        _editor.WordWrap = true;
        _editor.Font = new Font("Microsoft YaHei UI", 11F);
        _editor.HideSelection = false;
        _editor.TextChanged += EditorOnTextChanged;
        root.Controls.Add(_editor, 0, 1);

        var progressPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 2,
            Padding = new Padding(0, 8, 0, 0)
        };
        progressPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        progressPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _progressBar.Dock = DockStyle.Fill;
        _progressBar.Maximum = 1000;
        _progressBar.Height = 18;
        _progressLabel.AutoSize = true;
        _progressLabel.Margin = new Padding(10, 2, 0, 0);
        progressPanel.Controls.Add(_progressBar, 0, 0);
        progressPanel.Controls.Add(_progressLabel, 1, 0);
        root.Controls.Add(progressPanel, 0, 2);

        var footer = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            WrapContents = true,
            Padding = new Padding(0, 8, 0, 0)
        };
        footer.Controls.Add(new Label
        {
            Text = "本地语音：",
            AutoSize = true,
            Margin = new Padding(3, 8, 0, 0)
        });
        _voiceBox.DropDownStyle = ComboBoxStyle.DropDownList;
        _voiceBox.Width = 230;
        _voiceBox.SelectedIndexChanged += VoiceSelectionChanged;
        footer.Controls.Add(_voiceBox);
        footer.Controls.Add(new Label
        {
            Text = "语速（-10 至 +10）：",
            AutoSize = true,
            Margin = new Padding(12, 8, 0, 0)
        });
        _rateBox.Minimum = -10;
        _rateBox.Maximum = 10;
        _rateBox.Width = 58;
        _rateBox.ValueChanged += (_, _) =>
        {
            if (_reader is not null) _reader.Rate = (int)_rateBox.Value;
            MarkStateDirty();
        };
        footer.Controls.Add(_rateBox);
        _statusLabel.AutoSize = true;
        _statusLabel.MaximumSize = new Size(620, 0);
        _statusLabel.Margin = new Padding(12, 8, 0, 0);
        footer.Controls.Add(_statusLabel);
        root.Controls.Add(footer, 0, 3);
    }

    private static Button MakeButton(string text, EventHandler click)
    {
        var button = new Button
        {
            Text = text,
            AutoSize = true,
            Height = 32,
            Margin = new Padding(3, 0, 3, 3)
        };
        button.Click += click;
        return button;
    }

    private void LoadSavedState()
    {
        var result = _progressStore.Load();
        _restoredState = result.State;
        if (_restoredState is null)
        {
            _statusLabel.Text = result.Warning ?? "欢迎使用。打开 TXT 或粘贴文章即可开始。";
            return;
        }

        var text = _restoredState.Text;
        var position = _restoredState.CharacterPosition;
        _filePath = _restoredState.FilePath;
        _documentId = _restoredState.DocumentId;
        _articleTitle = _restoredState.Title;
        if (!string.IsNullOrWhiteSpace(_filePath))
        {
            if (File.Exists(_filePath))
            {
                try
                {
                    var currentFileText = ReadTextFile(_filePath);
                    if (!_restoredState.MatchesText(currentFileText))
                    {
                        text = currentFileText;
                        position = 0;
                        _statusLabel.Text = "源文件内容已变化，已载入新内容并重置阅读进度。";
                    }
                }
                catch (Exception exception)
                {
                    _statusLabel.Text = "无法重新读取源文件，已使用上次保存的正文：" + exception.Message;
                }
            }
            else
            {
                _statusLabel.Text = "原文件已移动或删除，已使用上次保存的正文副本。";
            }
        }

        _editor.Text = text;
        _rateBox.Value = Math.Clamp(_restoredState.Rate, (int)_rateBox.Minimum, (int)_rateBox.Maximum);
        _restoredState = ReaderState.Create(
            _articleTitle, text, position, _filePath, _restoredState.VoiceName, _restoredState.Rate, _documentId);
        if (string.IsNullOrWhiteSpace(_statusLabel.Text))
            _statusLabel.Text = result.Warning ?? "已恢复上次文章和阅读位置。";
        else if (!string.IsNullOrWhiteSpace(result.Warning))
            _statusLabel.Text += " " + result.Warning;
    }

    private void InitializeSpeech()
    {
        try
        {
            _speechService = new SapiSpeechService(SynchronizationContext.Current);
            _reader = new ReaderController(_speechService, new TextChunker(), queueDepth: 3)
            {
                Rate = (int)_rateBox.Value
            };
            _reader.SetDocument(_editor.Text, _restoredState?.CharacterPosition ?? 0);
            _reader.Changed += ReaderOnChanged;
            InitializeVoices();
        }
        catch (Exception exception)
        {
            _speechService?.Dispose();
            _speechService = null;
            _reader = null;
            _statusLabel.Text = "本地语音初始化失败：" + exception.Message;
        }
    }

    private void InitializeVoices()
    {
        if (_speechService is null) return;
        _initializingVoices = true;
        _voiceBox.Items.Clear();
        foreach (var voice in _speechService.Voices.Where(voice => voice.Enabled))
            _voiceBox.Items.Add(new VoiceItem(voice.Name, voice.Culture));
        if (_voiceBox.Items.Count == 0)
        {
            _statusLabel.Text = "没有发现已启用的本地 SAPI 语音。";
            _initializingVoices = false;
            return;
        }

        var preferredName = _restoredState?.VoiceName ?? string.Empty;
        var items = _voiceBox.Items.Cast<VoiceItem>().ToArray();
        var selected = items.FirstOrDefault(item =>
                           string.Equals(item.Name, preferredName, StringComparison.OrdinalIgnoreCase))
                       ?? items.FirstOrDefault(item =>
                           item.Name.Contains("Microsoft Xiaoxiao", StringComparison.OrdinalIgnoreCase))
                       ?? items.FirstOrDefault(item =>
                           string.Equals(item.Culture, "zh-CN", StringComparison.OrdinalIgnoreCase))
                       ?? items[0];
        _voiceBox.SelectedItem = selected;
        _initializingVoices = false;
        ApplySelectedVoice();
    }

    private void VoiceSelectionChanged(object? sender, EventArgs eventArgs)
    {
        if (_initializingVoices || _loading) return;
        ApplySelectedVoice();
        MarkStateDirty();
    }

    private void ApplySelectedVoice()
    {
        if (_speechService is null || _voiceBox.SelectedItem is not VoiceItem selected) return;
        try
        {
            _speechService.SelectVoice(selected.Name);
            _statusLabel.Text = "已选择本机语音：" + selected.Name + "（" + selected.Culture + "）";
        }
        catch (Exception exception)
        {
            _statusLabel.Text = "无法选择本地语音：" + exception.Message;
        }
    }

    private void EditorOnTextChanged(object? sender, EventArgs eventArgs)
    {
        if (_loading) return;
        if (_reader?.State is PlaybackState.Speaking or PlaybackState.Paused)
            _reader.Stop();
        _documentNeedsSync = true;
        _filePath = null;
        _articleTitle = "手动输入的文章";
        _statusLabel.Text = "文本已修改，阅读进度已重置。";
        MarkStateDirty();
        UpdateProgress(0);
        UpdateControls();
    }

    private void OpenTextFile()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "打开本地文本",
            Filter = "文本文件 (*.txt)|*.txt|所有文件 (*.*)|*.*",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            SetDocument(ReadTextFile(dialog.FileName), dialog.FileName, Path.GetFileNameWithoutExtension(dialog.FileName));
            _statusLabel.Text = "已打开：" + Path.GetFileName(dialog.FileName);
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "打开文件失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static string ReadTextFile(string path)
    {
        using var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private void PasteClipboard()
    {
        try
        {
            if (!Clipboard.ContainsText())
            {
                MessageBox.Show(this, "剪贴板中没有可读取的文本。", "提示",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            SetDocument(Clipboard.GetText(), null, "剪贴板文章");
            _statusLabel.Text = "已粘贴剪贴板文本。";
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, "读取剪贴板失败：" + exception.Message, "错误",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void ClearText()
    {
        SetDocument(string.Empty, null, "未命名文章");
        _statusLabel.Text = "文本已清空。";
    }

    private void SetDocument(
        string text,
        string? filePath,
        string title,
        string? documentId = null,
        int position = 0,
        bool archiveCurrent = true)
    {
        if (archiveCurrent) ArchiveCurrentDocument();
        _loading = true;
        _editor.Text = text;
        _documentId = documentId ?? Guid.NewGuid().ToString("N");
        _loading = false;
        _filePath = filePath;
        _articleTitle = title;
        _documentNeedsSync = false;
        _reader?.SetDocument(text, position);
        MarkStateDirty();
        SaveStateIfDirty(updateHistory: true);
        UpdateProgress();
        UpdateControls();
    }

    private void HistorySelectionChanged(object? sender, EventArgs eventArgs)
    {
        if (_initializingHistory || _loading || _historyBox.SelectedItem is not HistoryItem item) return;
        if (string.Equals(item.DocumentId, _documentId, StringComparison.Ordinal)) return;
        var state = _recentStore.Load(item.DocumentId);
        if (state is null)
        {
            _statusLabel.Text = "所选历史记录已损坏或不存在。";
            RefreshHistory();
            return;
        }

        ArchiveCurrentDocument();
        _rateBox.Value = Math.Clamp(state.Rate, (int)_rateBox.Minimum, (int)_rateBox.Maximum);
        SetDocument(
            state.Text,
            state.FilePath,
            state.Title,
            state.DocumentId,
            state.CharacterPosition,
            archiveCurrent: false);
        SelectVoiceByName(state.VoiceName);
        _statusLabel.Text = "已恢复最近文章：" + state.Title;
    }

    private void SelectVoiceByName(string voiceName)
    {
        var item = _voiceBox.Items.Cast<VoiceItem>().FirstOrDefault(candidate =>
            string.Equals(candidate.Name, voiceName, StringComparison.OrdinalIgnoreCase));
        if (item is not null) _voiceBox.SelectedItem = item;
    }

    private void RefreshHistory()
    {
        _initializingHistory = true;
        _historyBox.Items.Clear();
        foreach (var state in _recentStore.LoadRecent())
            _historyBox.Items.Add(new HistoryItem(state.DocumentId, state.Title, state.UpdatedAt));
        var current = _historyBox.Items.Cast<HistoryItem>().FirstOrDefault(item => item.DocumentId == _documentId);
        if (current is not null) _historyBox.SelectedItem = current;
        _initializingHistory = false;
    }

    private void EnsureDocumentSynchronized()
    {
        if (!_documentNeedsSync) return;
        _reader?.SetDocument(_editor.Text, 0);
        _documentNeedsSync = false;
    }

    private void StartPlayback()
    {
        if (_reader is null)
        {
            MessageBox.Show(this, "本地语音尚未成功初始化。", "无法朗读",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        if (string.IsNullOrWhiteSpace(_editor.Text))
        {
            MessageBox.Show(this, "请先打开 TXT 文件、粘贴或输入需要朗读的文字。", "没有可朗读的文本",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        EnsureDocumentSynchronized();
        _reader.Rate = (int)_rateBox.Value;
        _reader.Play();
    }

    private void PausePlayback()
    {
        _reader?.Pause();
        MarkStateDirty();
        SaveStateIfDirty(updateHistory: true);
    }

    private void ResumePlayback() => _reader?.Resume();

    private void StopPlayback()
    {
        _reader?.Stop();
        MarkStateDirty();
        SaveStateIfDirty(updateHistory: true);
    }

    private void MoveBySentence(int direction)
    {
        if (_reader is null || string.IsNullOrEmpty(_editor.Text)) return;
        EnsureDocumentSynchronized();
        _reader.MoveBySentence(direction);
        MarkStateDirty();
        SaveStateIfDirty(updateHistory: true);
    }

    private void ReaderOnChanged(object? sender, ReaderChangedEventArgs eventArgs) =>
        Ui(() => ApplyReaderChange(eventArgs));

    private void ApplyReaderChange(ReaderChangedEventArgs eventArgs)
    {
        if (_closing) return;
        _statusLabel.Text = eventArgs.State switch
        {
            PlaybackState.Ready => "准备就绪。",
            PlaybackState.Speaking => "正在使用本机语音朗读……",
            PlaybackState.Paused => "已暂停，阅读进度已保存。",
            PlaybackState.Stopped => "已停止，阅读位置已保存。",
            PlaybackState.Completed => "朗读完成。",
            PlaybackState.Error => eventArgs.ErrorMessage ?? "朗读发生错误。",
            _ => _statusLabel.Text
        };
        UpdateProgress(eventArgs.Position);
        HighlightCurrentSentence(eventArgs);
        MarkStateDirty();
        UpdateControls();
    }

    private void HighlightCurrentSentence(ReaderChangedEventArgs eventArgs)
    {
        if (eventArgs.State is not (PlaybackState.Speaking or PlaybackState.Paused)) return;
        var start = Math.Clamp(eventArgs.Sentence.Start, 0, _editor.TextLength);
        var length = Math.Clamp(eventArgs.Sentence.Length, 0, _editor.TextLength - start);
        _editor.SelectionStart = start;
        _editor.SelectionLength = length;
        _editor.ScrollToCaret();
    }

    private void UpdateProgress(int? positionOverride = null)
    {
        var total = _editor.TextLength;
        var current = Math.Clamp(positionOverride ?? (_documentNeedsSync ? 0 : _reader?.Position ?? 0), 0, total);
        var percent = total == 0 ? 0 : (int)Math.Round(current * 100.0 / total);
        _progressBar.Value = total == 0 ? 0 : Math.Clamp((int)Math.Round(current * 1000.0 / total), 0, 1000);
        _progressLabel.Text = $"进度：{current:N0}/{total:N0} 字（{percent}%）";
    }

    private void UpdateControls()
    {
        var state = _reader?.State ?? PlaybackState.Error;
        var hasText = !string.IsNullOrWhiteSpace(_editor.Text);
        var speaking = state == PlaybackState.Speaking;
        var paused = state == PlaybackState.Paused;
        _playButton.Enabled = _reader is not null && hasText && !speaking;
        _pauseButton.Enabled = speaking;
        _resumeButton.Enabled = paused;
        _stopButton.Enabled = speaking || paused;
        _previousButton.Enabled = _reader is not null && hasText && !speaking && !paused;
        _nextButton.Enabled = _reader is not null && hasText && !speaking && !paused;
        _voiceBox.Enabled = _speechService is not null && !speaking && !paused;
        _historyBox.Enabled = !speaking && !paused;
        _rateBox.Enabled = !speaking && !paused;
        _editor.ReadOnly = speaking || paused;
    }

    private void MarkStateDirty()
    {
        if (!_loading && !_closing) _stateDirty = true;
    }

    private ReaderState CreateCurrentState()
    {
        var position = _documentNeedsSync ? 0 : _reader?.Position ?? 0;
        var voiceName = (_voiceBox.SelectedItem as VoiceItem)?.Name ?? _restoredState?.VoiceName ?? string.Empty;
        return ReaderState.Create(
            _articleTitle,
            _editor.Text,
            position,
            _filePath,
            voiceName,
            (int)_rateBox.Value,
            _documentId);
    }

    private void ArchiveCurrentDocument()
    {
        if (string.IsNullOrWhiteSpace(_editor.Text)) return;
        try
        {
            _recentStore.Save(CreateCurrentState());
        }
        catch (Exception exception)
        {
            _statusLabel.Text = "保存最近文章失败：" + exception.Message;
        }
    }

    private void SaveStateIfDirty(bool updateHistory = false)
    {
        if (!_stateDirty || _closing) return;
        try
        {
            var state = CreateCurrentState();
            _progressStore.Save(state);
            if (updateHistory && !string.IsNullOrWhiteSpace(state.Text))
            {
                _recentStore.Save(state);
                RefreshHistory();
            }
            _stateDirty = false;
        }
        catch (Exception exception)
        {
            _statusLabel.Text = "保存阅读进度失败：" + exception.Message;
        }
    }

    private void MainForm_KeyDown(object? sender, KeyEventArgs eventArgs)
    {
        if (eventArgs.KeyCode == Keys.F5)
        {
            if (_reader?.State == PlaybackState.Speaking) PausePlayback();
            else if (_reader?.State == PlaybackState.Paused) ResumePlayback();
            else StartPlayback();
            eventArgs.Handled = true;
        }
        else if (eventArgs.KeyCode == Keys.Escape)
        {
            StopPlayback();
            eventArgs.Handled = true;
        }
        else if (eventArgs.Alt && eventArgs.KeyCode == Keys.Left)
        {
            MoveBySentence(-1);
            eventArgs.Handled = true;
        }
        else if (eventArgs.Alt && eventArgs.KeyCode == Keys.Right)
        {
            MoveBySentence(1);
            eventArgs.Handled = true;
        }
    }

    private void MainForm_FormClosing(object? sender, FormClosingEventArgs eventArgs)
    {
        _saveTimer.Stop();
        _stateDirty = true;
        SaveStateIfDirty(updateHistory: true);
        _closing = true;
        if (_reader is not null)
        {
            _reader.Changed -= ReaderOnChanged;
            _reader.Dispose();
        }
        else
        {
            _speechService?.Dispose();
        }
    }

    private void Ui(Action action)
    {
        if (_closing || IsDisposed || !IsHandleCreated) return;
        if (InvokeRequired)
        {
            try { BeginInvoke(action); }
            catch (InvalidOperationException) { }
            return;
        }
        action();
    }

    private sealed record VoiceItem(string Name, string Culture)
    {
        public override string ToString() => Name + "（" + Culture + "）";
    }

    private sealed record HistoryItem(string DocumentId, string Title, DateTimeOffset UpdatedAt)
    {
        public override string ToString() => Title;
    }
}
