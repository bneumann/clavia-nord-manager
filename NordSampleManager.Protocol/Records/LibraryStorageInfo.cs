namespace NordSampleManager.Protocol.Records;

/// <summary>
/// Storage capacity parsed from a LibraryInfoAck (p2=0x09) response.
/// For piano/sample libraries the unit is 128 KiB allocation blocks;
/// for program libraries the unit is KB.
/// </summary>
public readonly record struct LibraryStorageInfo(long FreeBytes, long UsedBytes)
{
    public long TotalBytes => UsedBytes + FreeBytes;
    public double UsedFraction => TotalBytes > 0 ? (double)UsedBytes / TotalBytes : 0;
}
