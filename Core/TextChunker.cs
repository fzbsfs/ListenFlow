namespace ListenFlow.Core;

public sealed class TextChunker
{
    private static readonly char[] StrongBreaks = ['\n', '。', '！', '？', '!', '?', '；', ';'];
    private static readonly char[] SoftBreaks = ['，', ',', '、', '：', ':', ' ', '\t'];

    private readonly int _maxChunkLength;
    private readonly int _minimumBreakLength;

    public TextChunker(int maxChunkLength = 420, int minimumBreakLength = 100)
    {
        if (maxChunkLength < 4)
            throw new ArgumentOutOfRangeException(nameof(maxChunkLength), "分段长度至少为 4 个字符。");
        if (minimumBreakLength < 1 || minimumBreakLength >= maxChunkLength)
            throw new ArgumentOutOfRangeException(nameof(minimumBreakLength), "最短分段必须小于最大分段长度。");

        _maxChunkLength = maxChunkLength;
        _minimumBreakLength = minimumBreakLength;
    }

    public IReadOnlyList<TextSegment> Split(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length == 0) return [];

        var result = new List<TextSegment>();
        var start = 0;
        while (start < text.Length)
        {
            var hardEnd = Math.Min(start + _maxChunkLength, text.Length);
            var end = hardEnd;
            if (hardEnd < text.Length)
            {
                end = FindPreferredBreak(text, start, hardEnd, StrongBreaks);
                if (end < 0)
                    end = FindPreferredBreak(text, start, hardEnd, SoftBreaks);
                if (end < 0)
                    end = hardEnd;
            }

            end = KeepUtf16PairTogether(text, start, end);
            result.Add(new TextSegment(start, text[start..end]));
            start = end;
        }

        return result;
    }

    public TextRange FindSentence(string text, int position)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length == 0) return new TextRange(0, 0);

        var cursor = Math.Clamp(position, 0, text.Length - 1);
        var start = cursor;
        while (start > 0 && !IsSentenceBreak(text[start - 1]))
            start--;

        while (start < text.Length && char.IsWhiteSpace(text[start]))
            start++;

        var end = Math.Max(cursor + 1, start + 1);
        while (end < text.Length && !IsSentenceBreak(text[end - 1]))
            end++;

        while (end < text.Length && IsClosingPunctuation(text[end]))
            end++;

        return new TextRange(start, Math.Max(0, end - start));
    }

    public int FindRecoveryPosition(string text, int position) => FindSentence(text, position).Start;

    private int FindPreferredBreak(string text, int start, int hardEnd, char[] breaks)
    {
        var minimum = start + _minimumBreakLength;
        for (var index = hardEnd - 1; index >= minimum; index--)
        {
            if (Array.IndexOf(breaks, text[index]) >= 0)
                return index + 1;
        }

        return -1;
    }

    private static int KeepUtf16PairTogether(string text, int start, int end)
    {
        if (end < text.Length && end > start &&
            char.IsHighSurrogate(text[end - 1]) && char.IsLowSurrogate(text[end]))
        {
            end--;
        }

        return end > start ? end : Math.Min(start + 2, text.Length);
    }

    private static bool IsSentenceBreak(char value) =>
        value is '。' or '！' or '？' or '!' or '?' or '；' or ';' or '\n' or '\r';

    private static bool IsClosingPunctuation(char value) => value is '”' or '’' or '」' or '』' or ')' or '）' or ']' or '】';
}
