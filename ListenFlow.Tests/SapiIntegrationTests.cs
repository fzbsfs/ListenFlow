using System.Collections.Concurrent;
using System.Speech.Synthesis;
using ListenFlow.Speech;
using Core = ListenFlow.Core;

namespace ListenFlow.Tests;

public sealed class SapiIntegrationTests
{
    [Fact]
    [Trait("类别", "本机语音集成")]
    public async Task 本机中英文语音_播放中切换_新语音返回真实进度()
    {
        using var context = new DedicatedThreadSynchronizationContext();
        using var speech = new SapiSpeechService(new SynchronizationContext());
        var chinese = Assert.Single(speech.Voices.Where(item => item.Enabled && !item.IsKnownOnline &&
            item.Culture.StartsWith("zh", StringComparison.OrdinalIgnoreCase)).Take(1));
        var english = Assert.Single(speech.Voices.Where(item => item.Enabled && !item.IsKnownOnline &&
            item.Culture.StartsWith("en", StringComparison.OrdinalIgnoreCase)).Take(1));
        speech.SelectVoice(chinese.Name);
        using var reader = new Core.ReaderController(speech, new Core.TextChunker(), 3, context);
        reader.SetDocument(string.Concat(Enumerable.Repeat("语音切换测试。This is a local voice test. ", 60)));
        var firstProgress = WaitForChange(reader, item => item.SpokenRange.Length > 0);
        reader.Play();
        await firstProgress.WaitAsync(TimeSpan.FromSeconds(15));
        var oldGeneration = reader.Generation;
        var before = reader.Position;
        var englishProgress = WaitForChange(reader, item => item.Generation > oldGeneration && item.SpokenRange.Length > 0);
        reader.SelectVoice(english.Name);
        var observed = await englishProgress.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(english.Name, speech.SelectedVoiceName);
        Assert.True(observed.Position >= before);
        Assert.True(observed.SpokenRange.Length > 0);
        reader.Stop();
    }

    [Fact]
    [Trait("类别", "本机语音集成")]
    public async Task 本机语音_暂停后先取消_隔离音频流不再增加()
    {
        using var synthesizer = new SpeechSynthesizer();
        using var output = new MemoryStream();
        synthesizer.SetOutputToWaveStream(output);
        using var voiceCatalog = new SapiSpeechService(new SynchronizationContext());
        var voice = Assert.Single(voiceCatalog.Voices, item => item.Enabled && !item.IsKnownOnline &&
            item.Name.Contains("Microsoft Xiaoxiao", StringComparison.OrdinalIgnoreCase));
        synthesizer.SelectVoice(voice.Name);
        var progressed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        synthesizer.SpeakProgress += (_, _) => progressed.TrySetResult();
        synthesizer.SpeakCompleted += (_, args) => { if (args.Cancelled) cancelled.TrySetResult(); };
        synthesizer.SpeakAsync(string.Concat(Enumerable.Repeat("暂停之后取消这段本地语音。", 200)));
        await progressed.Task.WaitAsync(TimeSpan.FromSeconds(15));
        synthesizer.Pause();
        await Task.Delay(500);
        var before = output.Length;
        synthesizer.SpeakAsyncCancelAll();
        if (synthesizer.State == SynthesizerState.Paused) synthesizer.Resume();
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Task.Delay(500);
        Assert.Equal(before, output.Length);
    }

