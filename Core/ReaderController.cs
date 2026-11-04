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
    TextRange spokenRange,
    long generation,
    string? errorMessage) : EventArgs
{
    public PlaybackState State { get; } = state;
    public int Position { get; } = position;
    public TextRange Sentence { get; } = sentence;
    public TextRange SpokenRange { get; } = spokenRange;
    public long Generation { get; } = generation;
    public string? ErrorMessage { get; } = errorMessage;
}

public sealed class ReaderController : IDisposable
{
    private readonly ISpeechService _speech;
    private readonly TextChunker _chunker;
    private readonly int _queueDepth;
    private readonly SynchronizationContext? _stateContext;
    private readonly Action<string, IReadOnlyDictionary<string, object?>?, Exception?>? _diagnostic;
    private readonly Dictionary<Guid, QueuedPrompt> _queued = [];
    private readonly Queue<Guid> _promptOrder = [];
    private IReadOnlyList<TextSegment> _chunks = [];
    private string _text = string.Empty;
    private int _nextChunkIndex;
    private long _generation;
    private PlaybackState _state = PlaybackState.Ready;
    private int _position;
    private int _rate;
    private TextRange _spokenRange;
    private string? _errorMessage;
    private bool _cancellationUncertain;
    private bool _disposed;

    public ReaderController(
        ISpeechService speech,
        TextChunker chunker,
        int queueDepth = 2,
        SynchronizationContext? stateContext = null,
        Action<string, IReadOnlyDictionary<string, object?>?, Exception?>? diagnostic = null)
    {
        _speech = speech ?? throw new ArgumentNullException(nameof(speech));
        _chunker = chunker ?? throw new ArgumentNullException(nameof(chunker));
        if (queueDepth is < 1 or > 8)
            throw new ArgumentOutOfRangeException(nameof(queueDepth));
        _queueDepth = queueDepth;
        _stateContext = stateContext;
        _diagnostic = diagnostic;
        _speech.Progressed += OnProgressed;
        _speech.Completed += OnCompleted;
    }

    public event EventHandler<ReaderChangedEventArgs>? Changed;

    public PlaybackState State => Read(() => _state);
    public int Position => Read(() => _position);
    public long Generation => Read(() => _generation);
    public TextRange SpokenRange => Read(() => _spokenRange);

    public int Rate
    {
        get => Read(() => _rate);
        set => Invoke(() => _rate = Math.Clamp(value, -10, 10));
    }

    public string Text => Read(() => _text);
    public string? ErrorMessage => Read(() => _errorMessage);
    public TextRange CurrentSentence => Read(() => _chunker.FindSentence(_text, _position));

    public void SetDocument(string text, int position = 0) => Invoke(() =>
    {
        ThrowIfDisposed();
        if (!CancelCurrentSession("切换文章")) return;
        _text = text ?? string.Empty;
        _chunks = _chunker.Split(_text);
        Log("reader-document-prepared", new Dictionary<string, object?>
        {
            ["textLength"] = _text.Length,
            ["chunkCount"] = _chunks.Count,
            ["position"] = position,
            ["queueDepth"] = _queueDepth
        });
        _position = Math.Clamp(position, 0, _text.Length);
        _nextChunkIndex = FindChunkIndex(_position);
        _spokenRange = new TextRange(_position, 0);
        _errorMessage = null;
        _state = PlaybackState.Ready;
        RaiseChanged();
    });

    public void Play() => Invoke(() =>
    {
        ThrowIfDisposed();
        if (!EnsurePlaybackSafe("开始朗读")) return;
        if (_state == PlaybackState.Paused)
        {
            ResumeCore();
            return;
        }
        if (_state == PlaybackState.Speaking) return;
        if (string.IsNullOrWhiteSpace(_text))
        {
            Fail("没有可朗读的文本。", cancelPlayback: false);
            return;
        }

        if (_position >= _text.Length)
            _position = 0;
        _generation++;
        _nextChunkIndex = FindChunkIndex(_position);
        _spokenRange = new TextRange(_position, 0);
        _errorMessage = null;
        _state = PlaybackState.Speaking;
        FillQueue();
        if (_state != PlaybackState.Error)
            RaiseChanged();
    });

