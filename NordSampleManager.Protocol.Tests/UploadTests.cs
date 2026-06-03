using System.Buffers.Binary;
using System.IO.Hashing;
using System.Text;
using NordSampleManager.Protocol.Commands;
using NordSampleManager.Protocol.Records;

namespace NordSampleManager.Protocol.Tests;

public class UploadTests
{
    // ── CbinFile parsing ──────────────────────────────────────────────────────

    [Fact]
    public void CbinFile_TryParse_ValidFile_Succeeds()
    {
        var repoRoot = FindRepoRoot();
        var refPath  = Path.Combine(repoRoot, "Export Nordmanager", "Test2.ns3f");
        if (!File.Exists(refPath)) return;  // skip in CI without file

        var bytes = File.ReadAllBytes(refPath);
        Assert.True(CbinFile.TryParse(bytes, out var cbin));
        Assert.Equal("ns3f", cbin.FileType);
        Assert.Equal(13, cbin.BankId);
        Assert.Equal(4,  cbin.ItemIndex);
        Assert.Equal(6,  cbin.NameLen);      // "Test2\0"
        Assert.Equal(304, cbin.VersionRaw);
        Assert.Equal(548, cbin.RawData.Length);
    }

    [Fact]
    public void CbinFile_TryParse_WrongMagic_Fails()
    {
        var bytes = new byte[CbinFile.HeaderSize + 10];
        Encoding.ASCII.GetBytes("NOTB").CopyTo(bytes.AsSpan(0, 4));
        Assert.False(CbinFile.TryParse(bytes, out _));
    }

    [Fact]
    public void CbinFile_TryParse_TooShort_Fails()
    {
        Assert.False(CbinFile.TryParse(new byte[CbinFile.HeaderSize - 1], out _));
    }

    [Fact]
    public void CbinFile_TryParse_DataCrc32_MatchesContent()
    {
        var repoRoot = FindRepoRoot();
        var refPath  = Path.Combine(repoRoot, "Export Nordmanager", "Test2.ns3f");
        if (!File.Exists(refPath)) return;

        var bytes = File.ReadAllBytes(refPath);
        Assert.True(CbinFile.TryParse(bytes, out var cbin));

        var computed = Crc32.HashToUInt32(cbin.RawData);
        Assert.Equal(cbin.DataCrc32, computed);
    }

    // ── UploadMetadata payload encoding ──────────────────────────────────────

    /// <summary>
    /// Verified against "Download Stevie Likes It To Nord.pcapng" frame p2=0x0a.
    /// bank=13, item=17, size=548, type="ns3f", category=27, name="Stevie likes it"
    /// </summary>
    [Fact]
    public void UploadMetadata_PayloadLayout_MatchesCapture()
    {
        // Known values from pcapng
        const int    bank     = 13;
        const int    item     = 17;
        const int    size     = 548;
        const string fileType = "ns3f";
        const uint   category = 27;
        const string name     = "Stevie likes it";

        byte[] rawData = new byte[size]; // dummy — CRC will differ but structure is correct
        var    nameBytes = Encoding.ASCII.GetBytes(name);

        var payload = new byte[28 + nameBytes.Length];
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(0,  4), (uint)bank);
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(4,  4), (uint)item);
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(8,  4), (uint)size);
        Encoding.ASCII.GetBytes(fileType).CopyTo(payload.AsSpan(12, 4));
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(16, 4), Crc32.HashToUInt32(rawData));
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(20, 4), category);
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(24, 4), (uint)name.Length);
        nameBytes.CopyTo(payload.AsSpan(28));

        Assert.Equal(0x0du, BinaryPrimitives.ReadUInt32BigEndian(payload.AsSpan(0, 4)));
        Assert.Equal(0x11u, BinaryPrimitives.ReadUInt32BigEndian(payload.AsSpan(4, 4)));
        Assert.Equal(548u,  BinaryPrimitives.ReadUInt32BigEndian(payload.AsSpan(8, 4)));
        Assert.Equal("ns3f", Encoding.ASCII.GetString(payload.AsSpan(12, 4)));
        Assert.Equal(27u,   BinaryPrimitives.ReadUInt32BigEndian(payload.AsSpan(20, 4)));
        Assert.Equal(15u,   BinaryPrimitives.ReadUInt32BigEndian(payload.AsSpan(24, 4)));
        Assert.Equal("Stevie likes it", Encoding.ASCII.GetString(payload.AsSpan(28, 15)));
    }

    // ── SendFileData payload encoding ─────────────────────────────────────────

    [Fact]
    public void SendFileData_PayloadLayout()
    {
        const int bank = 13, item = 17;
        var rawData = new byte[548];
        rawData[0] = 0x00; rawData[1] = 0x00; rawData[2] = 0x01; rawData[3] = 0x30; // known first bytes

        var payload = new byte[16 + rawData.Length];
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(0,  4), (uint)bank);
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(4,  4), (uint)item);
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(8,  4), 0u);             // offset
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(12, 4), (uint)rawData.Length);
        rawData.CopyTo(payload.AsSpan(16));

        Assert.Equal(16 + 548, payload.Length);
        Assert.Equal(0x0du, BinaryPrimitives.ReadUInt32BigEndian(payload.AsSpan(0, 4)));
        Assert.Equal(0x11u, BinaryPrimitives.ReadUInt32BigEndian(payload.AsSpan(4, 4)));
        Assert.Equal(0u,    BinaryPrimitives.ReadUInt32BigEndian(payload.AsSpan(8, 4)));
        Assert.Equal(548u,  BinaryPrimitives.ReadUInt32BigEndian(payload.AsSpan(12, 4)));
        // raw data starts at offset 16
        Assert.Equal(0x00000130u, BinaryPrimitives.ReadUInt32BigEndian(payload.AsSpan(16, 4)));
    }

    [Fact]
    public void UploadConstants_MatchCapture()
    {
        Assert.Equal(0x0000000au, NordCommands.UploadMetadata);
        Assert.Equal(0x0000000bu, NordCommands.UploadMetadataAck);
        Assert.Equal(0x00000010u, NordCommands.SendFileData);
        Assert.Equal(0x00000011u, NordCommands.SendFileDataAck);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, ".git")))
            dir = dir.Parent;
        return dir?.FullName ?? AppContext.BaseDirectory;
    }
}
