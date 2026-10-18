namespace ListenFlow.Core;

public readonly record struct TextRange(int Start, int Length)
{
    public int End => Start + Length;

    public string Slice(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var start = Math.Clamp(Start, 0, text.Length);
        var length = Math.Clamp(Length, 0, text.Length - start);
        return text.Substring(start, length);
    }
}

public sealed record TextSegment(int Start, string Text)
{
    public int Length => Text.Length;
    public int End => Start + Length;
}
