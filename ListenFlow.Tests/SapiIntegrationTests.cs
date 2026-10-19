using System.Collections.Concurrent;
using ListenFlow.Speech;
using Core = ListenFlow.Core;

namespace ListenFlow.Tests;

public sealed class SapiIntegrationTests
{
    [Fact]
    [Trait("类别", "本机语音集成")]
    public async Task Xiaoxiao_枚举选择与短中文异步朗读_返回有效进度和完成事件()
    {
        using var speech = new SapiSpeechService(new SynchronizationContext());
        var voice = Assert.Single(speech.Voices, item =>
            item.Enabled &&
            item.Name.Contains("Microsoft Xiaoxiao", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(item.Culture, "zh-CN", StringComparison.OrdinalIgnoreCase));
        speech.SelectVoice(voice.Name);
        Assert.Equal(voice.Name, speech.SelectedVoiceName);
        const string text = "你好，这是本地语音进度与字符偏移集成测试。";
        var progress = new ConcurrentBag<SpeechProgressEventArgs>();
        var completion = new TaskCompletionSource<SpeechCompletedEventArgs>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        speech.Progressed += (_, eventArgs) => progress.Add(eventArgs);
        speech.Completed += (_, eventArgs) => completion.TrySetResult(eventArgs);

        var promptId = speech.SpeakAsync(text, 0);
        var result = await completion.Task.WaitAsync(TimeSpan.FromSeconds(15));

        Assert.Equal(promptId, result.PromptId);
        Assert.False(result.Cancelled);
        Assert.Null(result.Error);
        Assert.NotEmpty(progress);
        Assert.All(progress.Where(item => item.PromptId == promptId), item =>
        {
            Assert.InRange(item.CharacterPosition, 0, text.Length);
            Assert.InRange(item.CharacterCount, 1, text.Length - item.CharacterPosition);
        });
    }

    [Fact]
    [Trait("类别", "本机语音集成")]
    public async Task Xiaoxiao_长文本暂停继续停止_收到取消完成且停止后不再推进位置()
    {
        using var stateContext = new DedicatedThreadSynchronizationContext();
        using var speech = new SapiSpeechService();
        var voice = Assert.Single(speech.Voices, item =>
            item.Enabled && item.Name.Contains("Microsoft Xiaoxiao", StringComparison.OrdinalIgnoreCase));
        speech.SelectVoice(voice.Name);
        using var controller = new Core.ReaderController(
            speech,
            new Core.TextChunker(maxChunkLength: 120, minimumBreakLength: 30),
            queueDepth: 3,
            stateContext: stateContext)
        {
            Rate = 5
        };
        var cancelled = new TaskCompletionSource<SpeechCompletedEventArgs>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        speech.Completed += (_, eventArgs) =>
        {
            if (eventArgs.Cancelled)
                cancelled.TrySetResult(eventArgs);
        };
        var text = string.Concat(Enumerable.Repeat(
            "这是 ListenFlow 长文本播放控制测试。它会验证暂停、继续和停止后的回调隔离。", 40));
        controller.SetDocument(text);
        var firstProgress = WaitForChange(controller, args => args.SpokenRange.Length > 0);

        controller.Play();
        await firstProgress.WaitAsync(TimeSpan.FromSeconds(15));
        controller.Pause();
        await Task.Delay(500);
        var pausedPosition = controller.Position;
        await Task.Delay(500);

        Assert.Equal(Core.PlaybackState.Paused, controller.State);
        Assert.Equal(pausedPosition, controller.Position);

        var resumedProgress = WaitForChange(controller, args =>
            args.State == Core.PlaybackState.Speaking && args.Position > pausedPosition);
        controller.Resume();
        await resumedProgress.WaitAsync(TimeSpan.FromSeconds(15));
        controller.Stop();
        var cancelResult = await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var stoppedPosition = controller.Position;
        await Task.Delay(800);

        Assert.True(cancelResult.Cancelled);
        Assert.True(cancelResult.Error is null or OperationCanceledException);
        Assert.Equal(Core.PlaybackState.Stopped, controller.State);
        Assert.Equal(stoppedPosition, controller.Position);
    }

    [Fact]
    [Trait("类别", "本机语音集成")]
    public async Task Xiaoxiao_朗读中连续定位_旧代次取消且只从最后位置继续推进()
    {
        using var stateContext = new DedicatedThreadSynchronizationContext();
        using var speech = new SapiSpeechService();
        var voice = Assert.Single(speech.Voices, item =>
            item.Enabled && item.Name.Contains("Microsoft Xiaoxiao", StringComparison.OrdinalIgnoreCase));
        speech.SelectVoice(voice.Name);
        using var controller = new Core.ReaderController(
            speech,
            new Core.TextChunker(maxChunkLength: 120, minimumBreakLength: 30),
            queueDepth: 3,
            stateContext: stateContext)
        {
            Rate = 5
        };
        var text = string.Concat(Enumerable.Repeat(
            "第一句用于铺垫。第二句用于定位。第三句用于验证真实语音进度。", 60));
        controller.SetDocument(text);
        var initialProgress = WaitForChange(controller, args => args.SpokenRange.Length > 0);
        controller.Play();
        await initialProgress.WaitAsync(TimeSpan.FromSeconds(15));
        var oldGeneration = controller.Generation;
        var firstTarget = text.Length / 3;
        var finalTarget = text.Length * 2 / 3;
        var finalProgress = WaitForChange(controller, args =>
            args.Generation > oldGeneration &&
            args.SpokenRange.Length > 0 &&
            args.Position >= finalTarget);

        controller.SeekTo(firstTarget);
        var intermediateGeneration = controller.Generation;
        controller.SeekTo(finalTarget);
        var finalGeneration = controller.Generation;
        var progress = await finalProgress.WaitAsync(TimeSpan.FromSeconds(15));

        Assert.True(intermediateGeneration > oldGeneration);
        Assert.True(finalGeneration > intermediateGeneration);
        Assert.Equal(finalGeneration, progress.Generation);
        Assert.InRange(progress.Position, finalTarget, text.Length);
        Assert.InRange(progress.SpokenRange.End, progress.Position + 1, text.Length);
        await Task.Delay(500);
        Assert.True(controller.Position >= finalTarget);
        controller.Stop();
    }

    private static Task<Core.ReaderChangedEventArgs> WaitForChange(
        Core.ReaderController controller,
        Func<Core.ReaderChangedEventArgs, bool> predicate)
    {
        var completion = new TaskCompletionSource<Core.ReaderChangedEventArgs>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<Core.ReaderChangedEventArgs>? handler = null;
        handler = (_, eventArgs) =>
        {
            if (!predicate(eventArgs)) return;
            controller.Changed -= handler;
            completion.TrySetResult(eventArgs);
        };
        controller.Changed += handler;
        return completion.Task;
    }
}
