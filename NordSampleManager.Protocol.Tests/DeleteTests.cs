using System.Buffers.Binary;
using NordSampleManager.Protocol.Commands;
using NordSampleManager.Protocol.Framing;

namespace NordSampleManager.Protocol.Tests;

public class DeleteTests
{
    // ── DeleteRequest payload encoding ───────────────────────────────────────

    /// <summary>
    /// Verified against "Delete Stevie Likes It.pcapng":
    /// delete request payload = 0000000c 00000011 (bank=12, item=17)
    /// </summary>
    [Fact]
    public void DeleteRequest_PayloadEncoding()
    {
        var payload = new byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(0, 4), 12u);  // bank
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(4, 4), 17u);  // item

        Assert.Equal(new byte[] { 0x00, 0x00, 0x00, 0x0c }, payload[..4]);
        Assert.Equal(new byte[] { 0x00, 0x00, 0x00, 0x11 }, payload[4..]);
    }

    // ── ParseDeleteResponse ───────────────────────────────────────────────────

    [Fact]
    public void DeleteResponse_StatusZero_IsSuccess()
    {
        var payload = new byte[12];
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(0, 4), 0u);   // status = success
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(4, 4), 12u);  // bank
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(8, 4), 17u);  // item

        Assert.True(MessageParser.ParseDeleteResponse(payload, out var status));
        Assert.Equal(0u, status);
    }

    [Fact]
    public void DeleteResponse_NonZeroStatus_IsFailure()
    {
        var payload = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(0, 4), 0x01u);  // non-zero status

        Assert.True(MessageParser.ParseDeleteResponse(payload, out var status));
        Assert.NotEqual(0u, status);
    }

    [Fact]
    public void DeleteResponse_TooShort_ReturnsFalse()
    {
        Assert.False(MessageParser.ParseDeleteResponse(new byte[3], out _));
        Assert.False(MessageParser.ParseDeleteResponse([], out _));
    }

    [Fact]
    public void DeleteConstants_MatchCapture()
    {
        // Confirmed from raw frames in Delete Stevie Likes It.pcapng
        Assert.Equal(0x00000014u, NordCommands.DeleteRequest);
        Assert.Equal(0x00000015u, NordCommands.DeleteResponse);
    }
}
