using System.Reflection;
using ListenFlow.Core;
using ListenFlow.Speech;

namespace ListenFlow.Tests;

[Collection("主窗口隔离测试")]
public sealed class ReadingQueueTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void 重听当前句_回到句首并保留暂停(bool paused)
    {
        Run((form, reader, speech) =>
        {
            Call(form, "SetDocument", "第一句。第二句。", null, "原文", null, 5, true, false);
            Call(form, "StartPlayback");
            if (paused) Call(form, "PausePlayback");
            Call(form, "RepeatCurrentSentence");
            Assert.Equal(4, reader.Position);
            Assert.Equal(paused ? PlaybackState.Paused : PlaybackState.Speaking, reader.State);
        });
    }

    [Fact]
    public void 从选区位置朗读_不切断代理对且保留整篇正文()
    {
        Run((form, reader, speech) =>
        {
            const string text = "第一句。💻第二句。";
            Call(form, "SetDocument", text, null, "原文", null, 0, true, false);
            Get<TextBox>(form, "_editor").Select(5, 0);
            Call(form, "StartFromEditorSelection");
            Assert.Equal(text, reader.Text);
            Assert.Equal(4, reader.Position);
            Assert.Equal(PlaybackState.Speaking, reader.State);
        });
    }

    [Fact]
    public void 编辑尚未同步时插播_返回新正文起点()
    {
        Run((form, reader, speech) =>
        {
            Call(form, "SetDocument", "第一句。第二句。", null, "原文", null, 4, true, false);
            Get<TextBox>(form, "_editor").Text = "修改后的新正文。";
            Call(form, "InterruptWithText", "插播内容。");
            speech.Finish();
            Application.DoEvents();
            Assert.Equal("修改后的新正文。", reader.Text);
            Assert.Equal(0, reader.Position);
            Assert.Equal(PlaybackState.Ready, reader.State);
        });
    }

    [Fact]
    public void 返回原文启动失败_仍保留原文位置供重试()
    {
        Run((form, reader, speech) =>
        {
            Call(form, "SetDocument", "第一句。第二句。", null, "原文", null, 4, true, false);
            Call(form, "StartPlayback");
            var recovery = reader.GetRecoveryPosition();
            Call(form, "InterruptWithText", "插播内容。");
            speech.Finish();
            speech.FailNextSpeak = true;
            Application.DoEvents();
            Assert.Equal(PlaybackState.Error, reader.State);
            Assert.NotNull(Get<object?>(form, "_returnArticle"));
            Call(form, "RestoreInterruptedArticle");
            Assert.Equal("第一句。第二句。", reader.Text);
            Assert.Equal(recovery, reader.Position);
            Assert.Equal(PlaybackState.Speaking, reader.State);
            Assert.Null(Get<object?>(form, "_returnArticle"));
        });
    }

    [Fact]
    public void 队列窗口所选项自动播出后_移除不误删下一项()
    {
        Run((form, reader, speech) =>
        {
            Call(form, "SetDocument", "原文。", null, "原文", null, 0, true, false);
            var queue = Get<List<string>>(form, "_readingQueue");
            queue.AddRange(["第一待听。", "第二待听。"]);
            Call(form, "StartPlayback");
            using var timer = new System.Windows.Forms.Timer { Interval = 50 };
            Exception? failure = null;
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                var dialog = Application.OpenForms.Cast<Form>().Single(window => window.Text.StartsWith("待听队列 ·", StringComparison.Ordinal));
                try
                {
                    dialog.Controls.OfType<ListBox>().Single().SelectedIndex = 0;
                    speech.Finish();
                    Application.DoEvents();
                    dialog.Controls.OfType<FlowLayoutPanel>().Single().Controls.OfType<Button>()
                        .Single(button => button.Text == "移除选中").PerformClick();
                    Assert.Equal("第二待听。", Assert.Single(queue));
                }
                catch (Exception exception) { failure = exception; }
                finally { dialog.Close(); }
            };
            timer.Start();
            Call(form, "ShowReadingQueue");
            if (failure is not null) throw failure;
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void 插播结束后返回原文_原来暂停则保持安静(bool paused)
    {
        Run((form, reader, speech) =>
        {
            Call(form, "SetDocument", "第一句。第二句。", null, "原文", null, 4, true, false);
            Call(form, "StartPlayback");
            if (paused) Call(form, "PausePlayback");
            var recovery = reader.GetRecoveryPosition();
            Call(form, "InterruptWithClipboard", (Func<string>)(() => "临时内容。"));
            Assert.Equal("临时内容。", reader.Text);
            speech.Finish();
            Application.DoEvents();
            Assert.Equal("第一句。第二句。", reader.Text);
            Assert.Equal(recovery, reader.Position);
            Assert.Equal(paused ? PlaybackState.Ready : PlaybackState.Speaking, reader.State);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void 完成后排队推进_停止可撤销尚未执行的连读(bool stop)
    {
        Run((form, reader, speech) =>
        {
            Call(form, "SetDocument", "原文。", null, "原文", null, 0, true, false);
            Get<List<string>>(form, "_readingQueue").Add("下一篇。");
            Call(form, "StartPlayback");
            speech.Finish();
            if (stop) Call(form, "StopPlayback");
            Application.DoEvents();
            Assert.Equal(stop ? "原文。" : "下一篇。", reader.Text);
            Assert.Equal(stop ? PlaybackState.Stopped : PlaybackState.Speaking, reader.State);
            Assert.Equal(stop ? 1 : 0, Get<List<string>>(form, "_readingQueue").Count);
        });
    }

    [Fact]
    public void 完成回调排队后换文章_不得跳入旧队列()
    {
        Run((form, reader, speech) =>
        {
            Call(form, "SetDocument", "原文。", null, "原文", null, 0, true, false);
            Get<List<string>>(form, "_readingQueue").Add("下一篇。");
            Call(form, "StartPlayback");
            speech.Finish();
            Call(form, "SetDocument", "新文章。", null, "新文章", null, 0, true, false);
            Application.DoEvents();
            Assert.Equal("新文章。", reader.Text);
            Assert.Equal(PlaybackState.Ready, reader.State);
            Assert.Single(Get<List<string>>(form, "_readingQueue"));
        });
    }

    private static void Run(Action<MainForm, ReaderController, QueueSpeech> action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            var old = Environment.GetEnvironmentVariable("LISTENFLOW_DATA_DIR");
            Environment.SetEnvironmentVariable("LISTENFLOW_DATA_DIR", Path.Combine(Path.GetTempPath(), "ListenFlow-Queue", Guid.NewGuid().ToString("N")));
            try
            {
                using var form = new MainForm();
                _ = form.Handle;
                if (typeof(MainForm).GetField("_reader", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form) is ReaderController originalReader)
                    originalReader.Dispose();
                var speech = new QueueSpeech();
                using var reader = new ReaderController(speech, new TextChunker());
                typeof(MainForm).GetField("_reader", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(form, reader);
                reader.Changed += (_, args) => Call(form, "ApplyReaderChange", args);
                action(form, reader, speech);
                Call(form, "StopPlayback");
                Get<System.Windows.Forms.Timer>(form, "_saveTimer").Stop();
            }
            catch (Exception exception) { error = exception; }
            finally { Environment.SetEnvironmentVariable("LISTENFLOW_DATA_DIR", old); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "队列测试未按时结束");
        if (error is not null) throw error;
    }

    private static T Get<T>(MainForm form, string name) => (T)typeof(MainForm).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;
    private static void Call(MainForm form, string name, params object?[] args) => typeof(MainForm).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form, args);

    private sealed class QueueSpeech : ISpeechService
    {
        public event EventHandler<SpeechProgressEventArgs>? Progressed { add { } remove { } }
        public event EventHandler<SpeechCompletedEventArgs>? Completed;
        public IReadOnlyList<LocalVoice> Voices => [];
        public string? SelectedVoiceName => null;
        public bool FailNextSpeak { get; set; }
        private Guid _id;
        public Guid SpeakAsync(string text, int rate)
        {
            if (FailNextSpeak)
            {
                FailNextSpeak = false;
                throw new InvalidOperationException("模拟语音启动失败");
            }
            return _id = Guid.NewGuid();
        }
        public void Finish() => Completed?.Invoke(this, new SpeechCompletedEventArgs(_id, false, null));
        public void SelectVoice(string name) { }
        public void Pause() { }
        public void Resume() { }
        public void CancelAll() { }
        public void Dispose() { }
    }
}
