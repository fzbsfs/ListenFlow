using System.Speech.Synthesis;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;

namespace ListenFlow;

internal sealed class MainForm : Form
{
    private readonly SpeechSynthesizer _speech = new();
    private readonly TextBox _editor = new();
    private readonly ComboBox _voiceBox = new();
    private readonly NumericUpDown _rateBox = new();
    private readonly Label _statusLabel = new();
    private readonly Label _progressLabel = new();
    private readonly Button _playButton = new();
    private readonly Button _pauseButton = new();
    private readonly Button _resumeButton = new();
    private readonly Button _stopButton = new();

    private readonly string _statePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ListenFlow", "progress.json");

    private List<TextChunk> _chunks = new();
    private string? _filePath;
    private string _preferredVoice = "";
    private int _characterPosition;
    private int _playStartOffset;
    private int _currentChunkIndex;
    private bool _loadingState = true;
    private bool _initializingVoices = true;
    private bool _isSpeaking;
    private bool _isPaused;
    private bool _userStopped;
    private bool _cancelPending;
    private bool _closing;

    private static readonly char[] ChunkBreakChars =
        ['。', '！', '？', '；', '.', '!', '?', ';', '\n', ' '];

    public MainForm()
    {
        Text = "ListenFlow · 本地听读助手";
        Width = 1050;
        Height = 720;
        MinimumSize = new Size(760, 500);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Microsoft YaHei UI", 9F);
        BuildUi();

        LoadSavedState();
        InitializeVoices();

        _speech.SetOutputToDefaultAudioDevice();
        _speech.SpeakProgress += SpeechOnSpeakProgress;
        _speech.SpeakCompleted += SpeechOnSpeakCompleted;

        _loadingState = false;
        UpdateProgressLabel();
        UpdateControls();

        FormClosing += MainForm_FormClosing;
    }

    private void BuildUi()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(10),
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Controls.Add(root);

