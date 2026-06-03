using System.Buffers.Binary;
using System.Text;
using NordSampleManager.Protocol.Framing;

namespace NordSampleManager.Protocol.Tests;

public class MessageParserTests
{
    // ── TryParse ──────────────────────────────────────────────────────────────

    [Fact]
    public void TryParse_ValidFrame_ExtractsAllFields()
    {
        var payload = new byte[] { 0x01, 0x02 };
        var frame = MessageBuilder.Build(command: 0x0cu, param1: 0x0au, param2: 0x04u, payload: payload);

        Assert.True(MessageParser.TryParse(frame, out var msg));
        Assert.Equal((uint)frame.Length, msg.Length);
        Assert.Equal(0x0cu, msg.Command);
        Assert.Equal(0x0au, msg.Param1);
        Assert.Equal(0x04u, msg.Param2);
        Assert.Equal(payload, msg.Payload.ToArray());
    }

    [Fact]
    public void TryParse_TooShort_ReturnsFalse()
    {
        Assert.False(MessageParser.TryParse(new byte[MessageBuilder.HeaderSize + MessageBuilder.CrcSize - 1], out _));
        Assert.False(MessageParser.TryParse(Array.Empty<byte>(), out _));
    }

    [Fact]
    public void TryParse_LengthFieldLargerThanBuffer_ReturnsFalse()
    {
        var frame = MessageBuilder.Build(command: 0, param1: 0, param2: 0, payload: []).ToArray();
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(0, 4), (uint)(frame.Length + 10));
        Assert.False(MessageParser.TryParse(frame, out _));
    }

    // ── Parse (functional variant) ────────────────────────────────────────────

    [Fact]
    public void Parse_ValidFrame_ReturnsRight()
    {
        var frame = MessageBuilder.Build(command: 0x07, param1: 0, param2: 2, payload: []);
        Assert.True(MessageParser.Parse(frame).IsRight);
    }

    [Fact]
    public void Parse_TooShort_ReturnsLeft()
    {
        Assert.True(MessageParser.Parse(new byte[10]).IsLeft);
    }

    // ── VerifyCrc ─────────────────────────────────────────────────────────────

    [Fact]
    public void VerifyCrc_ValidFrame_ReturnsTrue()
    {
        var frame = MessageBuilder.Build(command: 0x07, param1: 0, param2: 2, payload: []);
        Assert.True(MessageParser.TryParse(frame, out var msg));
        Assert.True(MessageParser.VerifyCrc(frame, msg));
    }

    [Fact]
    public void VerifyCrc_TamperedPayload_ReturnsFalse()
    {
        var frame = MessageBuilder.Build(command: 0x07, param1: 0, param2: 2, payload: new byte[4]).ToArray();
        Assert.True(MessageParser.TryParse(frame, out var msg));
        frame[MessageBuilder.HeaderSize] ^= 0xFF;  // corrupt first payload byte
        Assert.False(MessageParser.VerifyCrc(frame, msg));
    }

    // ── ParseProgramDetail ────────────────────────────────────────────────────

    [Fact]
    public void ParseProgramDetail_OccupiedSlot_DecodesCorrectly()
    {
        // Layout: [4..7]=bank, [8..11]=item, [16]=is_occupied, [32]=name_len, [33..]=name
        var payload = new byte[40];
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(4, 4), 13u);
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(8, 4), 6u);
        payload[16] = 1;
        payload[32] = 5;
        Encoding.ASCII.GetBytes("Piano").CopyTo(payload.AsSpan(33, 5));

        var result = MessageParser.ParseProgramDetail(payload);
        Assert.True(result.IsSome);
        result.IfSome(d =>
        {
            Assert.Equal(13, d.BankId);
            Assert.Equal(6, d.ItemId);
            Assert.True(d.IsOccupied);
            Assert.Equal("Piano", d.Name);
        });
    }

    [Fact]
    public void ParseProgramDetail_ZeroNameLen_ReturnsNone()
    {
        var payload = new byte[40];
        payload[16] = 1;   // occupied, but…
        payload[32] = 0;   // nameLen = 0 → None
        Assert.True(MessageParser.ParseProgramDetail(payload).IsNone);
    }

    [Fact]
    public void ParseProgramDetail_NotOccupied_ReturnsSomeWithFlagFalse()
    {
        var payload = new byte[40];
        payload[16] = 0;   // not occupied
        payload[32] = 3;
        Encoding.ASCII.GetBytes("ABC").CopyTo(payload.AsSpan(33, 3));

        var result = MessageParser.ParseProgramDetail(payload);
        Assert.True(result.IsSome);
        result.IfSome(d => Assert.False(d.IsOccupied));
    }

    [Fact]
    public void ParseProgramDetail_TooShort_ReturnsNone()
    {
        Assert.True(MessageParser.ParseProgramDetail(new byte[33]).IsNone);
        Assert.True(MessageParser.ParseProgramDetail(Array.Empty<byte>()).IsNone);
    }

    // ── ParseIteratorState ────────────────────────────────────────────────────

    [Fact]
    public void ParseIteratorState_Counter0_MoreItems_IsNotEndOfBank()
    {
        var payload = new byte[12];
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(0, 4), 0u);    // counter=0 → more items
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(4, 4), 13u);   // bank
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(8, 4), 7u);    // next_item

        var result = MessageParser.ParseIteratorState(payload);
        Assert.True(result.IsSome);
        result.IfSome(s =>
        {
            Assert.Equal(13u, s.Bank);
            Assert.Equal(7u, s.NextItem);
            Assert.False(s.IsEndOfBank);
        });
    }

    [Fact]
    public void ParseIteratorState_Counter1_IsEndOfBank()
    {
        var payload = new byte[12];
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(0, 4), 1u);    // counter=1 → end of bank

        var result = MessageParser.ParseIteratorState(payload);
        Assert.True(result.IsSome);
        result.IfSome(s => Assert.True(s.IsEndOfBank));
    }

    [Fact]
    public void ParseIteratorState_TooShort_ReturnsNone()
    {
        Assert.True(MessageParser.ParseIteratorState(new byte[11]).IsNone);
        Assert.True(MessageParser.ParseIteratorState(Array.Empty<byte>()).IsNone);
    }

    // ── ExtractStrings ────────────────────────────────────────────────────────

    [Fact]
    public void ExtractStrings_ValidRecord_ExtractsNameAndId()
    {
        // Record layout: [0x00 × 5][id 4 BE][len 4 BE][ASCII]
        var payload = new byte[13 + 5];
        // bytes [0..4] are already zero (5 zero bytes)
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(5, 4), 0x42u);   // id
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(9, 4), 5u);       // length
        Encoding.ASCII.GetBytes("Hello").CopyTo(payload.AsSpan(13, 5));

        var records = MessageParser.ExtractStrings(payload);
        Assert.Single(records);
        Assert.Equal("Hello", records[0].Value);
        Assert.Equal(0x42u, records[0].Id);
    }

    [Fact]
    public void ExtractStrings_EmptyPayload_ReturnsEmpty()
    {
        Assert.Empty(MessageParser.ExtractStrings(Array.Empty<byte>()));
    }

    [Fact]
    public void ExtractStrings_NoValidRecords_ReturnsEmpty()
    {
        // 13 bytes of non-zero leading bytes — no valid 5-zero prefix
        var payload = new byte[13];
        for (var i = 0; i < payload.Length; i++) payload[i] = 0xFF;
        Assert.Empty(MessageParser.ExtractStrings(payload));
    }

    // ── ScanLengthPrefixedStrings ─────────────────────────────────────────────

    [Fact]
    public void ScanLengthPrefixedStrings_EmptyPayload_ReturnsEmpty()
    {
        Assert.Empty(MessageParser.ScanLengthPrefixedStrings(Array.Empty<byte>()));
    }

    [Fact]
    public void ScanLengthPrefixedStrings_ValidString_ExtractsIt()
    {
        // byte[0] = 3 (length), bytes[1..3] = "ABC"
        var payload = new byte[] { 3, (byte)'A', (byte)'B', (byte)'C' };
        var strings = MessageParser.ScanLengthPrefixedStrings(payload);
        Assert.Equal(["ABC"], strings);
    }

    [Fact]
    public void ScanLengthPrefixedStrings_NonPrintableContent_Skipped()
    {
        // A length byte whose content would be non-printable should not yield a string.
        var payload = new byte[] { 2, 0x01, 0x02, (byte)'X' };  // 0x01, 0x02 not printable
        var strings = MessageParser.ScanLengthPrefixedStrings(payload);
        Assert.DoesNotContain("\x01\x02", strings);
    }

    // ── ItemData.Version ─────────────────────────────────────────────────────

    [Fact]
    public void ItemData_Version_FormatsAsMajorDotMinorTwoDigits()
    {
        var data = new ItemData("Test", "ns3f", VersionMajor: 3, VersionMinor: 1, CategoryField: 0);
        Assert.Equal("3.01", data.Version);
    }

    [Fact]
    public void ItemData_Version_MinorAboveTen_NoPadding()
    {
        var data = new ItemData("Test", "ns3f", VersionMajor: 1, VersionMinor: 30, CategoryField: 0);
        Assert.Equal("1.30", data.Version);
    }
}
