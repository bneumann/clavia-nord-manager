namespace NordSampleManager.Protocol.Records;

public sealed record Piano(
    int CategoryIndex,
    string Category,
    int Location,
    string Name,
    string Version,
    long SizeBytes,
    byte[] RawPayload)
{
    public SoundRef Ref => new(SoundItemType.Piano, CategoryIndex, Location);
}

public sealed record PianoCategory(int Index, string Name);