        var toolbar = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            WrapContents = true,
            Padding = new Padding(0, 0, 0, 8),
        };

        toolbar.Controls.Add(MakeButton("打开 TXT", (_, _) => OpenTextFile()));
        toolbar.Controls.Add(MakeButton("粘贴剪贴板", (_, _) => PasteClipboard()));
        toolbar.Controls.Add(MakeButton("清空", (_, _) => ClearText()));

        _playButton.Text = "从当前位置播放";
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

        root.Controls.Add(toolbar, 0, 0);

        _editor.Dock = DockStyle.Fill;
        _editor.Multiline = true;
        _editor.AcceptsReturn = true;
        _editor.AcceptsTab = true;
        _editor.ScrollBars = ScrollBars.Both;
        _editor.WordWrap = true;
        _editor.Font = new Font("Microsoft YaHei UI", 11F);
        _editor.HideSelection = false;
        _editor.TextChanged += (_, _) =>
        {
            if (_loadingState) return;
            if (_isSpeaking) StopPlayback();
            _characterPosition = 0;
            _chunks.Clear();
            _statusLabel.Text = "文本已修改；播放进度已重置。";
            UpdateProgressLabel();
        };
        root.Controls.Add(_editor, 0, 1);

        var footer = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            WrapContents = true,
            Padding = new Padding(0, 8, 0, 0),
        };

        footer.Controls.Add(new Label
        {
            Text = "本地语音：",
            AutoSize = true,
            Margin = new Padding(3, 8, 0, 0),
        });

        _voiceBox.DropDownStyle = ComboBoxStyle.DropDownList;
        _voiceBox.Width = 220;
        _voiceBox.SelectedIndexChanged += (_, _) => VoiceSelectionChanged();
        footer.Controls.Add(_voiceBox);

        footer.Controls.Add(new Label
        {
            Text = "语速（-10 慢 / +10 快）：",
            AutoSize = true,
            Margin = new Padding(12, 8, 0, 0),
        });

        _rateBox.Minimum = -10;
        _rateBox.Maximum = 10;
        _rateBox.Value = 0;
        _rateBox.Width = 58;
        _rateBox.ValueChanged += (_, _) =>
        {
            if (!_loadingState) SaveState();
        };
        footer.Controls.Add(_rateBox);

        _progressLabel.AutoSize = true;
        _progressLabel.Margin = new Padding(12, 8, 0, 0);
        footer.Controls.Add(_progressLabel);

        _statusLabel.AutoSize = true;
        _statusLabel.Margin = new Padding(3, 8, 0, 0);
        footer.Controls.Add(_statusLabel);

        root.Controls.Add(footer, 0, 2);
    }

    private static Button MakeButton(string text, EventHandler click)
    {
        var button = new Button
        {
            Text = text,
            AutoSize = true,
            Height = 32,
            Margin = new Padding(3, 0, 3, 3),
        };
        button.Click += click;
        return button;
    }

    private void InitializeVoices()
    {
        _initializingVoices = true;
        _voiceBox.Items.Clear();

        try
        {
            foreach (InstalledVoice voice in _speech.GetInstalledVoices())
            {
                if (voice.Enabled)
                    _voiceBox.Items.Add(voice.VoiceInfo.Name);
            }
        }
        catch (Exception ex)
        {
            _statusLabel.Text = "读取本地语音失败：" + ex.Message;
        }

        string? preferred = null;
        if (_voiceBox.Items.Count > 0)
        {
            for (int i = 0; i < _voiceBox.Items.Count; i++)
            {
                string name = _voiceBox.Items[i]?.ToString() ?? "";
                if (string.Equals(name, _preferredVoice, StringComparison.OrdinalIgnoreCase))
                {
                    preferred = name;
                    break;
                }
            }

            if (preferred is null)
            {
                for (int i = 0; i < _voiceBox.Items.Count; i++)
                {
                    string name = _voiceBox.Items[i]?.ToString() ?? "";
                    if (name.Contains("Xiaoxiao", StringComparison.OrdinalIgnoreCase))
                    {
                        preferred = name;
                        break;
                    }
                }
            }

            if (preferred is null)
            {
                for (int i = 0; i < _voiceBox.Items.Count; i++)
                {
                    string name = _voiceBox.Items[i]?.ToString() ?? "";
                    if (name.Contains("Huihui", StringComparison.OrdinalIgnoreCase))
                    {
                        preferred = name;
                        break;
                    }
                }
            }

            _voiceBox.SelectedItem = preferred ?? _voiceBox.Items[0];
            ApplySelectedVoice();
        }
        else
        {
            _statusLabel.Text = "没有发现可用的本地 SAPI 语音。";
        }

        _initializingVoices = false;
    }

    private void VoiceSelectionChanged()
    {
        if (_initializingVoices || _voiceBox.SelectedItem is null) return;
        if (_isSpeaking)
        {
            _statusLabel.Text = "请先停止朗读，再切换语音。";
            return;
        }
        ApplySelectedVoice();
        SaveState();
    }

    private void ApplySelectedVoice()
    {
        if (_voiceBox.SelectedItem is null) return;
        string name = _voiceBox.SelectedItem.ToString() ?? "";
        try
        {
            _speech.SelectVoice(name);
            _preferredVoice = name;
            _statusLabel.Text = "已选择本机语音：" + name;
        }
        catch (Exception ex)
        {
            _statusLabel.Text = "无法选择语音：" + ex.Message;
        }
    }

    private void LoadSavedState()
    {
        try
        {
            if (!File.Exists(_statePath))
            {
                _loadingState = false;
                _statusLabel.Text = "欢迎使用。打开 TXT 或粘贴一篇文章即可开始。";
                return;
            }

            var state = JsonSerializer.Deserialize<ReaderState>(File.ReadAllText(_statePath));
            if (state is null)
            {
                _loadingState = false;
                return;
            }

            _loadingState = true;
            _editor.Text = state.Text ?? "";
            _filePath = state.FilePath;
            _characterPosition = Math.Clamp(state.CharacterPosition, 0, _editor.TextLength);
            _preferredVoice = state.VoiceName ?? "";
            _rateBox.Value = Math.Clamp(state.Rate, (int)_rateBox.Minimum, (int)_rateBox.Maximum);
            _statusLabel.Text = _editor.TextLength > 0
                ? "已恢复上次文章和阅读位置。"
                : "欢迎使用。打开 TXT 或粘贴一篇文章即可开始。";
        }
        catch (Exception ex)
        {
            _statusLabel.Text = "恢复上次进度失败：" + ex.Message;
        }
        finally
        {
            _loadingState = false;
        }
    }

    private void OpenTextFile()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "打开本地文本",
            Filter = "文本文件 (*.txt)|*.txt|所有文件 (*.*)|*.*",
            CheckFileExists = true,
        };

        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            StopPlayback();
            _loadingState = true;
            using var reader = new StreamReader(dialog.FileName, Encoding.UTF8, true);
            _editor.Text = reader.ReadToEnd();
            _filePath = dialog.FileName;
            _characterPosition = 0;
            _chunks.Clear();
            _loadingState = false;
            _statusLabel.Text = "已打开：" + Path.GetFileName(dialog.FileName);
            UpdateProgressLabel();
            SaveState();
        }
        catch (Exception ex)
        {
            _loadingState = false;
            MessageBox.Show(this, ex.Message, "打开文件失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void PasteClipboard()
    {
        try
        {
            if (Clipboard.ContainsText())
            {
                _editor.Text = Clipboard.GetText();
                _filePath = null;
                _statusLabel.Text = "已粘贴剪贴板文本。";
            }
            else
            {
                MessageBox.Show(this, "剪贴板中没有可读取的文本。", "提示",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "读取剪贴板失败：" + ex.Message, "错误",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void ClearText()
    {
        StopPlayback();
        _loadingState = true;
        _editor.Clear();
        _loadingState = false;
        _filePath = null;
        _characterPosition = 0;
        _chunks.Clear();
        _statusLabel.Text = "文本已清空。";
        UpdateProgressLabel();
        SaveState();
    }

    private void StartPlayback()
    {
        if (_cancelPending)
        {
            _statusLabel.Text = "正在停止上一段语音，请稍后再试。";
            return;
        }

        string text = _editor.Text;
        if (string.IsNullOrWhiteSpace(text))
        {
            MessageBox.Show(this, "请先打开 TXT 文件或粘贴需要朗读的文字。", "没有可朗读的文本",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (_isPaused)
        {
            ResumePlayback();
            return;
        }

        if (_isSpeaking)
        {
            _statusLabel.Text = "正在朗读。需要暂停时请点击“暂停”。";
            return;
        }

        if (_characterPosition >= text.Length)
            _characterPosition = 0;

        _chunks = BuildChunks(text);
        _currentChunkIndex = _chunks.FindIndex(c => _characterPosition < c.Start + c.Text.Length);
        if (_currentChunkIndex < 0) _currentChunkIndex = 0;

        _userStopped = false;
        _isPaused = false;
        _isSpeaking = true;
        _rateBox.Enabled = false;
        _voiceBox.Enabled = false;
        _statusLabel.Text = "正在使用本机语音朗读……";
        SaveState();
        PlayCurrentChunkFromPosition();
        UpdateControls();
    }

    private void PlayCurrentChunkFromPosition()
    {
        if (_closing || !_isSpeaking || _isPaused) return;

        if (_currentChunkIndex >= _chunks.Count)
        {
            FinishPlayback();
            return;
        }

        var chunk = _chunks[_currentChunkIndex];
        int offset = Math.Clamp(_characterPosition - chunk.Start, 0, chunk.Text.Length);
        string remaining = chunk.Text[offset..];
        if (string.IsNullOrWhiteSpace(remaining))
        {
            _currentChunkIndex++;
            PlayCurrentChunkFromPosition();
            return;
        }

        _playStartOffset = chunk.Start + offset;
        try
        {
            _speech.Rate = (int)_rateBox.Value;
            _speech.Volume = 100;
            _speech.SpeakAsync(remaining);
        }
        catch (Exception ex)
        {
            _isSpeaking = false;
            _statusLabel.Text = "启动朗读失败：" + ex.Message;
            UpdateControls();
        }
    }

    private void PausePlayback()
    {
        if (!_isSpeaking || _isPaused) return;
        try
        {
            _speech.Pause();
            _isPaused = true;
            _statusLabel.Text = "已暂停；阅读进度已保存。";
            SaveState();
            UpdateControls();
        }
        catch (Exception ex)
        {
            _statusLabel.Text = "暂停失败：" + ex.Message;
        }
    }

    private void ResumePlayback()
    {
        if (!_isSpeaking || !_isPaused) return;
        try
        {
            _speech.Resume();
            _isPaused = false;
            _statusLabel.Text = "继续朗读……";
            UpdateControls();
        }
        catch (Exception ex)
        {
            _statusLabel.Text = "继续朗读失败：" + ex.Message;
        }
    }

    private void StopPlayback()
    {
        bool wasSpeaking = _isSpeaking;
        _userStopped = true;
        _isSpeaking = false;
        _isPaused = false;

        if (wasSpeaking)
        {
            _cancelPending = true;
            try { _speech.SpeakAsyncCancelAll(); }
            catch { _cancelPending = false; }
        }

        _statusLabel.Text = "已停止；阅读位置已保存。";
        SaveState();
        UpdateControls();
    }

    private void SpeechOnSpeakProgress(object? sender, SpeakProgressEventArgs e)
    {
        if (_closing) return;
        _characterPosition = Math.Clamp(_playStartOffset + e.CharacterPosition, 0, _editor.TextLength);
        Ui(UpdateProgressLabel);
    }

    private void SpeechOnSpeakCompleted(object? sender, SpeakCompletedEventArgs e)
    {
        if (_closing) return;

        Ui(() =>
        {
            if (_cancelPending)
            {
                _cancelPending = false;
                if (_userStopped)
                {
                    UpdateControls();
                    return;
                }
            }

            if (_userStopped || !_isSpeaking)
            {
                UpdateControls();
                return;
            }

            if (e.Error is not null)
            {
                _isSpeaking = false;
                _isPaused = false;
                _statusLabel.Text = "朗读发生错误：" + e.Error.Message;
                SaveState();
                UpdateControls();
                return;
            }

            if (e.Cancelled)
            {
                _isSpeaking = false;
                _isPaused = false;
                _statusLabel.Text = "朗读已取消。";
                SaveState();
                UpdateControls();
                return;
            }

            if (_currentChunkIndex < _chunks.Count)
                _characterPosition = Math.Min(_editor.TextLength,
                    _chunks[_currentChunkIndex].Start + _chunks[_currentChunkIndex].Text.Length);

            _currentChunkIndex++;
            SaveState();

            if (_currentChunkIndex < _chunks.Count && _isSpeaking && !_isPaused)
            {
                PlayCurrentChunkFromPosition();
            }
            else
            {
                FinishPlayback();
            }

            UpdateProgressLabel();
            UpdateControls();
        });
    }

    private void FinishPlayback()
    {
        _isSpeaking = false;
        _isPaused = false;
        _characterPosition = _editor.TextLength;
        _statusLabel.Text = "朗读完成。";
        SaveState();
        UpdateProgressLabel();
        UpdateControls();
    }

    private static List<TextChunk> BuildChunks(string text)
    {
        const int maxChunkLength = 420;
        var result = new List<TextChunk>();
        int start = 0;

        while (start < text.Length)
        {
            int end = Math.Min(start + maxChunkLength, text.Length);
            if (end < text.Length)
            {
                int candidate = text.LastIndexOfAny(ChunkBreakChars, end - 1, end - start);
                if (candidate > start + 100)
                {
                    end = candidate + 1;
                }
            }

            if (end <= start) end = Math.Min(start + maxChunkLength, text.Length);
            result.Add(new TextChunk(start, text[start..end]));
            start = end;
        }

        return result;
    }

    private void UpdateProgressLabel()
    {
        int total = _editor.TextLength;
        int current = Math.Clamp(_characterPosition, 0, total);
        int percent = total == 0 ? 0 : (int)Math.Round(current * 100.0 / total);
        _progressLabel.Text = $"进度：{current:N0}/{total:N0} 字（{percent}%）";
    }

    private void UpdateControls()
    {
        if (_closing) return;
        _playButton.Enabled = !_isSpeaking && !_cancelPending;
        _pauseButton.Enabled = _isSpeaking && !_isPaused;
        _resumeButton.Enabled = _isSpeaking && _isPaused;
        _stopButton.Enabled = _isSpeaking || _isPaused;
        _voiceBox.Enabled = !_isSpeaking;
        _rateBox.Enabled = !_isSpeaking;
    }

    private void SaveState()
    {
        if (_closing) return;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
            var state = new ReaderState
            {
                Text = _editor.Text,
                FilePath = _filePath,
                CharacterPosition = Math.Clamp(_characterPosition, 0, _editor.TextLength),
                Rate = (int)_rateBox.Value,
                VoiceName = _voiceBox.SelectedItem?.ToString() ?? _preferredVoice,
            };
            File.WriteAllText(_statePath, JsonSerializer.Serialize(state, new JsonSerializerOptions
            {
                WriteIndented = true
            }), Encoding.UTF8);
        }
        catch (Exception ex)
        {
            _statusLabel.Text = "保存阅读进度失败：" + ex.Message;
        }
    }

    private void MainForm_FormClosing(object? sender, FormClosingEventArgs e)
    {
        _closing = true;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
            var state = new ReaderState
            {
                Text = _editor.Text,
                FilePath = _filePath,
                CharacterPosition = Math.Clamp(_characterPosition, 0, _editor.TextLength),
                Rate = (int)_rateBox.Value,
                VoiceName = _voiceBox.SelectedItem?.ToString() ?? _preferredVoice,
            };
            File.WriteAllText(_statePath, JsonSerializer.Serialize(state), Encoding.UTF8);
        }
        catch { }

        try { _speech.SpeakAsyncCancelAll(); } catch { }
        _speech.Dispose();
    }

    private void Ui(Action action)
    {
        if (_closing || IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke(action); }
        catch (InvalidOperationException) { }
    }

    private sealed record TextChunk(int Start, string Text);

    private sealed class ReaderState
    {
        public string? Text { get; set; }
        public string? FilePath { get; set; }
        public int CharacterPosition { get; set; }
        public int Rate { get; set; }
        public string? VoiceName { get; set; }
    }
}
