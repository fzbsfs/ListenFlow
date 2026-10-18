using System.Speech.Synthesis;

namespace ListenFlow.Speech;

public sealed class SapiSpeechService : ISpeechService
{
    private readonly SpeechSynthesizer _synthesizer = new();
    private readonly Dictionary<Prompt, Guid> _promptIds = [];
    private readonly SynchronizationContext? _callbackContext;
    private bool _disposed;

    public SapiSpeechService(SynchronizationContext? callbackContext = null)
    {
        try
        {
            _callbackContext = callbackContext ?? SynchronizationContext.Current;
            _synthesizer.SetOutputToDefaultAudioDevice();
            _synthesizer.SpeakProgress += OnSpeakProgress;
            _synthesizer.SpeakCompleted += OnSpeakCompleted;
            Voices = _synthesizer.GetInstalledVoices()
                .Select(voice => new LocalVoice(
                    voice.VoiceInfo.Name,
                    voice.VoiceInfo.Culture.Name,
                    voice.Enabled))
                .ToArray();
        }
        catch
        {
            _synthesizer.Dispose();
            throw;
        }
    }

    public event EventHandler<SpeechProgressEventArgs>? Progressed;
    public event EventHandler<SpeechCompletedEventArgs>? Completed;

    public IReadOnlyList<LocalVoice> Voices { get; }
    public string? SelectedVoiceName => _disposed ? null : _synthesizer.Voice?.Name;

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
        }

        Raise(() => Progressed?.Invoke(this, new SpeechProgressEventArgs(
            id, eventArgs.CharacterPosition, eventArgs.CharacterCount)));
    }

    private void OnSpeakCompleted(object? sender, SpeakCompletedEventArgs eventArgs)
    {
        Guid id;
        lock (_promptIds)
        {
            if (!_promptIds.Remove(eventArgs.Prompt, out id)) return;
        }

        Raise(() => Completed?.Invoke(this, new SpeechCompletedEventArgs(id, eventArgs.Cancelled, eventArgs.Error)));
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
