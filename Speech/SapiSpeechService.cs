using System.Speech.Synthesis;

namespace ListenFlow.Speech;

public sealed class SapiSpeechService : ISpeechService
{
    private SpeechSynthesizer _synthesizer = new();
    private readonly Dictionary<Prompt, Guid> _promptIds = [];
    private readonly HashSet<Guid> _progressedPromptIds = [];
    private readonly SynchronizationContext? _callbackContext;
    private readonly Action<string, IReadOnlyDictionary<string, object?>?, Exception?>? _diagnostic;
    private bool _disposed;

    public SapiSpeechService(
        SynchronizationContext? callbackContext = null,
        Action<string, IReadOnlyDictionary<string, object?>?, Exception?>? diagnostic = null)
    {
        try
        {
            _callbackContext = callbackContext ?? SynchronizationContext.Current;
            _diagnostic = diagnostic;
            _synthesizer.SetOutputToDefaultAudioDevice();
            _synthesizer.SpeakStarted += OnSpeakStarted;
            _synthesizer.SpeakProgress += OnSpeakProgress;
            _synthesizer.SpeakCompleted += OnSpeakCompleted;
            Voices = EnumerateVoices(_synthesizer);
        }
        catch
        {
            _synthesizer.Dispose();
            throw;
        }
    }

    public event EventHandler<SpeechProgressEventArgs>? Progressed;
    public event EventHandler<SpeechCompletedEventArgs>? Completed;

    public IReadOnlyList<LocalVoice> Voices { get; private set; }
    public string? SelectedVoiceName => _disposed ? null : _synthesizer.Voice?.Name;

