namespace NordSampleManager.Protocol.Records;

/// <summary>
/// Metadata for a single occupied program slot, from the confirmed iterator protocol (p2=0x29).
/// BankId is 0-indexed (0=Bank A, 1=Bank B, …); ItemIndex is 0-indexed within the bank.
/// </summary>
public sealed record ProgramInfo(
    string BankLetter,
    int BankId,
    int ItemIndex,
    string Name)
{
    /// <summary>Physical location on keyboard: row*10+col (e.g. item 0 → 11, item 4 → 15, item 5 → 21).</summary>
    public int Location => (ItemIndex / 5 + 1) * 10 + (ItemIndex % 5 + 1);
}
