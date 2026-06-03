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
    /// Decode a Param2=0x1f item-metadata payload (response to QueryItem=0x21).
    /// Payload layout (all big-endian):
    ///   [8..12]  echoed index, [12..16] file size,
    ///   [16..20] file type ASCII ("ns3f","npno","nsmp","ns3y","ns3s"),
    ///   [20..24] version×100, [28..32] category, [32..36] name length, [36+] name.
    /// </summary>
    /// <summary>Functional variant of <see cref="TryParseItemData"/> — returns None on malformed payloads.</summary>
    public static Option<ItemData> ParseItemData(ReadOnlyMemory<byte> payload) =>
        TryParseItemData(payload, out var data) ? Some(data) : None;

    public static bool TryParseItemData(ReadOnlyMemory<byte> payload, out ItemData data)
    {
        data = default;
        var span = payload.Span;
        if (span.Length < 40) return false;

        var fileType = Encoding.ASCII.GetString(span.Slice(16, 4));
        var versionRaw = (int)BinaryPrimitives.ReadUInt32BigEndian(span.Slice(20, 4));
        var categoryField = BinaryPrimitives.ReadUInt32BigEndian(span.Slice(28, 4));
        var nameLen = (int)BinaryPrimitives.ReadUInt32BigEndian(span.Slice(32, 4));
        if (nameLen <= 0 || 36 + nameLen > span.Length) return false;

        data = new ItemData(
            Encoding.ASCII.GetString(span.Slice(36, nameLen)),
            fileType,
            versionRaw / 100,
            versionRaw % 100,
            categoryField);
        return true;
    }

    /// <summary>
    /// Returns true when the message is the end-of-container sentinel for item queries.
    /// Param2=0x20 with 0xFFFFFFFF at payload[4..8].
    /// </summary>
    public static bool IsEndMarker(NordMessage msg)
    {
        if (msg.Param2 != NordCommands.ItemResponseEndMarker) return false;
        var span = msg.Payload.Span;
        return span.Length >= 8
            && BinaryPrimitives.ReadUInt32BigEndian(span.Slice(4, 4)) == 0xFFFF_FFFFu;
    }

    private static bool IsPrintableAscii(ReadOnlySpan<byte> bytes)
    {
        foreach (var b in bytes)
            if (b < 0x20 || b > 0x7e) return false;
        return true;
    }
}

public readonly record struct StringRecord(int Offset, uint Id, string Value);

/// <summary>Decoded item metadata from a Param2=0x1f response.</summary>
public readonly record struct ItemData(
    string Name,
    string FileType,
    int VersionMajor,
    int VersionMinor,
    uint CategoryField)
{
    public string Version => $"{VersionMajor}.{VersionMinor:D2}";
}
