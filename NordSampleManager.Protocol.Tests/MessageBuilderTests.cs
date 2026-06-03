using System.Buffers.Binary;
using NordSampleManager.Protocol.Framing;

namespace NordSampleManager.Protocol.Tests;

public class MessageBuilderTests
{
    [Fact]
    public void Build_EmptyPayload_TotalLength_IsHeaderPlusCrcSize()
    {
        var frame = MessageBuilder.Build(command: 0x07, param1: 0, param2: 2, payload: []);
        Assert.Equal(MessageBuilder.HeaderSize + MessageBuilder.CrcSize, frame.Length);
    }

    [Fact]
    public void Build_WithPayload_TotalLength_IncludesPayload()
    {
        var payload = new byte[10];
        var frame = MessageBuilder.Build(command: 0, param1: 0, param2: 0, payload: payload);
        Assert.Equal(MessageBuilder.HeaderSize + 10 + MessageBuilder.CrcSize, frame.Length);
    }

    [Fact]
    public void Build_LengthField_MatchesActualFrameLength()
    {
        var frame = MessageBuilder.Build(command: 0, param1: 0, param2: 0, payload: new byte[4]);
        var lenField = BinaryPrimitives.ReadUInt32BigEndian(frame.AsSpan(0, 4));
        Assert.Equal((uint)frame.Length, lenField);
    }

    [Fact]
    public void Build_HeaderFields_EncodedBigEndian()
    {
        var frame = MessageBuilder.Build(command: 0x0cu, param1: 0x0au, param2: 0x04u, payload: []);
        Assert.Equal(0x0cu, BinaryPrimitives.ReadUInt32BigEndian(frame.AsSpan(4, 4)));
        Assert.Equal(0x0au, BinaryPrimitives.ReadUInt32BigEndian(frame.AsSpan(8, 4)));
        Assert.Equal(0x04u, BinaryPrimitives.ReadUInt32BigEndian(frame.AsSpan(12, 4)));
    }

    [Fact]
    public void Build_WithPayload_PayloadCopiedCorrectly()
    {
        var payload = new byte[] { 0x01, 0x02, 0x03, 0x04 };
        var frame = MessageBuilder.Build(command: 0, param1: 0, param2: 0, payload: payload);
        Assert.Equal(payload, frame[MessageBuilder.HeaderSize..(frame.Length - MessageBuilder.CrcSize)]);
    }

    [Fact]
    public void Build_Crc_IsAtEnd_AndMatchesComputed()
    {
        var frame = MessageBuilder.Build(command: 0x07, param1: 0, param2: 2, payload: []);
        var expected = Crc16Ibm3740.Compute(frame.AsSpan(0, frame.Length - MessageBuilder.CrcSize));
        var stored = BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(frame.Length - MessageBuilder.CrcSize, 2));
        Assert.Equal(expected, stored);
    }

    [Fact]
    public void Build_RoundTrip_TryParse_RecoveredAllFields()
    {
        var payload = new byte[] { 0xAA, 0xBB, 0xCC, 0xDD };
        var frame = MessageBuilder.Build(command: 0x0cu, param1: 0x0au, param2: 0x04u, payload: payload);

        Assert.True(MessageParser.TryParse(frame, out var msg));
        Assert.Equal(0x0cu, msg.Command);
        Assert.Equal(0x0au, msg.Param1);
        Assert.Equal(0x04u, msg.Param2);
        Assert.Equal(payload, msg.Payload.ToArray());
        Assert.True(MessageParser.VerifyCrc(frame, msg));
    }
}
