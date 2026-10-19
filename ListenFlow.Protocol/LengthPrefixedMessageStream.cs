using System.Buffers.Binary;
using System.Text.Json;

namespace ListenFlow.Protocol;

public sealed class LengthPrefixedMessageStream(Stream input, Stream output)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public async ValueTask<BridgeMessage?> ReadAsync(CancellationToken cancellationToken = default)
    {
        var prefix = new byte[sizeof(int)];
        var prefixCount = await ReadAtMostAsync(input, prefix, cancellationToken).ConfigureAwait(false);
        if (prefixCount == 0) return null;
        if (prefixCount != prefix.Length)
            throw new EndOfStreamException("消息长度前缀不完整。");

        var length = BinaryPrimitives.ReadInt32LittleEndian(prefix);
        if (length <= 0 || length > ProtocolConstants.MaximumFrameBytes)
            throw new InvalidDataException("消息长度无效：" + length);

        var payload = new byte[length];
        await ReadExactlyAsync(input, payload, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<BridgeMessage>(payload, JsonOptions)
               ?? throw new InvalidDataException("消息 JSON 不能为空。");
    }

    public async ValueTask WriteAsync(BridgeMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        var payload = JsonSerializer.SerializeToUtf8Bytes(message, JsonOptions);
        if (payload.Length > ProtocolConstants.MaximumFrameBytes)
            throw new InvalidDataException("消息超过允许的最大长度。");

        var prefix = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(prefix, payload.Length);
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await output.WriteAsync(prefix, cancellationToken).ConfigureAwait(false);
            await output.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private static async ValueTask<int> ReadAtMostAsync(
        Stream stream,
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var count = await stream.ReadAsync(buffer[total..], cancellationToken).ConfigureAwait(false);
            if (count == 0) break;
            total += count;
        }
        return total;
    }

    private static async ValueTask ReadExactlyAsync(
        Stream stream,
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        var total = await ReadAtMostAsync(stream, buffer, cancellationToken).ConfigureAwait(false);
        if (total != buffer.Length)
            throw new EndOfStreamException("消息正文不完整。");
    }
}
