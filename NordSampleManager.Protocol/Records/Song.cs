namespace NordSampleManager.Protocol.Records;

public sealed record Song(
    int Bank,
    int Location,
    string Name,
    string Version,
    IReadOnlyList<string> ProgramNames,
    byte[] RawPayload)
{
    public SoundRef Ref => new(SoundItemType.Song, Bank, Location);
}
