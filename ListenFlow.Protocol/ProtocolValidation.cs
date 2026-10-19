namespace ListenFlow.Protocol;

public sealed record ProtocolValidationResult(bool IsValid, string? Code = null, string? Message = null)
{
    public static ProtocolValidationResult Success { get; } = new(true);

    public static ProtocolValidationResult Failure(string code, string message) =>
        new(false, code, message);
}

public static class ProtocolValidator
{
    public static ProtocolValidationResult ValidateCommand(BridgeMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (message.Version != ProtocolConstants.Version)
            return Fail("unsupported_version", $"不支持协议版本 {message.Version}。");
        if (!ProtocolConstants.CommandTypes.Contains(message.Type))
            return Fail("unknown_command", "未知消息类型：" + message.Type);
        if (message.OffsetEncoding is not null &&
            !string.Equals(message.OffsetEncoding, ProtocolConstants.OffsetEncoding, StringComparison.Ordinal))
            return Fail("invalid_offset_encoding", "字符偏移必须使用 UTF-16 代码单元。");

        return message.Type switch
        {
            ProtocolConstants.Types.Hello or ProtocolConstants.Types.Ping => ProtocolValidationResult.Success,
            ProtocolConstants.Types.Start => ValidateStart(message),
            ProtocolConstants.Types.Seek => ValidateSeek(message),
            ProtocolConstants.Types.Pause or ProtocolConstants.Types.Resume or ProtocolConstants.Types.Stop =>
                ValidateSessionCommand(message),
            _ => Fail("unknown_command", "未知消息类型：" + message.Type)
        };
    }

    public static bool IsUtf16Boundary(string text, int offset)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (offset < 0 || offset > text.Length) return false;
        return offset == 0 || offset == text.Length ||
               !(char.IsHighSurrogate(text[offset - 1]) && char.IsLowSurrogate(text[offset]));
    }

    public static bool IsValidRange(string text, int offset, int length)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (offset < 0 || length < 0 || offset > text.Length || length > text.Length - offset)
            return false;
        return IsUtf16Boundary(text, offset) && IsUtf16Boundary(text, offset + length);
    }

    private static ProtocolValidationResult ValidateStart(BridgeMessage message)
    {
        var common = ValidateSnapshotCommand(message);
        if (!common.IsValid) return common;
        if (message.Text is null)
            return Fail("missing_text", "开始朗读消息缺少正文。");
        if (message.Text.Length > ProtocolConstants.MaximumTextLength)
            return Fail("text_too_large", "正文超过协议允许的最大长度。");
        var offset = message.Offset ?? 0;
        if (!IsUtf16Boundary(message.Text, offset))
            return Fail("invalid_offset", "开始位置超出正文范围或切断了 UTF-16 代理项。");
        if (message.Rate is < -10 or > 10)
            return Fail("invalid_rate", "语速必须在 -10 到 10 之间。");
        return ProtocolValidationResult.Success;
    }

    private static ProtocolValidationResult ValidateSeek(BridgeMessage message)
    {
        var common = ValidateSnapshotCommand(message);
        if (!common.IsValid) return common;
        if (message.Offset is null or < 0)
            return Fail("invalid_offset", "定位消息缺少有效的非负 UTF-16 偏移。");
        return ProtocolValidationResult.Success;
    }

    private static ProtocolValidationResult ValidateSnapshotCommand(BridgeMessage message)
    {
        var session = ValidateSessionCommand(message);
        if (!session.IsValid) return session;
        if (string.IsNullOrWhiteSpace(message.SnapshotId))
            return Fail("missing_snapshot", "消息缺少网页正文快照标识。");
        if (message.SnapshotVersion is null or < 0)
            return Fail("invalid_snapshot_version", "消息缺少有效的快照版本。");
        return ProtocolValidationResult.Success;
    }

    private static ProtocolValidationResult ValidateSessionCommand(BridgeMessage message)
    {
        if (string.IsNullOrWhiteSpace(message.SessionId))
            return Fail("missing_session", "消息缺少播放会话标识。");
        if (message.CommandSeq is null or < 0)
            return Fail("invalid_command_seq", "消息缺少有效的命令序号。");
        return ProtocolValidationResult.Success;
    }

    private static ProtocolValidationResult Fail(string code, string message) =>
        ProtocolValidationResult.Failure(code, message);
}
