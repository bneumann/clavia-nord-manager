namespace NordSampleManager.Protocol.Records;

/// <summary>
/// Metadata for a single program slot as returned by the Param2=0x21 item query.
/// RE findings: see query_programs.py (2026-06-03).
/// </summary>
public sealed record ProgramInfo(
    string BankLetter,
    int Container,
    int ItemIndex,
    string Name,
    string FileType,
    int VersionMajor,
    int VersionMinor,
    uint CategoryField)
{
    /// <summary>Physical location on keyboard: row*10+col (e.g. item 0 → 11, item 4 → 15, item 5 → 21).</summary>
    public int Location => (ItemIndex / 5 + 1) * 10 + (ItemIndex % 5 + 1);

    public string Version => $"{VersionMajor}.{VersionMinor:D2}";
}
