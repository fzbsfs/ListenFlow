using System.Text;
using System.Speech.Synthesis;
using ListenFlow.AIReader;
using ListenFlow.Communication;
using ListenFlow.Core;
using ListenFlow.Diagnostics;
using ListenFlow.Speech;
using ListenFlow.Storage;

namespace ListenFlow;

internal sealed partial class MainForm : Form, IRemotePlaybackTarget
{
    private static readonly string StatePath = Path.Combine(AppPaths.DataDirectory, "progress.json");

    private readonly TextBox _editor = new();
    private readonly ComboBox _voiceBox = new();
    private readonly Button _refreshVoicesButton = new();
    private readonly Button _voiceStatusButton = new();
    private readonly Button _previewVoiceButton = new();
    private readonly Button _speechSettingsButton = new();
    private readonly ComboBox _historyBox = new();
    private readonly NumericUpDown _rateBox = new();
    private readonly Label _statusLabel = new();
    private readonly Label _progressLabel = new();
    private readonly ProgressBar _progressBar = new();
    private readonly Button _playButton = new();
    private readonly CheckBox _followReading = new();
    private readonly Label _articleLabel = new();
    private readonly NumericUpDown _fontSizeBox = new();
    private readonly DesktopPreferences _desktopPreferences = DesktopPreferences.Load();
    private readonly Button _stopButton = new();
    private readonly Button _previousButton = new();
    private readonly Button _nextButton = new();
    private readonly ProgressStore _progressStore = new(StatePath);
    private readonly RecentArticleStore _recentStore = new(Path.Combine(AppPaths.DataDirectory, "history"));
    private readonly System.Windows.Forms.Timer _saveTimer = new() { Interval = 1000 };

    private SapiSpeechService? _speechService;
    private SpeechSynthesizer? _previewSynthesizer;
    private bool _previewStopFailed;
    private ReaderController? _reader;
    private ReaderState? _restoredState;
    private string? _unavailableVoiceName;
    private string? _filePath;
    private string _documentId = Guid.NewGuid().ToString("N");
    private string _articleTitle = "未命名文章";
    private bool _loading = true;
    private bool _initializingVoices;
    private bool _initializingHistory;
    private bool _stateDirty;
    private bool _documentNeedsSync;
    private bool _closing;
    private NotifyIcon? _tray;
    private Icon? _appIcon;
    private FloatingControls? _floating;

    public event EventHandler<ReaderChangedEventArgs>? PlaybackChanged;
    public event EventHandler? LocalPlaybackStarting;

    public MainForm()
    {
        var version = System.Reflection.CustomAttributeExtensions
            .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(typeof(MainForm).Assembly)?
            .InformationalVersion?.Split('+')[0] ?? Application.ProductVersion.Split('+')[0];
        Text = $"ListenFlow · 本地听读助手 · {version}";
        using (var iconStream = typeof(MainForm).Assembly.GetManifestResourceStream("ListenFlow.AppIcon.ico"))
        {
            if (iconStream is not null)
            {
                using var icon = new Icon(iconStream);
                _appIcon = (Icon)icon.Clone();
                Icon = _appIcon;
            }
        }
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
        Shown += (_, _) => { ApplyResponsiveMetrics(); InitializeDesktopControls(); InitializeDesktopFollow(); };
        Disposed += (_, _) => { CloseDesktopFollow(); ReleaseDesktopControls(); };
    }

