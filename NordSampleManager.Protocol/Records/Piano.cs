namespace NordSampleManager.Protocol.Records;

public sealed record Piano(
    int Category,
    int Location,
    string Name,
    string Version,
    long SizeBytes,
    byte[] RawPayload)
{
    public SoundRef Ref => new(SoundItemType.Piano, Category, Location);
}

public sealed record PianoCategory(int Index, string Name);
