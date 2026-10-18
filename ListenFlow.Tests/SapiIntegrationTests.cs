using ListenFlow.Speech;
using Core = ListenFlow.Core;

namespace ListenFlow.Tests;

public sealed class SapiIntegrationTests
{
    [Fact]
    [Trait("类别", "本机语音集成")]
    public async Task Xiaoxiao_短中文异步朗读_返回对应完成事件()
    {
        using var speech = new SapiSpeechService(new SynchronizationContext());
        var voice = speech.Voices.Single(item =>
            item.Enabled &&
            item.Name.Contains("Microsoft Xiaoxiao", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(item.Culture, "zh-CN", StringComparison.OrdinalIgnoreCase));
        speech.SelectVoice(voice.Name);
        var completion = new TaskCompletionSource<SpeechCompletedEventArgs>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        speech.Completed += (_, eventArgs) => completion.TrySetResult(eventArgs);

        var promptId = speech.SpeakAsync("你好，这是本地语音集成测试。", 0);
        var result = await completion.Task.WaitAsync(TimeSpan.FromSeconds(15));

        Assert.Equal(promptId, result.PromptId);
        Assert.False(result.Cancelled);
        Assert.Null(result.Error);
    }

    [Fact]
    [Trait("类别", "本机语音集成")]
    public async Task Xiaoxiao_长文本暂停继续停止_停止后不再推进位置()
    {
        using var speech = new SapiSpeechService(new SynchronizationContext());
        var voice = speech.Voices.Single(item =>
            item.Enabled && item.Name.Contains("Microsoft Xiaoxiao", StringComparison.OrdinalIgnoreCase));
        speech.SelectVoice(voice.Name);
        using var controller = new Core.ReaderController(
            speech,
            new Core.TextChunker(maxChunkLength: 120, minimumBreakLength: 30),
            queueDepth: 3)
        {
            Rate = 5
        };
        var text = string.Concat(Enumerable.Repeat(
            "这是 ListenFlow 长文本播放控制测试。它会验证暂停、继续和停止后的回调隔离。", 40));
        controller.SetDocument(text);
        var firstProgress = WaitForChange(controller, args => args.Position > 0);

        controller.Play();
        await firstProgress.WaitAsync(TimeSpan.FromSeconds(15));
        controller.Pause();
        await Task.Delay(500);
        var pausedPosition = controller.Position;
        await Task.Delay(500);

        Assert.Equal(Core.PlaybackState.Paused, controller.State);
        Assert.Equal(pausedPosition, controller.Position);

        var resumedProgress = WaitForChange(controller, args => args.Position > pausedPosition);
        controller.Resume();
        await resumedProgress.WaitAsync(TimeSpan.FromSeconds(15));
        controller.Stop();
        var stoppedPosition = controller.Position;
        await Task.Delay(800);

        Assert.Equal(Core.PlaybackState.Stopped, controller.State);
        Assert.Equal(stoppedPosition, controller.Position);
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
