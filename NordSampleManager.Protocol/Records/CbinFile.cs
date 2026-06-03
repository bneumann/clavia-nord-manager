using System.Buffers.Binary;
using System.Text;

namespace NordSampleManager.Protocol.Records;

/// <summary>
/// Parsed representation of a Nord .ns3f / .npno CBIN container file.
/// Header layout (44 bytes, integers little-endian):
///   [0..3]   magic "CBIN"
///   [4..7]   version = 1 (LE)
///   [8..11]  file type e.g. "ns3f"
///   [12]     bankId (byte)
///   [13]     0x00
///   [14]     itemIndex (byte)
///   [15]     0x00
///   [16..19] nameLen LE (includes null terminator the Nord appends)
///   [20..23] versionRaw LE
///   [24..27] CRC-32 of rawData (LE)
///   [28..43] zeros (reserved)
/// Note: the program name itself is NOT stored in the CBIN header.
/// Callers should derive the name from the filename or ask the user.
/// </summary>
public sealed record CbinFile(
    string FileType,
    int    BankId,
    int    ItemIndex,
    int    NameLen,
    int    VersionRaw,
    uint   DataCrc32,
    byte[] RawData)
{
    public const int HeaderSize = 44;

    /// <summary>Parse a .ns3f / .npno file. Returns false if the magic or length is invalid.</summary>
    public static bool TryParse(byte[] bytes, out CbinFile file)
    {
        file = null!;
        if (bytes.Length < HeaderSize) return false;

        var span = bytes.AsSpan();
        if (Encoding.ASCII.GetString(span[..4]) != "CBIN") return false;

        var fileType   = Encoding.ASCII.GetString(span.Slice(8, 4));
        var bankId     = (int)span[12];
        var itemIndex  = (int)span[14];
        var nameLen    = (int)BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(16, 4));
        var versionRaw = (int)BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(20, 4));
        var dataCrc32  = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(24, 4));
        var rawData    = bytes[HeaderSize..];

        file = new CbinFile(fileType, bankId, itemIndex, nameLen, versionRaw, dataCrc32, rawData);
        return true;
    }
}
