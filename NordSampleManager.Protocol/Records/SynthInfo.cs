namespace NordSampleManager.Protocol.Records;

public sealed record SynthInfo(
    int BankIndex,     // 0–7
    int Location,      // 0–49
    string Name,
    string Version,
    uint CategoryCode, // raw from ItemBasicData.CategoryField; codes confirmed from live device
    string FileType);  // "ns3y"

public static class SynthCategoryExtensions
{
    public static string DisplayName(uint code) => code switch
    {
        // TODO: fill in confirmed code → name mapping after live device capture.
        _ => $"Cat 0x{code:x}",
    };
}
