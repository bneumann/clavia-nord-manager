namespace NordSampleManager.Protocol.Records;

/// <summary>
/// Program category codes sent in p2=0x33 (EditItemOpen) during rename.
/// All 19 values confirmed by cross-referencing the protocol dump with
/// the Nord Stage 3 Program HTML export via extract_categories.py.
/// </summary>
public enum ProgramCategory : uint
{
    Acoustic = 0,
    Bass = 1,
    Wind = 2,
    Fantasy = 4,
    FX = 5,
    Lead = 6,
    Organ = 7,
    Pad = 8,
    GuitarPlucked = 10,  // display: "Guitar/Plucked"
    StringCat = 11,  // display: "String"
    Synth = 12,
    Grand = 21,
    Upright = 22,
    EPiano1 = 23,
    EPiano2 = 24,
    Clavinet = 27,
    Harpsichord = 28,
    Arpeggio = 30,
    Undefined = 0xFFFF_FFFF,
}

public static class ProgramCategoryExtensions
{
    public static string DisplayName(this ProgramCategory cat) =>
        cat switch
        {
            ProgramCategory.GuitarPlucked => "Guitar/Plucked",
            ProgramCategory.StringCat => "String",
            _ => cat.ToString()
        };

    public static ProgramCategory FromCode(uint code) =>
        Enum.IsDefined(typeof(ProgramCategory), code)
            ? (ProgramCategory)code
            : ProgramCategory.Undefined;

    public static IReadOnlyList<ProgramCategory> AllCategories { get; } =
        Enum.GetValues<ProgramCategory>()
            .OrderBy(c => c.DisplayName())
            .ToArray();
}
