namespace NordSampleManager.Protocol.Records;

public sealed record Synth(
    int Bank,
    int Location,
    string Name,
    string? Category,
    string Version,
    SoundRef? SampLib,
    byte[] RawPayload)
{
    public SoundRef Ref => new(SoundItemType.Synth, Bank, Location);
}
