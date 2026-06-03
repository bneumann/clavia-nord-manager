using System.Buffers.Binary;
using System.Text;
using LanguageExt;
using static LanguageExt.Prelude;
using NordSampleManager.Protocol.Commands;

namespace NordSampleManager.Protocol.Framing;

public static class MessageParser
{
    /// <summary>Functional variant of <see cref="TryParse"/> — returns Left on malformed frames.</summary>
    public static Either<NordError, NordMessage> Parse(ReadOnlyMemory<byte> frame) =>
        TryParse(frame, out var msg)
            ? Right<NordError, NordMessage>(msg)
            : Left<NordError, NordMessage>(new NordError.ParseFailed("Malformed frame: unexpected length or truncation."));

    public static bool TryParse(ReadOnlyMemory<byte> frame, out NordMessage message)
    {
        message = default;
        var span = frame.Span;
        if (span.Length < MessageBuilder.HeaderSize + MessageBuilder.CrcSize) return false;

        var length = BinaryPrimitives.ReadUInt32BigEndian(span[..4]);
        if (length < MessageBuilder.HeaderSize + MessageBuilder.CrcSize || length > span.Length) return false;

        var command = BinaryPrimitives.ReadUInt32BigEndian(span.Slice(4, 4));
        var param1 = BinaryPrimitives.ReadUInt32BigEndian(span.Slice(8, 4));
        var param2 = BinaryPrimitives.ReadUInt32BigEndian(span.Slice(12, 4));
        var payloadLength = (int)length - MessageBuilder.HeaderSize - MessageBuilder.CrcSize;
        var payload = frame.Slice(MessageBuilder.HeaderSize, payloadLength);
        var crc = BinaryPrimitives.ReadUInt16BigEndian(span.Slice((int)length - 2, 2));

        message = new NordMessage(length, command, param1, param2, payload, crc);
        return true;
    }

    public static bool VerifyCrc(ReadOnlyMemory<byte> frame, NordMessage message)
    {
        if (message.Length < MessageBuilder.HeaderSize + MessageBuilder.CrcSize) return false;
        var computed = Crc16Ibm3740.Compute(frame.Span[..((int)message.Length - 2)]);
        return computed == message.Crc;
    }

    /// <summary>
    /// Loose length-prefixed ASCII scanner. Matches nord_api.py._parse_string_list:
    /// scans each byte as a potential length, accepts the following <c>length</c>
    /// bytes if they form printable ASCII. Works for category-list responses where
    /// the strict record framing only covers the first entry.
    /// </summary>
    public static IReadOnlyList<string> ScanLengthPrefixedStrings(ReadOnlyMemory<byte> payload)
    {
        var span = payload.Span;
        var results = new List<string>();
        var offset = 0;
        while (offset < span.Length - 1)
        {
            int length = span[offset];
            if (length is > 0 and < 128 && offset + 1 + length <= span.Length)
            {
                var bytes = span.Slice(offset + 1, length);
                if (IsPrintableAscii(bytes))
                {
                    results.Add(Encoding.ASCII.GetString(bytes));
                    offset += 1 + length;
                    continue;
                }
            }
            offset++;
        }
        return results;
    }

    /// <summary>
    /// Strict record extractor matching interpret_protocol.py:extract_strings —
    /// 5 zero bytes, 4-byte big-endian id, 4-byte big-endian length, ASCII.
    /// Use this for header-style responses where each record carries an id;
    /// for category lists, prefer <see cref="ScanLengthPrefixedStrings"/>.
    /// </summary>
    public static IReadOnlyList<StringRecord> ExtractStrings(ReadOnlyMemory<byte> payload)
    {
        var span = payload.Span;
        var results = new List<StringRecord>();
        var i = 0;
        while (i <= span.Length - 13)
        {
            if (span[i] == 0 && span[i + 1] == 0 && span[i + 2] == 0 && span[i + 3] == 0 && span[i + 4] == 0)
            {
                var id = BinaryPrimitives.ReadUInt32BigEndian(span.Slice(i + 5, 4));
                var len = BinaryPrimitives.ReadUInt32BigEndian(span.Slice(i + 9, 4));
                if (len > 0 && len < 256 && i + 13 + len <= span.Length)
                {
                    var bytes = span.Slice(i + 13, (int)len);
                    if (IsPrintableAscii(bytes))
                    {
                        results.Add(new StringRecord(i, id, Encoding.ASCII.GetString(bytes)));
                        i += 13 + (int)len;
                        continue;
                    }
                }
            }
            i++;
        }
        return results;
    }

