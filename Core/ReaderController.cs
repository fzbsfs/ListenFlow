using ListenFlow.Speech;

namespace ListenFlow.Core;

public enum PlaybackState
{
    Ready,
    Speaking,
    Paused,
    Stopped,
    Completed,
    Error
}

public sealed class ReaderChangedEventArgs(
    PlaybackState state,
    int position,
    TextRange sentence,
    string? errorMessage) : EventArgs
{
    public PlaybackState State { get; } = state;
    public int Position { get; } = position;
    public TextRange Sentence { get; } = sentence;
    public string? ErrorMessage { get; } = errorMessage;
}

public sealed class ReaderController : IDisposable
{
    private readonly ISpeechService _speech;
    private readonly TextChunker _chunker;
    private readonly int _queueDepth;
    private readonly Dictionary<Guid, QueuedPrompt> _queued = [];
    private IReadOnlyList<TextSegment> _chunks = [];
    private string _text = string.Empty;
    private int _nextChunkIndex;
    private long _session;
    private bool _disposed;

    public ReaderController(ISpeechService speech, TextChunker chunker, int queueDepth = 2)
    {
        _speech = speech ?? throw new ArgumentNullException(nameof(speech));
        _chunker = chunker ?? throw new ArgumentNullException(nameof(chunker));
        if (queueDepth is < 1 or > 8)
            throw new ArgumentOutOfRangeException(nameof(queueDepth));
        _queueDepth = queueDepth;
        _speech.Progressed += OnProgressed;
        _speech.Completed += OnCompleted;
    }

    public event EventHandler<ReaderChangedEventArgs>? Changed;

    public PlaybackState State { get; private set; } = PlaybackState.Ready;
    public int Position { get; private set; }
    public int Rate { get; set; }
    public string Text => _text;
    public string? ErrorMessage { get; private set; }
    public TextRange CurrentSentence => _chunker.FindSentence(_text, Position);

