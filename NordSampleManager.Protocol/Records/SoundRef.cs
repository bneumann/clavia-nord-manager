namespace NordSampleManager.Protocol.Records;

public enum SoundItemType { Piano, Program, SampLib, Song, Synth }

public readonly record struct SoundRef(SoundItemType ItemType, int Bank, int Location)
{
    public override string ToString() => $"{ItemType} B{Bank}.L{Location}";
}
