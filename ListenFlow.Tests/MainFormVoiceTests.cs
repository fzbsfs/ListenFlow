using System.Reflection;
using System.Speech.Synthesis;
using ListenFlow.Communication;
using ListenFlow.Core;
using ListenFlow.Protocol;

namespace ListenFlow.Tests;

[CollectionDefinition("主窗口隔离测试", DisableParallelization = true)]
public sealed class MainFormVoiceTestCollection;

[Collection("主窗口隔离测试")]
public sealed class MainFormVoiceTests
{
    [Fact]
    [Trait("类别", "本机语音集成")]
    public void 主窗口试听停止_释放合成器并保留正文位置()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var oldPath = Environment.GetEnvironmentVariable("LISTENFLOW_DATA_DIR");
            var testPath = Path.Combine(Path.GetTempPath(), "ListenFlow-Voice-UI", Guid.NewGuid().ToString("N"));
            Environment.SetEnvironmentVariable("LISTENFLOW_DATA_DIR", testPath);
            try
            {
                using var form = new MainForm();
                var reader = Field<ReaderController>(form, "_reader");
                var service = Field<ListenFlow.Speech.SapiSpeechService>(form, "_speechService");
                string? statusText = null;
                using (var timer = new System.Windows.Forms.Timer { Interval = 100 })
                {
                    timer.Tick += (_, _) =>
                    {
                        var dialog = System.Windows.Forms.Application.OpenForms
                            .Cast<System.Windows.Forms.Form>().FirstOrDefault(item => item.Text == "语音状态");
                        if (dialog is null) return;
                        timer.Stop();
                        statusText = dialog.Controls.OfType<System.Windows.Forms.TextBox>().Single().Text;
                        dialog.Close();
                    };
                    timer.Start();
                    Invoke(form, "ShowVoiceStatus");
                }
                Assert.NotNull(statusText);
                Assert.Contains("离线", statusText);
                Assert.Contains(service.Voices.First().Name, statusText);
                reader.SetDocument("第一句。第二句。", 3);
                var position = reader.Position;
                Invoke(form, "PreviewSelectedVoice");
                var preview = Field<SpeechSynthesizer>(form, "_previewSynthesizer");
                Assert.NotNull(preview);
                Assert.True((bool)Invoke(form, "StopVoicePreview")!);
                Assert.Null(FieldOrNull(form, "_previewSynthesizer"));
                Assert.Throws<ObjectDisposedException>(() => preview.SpeakAsync("测试"));
                Assert.Equal(position, reader.Position);

                _ = form.Handle;
                Invoke(form, "PreviewSelectedVoice");
                var completedPreview = Field<SpeechSynthesizer>(form, "_previewSynthesizer");
                var previewDeadline = DateTime.UtcNow.AddSeconds(15);
                while (FieldOrNull(form, "_previewSynthesizer") is not null && DateTime.UtcNow < previewDeadline)
                {
                    System.Windows.Forms.Application.DoEvents();
                    Thread.Sleep(20);
                }
                Assert.Null(FieldOrNull(form, "_previewSynthesizer"));
                Assert.Throws<ObjectDisposedException>(() => completedPreview.SpeakAsync("测试"));
                Assert.Equal(position, reader.Position);

                var voiceBox = Field<System.Windows.Forms.ComboBox>(form, "_voiceBox");
                Assert.True(voiceBox.Items.Count >= 2, "该测试需要至少两种可选语音。");
                var initialIndex = voiceBox.SelectedIndex;
                var englishVoice = service.Voices.First(item => item.Enabled && !item.IsKnownOnline &&
                    item.Culture.StartsWith("en", StringComparison.OrdinalIgnoreCase));
                voiceBox.SelectedIndex = Enumerable.Range(0, voiceBox.Items.Count)
                    .First(index => voiceBox.Items[index]!.ToString()!.StartsWith(englishVoice.Name + "（", StringComparison.Ordinal));
                Invoke(form, "PreviewSelectedVoice");
                var englishPreview = Field<SpeechSynthesizer>(form, "_previewSynthesizer");
                Assert.Equal(englishVoice.Name, englishPreview.Voice.Name);
                Assert.StartsWith("en", englishPreview.Voice.Culture.Name, StringComparison.OrdinalIgnoreCase);
                englishPreview.SpeakAsyncCancelAll();
                form.GetType().GetMethod("CompleteVoicePreview", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(form, new object[] { englishPreview, new InvalidOperationException("模拟语音设备故障") });
                Assert.Null(FieldOrNull(form, "_previewSynthesizer"));
                Assert.Contains("模拟语音设备故障", Field<System.Windows.Forms.Label>(form, "_statusLabel").Text);
                Assert.Throws<ObjectDisposedException>(() => englishPreview.SpeakAsync("测试"));
                Assert.Equal(position, reader.Position);
                voiceBox.SelectedIndex = initialIndex;
                reader.Play();
                Invoke(form, "UpdateControls");
                Assert.True(voiceBox.Enabled);
                var generation = reader.Generation;
                voiceBox.SelectedIndex = (voiceBox.SelectedIndex + 1) % voiceBox.Items.Count;
                Assert.Equal(PlaybackState.Speaking, reader.State);
                Assert.True(reader.Generation > generation);
                Assert.Contains(service.SelectedVoiceName!, voiceBox.SelectedItem!.ToString());

                reader.Pause();
                Invoke(form, "UpdateControls");
                Assert.True(voiceBox.Enabled);
                voiceBox.SelectedIndex = (voiceBox.SelectedIndex + 1) % voiceBox.Items.Count;
                Assert.Equal(PlaybackState.Paused, reader.State);
                Assert.Contains(service.SelectedVoiceName!, voiceBox.SelectedItem!.ToString());
                reader.Stop();

                var names = service.Voices.Where(voice => voice.Enabled && !voice.IsKnownOnline)
                    .Select(voice => voice.Name).Take(2).ToArray();
                var history = Field<ListenFlow.Storage.RecentArticleStore>(form, "_recentStore");
                history.Save(ListenFlow.Storage.ReaderState.Create("语音测试甲", "甲正文。", 1, null, names[0], 0, "voice-test-a"));
                history.Save(ListenFlow.Storage.ReaderState.Create("语音测试乙", "乙正文。", 1, null, names[1], 0, "voice-test-b"));
                Invoke(form, "RefreshHistory");
                var historyBox = Field<System.Windows.Forms.ComboBox>(form, "_historyBox");
                historyBox.SelectedIndex = Enumerable.Range(0, historyBox.Items.Count)
                    .Single(i => historyBox.Items[i]!.ToString() == "语音测试甲");
                Assert.Equal(names[0], service.SelectedVoiceName);
                Assert.Equal(names[0], history.Load("voice-test-a")!.VoiceName);
                historyBox.SelectedIndex = Enumerable.Range(0, historyBox.Items.Count)
                    .Single(i => historyBox.Items[i]!.ToString() == "语音测试乙");
                Assert.Equal(names[1], service.SelectedVoiceName);
                Assert.Equal(names[1], history.Load("voice-test-b")!.VoiceName);

                history.Save(ListenFlow.Storage.ReaderState.Create("语音测试丙", "丙正文。", 1, null, "暂不可用的语音", 0, "voice-test-c"));
                Invoke(form, "RefreshHistory");
                historyBox.SelectedIndex = Enumerable.Range(0, historyBox.Items.Count)
                    .Single(i => historyBox.Items[i]!.ToString() == "语音测试丙");
                Invoke(form, "MarkStateDirty");
                form.GetType().GetMethod("SaveStateIfDirty", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(form, new object[] { true });
                Assert.Equal("暂不可用的语音", history.Load("voice-test-c")!.VoiceName);
                using (var reopened = new MainForm())
                {
                    var reopenedState = (ListenFlow.Storage.ReaderState)Invoke(reopened, "CreateCurrentState")!;
                    Assert.Equal("voice-test-c", reopenedState.DocumentId);
                    Assert.Equal("暂不可用的语音", reopenedState.VoiceName);
                    Assert.Contains("不可用", Field<System.Windows.Forms.Label>(reopened, "_statusLabel").Text);
                }
                typeof(System.Windows.Forms.ComboBox).GetMethod("OnSelectionChangeCommitted", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(voiceBox, new object[] { EventArgs.Empty });
                Assert.Equal(service.SelectedVoiceName, history.Load("voice-test-c")!.VoiceName);

                history.Save(ListenFlow.Storage.ReaderState.Create("语音测试丙", "丙正文。", 1, null, "暂不可用的语音", 0, "voice-test-c"));
                Invoke(form, "RefreshHistory");
                historyBox.SelectedIndex = Enumerable.Range(0, historyBox.Items.Count)
                    .Single(i => historyBox.Items[i]!.ToString() == "语音测试乙");
                historyBox.SelectedIndex = Enumerable.Range(0, historyBox.Items.Count)
                    .Single(i => historyBox.Items[i]!.ToString() == "语音测试丙");
                voiceBox.SelectedIndex = (voiceBox.SelectedIndex + 1) % voiceBox.Items.Count;
                Assert.Equal(service.SelectedVoiceName, history.Load("voice-test-c")!.VoiceName);

                voiceBox.SelectedIndex = Enumerable.Range(0, voiceBox.Items.Count)
                    .First(index => voiceBox.Items[index]!.ToString()!.StartsWith(names[1] + "（", StringComparison.Ordinal));
                form.GetType().GetField("_unavailableVoiceName", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .SetValue(form, names[0]);
                Invoke(form, "RefreshVoices");
                Assert.Equal(names[0], service.SelectedVoiceName);
                Assert.Equal(names[0], history.Load("voice-test-c")!.VoiceName);

                _ = form.Handle;
                using (var router = new DesktopCommandRouter(form,
                           SynchronizationContext.Current ?? new SynchronizationContext()))
                {
                    var outbound = new List<BridgeMessage>();
                    router.Outbound += (_, message) => outbound.Add(message);
                    var started = router.Handle(new BridgeMessage
                    {
                        Type = ProtocolConstants.Types.Start,
                        RequestId = "voice-switch-start",
                        SessionId = "voice-switch-session",
                        SnapshotId = "voice-switch-snapshot",
                        SnapshotVersion = 1,
                        CommandSeq = 1,
                        OffsetEncoding = ProtocolConstants.OffsetEncoding,
                        Offset = 0,
                        Text = string.Concat(Enumerable.Repeat("网页语音切换进度测试。", 80))
                    });
                    Assert.Equal(ProtocolConstants.Types.Ack, started.Type);
                    var oldGeneration = reader.Generation;
                    voiceBox.SelectedIndex = (voiceBox.SelectedIndex + 1) % voiceBox.Items.Count;
                    var newGeneration = reader.Generation;
                    Assert.True(newGeneration > oldGeneration);
                    var deadline = DateTime.UtcNow.AddSeconds(15);
                    while (!outbound.Any(item => item.Type == ProtocolConstants.Types.Progress &&
                               item.PlaybackGeneration == newGeneration) && DateTime.UtcNow < deadline)
                    {
                        System.Windows.Forms.Application.DoEvents();
                        Thread.Sleep(20);
                    }
                    Assert.Contains(outbound, item => item.Type == ProtocolConstants.Types.Progress &&
                        item.PlaybackGeneration == newGeneration && item.SessionId == "voice-switch-session" &&
                        item.SnapshotId == "voice-switch-snapshot" && item.Length > 0);
                    Assert.True(form.StartClipboardReading(() => "这是本地剪贴板朗读测试。"));
                    Assert.Contains(outbound, item => item.Code == "session_replaced" &&
                        item.SessionId == "voice-switch-session");
                    Assert.Null(router.CreateReadyMessage().SessionId);
                    Assert.Equal("这是本地剪贴板朗读测试。", ((IRemotePlaybackTarget)form).Text);
                    Assert.False(form.StartClipboardReading(() => "  "));
                    Assert.Equal("这是本地剪贴板朗读测试。", ((IRemotePlaybackTarget)form).Text);
                    reader.Stop();
                }
                var callbackRan = false;
                var ui = form.GetType().GetMethod("Ui", BindingFlags.Instance | BindingFlags.NonPublic)!;
                Task.Run(() => ui.Invoke(form, new object[] { (Action)(() => callbackRan = true) })).GetAwaiter().GetResult();
                var closing = form.GetType().GetField("_closing", BindingFlags.Instance | BindingFlags.NonPublic)!;
                closing.SetValue(form, true);
                System.Windows.Forms.Application.DoEvents();
                Assert.False(callbackRan);
                closing.SetValue(form, false);
            }
            catch (Exception exception) { failure = exception; }
            finally { Environment.SetEnvironmentVariable("LISTENFLOW_DATA_DIR", oldPath); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "主窗口测试超时。");
        if (failure is not null) throw new InvalidOperationException("主窗口语音测试失败。", failure);
    }

    private static object? FieldOrNull(object target, string name) =>
        target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target);
    private static T Field<T>(object target, string name) where T : class =>
        (T?)FieldOrNull(target, name) ?? throw new InvalidOperationException("缺少测试对象：" + name);
    private static object? Invoke(object target, string name) =>
        target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, null);
}
