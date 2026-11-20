using ListenFlow.Speech;

namespace ListenFlow.Tests;

public sealed class SapiTextPreparerTests
{
    [Theory]
    [InlineData("中文 💻 继续。", "中文    继续。")]
    [InlineData("甲\U0001F44D\U0001F3FB乙", "甲    乙")]
    [InlineData("\U0001F1E8\U0001F1F3", "    ")]
    public void 补充区符号使用等长占位_后续原文位置保持一致(string original, string expected)
    {
        var prepared = SapiTextPreparer.Prepare(original);
        Assert.Equal(expected, prepared);
        Assert.Equal(original.Length, prepared.Length);
        Assert.Equal(original.IndexOf('乙'), prepared.IndexOf('乙'));
    }

    [Theory]
    [InlineData("扩展汉字\U00020000与\U00030000。")]
    [InlineData("补充区字母\U00010400和数学字母\U0001D400。")]
    [InlineData("中文标点，英文 API 123。\r\n下一段。")]
    public void 有效文字保持原样_包含扩展区汉字(string original)
    {
        Assert.Same(original, SapiTextPreparer.Prepare(original));
    }

    [Fact]
    public void 孤立代理项等长替换_有效代理对保持完整()
    {
        var original = "甲\uD800乙\uDC00丙\U00020000丁";
        var prepared = SapiTextPreparer.Prepare(original);
        Assert.Equal("甲 乙 丙\U00020000丁", prepared);
        Assert.Equal(original.Length, prepared.Length);
    }
}