    private void InitializeDesktopControls()
    {
        if (_tray is not null) return;
        _floating = new FloatingControls(
            TogglePlayback,
            StopPlayback,
            () => ((IRemotePlaybackTarget)this).SetRate((int)_rateBox.Value - 1),
            () => ((IRemotePlaybackTarget)this).SetRate((int)_rateBox.Value + 1),
            ShowMainWindow,
            ShowFloatingReadingActions,
            () => MoveBySentence(-1),
            () => MoveBySentence(1),
            _desktopPreferences);
        var menu = new ContextMenuStrip();
        ListenFlow.UI.ComicTheme.StyleContextMenu(menu);
        menu.Items.Add("显示主窗口", null, (_, _) => ShowMainWindow());
        menu.Items.Add("显示浮动控制条", null, (_, _) => ShowFloatingControls());
        menu.Items.Add("暂停或继续", null, (_, _) => { if (_reader?.State == PlaybackState.Speaking) PausePlayback(); else if (_reader?.State == PlaybackState.Paused) ResumePlayback(); });
        menu.Items.Add("停止朗读", null, (_, _) => StopPlayback());
        menu.Items.Add("退出程序", null, (_, _) => Close());
        _tray = new NotifyIcon { Icon = _appIcon ?? SystemIcons.Application, Text = "ListenFlow 本地听读助手", ContextMenuStrip = menu, Visible = true };
        _tray.DoubleClick += (_, _) => ShowMainWindow();
    }

    private void ShowMainWindow()
    {
        Show();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
    }

    private void ShowFloatingControls()
    {
        InitializeDesktopControls();
        if (_floating is null || _floating.IsDisposed) return;
        UpdateFloatingControls();
        _floating.Show();
    }

    private void ReleaseDesktopControls()
    {
        if (_tray is not null) { _tray.Visible = false; _tray.ContextMenuStrip?.Dispose(); _tray.Dispose(); _tray = null; }
        if (_floating is not null) { _floating.CloseForExit(); _floating.Dispose(); _floating = null; }
        _appIcon?.Dispose();
        _appIcon = null;
    }

