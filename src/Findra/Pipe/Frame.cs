using System.Buffers.Binary;

namespace Findra.Pipe;

/// <summary>
/// Length-prefixed framing: 4 bytes little-endian payload length, then the payload.
/// A pipe read returns whatever has arrived, not whatever was written, so reads loop.
/// </summary>
public static class Frame
{
    public const int MaxPayload = 32 * 1024 * 1024;

    public static async Task WriteAsync(Stream s, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        if (payload.Length > MaxPayload)
            throw new InvalidDataException($"frame of {payload.Length} exceeds {MaxPayload}");

        byte[] header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        await s.WriteAsync(header, ct).ConfigureAwait(false);
        await s.WriteAsync(payload, ct).ConfigureAwait(false);
        await s.FlushAsync(ct).ConfigureAwait(false);
    }

    public static async Task<byte[]?> ReadAsync(Stream s, CancellationToken ct)
    {
        byte[] header = new byte[4];
        int got = await FillAsync(s, header, ct).ConfigureAwait(false);
        if (got == 0) return null;                       // clean end of stream
        if (got < 4) throw new EndOfStreamException("truncated frame header");

        int len = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (len < 0 || len > MaxPayload)
            throw new InvalidDataException($"frame length {len} out of range");
        if (len == 0) return [];

        byte[] payload = new byte[len];
        if (await FillAsync(s, payload, ct).ConfigureAwait(false) < len)
            throw new EndOfStreamException("truncated frame payload");
        return payload;
    }

    private static async Task<int> FillAsync(Stream s, Memory<byte> buf, CancellationToken ct)
    {
        int total = 0;
        while (total < buf.Length)
        {
            int n = await s.ReadAsync(buf[total..], ct).ConfigureAwait(false);
            if (n == 0) break;
            total += n;
        }
        return total;
    }
}
