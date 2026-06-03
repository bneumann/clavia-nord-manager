using System.Collections.ObjectModel;
using NordSampleManager.Protocol;
using NordSampleManager.Protocol.Records;
using ProgramCat = NordSampleManager.Protocol.Records.ProgramCategoryExtensions;

namespace NordSampleManager.Services;

/// <summary>
/// Cached snapshot of what we've fetched from the device. Lists are populated
/// from <see cref="DeviceService"/> on connect and observable so views can bind.
/// </summary>
public sealed class SoundLibrary
{
    public ObservableCollection<BankEntry> PianoCategories { get; } = new();
    public ObservableCollection<BankEntry> PianoNames { get; } = new();
    public ObservableCollection<BankEntry> ProgramBanks { get; } = new();
    public ObservableCollection<BankEntry> SampLibBanks { get; } = new();
    public ObservableCollection<BankEntry> SongBanks { get; } = new();
    public ObservableCollection<BankEntry> SynthBanks { get; } = new();

    public void Clear()
    {
        PianoCategories.Clear();
        PianoNames.Clear();
        ProgramBanks.Clear();
        SampLibBanks.Clear();
        SongBanks.Clear();
        SynthBanks.Clear();
    }

    /// <summary>
    /// Load all collections from the device. Throws <see cref="NordException"/> on a fatal
    /// transport or parse error. Per-item program and piano queries are best-effort — on failure
    /// they silently keep the bank-name placeholders already populated.
    /// </summary>
    public async Task LoadAsync(NordClient client, CancellationToken ct = default)
    {
        Clear();

        // Critical queries — failure throws and aborts the load.
        var banks  = await client.QueryBanksAtoPAsync(ct);
        var samp   = await client.QuerySampLibAsync(ct);
        var songs  = await client.QueryBanks1to8V1Async(ct);
        var synths = await client.QueryBanks1to8V2Async(ct);

        FillFromStrings(ProgramBanks, "Program bank", banks);
        FillFromStrings(SampLibBanks, "Samp Lib",     samp);
        FillFromStrings(SongBanks,    "Song bank",    songs);
        FillFromStrings(SynthBanks,   "Synth bank",   synths);

        // Best-effort: replace bank-name placeholders with rich per-item data.
        try
        {
            var programs = await client.QueryAllProgramsAsync(ct);
            ProgramBanks.Clear();
            foreach (var p in programs)
            {
                ProgramBanks.Add(new BankEntry($"Bank {p.BankLetter} · {p.Location:D2}", p.ItemIndex + 1, p.Name)
                {
                    Detail       = BuildProgramDetail(p),
                    Ref          = new SoundRef(SoundItemType.Program, p.BankId, p.ItemIndex),
                    CategoryCode = p.CategoryCode,
                    CategoryName = ProgramCat.FromCode(p.CategoryCode).DisplayName(),
                });
            }
        }
        catch { /* keep placeholder data */ }

        try
        {
            var pianos = await client.QueryAllPianoNamesAsync(ct);
            PianoCategories.Clear();
            foreach (var p in pianos)
            {
                PianoCategories.Add(new BankEntry(p.Category, p.Location + 1, p.Name)
                {
                    Detail = $"Version: {p.Version}"
                });
            }
        }
        catch { /* keep placeholder data */ }
    }

    public static string BuildProgramDetail(ProgramInfo p)
    {
        var cat = ProgramCat.FromCode(p.CategoryCode).DisplayName();
        var lines = new List<string> { $"Category: {cat}" };
        if (p.PianoA is not null) lines.Add($"Piano A:  {p.PianoA}");
        return string.Join("\n", lines);
    }

    private static void FillFromStrings(ObservableCollection<BankEntry> target, string label,
        IReadOnlyList<string> names)
    {
        for (var i = 0; i < names.Count; i++)
            target.Add(new BankEntry(label, i + 1, names[i]));
    }
}

public sealed record BankEntry(string Kind, int Index, string Name)
{
    public string?   Detail       { get; init; }
    public SoundRef? Ref          { get; init; }
    public uint      CategoryCode { get; init; }
    public string?   CategoryName { get; init; }
    public override string ToString() => $"{Kind} {Index}: {Name}";
}