    private void BuildUi()
    {
        BuildComicUi();
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
        UpdateArticleCaption();
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
            _speechService = new SapiSpeechService(SynchronizationContext.Current, AppLog.WriteEvent);
            _reader = new ReaderController(
                _speechService,
                new TextChunker(),
                queueDepth: 3,
                stateContext: SynchronizationContext.Current,
                diagnostic: AppLog.WriteEvent)
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
        foreach (var voice in _speechService.Voices.Where(voice => voice.Enabled && !voice.IsKnownOnline))
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
                           string.Equals(item.Culture, "zh-CN", StringComparison.OrdinalIgnoreCase))
                       ?? items[0];
        _unavailableVoiceName = !string.IsNullOrWhiteSpace(preferredName) &&
            !items.Any(item => string.Equals(item.Name, preferredName, StringComparison.OrdinalIgnoreCase))
            ? preferredName : null;
        _voiceBox.SelectedItem = selected;
        _initializingVoices = false;
        ApplySelectedVoice(preserveUnavailable: _unavailableVoiceName is not null);
        if (_unavailableVoiceName is not null)
            _statusLabel.Text = "上次选择的语音不可用：" + _unavailableVoiceName + "；当前使用备用语音。";
    }

    private void VoiceSelectionChanged(object? sender, EventArgs eventArgs)
    {
        if (_initializingVoices || _loading) return;
        ApplySelectedVoice();
        MarkStateDirty();
    }

    private void VoiceSelectionCommitted(object? sender, EventArgs eventArgs)
    {
        if (_initializingVoices || _loading || _unavailableVoiceName is null) return;
        ApplySelectedVoice();
    }

    private void ApplySelectedVoice(bool preserveUnavailable = false)
    {
        if (_speechService is null || _voiceBox.SelectedItem is not VoiceItem selected) return;
        try
        {
            if (!StopVoicePreview()) throw new InvalidOperationException("无法确认试听已停止，请重启程序。");
            _reader?.SelectVoice(selected.Name);
            if (!preserveUnavailable) _unavailableVoiceName = null;
            _statusLabel.Text = "已选择本机语音：" + selected.Name + "（" + selected.Culture + "）";
            MarkStateDirty();
            SaveStateIfDirty(updateHistory: true);
        }
        catch (Exception exception)
        {
            _initializingVoices = true;
            SelectVoiceByName(_speechService.SelectedVoiceName ?? string.Empty);
            _initializingVoices = false;
            _statusLabel.Text = "无法切换本地语音：" + exception.Message;
        }
    }

    private void RefreshVoices()
    {
        if (_speechService is null) return;
        try
        {
            var selectedName = _unavailableVoiceName ?? _speechService.SelectedVoiceName;
            if (!StopVoicePreview()) throw new InvalidOperationException("无法确认试听已停止，请重启程序。");
            var voices = _speechService.RefreshVoices();
            _initializingVoices = true;
            _voiceBox.Items.Clear();
            foreach (var voice in voices.Where(voice => voice.Enabled && !voice.IsKnownOnline))
                _voiceBox.Items.Add(new VoiceItem(voice.Name, voice.Culture));
            SelectVoiceByName(selectedName ?? string.Empty);
            var missing = !string.IsNullOrWhiteSpace(selectedName) && _voiceBox.SelectedItem is null;
            if (_voiceBox.SelectedItem is null && _voiceBox.Items.Count > 0)
                _voiceBox.SelectedIndex = 0;
            _initializingVoices = false;
            if (missing) _unavailableVoiceName = selectedName;
            if (_voiceBox.SelectedItem is VoiceItem selected &&
                (missing || !string.Equals(_speechService.SelectedVoiceName, selected.Name, StringComparison.OrdinalIgnoreCase)))
                ApplySelectedVoice(preserveUnavailable: missing);
            else if (!missing) _unavailableVoiceName = null;
            _statusLabel.Text = missing
                ? $"原语音不可用或已禁用；已刷新语音列表，发现 {_voiceBox.Items.Count} 个启用的语音。"
                : $"已刷新语音列表，发现 {voices.Count} 个语音名称，其中 {_voiceBox.Items.Count} 个可选择；离线能力需实测。";
        }
        catch (Exception exception)
        {
            _initializingVoices = false;
            _statusLabel.Text = "刷新本地语音失败：" + exception.Message;
        }
    }

    private void ShowVoiceStatus()
    {
        if (_speechService is null) return;
        var details = string.Join(Environment.NewLine + Environment.NewLine,
            _speechService.Voices.Select(voice =>
                $"{voice.Name}（{voice.Culture}）：{voice.StatusDescription}"));
        using var dialog = new Form
        {
            Text = "语音状态",
            StartPosition = FormStartPosition.CenterParent,
            Size = new Size(700, 460),
            MinimizeBox = false,
            MaximizeBox = false,
            Font = Font,
            BackColor = ListenFlow.UI.ComicTheme.Paper,
            ForeColor = ListenFlow.UI.ComicTheme.TextPrimary
        };
        dialog.Controls.Add(new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            BackColor = ListenFlow.UI.ComicTheme.Paper,
            ForeColor = ListenFlow.UI.ComicTheme.TextPrimary,
            BorderStyle = BorderStyle.FixedSingle,
            Text = "仅列出当前程序通过系统语音接口发现的名称；同名项按保守规则合并。" +
                   "语音可用不代表已验证离线，请关闭适配器在线语音并断网测试。" +
                   Environment.NewLine + Environment.NewLine + details
        });
        dialog.ShowDialog(this);
    }

    private void PreviewSelectedVoice()
    {
        if (_previewSynthesizer is not null)
        {
            if (StopVoicePreview()) _statusLabel.Text = "已停止语音试听。";
            return;
        }
        if (_reader?.State is PlaybackState.Speaking or PlaybackState.Paused)
        {
            _statusLabel.Text = "请先停止正文朗读，再试听语音。";
            return;
        }
        if (_voiceBox.SelectedItem is not VoiceItem selected) return;
        try
        {
            if (!StopVoicePreview()) return;
            _previewSynthesizer = new SpeechSynthesizer();
            _previewSynthesizer.SetOutputToDefaultAudioDevice();
            _previewSynthesizer.SelectVoice(selected.Name);
            _previewSynthesizer.Rate = Math.Clamp((int)_rateBox.Value, -10, 10);
            var preview = _previewSynthesizer;
            preview.SpeakCompleted += (_, eventArgs) => Ui(() => CompleteVoicePreview(preview, eventArgs.Error));
            _previewVoiceButton.Text = "停止试听";
            _previewSynthesizer.SpeakAsync(selected.Culture.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
                ? "你好，欢迎使用 ListenFlow。本地语音试听。"
                : "Hello. Welcome to ListenFlow. This is a local voice preview.");
        }
        catch (Exception exception)
        {
            if (StopVoicePreview())
                _statusLabel.Text = "语音试听失败：" + exception.Message;
        }
    }

    private void CompleteVoicePreview(SpeechSynthesizer preview, Exception? error)
    {
        if (!ReferenceEquals(_previewSynthesizer, preview)) return;
        try { preview.Dispose(); }
        catch (Exception exception)
        {
            _previewStopFailed = true;
            _statusLabel.Text = "无法确认试听资源已释放，请重启程序：" + exception.Message;
            return;
        }
        _previewSynthesizer = null;
        _previewVoiceButton.Text = "试听";
        if (error is not null)
            _statusLabel.Text = "语音试听失败：" + error.Message;
    }

    private bool StopVoicePreview()
    {
        var preview = _previewSynthesizer;
        if (preview is null) return !_previewStopFailed;
        try
        {
            preview.SpeakAsyncCancelAll();
            preview.Dispose();
        }
        catch (Exception exception)
        {
            _previewStopFailed = true;
            _statusLabel.Text = "无法确认试听已停止，请重启程序：" + exception.Message;
            return false;
        }
        _previewSynthesizer = null;
        _previewVoiceButton.Text = "试听";
        return !_previewStopFailed;
    }

    private void OpenSpeechSettings()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-settings:speech")
            {
                UseShellExecute = true
            });
        }
        catch (Exception exception)
        {
            _statusLabel.Text = "无法打开 Windows 语音设置：" + exception.Message;
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

    internal bool StartClipboardReading(Func<string> readText)
    {
        var result = DesktopClipboardConnector.Read(readText);
        if (!result.Success)
        {
            _statusLabel.Text = result.Error;
            return false;
        }
        if (_reader is null)
        {
            _statusLabel.Text = "本地语音尚未成功初始化，无法朗读剪贴板。";
            return false;
        }
        if (!StopVoicePreview()) return false;
        _reader.Stop();
        if (_reader.State != PlaybackState.Stopped)
        {
            _statusLabel.Text = _reader.ErrorMessage ?? "无法确认原朗读已经停止。";
            return false;
        }
        LocalPlaybackStarting?.Invoke(this, EventArgs.Empty);
        SetDocument(result.Text!, null, "剪贴板文章");
        StartPlayback();
        return _reader.State is PlaybackState.Speaking or PlaybackState.Completed;
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
        bool archiveCurrent = true,
        bool deferSave = false)
    {
        ClearDesktopFollow();
        if (!_changingQueueDocument)
        {
            _returnArticle = null;
            _queueAutoAdvance = false;
        }
        if (archiveCurrent) ArchiveCurrentDocument();
        _unavailableVoiceName = null;
        _loading = true;
        _editor.Text = text;
        _documentId = documentId ?? Guid.NewGuid().ToString("N");
        _loading = false;
        _filePath = filePath;
        _articleTitle = title;
        UpdateArticleCaption();
        _documentNeedsSync = false;
        _reader?.SetDocument(text, position);
        MarkStateDirty();
        if (!deferSave) SaveStateIfDirty(updateHistory: true);
        UpdateProgress();
        UpdateControls();
        if (!string.IsNullOrWhiteSpace(text))
            _mascot.NotifyArticleSwitched(_articleTitle);
        _paperPanel.SetActiveSentence(position > 0 ? position : -1);
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
            archiveCurrent: false,
            deferSave: true);
        var voiceAvailable = _voiceBox.Items.Cast<VoiceItem>().Any(candidate =>
            string.Equals(candidate.Name, state.VoiceName, StringComparison.OrdinalIgnoreCase));
        if (voiceAvailable)
        {
            _initializingVoices = true;
            SelectVoiceByName(state.VoiceName);
            _initializingVoices = false;
            ApplySelectedVoice();
            _statusLabel.Text = "已恢复最近文章：" + state.Title;
        }
        else
        {
            _unavailableVoiceName = string.IsNullOrWhiteSpace(state.VoiceName) ? null : state.VoiceName;
            _statusLabel.Text = "文章已恢复，但保存的语音不可用：" + state.VoiceName;
        }
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
            _historyBox.Items.Add(new HistoryItem(state.DocumentId, state.Title, state.UpdatedAt,
                state.Text.Length == 0 ? 0 : (int)Math.Clamp(100L * state.CharacterPosition / state.Text.Length, 0, 100)));
        var current = _historyBox.Items.Cast<HistoryItem>().FirstOrDefault(item => item.DocumentId == _documentId);
        if (current is not null) _historyBox.SelectedItem = current;
        _initializingHistory = false;
        SyncHistoryList();
    }

    private void EnsureDocumentSynchronized()
    {
        if (!_documentNeedsSync) return;
        _reader?.SetDocument(_editor.Text, 0);
        _documentNeedsSync = false;
    }

    private void StartPlayback()
    {
        if (!StopVoicePreview()) return;
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
        _queueAutoAdvance = _reader.State is PlaybackState.Speaking or PlaybackState.Completed;
    }

    private void TogglePlayback()
    {
        if (_reader?.State == PlaybackState.Speaking) PausePlayback();
        else if (_reader?.State == PlaybackState.Paused) ResumePlayback();
        else StartPlayback();
    }

    private void UpdateFloatingControls() => _floating?.SetState(
        _reader?.State ?? PlaybackState.Ready, (int)_rateBox.Value,
        _articleTitle, _reader is not null && !string.IsNullOrWhiteSpace(_editor.Text));

    private void ApplyPlaybackRate()
    {
        if (_reader is null) return;
        var rate = (int)_rateBox.Value;
        if (_reader.Rate == rate) return;
        _reader.Rate = rate;
        // 定位使用原有播放契约，暂停时清空旧速率队列但不提前发声。
        if (_reader.State is PlaybackState.Speaking or PlaybackState.Paused)
            _reader.SeekTo(_reader.Position);
        UpdateFloatingControls();
    }

    private void PausePlayback()
    {
        _reader?.Pause();
        MarkStateDirty();
        SaveStateIfDirty(updateHistory: true);
    }

    private void ResumePlayback()
    {
        if (!StopVoicePreview()) return;
        _queueAutoAdvance = true;
        _reader?.Resume();
    }

    private void StopPlayback()
    {
        _queueAutoAdvance = false;
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

    private void RepeatCurrentSentence()
    {
        if (_reader is null || string.IsNullOrWhiteSpace(_editor.Text)) return;
        EnsureDocumentSynchronized();
        var start = _reader.CurrentSentence.Start;
        if (_reader.State is PlaybackState.Speaking or PlaybackState.Paused)
            _reader.SeekTo(start);
        else
            _reader.SeekAndPlay(start);
        _queueAutoAdvance = _reader.State is PlaybackState.Speaking or PlaybackState.Completed;
        MarkStateDirty();
        SaveStateIfDirty(updateHistory: true);
    }

    private void ConfigureEditorReadingActions()
    {
        var menu = new ContextMenuStrip();
        var fromSelection = menu.Items.Add("从此处朗读", null, (_, _) => StartFromEditorSelection());
        var readSelection = menu.Items.Add("朗读所选文字并返回原文", null, (_, _) => InterruptWithText(_editor.SelectedText));
        var enqueueSelection = menu.Items.Add("所选文字加入待听", null, (_, _) => EnqueueText(_editor.SelectedText));
        menu.Items.Add(new ToolStripSeparator());
        var copy = menu.Items.Add("复制", null, (_, _) => _editor.Copy());
        var cut = menu.Items.Add("剪切", null, (_, _) => _editor.Cut());
        var paste = menu.Items.Add("粘贴", null, (_, _) => _editor.Paste());
        var undo = menu.Items.Add("撤销", null, (_, _) => _editor.Undo());
        menu.Items.Add("全选", null, (_, _) => _editor.SelectAll());
        menu.Opening += (_, _) =>
        {
            _followReading.Checked = false;
            var selected = !string.IsNullOrWhiteSpace(_editor.SelectedText);
            fromSelection.Enabled = _reader is not null && !string.IsNullOrWhiteSpace(_editor.Text);
            readSelection.Enabled = selected && !IsWebDocument && !IsDesktopFollowDocument;
            enqueueSelection.Enabled = selected && !IsDesktopFollowDocument;
            copy.Enabled = _editor.SelectionLength > 0;
            cut.Enabled = !_editor.ReadOnly && _editor.SelectionLength > 0;
            paste.Enabled = !_editor.ReadOnly;
            undo.Enabled = !_editor.ReadOnly && _editor.CanUndo;
        };
        _editor.ContextMenuStrip = menu;
        _editor.Disposed += (_, _) => menu.Dispose();
    }

    private void StartFromEditorSelection()
    {
        if (_reader is null || string.IsNullOrWhiteSpace(_editor.Text) || !StopVoicePreview()) return;
        var offset = _editor.SelectionStart;
        if (offset > 0 && offset < _editor.TextLength && char.IsLowSurrogate(_editor.Text[offset]) &&
            char.IsHighSurrogate(_editor.Text[offset - 1])) offset--;
        EnsureDocumentSynchronized();
        _reader.Rate = (int)_rateBox.Value;
        _reader.SeekAndPlay(offset);
        _queueAutoAdvance = _reader.State is PlaybackState.Speaking or PlaybackState.Completed;
        MarkStateDirty();
        SaveStateIfDirty(updateHistory: true);
    }

    private void ShowFloatingReadingActions()
    {
        if (_floating is null || _floating.IsDisposed) return;
        var menu = new ContextMenuStrip();
        ListenFlow.UI.ComicTheme.StyleContextMenu(menu);
        menu.Items.Add("立即朗读剪贴板", null, (_, _) => StartClipboardReading(Clipboard.GetText));
        menu.Items.Add("插播剪贴板并返回原文", null, (_, _) => InterruptWithClipboard()).Enabled = !IsWebDocument;
        menu.Items.Add("剪贴板加入待听", null, (_, _) => EnqueueClipboard());
        menu.Items.Add("管理待听队列", null, (_, _) => ShowReadingQueue());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("重听当前句", null, (_, _) => RepeatCurrentSentence()).Enabled =
            _reader is not null && !string.IsNullOrWhiteSpace(_editor.Text);
        menu.Items.Add("返回插播前文章", null, (_, _) => RestoreInterruptedArticle()).Enabled = _returnArticle is not null;
        menu.Closed += (_, _) => menu.Dispose();
        menu.Show(_floating, new Point(0, _floating.ClientSize.Height));
    }

    private void ReaderOnChanged(object? sender, ReaderChangedEventArgs eventArgs) =>
        Ui(() => ApplyReaderChange(eventArgs));

    private void ApplyReaderChange(ReaderChangedEventArgs eventArgs)
    {
        if (_closing) return;
        UpdateFloatingControls();
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
        _mascot.SyncPlaybackState(eventArgs.State, GetCurrentPercent(eventArgs.Position), (int)_rateBox.Value);
        UpdateDesktopFollow(eventArgs);
        PlaybackChanged?.Invoke(this, eventArgs);
        if (!IsDesktopFollowDocument) ScheduleNextReading(eventArgs);
    }

    private void HighlightCurrentSentence(ReaderChangedEventArgs eventArgs)
    {
        if (eventArgs.State is not (PlaybackState.Speaking or PlaybackState.Paused))
        {
            _paperPanel.SetActiveSentence(-1);
            return;
        }
        var start = Math.Clamp(eventArgs.Sentence.Start, 0, _editor.TextLength);
        var length = Math.Clamp(eventArgs.Sentence.Length, 0, _editor.TextLength - start);
        _paperPanel.SetActiveSentence(length > 0 ? start : -1);
        if (!_followReading.Checked) return;
        _editor.SelectionStart = start;
        _editor.SelectionLength = length;
        _editor.ScrollToCaret();
    }

    private void ScrollToCurrentSentence()
    {
        if (_reader is null || _documentNeedsSync) return;
        var sentence = _reader.CurrentSentence;
        var start = Math.Clamp(sentence.Start, 0, _editor.TextLength);
        var length = Math.Clamp(sentence.Length, 0, _editor.TextLength - start);
        _paperPanel.SetActiveSentence(length > 0 ? start : -1);
        _editor.Select(start, length);
        _editor.ScrollToCaret();
    }

    private void UpdateProgress(int? positionOverride = null)
    {
        var total = _editor.TextLength;
        var current = Math.Clamp(positionOverride ?? (_documentNeedsSync ? 0 : _reader?.Position ?? 0), 0, total);
        var percent = total == 0 ? 0 : (int)Math.Round(current * 100.0 / total);
        _progressBar.Value = total == 0 ? 0 : Math.Clamp((int)Math.Round(current * 1000.0 / total), 0, 1000);
        _progressLabel.Text = $"进度：{current:N0}/{total:N0} 字（{percent}%）";
        RefreshComicHero(current);
    }

    private void UpdateControls()
    {
        UpdateArticleCaption();
        var state = _reader?.State ?? PlaybackState.Error;
        var hasText = !string.IsNullOrWhiteSpace(_editor.Text);
        var speaking = state == PlaybackState.Speaking;
        var paused = state == PlaybackState.Paused;
        _playButton.Enabled = _reader is not null && hasText;
        _playButton.Text = speaking ? "暂停" : paused ? "继续" : "播放";
        _stopButton.Enabled = speaking || paused;
        _previousButton.Enabled = _reader is not null && hasText;
        _nextButton.Enabled = _reader is not null && hasText;
        _voiceBox.Enabled = _speechService is not null;
        _refreshVoicesButton.Enabled = _speechService is not null && !speaking && !paused;
        _voiceStatusButton.Enabled = _speechService is not null;
        _previewVoiceButton.Enabled = _speechService is not null && !speaking && !paused;
        _speechSettingsButton.Enabled = true;
        _historyBox.Enabled = !speaking && !paused;
        _historyList.Enabled = !speaking && !paused;
        _rateBox.Enabled = _reader is not null;
        _editor.ReadOnly = speaking || paused || IsDesktopFollowDocument;
        RefreshComicUi();
    }

    private void UpdateArticleCaption()
    {
        var source = _documentId.StartsWith("web:", StringComparison.Ordinal) ? "网页" : _filePath is not null ? "文件" : "文本";
        _articleLabel.Text = string.IsNullOrWhiteSpace(_editor.Text)
            ? "打开文章或复制文字，开始本地听读"
            : $"{source} · {_articleTitle}";
    }

    private void MarkStateDirty()
    {
        if (!_loading && !_closing) _stateDirty = true;
    }

    private ReaderState CreateCurrentState()
    {
        var position = _documentNeedsSync ? 0 : _reader?.Position ?? 0;
        var voiceName = _unavailableVoiceName ?? (_voiceBox.SelectedItem as VoiceItem)?.Name ?? _restoredState?.VoiceName ?? string.Empty;
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
        if (IsDesktopFollowDocument || string.IsNullOrWhiteSpace(_editor.Text)) return;
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
        if (!_stateDirty || _closing || IsDesktopFollowDocument) return;
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
            if (!ClosePopovers())
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
        CloseDesktopFollow();
        ReleaseDesktopControls();
        StopVoicePreview();
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
            try
            {
                BeginInvoke((Action)(() =>
                {
                    if (!_closing && !IsDisposed && IsHandleCreated) action();
                }));
            }
            catch (InvalidOperationException) { }
            return;
        }
        action();
    }

    int IRemotePlaybackTarget.TextLength => _editor.TextLength;
    string IRemotePlaybackTarget.Text => _editor.Text;
    PlaybackState IRemotePlaybackTarget.PlaybackState => _reader?.State ?? PlaybackState.Error;
    long IRemotePlaybackTarget.PlaybackGeneration => _reader?.Generation ?? 0;

    void IRemotePlaybackTarget.Start(RemoteStartRequest request)
    {
        if (!StopVoicePreview()) throw new InvalidOperationException("无法确认试听已停止，请重启程序。");
        if (_reader is null || _speechService is null)
            throw new InvalidOperationException("本地语音尚未成功初始化。");
        if (string.IsNullOrWhiteSpace(request.Text))
            throw new InvalidOperationException("网页正文为空。");

        if (!string.IsNullOrWhiteSpace(request.VoiceName))
        {
            var voice = _voiceBox.Items.Cast<VoiceItem>().FirstOrDefault(candidate =>
                string.Equals(candidate.Name, request.VoiceName, StringComparison.OrdinalIgnoreCase));
            if (voice is null)
                throw new InvalidOperationException("本机不存在指定语音：" + request.VoiceName);
            _voiceBox.SelectedItem = voice;
            _reader.SelectVoice(voice.Name);
        }

        if (request.Rate is not null)
            _rateBox.Value = Math.Clamp(request.Rate.Value, (int)_rateBox.Minimum, (int)_rateBox.Maximum);

        SetDocument(
            request.Text,
            filePath: null,
            request.Title,
            documentId: $"web:{request.SessionId}:{request.SnapshotId}:{request.SnapshotVersion}",
            position: request.Offset);
        _reader.Rate = (int)_rateBox.Value;
        _reader.SeekAndPlay(request.Offset);
        ShowFloatingControls();
    }

    void IRemotePlaybackTarget.Pause() => PausePlayback();
    void IRemotePlaybackTarget.Resume() => ResumePlayback();
    void IRemotePlaybackTarget.Stop() => StopPlayback();

    void IRemotePlaybackTarget.Seek(int offset)
    {
        if (_reader is null)
            throw new InvalidOperationException("本地语音尚未成功初始化。");
        EnsureDocumentSynchronized();
        _reader.SeekTo(offset);
        MarkStateDirty();
        SaveStateIfDirty(updateHistory: true);
    }

    void IRemotePlaybackTarget.SetRate(int rate)
    {
        if (_reader is null)
            throw new InvalidOperationException("本地语音尚未成功初始化。");
        var clamped = Math.Clamp(rate, (int)_rateBox.Minimum, (int)_rateBox.Maximum);
        _rateBox.Value = clamped;
        ApplyPlaybackRate();
        MarkStateDirty();
        SaveStateIfDirty(updateHistory: true);
    }

    private sealed record VoiceItem(string Name, string Culture)
    {
        public override string ToString() => Name + "（" + Culture + "）";
    }

    private sealed record HistoryItem(string DocumentId, string Title, DateTimeOffset UpdatedAt, int Percent)
    {
        public string DisplayText => $"{Title} · {Percent}% · {UpdatedAt.ToLocalTime():MM-dd}";
        public override string ToString() => Title;
    }
}
