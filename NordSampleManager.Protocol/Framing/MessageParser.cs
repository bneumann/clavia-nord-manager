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

    private static bool IsPrintableAscii(ReadOnlySpan<byte> bytes)
    {
        foreach (var b in bytes)
            if (b < 0x20 || b > 0x7e) return false;
        return true;
    }
}

public readonly record struct StringRecord(int Offset, uint Id, string Value);
