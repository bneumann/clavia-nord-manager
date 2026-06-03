namespace NordSampleManager.Protocol.Records;

public sealed record Program(
    int Bank,
    int Location,
    string Name,
    string? Category,
    string Version,
    SoundRef? PianoA,
    SoundRef? SampLibA,
    SoundRef? PianoB,
    SoundRef? SampLibB,
    byte[] RawPayload)
{
    public SoundRef Ref => new(SoundItemType.Program, Bank, Location);
}