    [Fact]
    [Trait("类别", "本机语音集成")]
    public async Task 本机语音_暂停时取消队列_新任务可以正常朗读()
    {
        using var speech = new SapiSpeechService(new SynchronizationContext());
        var voice = Assert.Single(speech.Voices, item => item.Enabled &&
            item.Name.Contains("Microsoft Xiaoxiao", StringComparison.OrdinalIgnoreCase));
        speech.SelectVoice(voice.Name);
        var firstProgress = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completions = new ConcurrentDictionary<Guid, TaskCompletionSource<SpeechCompletedEventArgs>>();
        speech.Progressed += (_, _) => firstProgress.TrySetResult();
        speech.Completed += (_, args) => completions.GetOrAdd(args.PromptId,
            _ => new TaskCompletionSource<SpeechCompletedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously))
            .TrySetResult(args);
        var first = speech.SpeakAsync(string.Concat(Enumerable.Repeat("暂停后取消旧语音。", 100)), 0);
        var cancelled = completions.GetOrAdd(first,
            _ => new TaskCompletionSource<SpeechCompletedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously));
        await firstProgress.Task.WaitAsync(TimeSpan.FromSeconds(15));
        speech.Pause();
        speech.CancelAll();
        Assert.True((await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10))).Cancelled);
        var next = speech.SpeakAsync("这是取消后的新任务。", 0);
        var completed = completions.GetOrAdd(next,
            _ => new TaskCompletionSource<SpeechCompletedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously));
        var result = await completed.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.False(result.Cancelled);
        Assert.Null(result.Error);
    }

    [Fact]
    [Trait("类别", "本机语音集成")]
    public async Task 本机试听_取消后释放_不改变正文位置且正文仍可播放()
    {
        using var context = new DedicatedThreadSynchronizationContext();
        using var main = new SapiSpeechService(new SynchronizationContext());
        var voice = Assert.Single(main.Voices, item => item.Enabled &&
            item.Name.Contains("Microsoft Xiaoxiao", StringComparison.OrdinalIgnoreCase));
        main.SelectVoice(voice.Name);
        using var controller = new Core.ReaderController(main, new Core.TextChunker(), 3, context);
        controller.SetDocument("正文阅读位置保持不变。试听结束后继续正文。", 3);
        var originalPosition = controller.Position;
        var cancelled = new TaskCompletionSource<SpeechCompletedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        using (var preview = new SapiSpeechService(new SynchronizationContext()))
        {
            preview.SelectVoice(voice.Name);
            preview.Completed += (_, args) => cancelled.TrySetResult(args);
            preview.SpeakAsync(string.Concat(Enumerable.Repeat("这是一段语音试听。", 30)), 0);
            preview.CancelAll();
            Assert.True((await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10))).Cancelled);
        }
        Assert.Equal(originalPosition, controller.Position);
        var progress = WaitForChange(controller, args => args.SpokenRange.Length > 0);
        controller.Play();
        var observed = await progress.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.True(observed.Position >= originalPosition);
        controller.Stop();
    }
    [Fact]
    [Trait("类别", "本机语音集成")]
    public async Task 本机语音_朗读中刷新被拒绝_停止后刷新保留选择()
    {
        using var speech = new SapiSpeechService(new SynchronizationContext());
        var voice = Assert.Single(speech.Voices, item => item.Enabled &&
            item.Name.Contains("Microsoft Xiaoxiao", StringComparison.OrdinalIgnoreCase));
        speech.SelectVoice(voice.Name);
        var completion = new TaskCompletionSource<SpeechCompletedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        speech.Completed += (_, args) => completion.TrySetResult(args);
        speech.SpeakAsync(string.Concat(Enumerable.Repeat("语音刷新测试。", 100)), 0);
        Assert.Throws<InvalidOperationException>(() => speech.RefreshVoices());
        speech.CancelAll();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var voices = speech.RefreshVoices();
        Assert.Contains(voices, item => item.Enabled && item.Name == voice.Name);
        Assert.Equal(voice.Name, speech.SelectedVoiceName);
    }
    [Fact]
    [Trait("类别", "本机语音集成")]
    public async Task 本机两种语音_播放中与暂停中切换_保持位置并恢复真实进度()
    {
        using var context = new DedicatedThreadSynchronizationContext();
        using var speech = new SapiSpeechService(new SynchronizationContext());
        var voices = speech.Voices.Where(voice => voice.Enabled && voice.Culture.StartsWith("zh", StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
        Assert.True(voices.Length >= 2, "该测试需要至少两种已启用的本机语音。");
        speech.SelectVoice(voices[0].Name);
        using var controller = new Core.ReaderController(speech, new Core.TextChunker(), 3, context);
        controller.SetDocument(string.Concat(Enumerable.Repeat("本地语音切换测试。播放位置应持续向前推进。", 80)));
        var first = WaitForChange(controller, change => change.SpokenRange.Length > 0);
        controller.Play();
        await first.WaitAsync(TimeSpan.FromSeconds(15));
        var previousGeneration = controller.Generation;
        var before = controller.Position;
        var second = WaitForChange(controller, change => change.Generation > previousGeneration && change.SpokenRange.Length > 0);
        controller.SelectVoice(voices[1].Name);
        var changed = await second.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.True(changed.Position >= before);
        Assert.Equal(voices[1].Name, speech.SelectedVoiceName);
        controller.Pause();
        controller.SelectVoice(voices[0].Name);
        var paused = controller.Position;
        await Task.Delay(500);
        Assert.Equal(Core.PlaybackState.Paused, controller.State);
        Assert.Equal(paused, controller.Position);
        var resumed = WaitForChange(controller, change => change.State == Core.PlaybackState.Speaking && change.SpokenRange.Length > 0);
        controller.Resume();
        await resumed.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(voices[0].Name, speech.SelectedVoiceName);
        controller.Stop();
    }
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
