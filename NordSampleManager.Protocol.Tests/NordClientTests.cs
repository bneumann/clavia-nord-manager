using System.Buffers.Binary;
using NordSampleManager.Protocol.Commands;
using NordSampleManager.Protocol.Framing;

namespace NordSampleManager.Protocol.Tests;

/// <summary>
/// Integration tests for NordClient that verify the command sequences sent to the device
/// and the handling of response frames, using FakeNordDevice as the transport.
/// </summary>
public class NordClientTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    // Builds a minimal valid ack for any param2.
    private static byte[] AckFrame(uint param2) =>
        MessageBuilder.Build(NordCommands.CmdQuery, NordCommands.ParamQuery, param2, []);

    // Builds a status-response frame (p2=DeleteResponse or SwapResponse) with the given status.
    private static byte[] StatusFrame(uint param2, uint status)
    {
        var payload = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(0, 4), status);
        return MessageBuilder.Build(NordCommands.CmdQuery, NordCommands.ParamQuery, param2, payload);
    }

    // Extracts the parsed Param2 from a sent frame for assertion.
    private static uint SentParam2(byte[] frame)
    {
        Assert.True(MessageParser.TryParse(frame, out var msg));
        return msg.Param2;
    }

    // ── DeleteProgramAsync ────────────────────────────────────────────────────

    [Fact]
    public async Task DeleteProgramAsync_Success_SendsLibrarySelectThenDeleteRequest()
    {
        var device = new FakeNordDevice();
        device.EnqueueReply(AckFrame(NordCommands.LibrarySelectAck));         // for LibrarySelect
        device.EnqueueReply(StatusFrame(NordCommands.DeleteResponse, 0u));    // for DeleteRequest

        using var client = new NordClient(device);
        var result = await client.DeleteProgramAsync(bankId: 12, itemIndex: 17).ToEither();

        Assert.True(result.IsRight);
        Assert.Equal(NordCommands.LibrarySelect,  SentParam2(device.SentFrames[0]));
        Assert.Equal(NordCommands.DeleteRequest,  SentParam2(device.SentFrames[1]));
    }

    [Fact]
    public async Task DeleteProgramAsync_Success_EncodesBank_And_ItemInPayload()
    {
        var device = new FakeNordDevice();
        device.EnqueueReply(AckFrame(NordCommands.LibrarySelectAck));
        device.EnqueueReply(StatusFrame(NordCommands.DeleteResponse, 0u));

        using var client = new NordClient(device);
        await client.DeleteProgramAsync(bankId: 12, itemIndex: 17).ToEither();

        MessageParser.TryParse(device.SentFrames[1], out var delMsg);
        var p = delMsg.Payload.Span;
        Assert.Equal(12u, BinaryPrimitives.ReadUInt32BigEndian(p[..4]));        // bank
        Assert.Equal(17u, BinaryPrimitives.ReadUInt32BigEndian(p.Slice(4, 4))); // item
    }

    [Fact]
    public async Task DeleteProgramAsync_NonZeroStatus_ReturnsLeft()
    {
        var device = new FakeNordDevice();
        device.EnqueueReply(AckFrame(NordCommands.LibrarySelectAck));
        device.EnqueueReply(StatusFrame(NordCommands.DeleteResponse, 1u));    // status != 0

        using var client = new NordClient(device);
        var result = await client.DeleteProgramAsync(bankId: 12, itemIndex: 17).ToEither();

        Assert.True(result.IsLeft);
        result.IfLeft(err => Assert.Contains("Delete failed", err.Message));
    }

    [Fact]
    public async Task DeleteProgramAsync_TransportError_ReturnsLeft()
    {
        var device = new FakeNordDevice();
        // No replies queued → first ReceiveAsync returns Left

        using var client = new NordClient(device);
        var result = await client.DeleteProgramAsync(bankId: 0, itemIndex: 0).ToEither();

        Assert.True(result.IsLeft);
    }

    // ── SwapProgramsAsync ─────────────────────────────────────────────────────

    [Fact]
    public async Task SwapProgramsAsync_Success_SendsLibrarySelectThenSwapRequest()
    {
        var device = new FakeNordDevice();
        device.EnqueueReply(AckFrame(NordCommands.LibrarySelectAck));
        device.EnqueueReply(StatusFrame(NordCommands.SwapResponse, 0u));

        using var client = new NordClient(device);
        var result = await client.SwapProgramsAsync(bank1: 13, item1: 6, bank2: 13, item2: 5).ToEither();

        Assert.True(result.IsRight);
        Assert.Equal(NordCommands.LibrarySelect, SentParam2(device.SentFrames[0]));
        Assert.Equal(NordCommands.SwapRequest,   SentParam2(device.SentFrames[1]));
    }

    [Fact]
    public async Task SwapProgramsAsync_Success_EncodesAllFourWordsInPayload()
    {
        var device = new FakeNordDevice();
        device.EnqueueReply(AckFrame(NordCommands.LibrarySelectAck));
        device.EnqueueReply(StatusFrame(NordCommands.SwapResponse, 0u));

        using var client = new NordClient(device);
        await client.SwapProgramsAsync(bank1: 13, item1: 6, bank2: 13, item2: 5).ToEither();

        // Confirmed from "Swap Bank N22 with N21.pcapng": 0000000d 00000006 0000000d 00000005
        MessageParser.TryParse(device.SentFrames[1], out var swapMsg);
        var p = swapMsg.Payload.Span;
        Assert.Equal(13u, BinaryPrimitives.ReadUInt32BigEndian(p[..4]));          // bank1
        Assert.Equal(6u,  BinaryPrimitives.ReadUInt32BigEndian(p.Slice(4,  4)));  // item1
        Assert.Equal(13u, BinaryPrimitives.ReadUInt32BigEndian(p.Slice(8,  4)));  // bank2
        Assert.Equal(5u,  BinaryPrimitives.ReadUInt32BigEndian(p.Slice(12, 4)));  // item2
    }

    [Fact]
    public async Task SwapProgramsAsync_NonZeroStatus_ReturnsLeft()
    {
        var device = new FakeNordDevice();
        device.EnqueueReply(AckFrame(NordCommands.LibrarySelectAck));
        device.EnqueueReply(StatusFrame(NordCommands.SwapResponse, 0xFFu));

        using var client = new NordClient(device);
        var result = await client.SwapProgramsAsync(bank1: 0, item1: 0, bank2: 0, item2: 1).ToEither();

        Assert.True(result.IsLeft);
        result.IfLeft(err => Assert.Contains("Swap failed", err.Message));
    }
}