    /// <summary>
    /// Decode a p2=0x29 (ItemDetailData) payload into a ProgramDetail.
    /// Confirmed layout from RE of detection+readlibrary new version.pcapng:
    ///   [8..11]  item_id (uint32 BE)
    ///   [16]     is_occupied byte (1 = slot has a program)
    ///   [32]     name_length byte
    ///   [33..]   ASCII program name (name_length bytes, no null terminator)
    /// Returns None when the payload is too short or the slot is empty.
    /// </summary>
    public static Option<ProgramDetail> ParseProgramDetail(ReadOnlyMemory<byte> payload)
    {
        var span = payload.Span;
        if (span.Length < 34) return None;

        var bankId = (int)BinaryPrimitives.ReadUInt32BigEndian(span.Slice(4, 4));
        var itemId = (int)BinaryPrimitives.ReadUInt32BigEndian(span.Slice(8, 4));
        var isOccupied = span[16] != 0;
        var nameLen = span[32];
        if (nameLen == 0 || 33 + nameLen > span.Length) return None;

        var name = Encoding.ASCII.GetString(span.Slice(33, nameLen));
        return Some(new ProgramDetail(bankId, itemId, name, isOccupied));
    }

    /// <summary>
    /// Decode a p2=0x21 (IteratorState) payload.
    /// Layout: [0..3] counter (uint32 BE), [4..7] bank (uint32 BE), [8..11] next_item (uint32 BE).
    /// counter=0 → more items at next_item; counter=1 → end of this bank.
    /// </summary>
    public static Option<IteratorStateData> ParseIteratorState(ReadOnlyMemory<byte> payload)
    {
        var span = payload.Span;
        if (span.Length < 12) return None;
        var counter = BinaryPrimitives.ReadUInt32BigEndian(span[..4]);
        var bank    = BinaryPrimitives.ReadUInt32BigEndian(span.Slice(4, 4));
        var next    = BinaryPrimitives.ReadUInt32BigEndian(span.Slice(8, 4));
        return Some(new IteratorStateData(counter, bank, next));
    }

    /// <summary>Functional variant of <see cref="TryParseItemData"/>.</summary>
    public static Option<ItemData> ParseItemData(ReadOnlyMemory<byte> payload) =>
        TryParseItemData(payload, out var data) ? Some(data) : None;

    public static bool TryParseItemData(ReadOnlyMemory<byte> payload, out ItemData data)
    {
        data = default;
        var span = payload.Span;
        if (span.Length < 40) return false;

        var fileType = Encoding.ASCII.GetString(span.Slice(16, 4));
        var versionRaw = (int)BinaryPrimitives.ReadUInt32BigEndian(span.Slice(20, 4));
        var dataSize = BinaryPrimitives.ReadUInt32BigEndian(span.Slice(12, 4));
        var categoryField = BinaryPrimitives.ReadUInt32BigEndian(span.Slice(28, 4));
        var nameLen = (int)BinaryPrimitives.ReadUInt32BigEndian(span.Slice(32, 4));
        if (nameLen <= 0 || 36 + nameLen > span.Length) return false;

        data = new ItemData(
            Encoding.ASCII.GetString(span.Slice(36, nameLen)),
            fileType,
            versionRaw / 100,
            versionRaw % 100,
            categoryField,
            dataSize);
        return true;
    }

    /// <summary>
    /// Parse a p2=0x15 (DeleteResponse) payload: first uint32 BE is the status code.
    /// Returns false when the payload is too short. status=0 means success.
    /// </summary>
    public static bool ParseDeleteResponse(ReadOnlySpan<byte> payload, out uint status)
    {
        status = 0;
        if (payload.Length < 4) return false;
        status = BinaryPrimitives.ReadUInt32BigEndian(payload[..4]);
        return true;
    }

    private static bool IsPrintableAscii(ReadOnlySpan<byte> bytes)
    {
        foreach (var b in bytes)
            if (b < 0x20 || b > 0x7e) return false;
        return true;
    }
}

public readonly record struct StringRecord(int Offset, uint Id, string Value);

/// <summary>Decoded item metadata from a p2=0x1f (ItemBasicData) response.</summary>
public readonly record struct ItemData(
    string Name,
    string FileType,
    int VersionMajor,
    int VersionMinor,
    uint CategoryField,
    uint DataSize = 0)
{
    public string Version => $"{VersionMajor}.{VersionMinor:D2}";
}

/// <summary>Decoded program detail from a p2=0x29 (ItemDetailData) response.</summary>
public sealed record ProgramDetail(int BankId, int ItemId, string Name, bool IsOccupied);

/// <summary>Decoded state from a p2=0x21 (IteratorState) device→host response.</summary>
public readonly record struct IteratorStateData(uint Counter, uint Bank, uint NextItem)
{
    /// <summary>True when the device signals end-of-bank (no more items to iterate).</summary>
    public bool IsEndOfBank => Counter == 1;
}
