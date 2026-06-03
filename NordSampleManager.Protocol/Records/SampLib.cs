namespace NordSampleManager.Protocol.Records;

public sealed record SampLib(
    int Location,
    string Name,
    string? Category,
    string Version,
    long SizeBytes,
    byte[] RawPayload)
{
    public SoundRef Ref => new(SoundItemType.SampLib, Bank: 0, Location);
}