    public IReadOnlyList<LocalVoice> RefreshVoices()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_synthesizer.State != SynthesizerState.Ready)
            throw new InvalidOperationException("请停止正文朗读后再刷新语音列表。");
        var selectedName = SelectedVoiceName;
        var replacement = new SpeechSynthesizer();
        IReadOnlyList<LocalVoice> voices;
        try
        {
            replacement.SetOutputToDefaultAudioDevice();
            voices = EnumerateVoices(replacement);
            if (!string.IsNullOrWhiteSpace(selectedName) && voices.Any(voice => voice.Enabled && !voice.IsKnownOnline &&
                    string.Equals(voice.Name, selectedName, StringComparison.OrdinalIgnoreCase)))
                replacement.SelectVoice(selectedName);
        }
        catch { replacement.Dispose(); throw; }
        replacement.SpeakStarted += OnSpeakStarted;
        replacement.SpeakProgress += OnSpeakProgress;
        replacement.SpeakCompleted += OnSpeakCompleted;
        var previous = _synthesizer;
        previous.SpeakStarted -= OnSpeakStarted;
        previous.SpeakProgress -= OnSpeakProgress;
        previous.SpeakCompleted -= OnSpeakCompleted;
        _synthesizer = replacement;
        Voices = voices;
        lock (_promptIds) { _promptIds.Clear(); _progressedPromptIds.Clear(); }
        previous.Dispose();
        return Voices;
    }

    private static IReadOnlyList<LocalVoice> EnumerateVoices(SpeechSynthesizer synthesizer) =>
        LocalVoice.Coalesce(synthesizer.GetInstalledVoices()
            .Select(voice => new LocalVoice(
                voice.VoiceInfo.Name,
                voice.VoiceInfo.Culture.Name,
                voice.Enabled,
                voice.VoiceInfo.Id)));

    public void SelectVoice(string name)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!Voices.Any(voice => voice.Enabled && !voice.IsKnownOnline &&
                string.Equals(voice.Name, name, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("所选语音不存在、未启用或已识别为在线语音。");
        _synthesizer.SelectVoice(name);
    }

    public Guid SpeakAsync(string text, int rate)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("朗读内容不能为空。", nameof(text));
        if (!Voices.Any(voice => voice.Enabled && !voice.IsKnownOnline &&
                string.Equals(voice.Name, SelectedVoiceName, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("当前语音不可用或已识别为在线语音，请选择已启用的本地语音。");

        _synthesizer.Rate = Math.Clamp(rate, -10, 10);
        _synthesizer.Volume = 100;
        var id = Guid.NewGuid();
        var preparedText = SapiTextPreparer.Prepare(text);
        var prompt = new Prompt(preparedText);
        lock (_promptIds)
            _promptIds[prompt] = id;
        try
        {
            _synthesizer.SpeakAsync(prompt);
            Log("sapi-prompt-queued", new Dictionary<string, object?>
            {
                ["promptId"] = id,
                ["textLength"] = text.Length,
                ["inputAdjusted"] = !ReferenceEquals(preparedText, text),
                ["rate"] = _synthesizer.Rate,
                ["voiceName"] = SelectedVoiceName
            });
        }
        catch
        {
            lock (_promptIds)
                _promptIds.Remove(prompt);
            throw;
        }
        return id;
    }

    public void Pause()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _synthesizer.Pause();
    }

    public void Resume()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_synthesizer.State == SynthesizerState.Paused)
            _synthesizer.Resume();
    }

    public void CancelAll()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _synthesizer.SpeakAsyncCancelAll();
        if (_synthesizer.State == SynthesizerState.Paused)
            _synthesizer.Resume();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _synthesizer.SpeakStarted -= OnSpeakStarted;
        _synthesizer.SpeakProgress -= OnSpeakProgress;
        _synthesizer.SpeakCompleted -= OnSpeakCompleted;
        try
        {
            _synthesizer.SpeakAsyncCancelAll();
            if (_synthesizer.State == SynthesizerState.Paused)
                _synthesizer.Resume();
        }
        catch
        {
        }
        _synthesizer.Dispose();
    }

    private void OnSpeakProgress(object? sender, SpeakProgressEventArgs eventArgs)
    {
        Guid id;
        lock (_promptIds)
        {
            if (!_promptIds.TryGetValue(eventArgs.Prompt, out id)) return;
            _progressedPromptIds.Add(id);
        }

        Log("sapi-progress", new Dictionary<string, object?>
        {
            ["promptId"] = id,
            ["position"] = eventArgs.CharacterPosition,
            ["characterCount"] = eventArgs.CharacterCount,
            ["sapiStarted"] = true
        });

        Raise(() => Progressed?.Invoke(this, new SpeechProgressEventArgs(
            id, eventArgs.CharacterPosition, eventArgs.CharacterCount)));
    }

    private void OnSpeakCompleted(object? sender, SpeakCompletedEventArgs eventArgs)
    {
        Guid id;
        bool hasProgress;
        lock (_promptIds)
        {
            if (!_promptIds.Remove(eventArgs.Prompt, out id)) return;
            hasProgress = _progressedPromptIds.Remove(id);
        }

        Log("sapi-completed", new Dictionary<string, object?>
        {
            ["promptId"] = id,
            ["cancelled"] = eventArgs.Cancelled,
            ["hasProgress"] = hasProgress
        }, eventArgs.Error);

        Raise(() => Completed?.Invoke(this, new SpeechCompletedEventArgs(id, eventArgs.Cancelled, eventArgs.Error)));
    }

    private void OnSpeakStarted(object? sender, SpeakStartedEventArgs eventArgs)
    {
        Guid id;
        lock (_promptIds)
        {
            if (!_promptIds.TryGetValue(eventArgs.Prompt, out id)) return;
        }
        Log("sapi-started", new Dictionary<string, object?>
        {
            ["promptId"] = id,
            ["sapiStarted"] = true
        });
    }

    private void Log(string stage, IReadOnlyDictionary<string, object?>? details = null,
        Exception? exception = null)
    {
        try { _diagnostic?.Invoke(stage, details, exception); }
        catch { }
    }

    private void Raise(Action action)
    {
        if (_callbackContext is null)
        {
            action();
            return;
        }
        _callbackContext.Post(_ => action(), null);
    }
}
