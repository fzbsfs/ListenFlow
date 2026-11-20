using ListenFlow.AIReader;

namespace ListenFlow.Tests;

public sealed class DesktopClipboardConnectorTests
{
    [Fact]
    public void 返回文本时保留原文()
    {
        const string text = "第一行\n代码与中文。";
        var result = DesktopClipboardConnector.Read(() => text);
        Assert.True(result.Success);
        Assert.Equal(text, result.Text);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  \r\n")]
    public void 空文本不允许替换当前文章(string value)
    {
        var result = DesktopClipboardConnector.Read(() => value);
        Assert.False(result.Success);
        Assert.Null(result.Text);
        Assert.Contains("没有可朗读", result.Error);
    }

    [Fact]
    public void 读取失败返回错误而不抛给界面()
    {
        var result = DesktopClipboardConnector.Read(() => throw new InvalidOperationException("剪贴板被占用"));
        Assert.False(result.Success);
        Assert.Contains("剪贴板被占用", result.Error);
    }
}