    public void Pause() => Invoke(() =>
    {
        ThrowIfDisposed();
        if (_state != PlaybackState.Speaking) return;
        try
        {
            _speech.Pause();
            _state = PlaybackState.Paused;
            RaiseChanged();
        }
        catch (Exception exception)
        {
            Fail("暂停失败：" + exception.Message);
        }
    });

    public void Resume() => Invoke(() =>
    {
        ThrowIfDisposed();
        ResumeCore();
    });

    public void SelectVoice(string name) => Invoke(() =>
    {
        ThrowIfDisposed();
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("语音名称不能为空。", nameof(name));
        if (string.Equals(_speech.SelectedVoiceName, name, StringComparison.OrdinalIgnoreCase))
            return;

        var previousState = _state;
        var previousVoiceName = _speech.SelectedVoiceName;
        if (!CancelCurrentSession("切换本地语音"))
            throw new InvalidOperationException(_errorMessage ?? "切换语音前无法安全停止当前朗读。");

        try
        {
            _speech.SelectVoice(name);
        }
        catch (Exception exception)
        {
            if (!string.IsNullOrWhiteSpace(previousVoiceName))
            {
                try { _speech.SelectVoice(previousVoiceName); }
                catch { }
            }

            _nextChunkIndex = FindChunkIndex(_position);
            _state = previousState;
            if (_state == PlaybackState.Speaking)
                FillQueue();
            if (_state != PlaybackState.Error)
                RaiseChanged();

            throw new InvalidOperationException("切换本地语音失败：" + exception.Message, exception);
        }

        _nextChunkIndex = FindChunkIndex(_position);
        _spokenRange = new TextRange(_position, 0);
        _errorMessage = null;
        _state = previousState;
        if (_state == PlaybackState.Speaking)
            FillQueue();
        if (_state != PlaybackState.Error)
            RaiseChanged();
    });

    public void Stop() => Invoke(() =>
    {
        ThrowIfDisposed();
        if (!CancelCurrentSession("停止朗读")) return;
        _state = PlaybackState.Stopped;
        _errorMessage = null;
        _spokenRange = new TextRange(_position, 0);
        RaiseChanged();
    });

    public void SeekTo(int position) => Invoke(() =>
    {
        ThrowIfDisposed();
        SeekCore(position, PreserveSeekState());
    });

    public void SeekAndPlay(int position) => Invoke(() =>
    {
        ThrowIfDisposed();
        SeekCore(position, PlaybackState.Speaking);
    });

    public void MoveBySentence(int direction) => Invoke(() =>
    {
        ThrowIfDisposed();
        if (direction == 0 || _text.Length == 0) return;
        var current = _chunker.FindSentence(_text, Math.Min(_position, _text.Length - 1));
        if (direction > 0)
        {
            var target = current.End >= _text.Length
                ? _text.Length
                : _chunker.FindSentence(_text, current.End).Start;
            SeekCore(target, PreserveSeekState());
            return;
        }

        var previous = _position > current.Start + 1
            ? current.Start
            : _chunker.FindSentence(_text, Math.Max(0, current.Start - 1)).Start;
        SeekCore(previous, PreserveSeekState());
    });

    public int GetRecoveryPosition() => Read(() =>
        _chunker.FindRecoveryPosition(_text, _position));

    public void Dispose() => Invoke(() =>
    {
        if (_disposed) return;
        CancelCurrentSession("释放语音服务");
        _disposed = true;
        _speech.Progressed -= OnProgressed;
        _speech.Completed -= OnCompleted;
        _speech.Dispose();
    });

    private void ResumeCore()
    {
        if (_state != PlaybackState.Paused) return;
        try
        {
            if (_queued.Count > 0)
                _speech.Resume();
            _state = PlaybackState.Speaking;
            FillQueue();
            if (_state != PlaybackState.Error)
                RaiseChanged();
        }
        catch (Exception exception)
        {
            Fail("继续朗读失败：" + exception.Message);
        }
    }

    private void SeekCore(int position, PlaybackState targetState)
    {
        if (!CancelCurrentSession("定位朗读位置")) return;
        _position = Math.Clamp(position, 0, _text.Length);
        _nextChunkIndex = FindChunkIndex(_position);
        _spokenRange = new TextRange(_position, 0);
        _errorMessage = null;
        _state = targetState;
        if (_state == PlaybackState.Speaking)
        {
            if (string.IsNullOrWhiteSpace(_text))
            {
                Fail("没有可朗读的文本。", cancelPlayback: false);
                return;
            }
            if (_position >= _text.Length)
            {
                _state = PlaybackState.Completed;
            }
            else
            {
                FillQueue();
            }
        }
        if (_state != PlaybackState.Error)
            RaiseChanged();
    }

