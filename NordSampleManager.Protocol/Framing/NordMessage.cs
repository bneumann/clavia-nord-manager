namespace NordSampleManager.Protocol.Framing;

public readonly record struct NordMessage(
    uint Length,
    uint Command,
    uint Param1,
    uint Param2,
    ReadOnlyMemory<byte> Payload,
    ushort Crc);
