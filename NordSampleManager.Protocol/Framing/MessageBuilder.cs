using System.Buffers.Binary;

namespace NordSampleManager.Protocol.Framing;

public static class MessageBuilder
{
    public const int HeaderSize = 16;
    public const int CrcSize = 2;

    public static byte[] Build(uint command, uint param1, uint param2, ReadOnlySpan<byte> payload)
    {
        var totalLength = HeaderSize + payload.Length + CrcSize;
        var frame = new byte[totalLength];

        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(0, 4), (uint)totalLength);
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(4, 4), command);
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(8, 4), param1);
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(12, 4), param2);
        payload.CopyTo(frame.AsSpan(HeaderSize, payload.Length));

        var crc = Crc16Ibm3740.Compute(frame.AsSpan(0, HeaderSize + payload.Length));
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(totalLength - CrcSize, CrcSize), crc);

        return frame;
    }
}
