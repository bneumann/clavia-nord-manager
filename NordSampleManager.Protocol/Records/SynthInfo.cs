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
    // Category codes confirmed by cross-referencing p2=0x1f payloads (detection+readlibrary.pcapng)
    // against the Windows Sound Manager HTML export (Nord Stage 3 Synth 2025-12-13.html).
    public static string DisplayName(uint code) => code switch
    {
        0x00020000 => "Drums",
        0x00040000 => "Effects",
        0x00070001 => "Tuned Percussion",
        0x00080000 => "Piano",
        0x00090003 => "Analog Strings",
        0x000a0001 => "Pad Synth",
        0x000a0003 => "Bass Synth",
        0x000a0004 => "Classic Synth",
        0x000a0007 => "Lead Synth",
        0x000e0000 => "Misc",
        0x00110000 => "Rhythmic",
        _ => $"Cat 0x{code:x}",
    };
}
