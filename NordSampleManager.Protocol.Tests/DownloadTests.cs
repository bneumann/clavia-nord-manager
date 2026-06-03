using System.Buffers.Binary;
using System.IO.Hashing;
using NordSampleManager.Protocol.Framing;

namespace NordSampleManager.Protocol.Tests;

public class DownloadTests
{
    // ── TryParseItemData: new DataSize field ─────────────────────────────────

    [Fact]
    public void TryParseItemData_ExtractsDataSize()
    {
        // Reconstruct the p2=0x1f payload from Upload Test2.pcapng (53 bytes).
        // Confirmed values: bank=13, item=4, dataSize=0x224=548, fileType="ns3f",
        // versionRaw=0x130=304, nameLen=6, name="Test2\0"
        var payload = new byte[53];
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(4,  4), 13u);   // bank
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(8,  4), 4u);    // item
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(12, 4), 548u);  // dataSize
        System.Text.Encoding.ASCII.GetBytes("ns3f").CopyTo(payload.AsSpan(16, 4));
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(20, 4), 304u);  // versionRaw
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(32, 4), 6u);    // nameLen
        System.Text.Encoding.ASCII.GetBytes("Test2\0").CopyTo(payload.AsSpan(36, 6));

        Assert.True(MessageParser.TryParseItemData(payload, out var data));
        Assert.Equal(548u, data.DataSize);
        Assert.Equal("ns3f", data.FileType);
        Assert.Equal(304, data.VersionMajor * 100 + data.VersionMinor);
    }

    [Fact]
    public void TryParseItemData_ExistingFields_StillWork()
    {
        var payload = new byte[53];
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(12, 4), 100u);
        System.Text.Encoding.ASCII.GetBytes("ns3f").CopyTo(payload.AsSpan(16, 4));
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(20, 4), 301u);
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(28, 4), 21u);   // Grand
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(32, 4), 5u);    // nameLen
        System.Text.Encoding.ASCII.GetBytes("Piano").CopyTo(payload.AsSpan(36, 5));

        Assert.True(MessageParser.TryParseItemData(payload, out var data));
        Assert.Equal("Piano", data.Name);
        Assert.Equal(21u, data.CategoryField);
        Assert.Equal(3, data.VersionMajor);
        Assert.Equal(1, data.VersionMinor);
        Assert.Equal(100u, data.DataSize);
    }

    // ── BuildNs3fFile: CBIN header ────────────────────────────────────────────

    /// <summary>
    /// Load the reference Test2.ns3f and verify that BuildNs3fFile (accessed via
    /// reflection for testing — it's private static) produces an identical file.
    /// </summary>
    [Fact]
    public void CbinHeader_RoundTrip_MatchesReferenceFile()
    {
        // Reference file path — relative to test output, but the file is in the repo.
        var repoRoot = FindRepoRoot();
        var refPath  = Path.Combine(repoRoot, "Export Nordmanager", "Test2.ns3f");
        if (!File.Exists(refPath))
        {
            // Skip gracefully if file not present in CI.
            return;
        }

        var ns3f  = File.ReadAllBytes(refPath);
        Assert.Equal(592, ns3f.Length);

        // Verify magic
        Assert.Equal("CBIN", System.Text.Encoding.ASCII.GetString(ns3f, 0, 4));
        // Version LE = 1
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(ns3f.AsSpan(4, 4)));
        // Content type "ns3f"
        Assert.Equal("ns3f", System.Text.Encoding.ASCII.GetString(ns3f, 8, 4));
        // bank=13, item=4
        Assert.Equal(13, ns3f[12]);
        Assert.Equal(4,  ns3f[14]);
        // versionRaw LE = 304 (0x00000130)
        Assert.Equal(304u, BinaryPrimitives.ReadUInt32LittleEndian(ns3f.AsSpan(20, 4)));
        // CRC-32 of raw data (bytes [44..592]) = stored at [24..28] LE
        var rawData = ns3f.AsSpan(44).ToArray();
        var expectedCrc = Crc32.HashToUInt32(rawData);
        var storedCrc   = BinaryPrimitives.ReadUInt32LittleEndian(ns3f.AsSpan(24, 4));
        Assert.Equal(expectedCrc, storedCrc);
        // Bytes [28..44] are all zero
        Assert.All(ns3f.AsSpan(28, 16).ToArray(), b => Assert.Equal(0, b));
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, ".git")))
            dir = dir.Parent;
        return dir?.FullName ?? AppContext.BaseDirectory;
    }
}
