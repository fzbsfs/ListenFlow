using System.Collections.Concurrent;
using ListenFlow.Core;
using ListenFlow.Speech;

namespace ListenFlow.Tests;

public sealed class ReaderControllerTests
{
    [Fact]
    public void Stop_暂停状态_取消全部提示并进入停止状态()
    {
        var speech = new FakeSpeechService();
        using var controller = CreateController(speech);
        controller.SetDocument("第一段。第二段。", 0);
        controller.Play();
        controller.Pause();

        controller.Stop();

        Assert.Equal(PlaybackState.Stopped, controller.State);
        Assert.Equal(new[] { "Pause", "CancelAll" }, speech.ControlCalls);
    }

    [Fact]
    public void SetDocument_旧会话回调到达_不污染新文章的进度或状态()
    {
        var speech = new FakeSpeechService();
        using var controller = CreateController(speech);
        controller.SetDocument("旧文章第一句。旧文章第二句。", 0);
        controller.Play();
        var oldPrompt = speech.Requests.Single().Id;

        controller.SetDocument("新文章第一句。新文章第二句。", 0);
        controller.Play();
        var newPrompt = speech.Requests.Last().Id;
        speech.EmitProgress(oldPrompt, 5, 1);
        speech.EmitCompleted(oldPrompt);

        Assert.Equal(0, controller.Position);
        Assert.Equal(PlaybackState.Speaking, controller.State);
        Assert.Equal(newPrompt, speech.Requests.Last().Id);
    }

    [Fact]
    public void Pause_恰逢分段完成_恢复后继续下一段而不是误报全文完成()
    {
        var speech = new FakeSpeechService();
        using var controller = CreateController(speech);
        controller.SetDocument("第一段。第二段。第三段。", 0);
        controller.Play();
        var firstPrompt = speech.Requests.Single().Id;
        controller.Pause();

        speech.EmitCompleted(firstPrompt);

        Assert.Equal(PlaybackState.Paused, controller.State);
        controller.Resume();
        Assert.Equal(PlaybackState.Speaking, controller.State);
        Assert.Equal(2, speech.Requests.Count);
    }

    [Fact]
    public void SpeakCompleted_所有分段依次完成_进度到达全文末尾()
    {
        var speech = new FakeSpeechService();
        using var controller = CreateController(speech);
        const string text = "第一段。第二段。第三段。";
        controller.SetDocument(text, 0);
        controller.Play();

        while (controller.State == PlaybackState.Speaking)
            speech.EmitCompleted(speech.PendingPromptIds.First());

        Assert.Equal(PlaybackState.Completed, controller.State);
        Assert.Equal(text.Length, controller.Position);
    }

    [Fact]
    public void Play_空白文本_进入错误状态且不调用语音引擎()
    {
        var speech = new FakeSpeechService();
        using var controller = CreateController(speech);
        controller.SetDocument("  \r\n  ", 0);

        controller.Play();

        Assert.Equal(PlaybackState.Error, controller.State);
        Assert.Empty(speech.Requests);
        Assert.Contains("没有可朗读", controller.ErrorMessage);
    }

    [Fact]
    public void LongText_十万字符完成_进度准确且无递归溢出()
    {
        var speech = new FakeSpeechService();
        using var controller = new ReaderController(
            speech,
            new TextChunker(maxChunkLength: 120, minimumBreakLength: 30),
            queueDepth: 1);
        var text = string.Concat(Enumerable.Repeat("这是用于超长文本测试的一句话。", 7000));
        controller.SetDocument(text);
        controller.Play();

        while (controller.State == PlaybackState.Speaking)
            speech.EmitCompleted(speech.PendingPromptIds.First());

        Assert.Equal(PlaybackState.Completed, controller.State);
        Assert.Equal(text.Length, controller.Position);
        Assert.True(speech.Requests.Count > 100);
    }

    [Fact]
    public void SeekTo_朗读中定位_原子取消旧队列并立即从目标继续()
    {
        var speech = new FakeSpeechService();
        using var controller = CreateController(speech, queueDepth: 3);
        const string text = "第一段。第二段。第三段。第四段。";
        var target = text.IndexOf("第三段", StringComparison.Ordinal);
        controller.SetDocument(text);
        controller.Play();
        var oldGeneration = controller.Generation;
        var oldPrompts = speech.PendingPromptIds.ToArray();
        var oldRequestCount = speech.Requests.Count;

        controller.SeekTo(target);

        Assert.Equal(PlaybackState.Speaking, controller.State);
        Assert.Equal(target, controller.Position);
        Assert.True(controller.Generation > oldGeneration);
        Assert.Contains("CancelAll", speech.ControlCalls);
        Assert.True(speech.Requests.Count > oldRequestCount);
        Assert.Equal(
            text.Substring(target, speech.Requests[oldRequestCount].Text.Length),
            speech.Requests[oldRequestCount].Text);

        foreach (var oldPrompt in oldPrompts)
        {
            speech.EmitProgress(oldPrompt, 1, 1);
            speech.EmitCompleted(oldPrompt);
        }

        Assert.Equal(target, controller.Position);
        Assert.Equal(PlaybackState.Speaking, controller.State);
    }

