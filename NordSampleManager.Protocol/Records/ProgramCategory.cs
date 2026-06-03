namespace NordSampleManager.Protocol.Records;

/// <summary>
/// Program category codes sent in p2=0x33 (EditItemOpen) during rename.
/// 3 values confirmed from pcapng RE; remaining 16 are placeholders —
/// verify by running query_programs.py on a live device.
/// </summary>
public enum ProgramCategory : uint
{
    Bass          = 1,    // verified by user
    Acoustic      = 3,    // TODO verify
    Arpeggio      = 2,    // TODO verify
    Clavinet      = 27,   // confirmed
    EPiano1       = 4,    // TODO verify
    EPiano2       = 24,   // confirmed
    Fantasy       = 5,    // TODO verify
    FX            = 6,    // TODO verify
    Grand         = 21,   // confirmed
    GuitarPlucked = 7,    // TODO verify  (display: "Guitar/Plucked")
    Harpsichord   = 8,    // TODO verify
    Lead          = 9,    // TODO verify
    Organ         = 10,   // TODO verify
    Pad           = 11,   // TODO verify
    StringCat     = 12,   // TODO verify  (display: "String")
    Synth         = 13,   // TODO verify
    Undefined     = 14,   // TODO verify
    Upright       = 15,   // TODO verify
    Wind          = 16,   // TODO verify
}

public static class ProgramCategoryExtensions
{
    private static readonly Dictionary<ProgramCategory, string> SpecialNames = new()
    {
        [ProgramCategory.GuitarPlucked] = "Guitar/Plucked",
        [ProgramCategory.StringCat]     = "String",
    };

    public static string DisplayName(this ProgramCategory cat) =>
        SpecialNames.TryGetValue(cat, out var name) ? name : cat.ToString();

    public static ProgramCategory FromCode(uint code) =>
        Enum.IsDefined(typeof(ProgramCategory), code)
            ? (ProgramCategory)code
            : ProgramCategory.Undefined;

    public static IReadOnlyList<ProgramCategory> AllCategories { get; } =
        Enum.GetValues<ProgramCategory>()
            .OrderBy(c => c.DisplayName())
            .ToArray();
}
