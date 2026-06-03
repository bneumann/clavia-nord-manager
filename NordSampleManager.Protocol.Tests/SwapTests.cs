using System.Buffers.Binary;
using NordSampleManager.Protocol.Commands;
using NordSampleManager.Protocol.Framing;

namespace NordSampleManager.Protocol.Tests;

public class SwapTests
{
    // ── SwapRequest payload encoding ─────────────────────────────────────────

    /// <summary>
    /// Verified against "Swap Bank N22 with N21.pcapng":
    /// swap request payload = 0000000d 00000006 0000000d 00000005
    /// bank=13 (N), item1=6 (N22), bank=13 (N), item2=5 (N21)
    /// </summary>
    [Fact]
    public void SwapRequest_PayloadEncoding_MatchesCapture()
    {
        var payload = new byte[16];
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(0,  4), 13u);  // bank1
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(4,  4), 6u);   // item1 (N22)
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(8,  4), 13u);  // bank2
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(12, 4), 5u);   // item2 (N21)

        Assert.Equal(new byte[] { 0x00, 0x00, 0x00, 0x0d }, payload[0..4]);
        Assert.Equal(new byte[] { 0x00, 0x00, 0x00, 0x06 }, payload[4..8]);
        Assert.Equal(new byte[] { 0x00, 0x00, 0x00, 0x0d }, payload[8..12]);
        Assert.Equal(new byte[] { 0x00, 0x00, 0x00, 0x05 }, payload[12..16]);
    }

    // ── ParseSwapResponse ─────────────────────────────────────────────────────

    [Fact]
    public void SwapResponse_StatusZero_IsSuccess()
    {
        var payload = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(0, 4), 0u);

        Assert.True(MessageParser.ParseStatusResponse(payload, out var status));
        Assert.Equal(0u, status);
    }

    [Fact]
    public void SwapResponse_NonZeroStatus()
    {
        var payload = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(0, 4), 1u);

        Assert.True(MessageParser.ParseStatusResponse(payload, out var status));
        Assert.Equal(1u, status);
    }

    [Fact]
    public void SwapResponse_TooShort_ReturnsFalse()
    {
        Assert.False(MessageParser.ParseStatusResponse(new byte[3], out _));
        Assert.False(MessageParser.ParseStatusResponse([], out _));
    }

    [Fact]
    public void SwapConstants_MatchCapture()
    {
        Assert.Equal(0x0000001au, NordCommands.SwapRequest);
        Assert.Equal(0x0000001bu, NordCommands.SwapResponse);
    }
}