    [Fact]
    public void SeekTo_暂停期间定位_不提前发声且恢复后从新位置开始()
    {
        var speech = new FakeSpeechService();
        using var controller = CreateController(speech, queueDepth: 3);
        const string text = "第一段。第二段。第三段。第四段。";
        var target = text.IndexOf("第三段", StringComparison.Ordinal);
        controller.SetDocument(text);
        controller.Play();
        controller.Pause();
        var requestCountBeforeSeek = speech.Requests.Count;

        controller.SeekTo(target);

        Assert.Equal(PlaybackState.Paused, controller.State);
        Assert.Equal(target, controller.Position);
        Assert.Equal(requestCountBeforeSeek, speech.Requests.Count);

        controller.Resume();

        Assert.Equal(PlaybackState.Speaking, controller.State);
        Assert.True(speech.Requests.Count > requestCountBeforeSeek);
        Assert.Equal(
            text.Substring(target, speech.Requests[requestCountBeforeSeek].Text.Length),
            speech.Requests[requestCountBeforeSeek].Text);
        Assert.DoesNotContain("Resume", speech.ControlCalls);
    }

    [Fact]
    public void SeekAndPlay_停止状态_从目标直接开始新的播放代次()
    {
        var speech = new FakeSpeechService();
        using var controller = CreateController(speech, queueDepth: 3);
        const string text = "第一段。第二段。第三段。";
        var target = text.IndexOf("第二段", StringComparison.Ordinal);
        controller.SetDocument(text);
        var generation = controller.Generation;

        controller.SeekAndPlay(target);

        Assert.Equal(PlaybackState.Speaking, controller.State);
        Assert.Equal(target, controller.Position);
        Assert.True(controller.Generation > generation);
        Assert.Equal(text.Substring(target, speech.Requests[0].Text.Length), speech.Requests[0].Text);
    }

    [Fact]
    public void SeekTo_取消失败_进入明确错误状态且不启动新提示()
    {
        var speech = new FakeSpeechService();
        using var controller = CreateController(speech, queueDepth: 3);
        controller.SetDocument("第一段。第二段。第三段。");
        controller.Play();
        var requestCount = speech.Requests.Count;
        speech.CancelException = new InvalidOperationException("设备拒绝取消");

        controller.SeekTo(3);

        Assert.Equal(PlaybackState.Error, controller.State);
        Assert.Equal(requestCount, speech.Requests.Count);
        Assert.Contains("无法确认旧语音已经停止", controller.ErrorMessage);
        Assert.Contains("设备拒绝取消", controller.ErrorMessage);

        speech.CancelException = null;
        controller.SetDocument("替换文章。");
        controller.Play();

        Assert.Equal(PlaybackState.Error, controller.State);
        Assert.Equal(requestCount, speech.Requests.Count);
        Assert.Contains("此前取消操作失败", controller.ErrorMessage);
    }

    [Fact]
    public void QueueDepth3_乱序完成和重复回调_只按播放顺序推进()
    {
        var speech = new FakeSpeechService();
        using var controller = CreateController(speech, queueDepth: 3);
        const string text = "第一段。第二段。第三段。第四段。第五段。";
        controller.SetDocument(text);
        controller.Play();
        var prompts = speech.PendingPromptIds.ToArray();
        Assert.Equal(3, prompts.Length);
        var firstTwoLength = speech.Requests[0].Text.Length + speech.Requests[1].Text.Length;

        speech.EmitCompleted(prompts[1]);
        speech.EmitCompleted(prompts[1]);
        speech.EmitProgress(prompts[1], 1, 1);

        Assert.Equal(0, controller.Position);
        Assert.Equal(3, speech.Requests.Count);

        speech.EmitProgress(prompts[0], 1, 2);
        Assert.Equal(1, controller.Position);
        Assert.Equal(new TextRange(1, 2), controller.SpokenRange);

        speech.EmitCompleted(prompts[0]);

        Assert.Equal(firstTwoLength, controller.Position);
        Assert.Equal(5, speech.Requests.Count);
        var position = controller.Position;
        speech.EmitCompleted(prompts[0]);
        Assert.Equal(position, controller.Position);
    }

    [Fact]
    public void SeekTo_连续定位_只有最后代次可以更新进度()
    {
        var speech = new FakeSpeechService();
        using var controller = CreateController(speech, queueDepth: 3);
        const string text = "第一段。第二段。第三段。第四段。第五段。";
        var firstTarget = text.IndexOf("第二段", StringComparison.Ordinal);
        var lastTarget = text.IndexOf("第四段", StringComparison.Ordinal);
        controller.SetDocument(text);
        controller.Play();
        controller.SeekTo(firstTarget);
        var firstSeekPrompt = speech.PendingPromptIds.First();
        var firstGeneration = controller.Generation;
        controller.SeekTo(lastTarget);
        var lastSeekPrompt = speech.PendingPromptIds.First();

        speech.EmitProgress(firstSeekPrompt, 2, 1);
        Assert.Equal(lastTarget, controller.Position);
        Assert.True(controller.Generation > firstGeneration);

        speech.EmitProgress(lastSeekPrompt, 1, 2);
        Assert.Equal(lastTarget + 1, controller.Position);
        Assert.Equal(new TextRange(lastTarget + 1, 2), controller.SpokenRange);
    }

