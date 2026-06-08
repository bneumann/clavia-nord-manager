namespace NordSampleManager.Protocol.Records;

public sealed record SampInfo(
    int Location,      // 0-based sequential index in the single "Samp Lib" bank
    string Name,
    string Version,
    uint CategoryCode,
    long SizeBytes,
    string FileType);  // "nsmp"

public static class SampCategoryExtensions
{
    // Maps nordkeyboards.com sample library category titles to device category codes.
    // When uploading a sample from the web library, use this to derive the categoryCode
    // for the UploadMetadata payload. Ambiguous website categories (Strings, Synth, Brass)
    // are mapped to the most common device sub-code; the device uses the code for display only.
    public static uint FromWebCategoryTitle(string title) => title switch
    {
        "Bass"                 => 0x00010000,
        "Accordion/Harmonium"  => 0x00030000,
        "Guitar/Plucked"       => 0x00050000,
        "Organ"                => 0x00060000,
        "Tuned Percussion"     => 0x00070001,
        "Piano/Keyboard"       => 0x00080000,
        "Strings"              => 0x00090002,
        "Strings Analog"       => 0x00090003,
        "Synth"                => 0x000a0004,
        "Voice/Choir"          => 0x000b0000,
        "Brass"                => 0x000c0002,
        "Orchestral"           => 0x000d0000,
        "Mellotron/Chamberlin" => 0x00100000,
        "Woodwinds"            => 0x000e0000,
        _                      => 0x000e0000,
    };

    // Category codes confirmed by cross-referencing p2=0x1f payloads (detection+readlibrary.pcapng)
    // against the Windows Sound Manager HTML export (Nord Stage 3 Samp Lib 2025-12-13.html).
    public static string DisplayName(uint code) => code switch
    {
        0x00010000 => "Bass",
        0x00030000 => "Accordion/Harm",
        0x00050000 => "Guitar/Plucked",
        0x00060000 => "Organ",
        0x00070001 => "Tuned Percussion",
        0x00080000 => "Piano",
        0x00090001 => "Solo Strings",
        0x00090002 => "Ensemble Strings",
        0x00090003 => "Analog Strings",
        0x000a0003 => "Bass Synth",
        0x000a0004 => "Classic Synth",
        0x000a0007 => "Lead Synth",
        0x000b0000 => "Choir",
        0x000c0001 => "Solo Brass",
        0x000c0002 => "Ensemble Brass",
        0x000d0000 => "Orchestral",
        0x000e0000 => "Misc",
        0x000f0000 => "Classic Synth",  // Super Saw_HN — file pattern matches Classic Synth
        0x00100000 => "Mellotron",
        _ => $"Cat 0x{code:x}",
    };
}
