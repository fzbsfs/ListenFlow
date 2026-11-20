using System.Globalization;

namespace ListenFlow.Speech;

internal static class SapiTextPreparer
{
    public static string Prepare(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        char[]? prepared = null;
        for (var index = 0; index < text.Length; index++)
        {
            if (!char.IsSurrogate(text[index])) continue;

            var isPair = char.IsHighSurrogate(text[index]) &&
                index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]);
            if (isPair)
            {
                var category = CharUnicodeInfo.GetUnicodeCategory(text, index);
                // 补充区图形符号已触发本地语音进度异常。保留等长占位，原文偏移无需重算。
                // 扩展区汉字和补充区字母仍交给语音引擎，不能把所有代理项一起删掉。
                if (category is UnicodeCategory.OtherSymbol or UnicodeCategory.ModifierSymbol)
                {
                    prepared ??= text.ToCharArray();
                    prepared[index] = ' ';
                    prepared[index + 1] = ' ';
                }
                index++;
            }
            else
            {
                // 孤立代理项不是有效 Unicode 字符，也使用一个 UTF-16 单位的占位。
                prepared ??= text.ToCharArray();
                prepared[index] = ' ';
            }
        }

        return prepared is null ? text : new string(prepared);
    }
}