    private PlaybackState PreserveSeekState() => _state switch
    {
        PlaybackState.Speaking => PlaybackState.Speaking,
        PlaybackState.Paused => PlaybackState.Paused,
        _ => PlaybackState.Stopped
    };

    private void FillQueue()
    {
        while (_state == PlaybackState.Speaking &&
               _queued.Count < _queueDepth &&
               _nextChunkIndex < _chunks.Count)
        {
            var chunk = _chunks[_nextChunkIndex++];
            var offset = Math.Clamp(_position - chunk.Start, 0, chunk.Length);
            var remaining = chunk.Text[offset..];
            var promptStart = chunk.Start + offset;
            if (string.IsNullOrWhiteSpace(remaining))
                continue;

            try
            {
                var id = _speech.SpeakAsync(remaining, _rate);
                _queued[id] = new QueuedPrompt(_generation, promptStart, chunk.End, remaining.Length);
                _promptOrder.Enqueue(id);
                Log("reader-prompt-queued", new Dictionary<string, object?>
                {
                    ["promptId"] = id,
                    ["generation"] = _generation,
                    ["chunkIndex"] = _nextChunkIndex - 1,
                    ["queueCount"] = _queued.Count,
                    ["rangeStart"] = promptStart,
                    ["rangeEnd"] = chunk.End,
                    ["rangeLength"] = remaining.Length
                });
            }
            catch (Exception exception)
            {
                Fail("启动朗读失败：" + exception.Message);
                return;
            }
        }

        if (_state == PlaybackState.Speaking && _queued.Count == 0 && _nextChunkIndex >= _chunks.Count)
        {
            _position = _text.Length;
            _spokenRange = new TextRange(_position, 0);
            _state = PlaybackState.Completed;
        }
    }

    private void OnProgressed(object? sender, SpeechProgressEventArgs eventArgs) =>
        Invoke(() => OnProgressedCore(eventArgs));

    private void OnProgressedCore(SpeechProgressEventArgs eventArgs)
    {
        if (!_queued.TryGetValue(eventArgs.PromptId, out var prompt) ||
            prompt.Generation != _generation ||
            !_promptOrder.TryPeek(out var activePromptId) ||
            activePromptId != eventArgs.PromptId)
            return;

        if (eventArgs.CharacterPosition < 0 ||
            eventArgs.CharacterCount < 0 ||
            eventArgs.CharacterPosition > prompt.Length ||
            eventArgs.CharacterCount > prompt.Length - eventArgs.CharacterPosition)
        {
            Fail("语音进度超出当前分段边界。", cancelPlayback: true);
            return;
        }

        _position = prompt.Start + eventArgs.CharacterPosition;
        _spokenRange = new TextRange(_position, eventArgs.CharacterCount);
        Log("reader-progress-accepted", new Dictionary<string, object?>
        {
            ["promptId"] = eventArgs.PromptId,
            ["generation"] = _generation,
            ["position"] = _position,
            ["rangeStart"] = _spokenRange.Start,
            ["rangeLength"] = _spokenRange.Length,
            ["rangeEnd"] = _spokenRange.End
        });
        RaiseChanged();
    }

    private void OnCompleted(object? sender, SpeechCompletedEventArgs eventArgs) =>
        Invoke(() => OnCompletedCore(eventArgs));

    private void OnCompletedCore(SpeechCompletedEventArgs eventArgs)
    {
        if (!_queued.TryGetValue(eventArgs.PromptId, out var prompt) ||
            prompt.Generation != _generation ||
            prompt.Completion is not null)
            return;

        prompt.Completion = eventArgs;
        DrainCompletedPrompts();
    }