    public void SetDocument(string text, int position = 0)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        CancelCurrentSession();
        _text = text ?? string.Empty;
        _chunks = _chunker.Split(_text);
        Position = Math.Clamp(position, 0, _text.Length);
        _nextChunkIndex = FindChunkIndex(Position);
        ErrorMessage = null;
        State = PlaybackState.Ready;
        RaiseChanged();
    }

    public void Play()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (State == PlaybackState.Paused)
        {
            Resume();
            return;
        }
        if (State == PlaybackState.Speaking) return;
        if (string.IsNullOrWhiteSpace(_text))
        {
            Fail("没有可朗读的文本。");
            return;
        }

        if (Position >= _text.Length)
            Position = 0;
        _nextChunkIndex = FindChunkIndex(Position);
        ErrorMessage = null;
        State = PlaybackState.Speaking;
        FillQueue();
        RaiseChanged();
    }

    public void Pause()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (State != PlaybackState.Speaking) return;
        try
        {
            _speech.Pause();
            State = PlaybackState.Paused;
            RaiseChanged();
        }
        catch (Exception exception)
        {
            Fail("暂停失败：" + exception.Message);
        }
    }

    public void Resume()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (State != PlaybackState.Paused) return;
        try
        {
            _speech.Resume();
            State = PlaybackState.Speaking;
            FillQueue();
            RaiseChanged();
        }
        catch (Exception exception)
        {
            Fail("继续朗读失败：" + exception.Message);
        }
    }

    public void Stop()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var wasPaused = State == PlaybackState.Paused;
        if (wasPaused)
        {
            try { _speech.Resume(); }
            catch { }
        }

        CancelCurrentSession();
        State = PlaybackState.Stopped;
        ErrorMessage = null;
        RaiseChanged();
    }

    public void SeekTo(int position)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        CancelCurrentSession();
        Position = Math.Clamp(position, 0, _text.Length);
        _nextChunkIndex = FindChunkIndex(Position);
        State = PlaybackState.Stopped;
        ErrorMessage = null;
        RaiseChanged();
    }

    public void MoveBySentence(int direction)
    {
        if (direction == 0 || _text.Length == 0) return;
        var current = _chunker.FindSentence(_text, Math.Min(Position, _text.Length - 1));
        if (direction > 0)
        {
            SeekTo(current.End >= _text.Length ? _text.Length : _chunker.FindSentence(_text, current.End).Start);
            return;
        }

        var target = Position > current.Start + 1
            ? current.Start
            : _chunker.FindSentence(_text, Math.Max(0, current.Start - 1)).Start;
        SeekTo(target);
    }

    public int GetRecoveryPosition() => _chunker.FindRecoveryPosition(_text, Position);

    public void Dispose()
    {
        if (_disposed) return;
        CancelCurrentSession();
        _disposed = true;
        _speech.Progressed -= OnProgressed;
        _speech.Completed -= OnCompleted;
        _speech.Dispose();
    }

    private void FillQueue()
    {
        while (State == PlaybackState.Speaking &&
               _queued.Count < _queueDepth &&
               _nextChunkIndex < _chunks.Count)
        {
            var chunk = _chunks[_nextChunkIndex++];
            var offset = Math.Clamp(Position - chunk.Start, 0, chunk.Length);
            var remaining = chunk.Text[offset..];
            var promptStart = chunk.Start + offset;
            if (string.IsNullOrWhiteSpace(remaining))
            {
                Position = Math.Max(Position, chunk.End);
                continue;
            }

            try
            {
                var id = _speech.SpeakAsync(remaining, Math.Clamp(Rate, -10, 10));
                _queued[id] = new QueuedPrompt(_session, promptStart, chunk.End);
            }
            catch (Exception exception)
            {
                Fail("启动朗读失败：" + exception.Message);
                return;
            }
        }

        if (State == PlaybackState.Speaking && _queued.Count == 0 && _nextChunkIndex >= _chunks.Count)
        {
            Position = _text.Length;
            State = PlaybackState.Completed;
        }
    }

    private void OnProgressed(object? sender, SpeechProgressEventArgs eventArgs)
    {
        if (!_queued.TryGetValue(eventArgs.PromptId, out var prompt) || prompt.Session != _session)
            return;

        Position = Math.Clamp(
            prompt.Start + eventArgs.CharacterPosition,
            0,
            _text.Length);
        RaiseChanged();
    }

    private void OnCompleted(object? sender, SpeechCompletedEventArgs eventArgs)
    {
        if (!_queued.Remove(eventArgs.PromptId, out var prompt) || prompt.Session != _session)
            return;

        if (eventArgs.Error is not null)
        {
            Fail("朗读发生错误：" + eventArgs.Error.Message);
            return;
        }
        if (eventArgs.Cancelled)
        {
            if (State is PlaybackState.Speaking or PlaybackState.Paused)
                State = PlaybackState.Stopped;
            RaiseChanged();
            return;
        }

        Position = Math.Max(Position, prompt.End);
        if (State == PlaybackState.Speaking)
        {
            FillQueue();
        }
        else if (State == PlaybackState.Paused && _queued.Count == 0 && _nextChunkIndex >= _chunks.Count)
        {
            Position = _text.Length;
            State = PlaybackState.Completed;
        }
        RaiseChanged();
    }

    private void CancelCurrentSession()
    {
        _session++;
        if (_queued.Count > 0 || State is PlaybackState.Speaking or PlaybackState.Paused)
        {
            try { _speech.CancelAll(); }
            catch { }
        }
        _queued.Clear();
    }

    private int FindChunkIndex(int position)
    {
        if (_chunks.Count == 0) return 0;
        for (var index = 0; index < _chunks.Count; index++)
        {
            if (position < _chunks[index].End)
                return index;
        }
        return _chunks.Count;
    }

    private void Fail(string message)
    {
        try { _speech.CancelAll(); }
        catch { }
        _session++;
        _queued.Clear();
        State = PlaybackState.Error;
        ErrorMessage = message;
        RaiseChanged();
    }

    private void RaiseChanged() => Changed?.Invoke(this,
        new ReaderChangedEventArgs(State, Position, CurrentSentence, ErrorMessage));

    private sealed record QueuedPrompt(long Session, int Start, int End);
}
