namespace NordSampleManager.Protocol.Records;

/// <summary>
/// Metadata for a single occupied program slot, from the confirmed iterator protocol.
/// Patch name and CategoryCode come from p2=0x1f; PianoA comes from p2=0x29.
/// PianoA is null when the program's Piano A layer is inactive (e.g. organ programs).
/// </summary>
public sealed record ProgramInfo(
    string BankLetter,
    int BankId,
    int ItemIndex,
    string Name,
    uint CategoryCode,
    string? PianoA = null)
{
    /// <summary>Physical location on keyboard: row*10+col (e.g. item 0 → 11, item 4 → 15, item 5 → 21).</summary>
    public int Location => (ItemIndex / 5 + 1) * 10 + (ItemIndex % 5 + 1);
}
