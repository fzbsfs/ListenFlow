using System.Drawing.Imaging;
using System.Reflection;
using System.Runtime.InteropServices;
using ListenFlow.Core;
using ListenFlow.UI;

namespace ListenFlow.Tests;

[Collection("主窗口隔离测试")]
public sealed class ComicUiVerificationTests
{
    [DllImport("user32.dll")]
    private static extern bool PrintWindow(nint hwnd, nint hdc, uint flags);

    [Fact]
    public void G2漫画票根界面_默认与交互与窄窗渲染无异常并输出截图()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var oldDir = Environment.GetEnvironmentVariable("LISTENFLOW_DATA_DIR");
            var tempDir = Path.Combine(Path.GetTempPath(), "ListenFlow-G2-Verify", Guid.NewGuid().ToString("N"));
            Environment.SetEnvironmentVariable("LISTENFLOW_DATA_DIR", tempDir);
            try
            {
                var outDir = FindStyleRedesignDir();
                using var form = new MainForm();
                form.StartPosition = FormStartPosition.Manual;
                form.Location = new Point(-2400, -2400);
                form.Show();
                Application.DoEvents();

                const string sampleText =
                    "长时间阅读最累的不是理解，而是眼睛一直在找下一行。边听边看，可以把一部分注意力交给耳朵。\r\n\r\n" +
                    "当语音读到某一句时，那一句会被轻轻标出来。你随时可以抬头看别处，回来时一眼就能找到读到了哪里。如果想跳到后面，直接点击那一段文字，语音会从点击处继续。\r\n\r\n" +
                    "正文区保持安静：暖米白的纸面、深色的字、宽松的行距。所有的颜色和个性，都留给上方的票根播放台与右下角的 G2 交互看板娘。";
                Invoke(form, "SetDocument", sampleText, null, "为什么边听边看更省力", "desktop-follow:codex-demo", 68, false, true);

                var queue = Field<List<string>>(form, "_readingQueue");
                queue.Add("本周阅读笔记：把长文章切分成短句后，耳朵接收信息的节奏会明显更轻松。");
                queue.Add("Claude 回复：原窗口跟读与 G2 正式版交接说明。");
                Invoke(form, "UpdateQueueCaption");

                var hero = Field<HeroTicketPlayer>(form, "_heroPlayer");
                var mascot = Field<ComicMascotControl>(form, "_mascot");
                var paper = Field<ComicPaperPanel>(form, "_paperPanel");
                var queuePanel = Field<ComicQueuePanel>(form, "_queuePanel");
                var statusStrip = Field<ComicStatusStrip>(form, "_statusStrip");
                var editor = Field<TextBox>(form, "_editor");
                editor.Select(68, 30);
                paper.SetActiveSentence(68);
                hero.UpdateState(PlaybackState.Speaking, "为什么边听边看更省力", "Codex", true, 68, sampleText.Length, 2, true, true);
                mascot.SyncPlaybackState(PlaybackState.Speaking, 42, 2);

                Assert.True(mascot.Visible);
                Assert.Equal(0, mascot.InteractCount);
                Assert.Contains("0.2.4", form.Text);

                form.Size = new Size(1080, 740);
                Pump(8);
                Assert.True(statusStrip.Visible && statusStrip.Height >= 30 && statusStrip.Bottom <= form.ClientSize.Height,
                    $"默认窗底部状态栏异常：Visible={statusStrip.Visible}, Bounds={statusStrip.Bounds}, Client={form.ClientSize}");
                Assert.True(queuePanel.Height >= 200, $"默认窗待听队列高度不足：{queuePanel.Height}");
                if (outDir is not null)
                    CaptureWindow(form, Path.Combine(outDir, "verify-g2-default.png"));

                mascot.DoHeadpat();
                Assert.Equal(1, mascot.InteractCount);
                Pump(6);
                if (outDir is not null)
                    CaptureWindow(form, Path.Combine(outDir, "verify-g2-happy.png"));

                Invoke(form, "ToggleSettingsPopover");
                Assert.True(Field<ComicPopoverPanel>(form, "_settingsPopover").Visible);
                Pump(6);
                if (outDir is not null)
                    CaptureWindow(form, Path.Combine(outDir, "verify-g2-settings.png"));
                Invoke(form, "ClosePopovers");
                Assert.False(Field<ComicPopoverPanel>(form, "_settingsPopover").Visible);

                form.Size = new Size(780, 540);
                Pump(8);
                Assert.True(paper.Width > 320, $"窄窗下正文纸张宽度过窄：{paper.Width}");
                Assert.True(queuePanel.Height >= 105, $"窄窗下待听队列高度不足以显示当前卡：{queuePanel.Height}");
                Assert.True(statusStrip.Visible && statusStrip.Bottom <= form.ClientSize.Height,
                    $"窄窗底部状态栏异常：Bounds={statusStrip.Bounds}, Client={form.ClientSize}");
                if (outDir is not null)
                    CaptureWindow(form, Path.Combine(outDir, "verify-g2-narrow.png"));

                Invoke(form, "SetMascotVisible", false);
                Assert.False(mascot.Visible);
                Invoke(form, "SetMascotVisible", true);
                Assert.True(mascot.Visible);

                Invoke(form, "ShowFloatingControls");
                var floating = Field<FloatingControls>(form, "_floating");
                Assert.NotNull(floating);
                floating.Location = new Point(-2400, -2400);
                Pump(4);
                if (outDir is not null)
                    CaptureWindow(floating, Path.Combine(outDir, "verify-g2-floating.png"));

                Field<System.Windows.Forms.Timer>(form, "_saveTimer").Stop();
                form.Close();
            }
            catch (Exception ex) { failure = ex; }
            finally
            {
                Environment.SetEnvironmentVariable("LISTENFLOW_DATA_DIR", oldDir);
                try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { }
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(25)), "G2 界面验证超时。");
        if (failure is not null) throw new InvalidOperationException("G2 界面验证失败。", failure);
    }

    private static void Pump(int rounds)
    {
        for (var i = 0; i < rounds; i++)
        {
            Application.DoEvents();
            Thread.Sleep(25);
        }
    }

    private static void CaptureWindow(Form form, string path)
    {
        using var bmp = new Bitmap(form.Width, form.Height);
        using (var g = Graphics.FromImage(bmp))
        {
            var dc = g.GetHdc();
            PrintWindow(form.Handle, dc, 2);
            g.ReleaseHdc(dc);
        }
        bmp.Save(path, ImageFormat.Png);
    }

    private static string? FindStyleRedesignDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "artifacts", "style-redesign");
            if (Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return null;
    }

    private static T Field<T>(object target, string name) where T : class =>
        (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;

    private static object? Invoke(object target, string name, params object?[] args) =>
        target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args);
}
