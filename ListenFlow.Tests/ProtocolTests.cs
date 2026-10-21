using System.Buffers.Binary;
using System.Text;
using ListenFlow.Protocol;

namespace ListenFlow.Tests;

public sealed class ProtocolTests
{
    [Fact]
    public void Start_包含完整会话快照与Utf16位置_验证通过()
    {
        const string text = "第一句🙂。第二句。";
        var offset = text.IndexOf("第二", StringComparison.Ordinal);
        var message = StartMessage(text, offset);

        var result = ProtocolValidator.ValidateCommand(message);

        Assert.True(result.IsValid, result.Message);
        Assert.Equal(6, offset);
        Assert.Equal(ProtocolConstants.OffsetEncoding, message.OffsetEncoding);
    }

    [Fact]
    public void Start_偏移切断代理项_返回明确错误()
    {
        const string text = "甲🙂乙";
        var message = StartMessage(text, 2);

        var result = ProtocolValidator.ValidateCommand(message);

        Assert.False(result.IsValid);
        Assert.Equal("invalid_offset", result.Code);
    }

    [Theory]
    [InlineData("", "missing_session")]
    [InlineData("   ", "missing_session")]
    public void Start_缺少会话标识_拒绝(string sessionId, string expectedCode)
    {
        var result = ProtocolValidator.ValidateCommand(StartMessage("正文。", 0) with
        {
            SessionId = sessionId
        });

        Assert.False(result.IsValid);
        Assert.Equal(expectedCode, result.Code);
    }

    [Fact]
    public void Seek_缺少快照版本_拒绝()
    {
        var result = ProtocolValidator.ValidateCommand(new BridgeMessage
        {
            Type = ProtocolConstants.Types.Seek,
            SessionId = "session-1",
            SnapshotId = "snapshot-1",
            CommandSeq = 2,
            Offset = 3,
            OffsetEncoding = ProtocolConstants.OffsetEncoding
        });

        Assert.False(result.IsValid);
        Assert.Equal("invalid_snapshot_version", result.Code);
    }

    [Theory]
    [InlineData(2, true)]
    [InlineData(10, true)]
    [InlineData(11, false)]
    public void SetRate_只接受活动会话允许的语速范围(int rate, bool expected)
    {
        var result = ProtocolValidator.ValidateCommand(new BridgeMessage
        {
            Type = ProtocolConstants.Types.SetRate,
            SessionId = "session-1",
            CommandSeq = 2,
            Rate = rate
        });

        Assert.Equal(expected, result.IsValid);
        if (!expected) Assert.Equal("invalid_rate", result.Code);
    }

    [Fact]
    public async Task 长度前缀流_分片读取后完整往返中文消息()
    {
        var output = new MemoryStream();
        var writer = new LengthPrefixedMessageStream(Stream.Null, output);
        var expected = StartMessage("中文🙂正文。", 0);
        await writer.WriteAsync(expected);
        var bytes = output.ToArray();

        await using var fragmented = new FragmentedReadStream(bytes, 3);
        var reader = new LengthPrefixedMessageStream(fragmented, Stream.Null);
        var actual = await reader.ReadAsync();

        Assert.NotNull(actual);
        Assert.Equal(expected, actual);
        Assert.Equal(bytes.Length - sizeof(int), BinaryPrimitives.ReadInt32LittleEndian(bytes));
    }

    [Fact]
    public async Task 长度前缀流_超大长度_在分配正文前拒绝()
    {
        var prefix = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(prefix, ProtocolConstants.MaximumFrameBytes + 1);
        var reader = new LengthPrefixedMessageStream(new MemoryStream(prefix), Stream.Null);

        var error = await Assert.ThrowsAsync<InvalidDataException>(async () => await reader.ReadAsync());

        Assert.Contains("消息长度无效", error.Message);
    }

    [Fact]
    public void Utf16范围_不允许越界或切断代理项()
    {
        const string text = "甲🙂乙";

        Assert.True(ProtocolValidator.IsValidRange(text, 1, 2));
        Assert.False(ProtocolValidator.IsValidRange(text, 2, 1));
        Assert.False(ProtocolValidator.IsValidRange(text, 1, 1));
        Assert.False(ProtocolValidator.IsValidRange(text, 0, text.Length + 1));
    }

    private static BridgeMessage StartMessage(string text, int offset) => new()
    {
        Type = ProtocolConstants.Types.Start,
        RequestId = "request-1",
        SessionId = "session-1",
        SnapshotId = "snapshot-1",
        SnapshotVersion = 1,
        CommandSeq = 1,
        OffsetEncoding = ProtocolConstants.OffsetEncoding,
        Offset = offset,
        Text = text,
        VoiceName = "Microsoft Xiaoxiao",
        Rate = 0
    };

    private sealed class FragmentedReadStream(byte[] bytes, int maximumRead) : Stream
    {
        private readonly MemoryStream _inner = new(bytes);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _inner.Length;
        public override long Position { get => _inner.Position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) =>
            _inner.Read(buffer, offset, Math.Min(count, maximumRead));
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            _inner.ReadAsync(buffer[..Math.Min(buffer.Length, maximumRead)], cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
