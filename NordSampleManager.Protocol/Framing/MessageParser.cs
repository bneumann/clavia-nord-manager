using System.Buffers.Binary;
using System.Text;

namespace NordSampleManager.Protocol.Framing;

public static class MessageParser
{
    public static bool TryParse(ReadOnlyMemory<byte> frame, out NordMessage message)
    {
        message = default;
        var span = frame.Span;
        if (span.Length < MessageBuilder.HeaderSize + MessageBuilder.CrcSize) return false;

        var length = BinaryPrimitives.ReadUInt32BigEndian(span[..4]);
        if (length < MessageBuilder.HeaderSize + MessageBuilder.CrcSize || length > span.Length) return false;

        var command = BinaryPrimitives.ReadUInt32BigEndian(span.Slice(4, 4));
        var param1  = BinaryPrimitives.ReadUInt32BigEndian(span.Slice(8, 4));
        var param2  = BinaryPrimitives.ReadUInt32BigEndian(span.Slice(12, 4));
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
    /// Loose length-prefixed ASCII scanner. Matches nord_api.py._parse_string_list.
    /// Works for category-list responses where the strict record framing only covers the first entry.
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
                var id  = BinaryPrimitives.ReadUInt32BigEndian(span.Slice(i + 5, 4));
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
    /// Returns null when the payload is too short or nameLen is zero.
    /// Note: IsOccupied=false is a valid result; callers filter by it.
    /// </summary>
    public static ProgramDetail? ParseProgramDetail(ReadOnlyMemory<byte> payload)
    {
        var span = payload.Span;
        if (span.Length < 34) return null;

        var bankId     = (int)BinaryPrimitives.ReadUInt32BigEndian(span.Slice(4, 4));
        var itemId     = (int)BinaryPrimitives.ReadUInt32BigEndian(span.Slice(8, 4));
        var isOccupied = span[16] != 0;
        var nameLen    = span[32];
        if (nameLen == 0 || 33 + nameLen > span.Length) return null;

        var name = Encoding.ASCII.GetString(span.Slice(33, nameLen));
        return new ProgramDetail(bankId, itemId, name, isOccupied);
    }

    /// <summary>
    /// Decode a p2=0x21 (IteratorState) payload.
    /// Returns null when the payload is too short.
    /// </summary>
    public static IteratorStateData? ParseIteratorState(ReadOnlyMemory<byte> payload)
    {
        var span = payload.Span;
        if (span.Length < 12) return null;
        var counter = BinaryPrimitives.ReadUInt32BigEndian(span[..4]);
        var bank    = BinaryPrimitives.ReadUInt32BigEndian(span.Slice(4, 4));
        var next    = BinaryPrimitives.ReadUInt32BigEndian(span.Slice(8, 4));
        return new IteratorStateData(counter, bank, next);
    }

    public static bool TryParseItemData(ReadOnlyMemory<byte> payload, out ItemData data)
    {
        data = default;
        var span = payload.Span;
        if (span.Length < 40) return false;

        var fileType      = Encoding.ASCII.GetString(span.Slice(16, 4));
        var versionRaw    = (int)BinaryPrimitives.ReadUInt32BigEndian(span.Slice(20, 4));
        var dataSize      = BinaryPrimitives.ReadUInt32BigEndian(span.Slice(12, 4));
        var categoryField = BinaryPrimitives.ReadUInt32BigEndian(span.Slice(28, 4));
        var nameLen       = (int)BinaryPrimitives.ReadUInt32BigEndian(span.Slice(32, 4));
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
    /// Parse a status-response payload (p2=0x15 DeleteResponse, p2=0x1b SwapResponse, etc.):
    /// first uint32 BE is the status code. Returns false when too short. status=0 means success.
    /// </summary>
    public static bool ParseStatusResponse(ReadOnlySpan<byte> payload, out uint status)
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