    private void DrainCompletedPrompts()
    {
        var changed = false;
        while (_promptOrder.TryPeek(out var promptId) &&
               _queued.TryGetValue(promptId, out var prompt) &&
               prompt.Completion is not null)
        {
            _promptOrder.Dequeue();
            _queued.Remove(promptId);
            var completion = prompt.Completion;
            if (completion.Cancelled)
            {
                if (!CancelCurrentSession("处理意外取消")) return;
                _state = PlaybackState.Stopped;
                _spokenRange = new TextRange(_position, 0);
                RaiseChanged();
                return;
            }
            if (completion.Error is not null)
            {
                Fail("朗读发生错误：" + completion.Error.Message);
                return;
            }

            _position = Math.Max(_position, prompt.End);
            _spokenRange = new TextRange(_position, 0);
            changed = true;
        }

        if (_state == PlaybackState.Speaking)
        {
            FillQueue();
        }
        else if (_state == PlaybackState.Paused && _queued.Count == 0 && _nextChunkIndex >= _chunks.Count)
        {
            _position = _text.Length;
            _spokenRange = new TextRange(_position, 0);
            _state = PlaybackState.Completed;
            changed = true;
        }

        if (changed && _state != PlaybackState.Error)
            RaiseChanged();
    }

    private bool CancelCurrentSession(string operation)
    {
        if (!EnsurePlaybackSafe(operation)) return false;
        var hadPlayback = _queued.Count > 0 || _state is PlaybackState.Speaking or PlaybackState.Paused;
        _generation++;
        if (hadPlayback)
        {
            try
            {
                _speech.CancelAll();
            }
            catch (Exception exception)
            {
                ClearQueue();
                _cancellationUncertain = true;
                _state = PlaybackState.Error;
                _errorMessage = operation + "失败，无法确认旧语音已经停止。请重新启动 ListenFlow：" +
                                exception.Message;
                _spokenRange = new TextRange(_position, 0);
                RaiseChanged();
                return false;
            }
        }
        ClearQueue();
        return true;
    }

    private void ClearQueue()
    {
        _queued.Clear();
        _promptOrder.Clear();
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

    private void Fail(string message, bool cancelPlayback = true)
    {
        Log("reader-error", new Dictionary<string, object?>
        {
            ["generation"] = _generation,
            ["position"] = _position,
            ["state"] = _state.ToString(),
            ["reason"] = message,
            ["lastProgressPosition"] = _spokenRange.Start,
            ["lastProgressLength"] = _spokenRange.Length
        });
        _generation++;
        if (cancelPlayback && (_queued.Count > 0 || _state is PlaybackState.Speaking or PlaybackState.Paused))
        {
            try
            {
                _speech.CancelAll();
            }
            catch (Exception exception)
            {
                _cancellationUncertain = true;
                message += " 同时无法确认旧语音已经停止：" + exception.Message;
            }
        }
        ClearQueue();
        _state = PlaybackState.Error;
        _errorMessage = message;
        _spokenRange = new TextRange(_position, 0);
        RaiseChanged();
    }

    private bool EnsurePlaybackSafe(string operation)
    {
        if (!_cancellationUncertain) return true;
        _state = PlaybackState.Error;
        _errorMessage = operation + "未执行：此前取消操作失败，旧语音可能仍在播放。请重新启动 ListenFlow。";
        _spokenRange = new TextRange(_position, 0);
        RaiseChanged();
        return false;
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private void Log(string stage, IReadOnlyDictionary<string, object?>? details = null,
        Exception? exception = null)
    {
        try { _diagnostic?.Invoke(stage, details, exception); }
        catch { }
    }

    private void RaiseChanged() => Changed?.Invoke(this,
        new ReaderChangedEventArgs(
            _state,
            _position,
            _chunker.FindSentence(_text, _position),
            _spokenRange,
            _generation,
            _errorMessage));

    private void Invoke(Action action)
    {
        if (_stateContext is null || ReferenceEquals(SynchronizationContext.Current, _stateContext))
        {
            action();
            return;
        }
        _stateContext.Send(static state => ((Action)state!).Invoke(), action);
    }

    private T Read<T>(Func<T> read)
    {
        T? result = default;
        Invoke(() => result = read());
        return result!;
    }

    private sealed class QueuedPrompt(long generation, int start, int end, int length)
    {
        public long Generation { get; } = generation;
        public int Start { get; } = start;
        public int End { get; } = end;
        public int Length { get; } = length;
        public SpeechCompletedEventArgs? Completion { get; set; }
    }
}
