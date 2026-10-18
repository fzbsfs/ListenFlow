using ListenFlow.Core;
using ListenFlow.Speech;

namespace ListenFlow.Tests;

public sealed class ReaderControllerTests
{
    [Fact]
    public void Stop_暂停状态_先恢复引擎再取消并进入停止状态()
    {
        var speech = new FakeSpeechService();
        var controller = CreateController(speech);
        controller.SetDocument("第一段。第二段。", 0);
        controller.Play();
        controller.Pause();

        controller.Stop();

        Assert.Equal(PlaybackState.Stopped, controller.State);
        Assert.Equal(new[] { "Pause", "Resume", "CancelAll" }, speech.ControlCalls);
    }

    [Fact]
    public void SetDocument_旧会话回调到达_不污染新文章的进度或状态()
    {
        var speech = new FakeSpeechService();
        var controller = CreateController(speech);
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
        var controller = CreateController(speech);
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
        var controller = CreateController(speech);
        const string text = "第一段。第二段。第三段。";
        controller.SetDocument(text, 0);
        controller.Play();

        while (controller.State == PlaybackState.Speaking)
        {
            speech.EmitCompleted(speech.Requests.Last().Id);
        }

        Assert.Equal(PlaybackState.Completed, controller.State);
        Assert.Equal(text.Length, controller.Position);
    }

    [Fact]
    public void Play_空白文本_进入错误状态且不调用语音引擎()
    {
        var speech = new FakeSpeechService();
        var controller = CreateController(speech);
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
        var controller = new ReaderController(
            speech,
            new TextChunker(maxChunkLength: 120, minimumBreakLength: 30),
            queueDepth: 1);
        var text = string.Concat(Enumerable.Repeat("这是用于超长文本测试的一句话。", 7000));
        controller.SetDocument(text);
        controller.Play();

        while (controller.State == PlaybackState.Speaking)
            speech.EmitCompleted(speech.Requests.Last().Id);

        Assert.Equal(PlaybackState.Completed, controller.State);
        Assert.Equal(text.Length, controller.Position);
        Assert.True(speech.Requests.Count > 100);
    }

    private static ReaderController CreateController(FakeSpeechService speech) =>
        new(speech, new TextChunker(maxChunkLength: 5, minimumBreakLength: 2), queueDepth: 1);

    private sealed class FakeSpeechService : ISpeechService
    {
        public event EventHandler<SpeechProgressEventArgs>? Progressed;
        public event EventHandler<SpeechCompletedEventArgs>? Completed;

        public List<SpeechRequest> Requests { get; } = [];
        public List<string> ControlCalls { get; } = [];
        public IReadOnlyList<LocalVoice> Voices { get; } =
            [new LocalVoice("Microsoft Xiaoxiao", "zh-CN", true)];
        public string? SelectedVoiceName { get; private set; }

        public void SelectVoice(string name) => SelectedVoiceName = name;

        public Guid SpeakAsync(string text, int rate)
        {
            var request = new SpeechRequest(Guid.NewGuid(), text, rate);
            Requests.Add(request);
            return request.Id;
        }

        public void Pause() => ControlCalls.Add("Pause");
        public void Resume() => ControlCalls.Add("Resume");
        public void CancelAll() => ControlCalls.Add("CancelAll");
        public void Dispose() { }

        public void EmitProgress(Guid promptId, int characterPosition, int characterCount) =>
            Progressed?.Invoke(this, new SpeechProgressEventArgs(promptId, characterPosition, characterCount));

        public void EmitCompleted(Guid promptId, bool cancelled = false, Exception? error = null) =>
            Completed?.Invoke(this, new SpeechCompletedEventArgs(promptId, cancelled, error));
    }
}
