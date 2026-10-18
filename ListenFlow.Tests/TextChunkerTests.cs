using ListenFlow.Core;

namespace ListenFlow.Tests;

public sealed class TextChunkerTests
{
    [Fact]
    public void Split_长文本_保留所有原文字符与连续偏移()
    {
        const string text = "第一句。第二句包含 English 123。\r\n\r\n下一段没有句号但内容很长，需要安全切分🙂继续。";
        var chunker = new TextChunker(maxChunkLength: 18, minimumBreakLength: 6);

        var chunks = chunker.Split(text);

        Assert.NotEmpty(chunks);
        Assert.Equal(text, string.Concat(chunks.Select(chunk => chunk.Text)));
        Assert.Equal(0, chunks[0].Start);
        for (var index = 1; index < chunks.Count; index++)
        {
            Assert.Equal(chunks[index - 1].End, chunks[index].Start);
        }

        Assert.All(chunks, chunk => Assert.InRange(chunk.Length, 1, 18));
        Assert.DoesNotContain(chunks, chunk =>
            char.IsHighSurrogate(chunk.Text[^1]) || char.IsLowSurrogate(chunk.Text[0]));
    }

    [Fact]
    public void FindSentence_任意阅读位置_返回包含该位置的自然句子()
    {
        const string text = "第一句。第二句！第三句？";
        var chunker = new TextChunker();

        var range = chunker.FindSentence(text, 6);

        Assert.Equal("第二句！", range.Slice(text));
        Assert.Equal(4, range.Start);
    }
}
