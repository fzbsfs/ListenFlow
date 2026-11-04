using System.Speech.Synthesis;

namespace ListenFlow.Speech;

public sealed class SapiSpeechService : ISpeechService
{
    private readonly SpeechSynthesizer _synthesizer = new();
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
            Voices = EnumerateVoices();
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
        var selectedName = SelectedVoiceName;
        Voices = EnumerateVoices();
        if (!string.IsNullOrWhiteSpace(selectedName) &&
            Voices.Any(voice => voice.Enabled &&
                string.Equals(voice.Name, selectedName, StringComparison.OrdinalIgnoreCase)))
        {
            _synthesizer.SelectVoice(selectedName);
        }
        return Voices;
    }

    private IReadOnlyList<LocalVoice> EnumerateVoices() =>
        _synthesizer.GetInstalledVoices()
            .Select(voice => new LocalVoice(
                voice.VoiceInfo.Name,
                voice.VoiceInfo.Culture.Name,
                voice.Enabled))
            .GroupBy(voice => voice.Name, StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderByDescending(voice => voice.Enabled)
                .ThenBy(voice => voice.Culture, StringComparer.OrdinalIgnoreCase)
                .First())
            .OrderBy(voice => voice.Culture, StringComparer.OrdinalIgnoreCase)
            .ThenBy(voice => voice.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public void SelectVoice(string name)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _synthesizer.SelectVoice(name);
    }

    public Guid SpeakAsync(string text, int rate)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("朗读内容不能为空。", nameof(text));

        _synthesizer.Rate = Math.Clamp(rate, -10, 10);
        _synthesizer.Volume = 100;
        var id = Guid.NewGuid();
        var prompt = new Prompt(text);
        lock (_promptIds)
            _promptIds[prompt] = id;
        try
        {
            _synthesizer.SpeakAsync(prompt);
            Log("sapi-prompt-queued", new Dictionary<string, object?>
            {
                ["promptId"] = id,
                ["textLength"] = text.Length,
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
        if (_synthesizer.State == SynthesizerState.Paused)
            _synthesizer.Resume();
        _synthesizer.SpeakAsyncCancelAll();
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
            if (_synthesizer.State == SynthesizerState.Paused)
                _synthesizer.Resume();
            _synthesizer.SpeakAsyncCancelAll();
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
