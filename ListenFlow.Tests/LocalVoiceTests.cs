using ListenFlow.Speech;

namespace ListenFlow.Tests;

public sealed class LocalVoiceTests
{
    [Fact]
    public void 语音状态_区分已启用未启用和已知在线_不将可选等同于离线验证()
    {
        Assert.Contains("离线能力需实测", new LocalVoice("甲", "zh-CN", true, "Local-voice").StatusDescription);
        Assert.Contains("未启用", new LocalVoice("乙", "en-US", false, "Local-other").StatusDescription);
        Assert.Contains("在线", new LocalVoice("丙", "zh-CN", true, "Edge-online").StatusDescription);
    }

    [Fact]
    public void 同名本地和在线语音_合并后必须禁止选择()
    {
        var voices = new[]
        {
            new LocalVoice("同名语音", "zh-CN", true, "Local-voice"),
            new LocalVoice("同名语音", "zh-CN", true, "Edge-online")
        };
        var merged = Assert.Single(LocalVoice.Coalesce(voices));
        Assert.True(merged.IsKnownOnline);
    }
    [Theory]
    [InlineData(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Speech\Voices\TokenEnums\NaturalVoiceEnumerator\Edge-zh-CN-TestNeural", true)]
    [InlineData(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Speech\Voices\TokenEnums\NaturalVoiceEnumerator\Azure-en-US-TestNeural", true)]
    [InlineData("Edge-zh-CN-TestNeural", true)]
    [InlineData("Azure-en-US-TestNeural", true)]
    [InlineData("Local-MicrosoftWindows.Voice.zh-CN.Test.1", false)]
    [InlineData(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Speech\Voices\TokenEnums\NaturalVoiceEnumerator\Local-Test", false)]
    [InlineData(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Speech\Voices\Tokens\TTS_TEST", false)]
    public void 识别适配器在线语音标识(string id, bool expected)
    {
        Assert.Equal(expected, LocalVoice.IsKnownOnlineIdentifier(id));
    }
}