    [Fact]
    public void SeekTo_重复定位同一位置_最后一次请求接管且前一次回调失效()
    {
        var speech = new FakeSpeechService();
        using var controller = CreateController(speech, queueDepth: 3);
        const string text = "第一段。第二段。第三段。第四段。";
        var target = text.IndexOf("第三段", StringComparison.Ordinal);
        controller.SetDocument(text);
        controller.Play();
        controller.SeekTo(target);
        var repeatedPrompt = speech.PendingPromptIds.First();
        var generation = controller.Generation;

        controller.SeekTo(target);
        var finalPrompt = speech.PendingPromptIds.First();

        Assert.True(controller.Generation > generation);
        Assert.NotEqual(repeatedPrompt, finalPrompt);
        speech.EmitProgress(repeatedPrompt, 2, 1);
        Assert.Equal(target, controller.Position);
        speech.EmitProgress(finalPrompt, 1, 1);
        Assert.Equal(target + 1, controller.Position);
    }

    [Fact]
    public void Progress_字符范围越过当前分段_进入错误状态()
    {
        var speech = new FakeSpeechService();
        using var controller = CreateController(speech);
        controller.SetDocument("第一段。第二段。");
        controller.Play();
        var prompt = speech.PendingPromptIds.Single();
        var promptLength = speech.Requests.Single().Text.Length;

        speech.EmitProgress(prompt, promptLength, 1);

        Assert.Equal(PlaybackState.Error, controller.State);
        Assert.Contains("分段边界", controller.ErrorMessage);
    }

    [Fact]
    public async Task StateContext_后台命令与语音回调_全部在同一状态线程执行()
    {
        using var context = new DedicatedThreadSynchronizationContext();
        var speech = new FakeSpeechService();
        using var controller = CreateController(speech, queueDepth: 1, stateContext: context);
        var eventThreads = new ConcurrentBag<int>();
        controller.Changed += (_, _) => eventThreads.Add(Environment.CurrentManagedThreadId);

        await Task.Run(() =>
        {
            controller.SetDocument("第一段。第二段。");
            controller.Play();
        });
        var prompt = speech.PendingPromptIds.Single();
        await Task.Run(() => speech.EmitProgress(prompt, 1, 1));

        Assert.NotEmpty(eventThreads);
        Assert.All(eventThreads, threadId => Assert.Equal(context.ThreadId, threadId));
        Assert.All(speech.OperationThreadIds, threadId => Assert.Equal(context.ThreadId, threadId));
    }

    private static ReaderController CreateController(
        FakeSpeechService speech,
        int queueDepth = 1,
        SynchronizationContext? stateContext = null) =>
        new(
            speech,
            new TextChunker(maxChunkLength: 5, minimumBreakLength: 2),
            queueDepth,
            stateContext);

    private sealed class FakeSpeechService : ISpeechService
    {
        private readonly HashSet<Guid> _pendingPromptIds = [];

        public event EventHandler<SpeechProgressEventArgs>? Progressed;
        public event EventHandler<SpeechCompletedEventArgs>? Completed;

        public List<SpeechRequest> Requests { get; } = [];
        public List<string> ControlCalls { get; } = [];
        public List<int> OperationThreadIds { get; } = [];
        public Exception? CancelException { get; set; }
        public IReadOnlyCollection<Guid> PendingPromptIds => _pendingPromptIds;
        public IReadOnlyList<LocalVoice> Voices { get; } =
            [new LocalVoice("Microsoft Xiaoxiao", "zh-CN", true)];
        public string? SelectedVoiceName { get; private set; }

        public void SelectVoice(string name)
        {
            RecordThread();
            SelectedVoiceName = name;
        }

        public Guid SpeakAsync(string text, int rate)
        {
            RecordThread();
            var request = new SpeechRequest(Guid.NewGuid(), text, rate);
            Requests.Add(request);
            _pendingPromptIds.Add(request.Id);
            return request.Id;
        }

        public void Pause()
        {
            RecordThread();
            ControlCalls.Add("Pause");
        }

        public void Resume()
        {
            RecordThread();
            ControlCalls.Add("Resume");
        }

        public void CancelAll()
        {
            RecordThread();
            ControlCalls.Add("CancelAll");
            if (CancelException is not null) throw CancelException;
            _pendingPromptIds.Clear();
        }

        public void Dispose()
        {
            RecordThread();
        }

        public void EmitProgress(Guid promptId, int characterPosition, int characterCount) =>
            Progressed?.Invoke(this, new SpeechProgressEventArgs(promptId, characterPosition, characterCount));

        public void EmitCompleted(Guid promptId, bool cancelled = false, Exception? error = null)
        {
            _pendingPromptIds.Remove(promptId);
            Completed?.Invoke(this, new SpeechCompletedEventArgs(promptId, cancelled, error));
        }

        private void RecordThread() => OperationThreadIds.Add(Environment.CurrentManagedThreadId);
    }

}
