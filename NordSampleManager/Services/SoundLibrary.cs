using System.Collections.ObjectModel;
using LanguageExt;
using static LanguageExt.Prelude;
using NordSampleManager.Protocol;
using NordSampleManager.Protocol.Records;

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
    /// Load all collections from the device. Returns Left on a fatal transport or parse error.
    /// The per-item program query (Param2=0x21, RE hypothesis) is best-effort: on failure it
    /// silently keeps the bank-name fallback already in <see cref="ProgramBanks"/>.
    /// </summary>
    public async Task<Either<NordError, Unit>> LoadAsync(NordClient client, CancellationToken ct = default)
    {
        Clear();

        // Critical queries — all confirmed working. Failure here returns Left.
        var listsResult = await (
            from pianos in client.QueryPianoCategoriesAsync(ct)
            from pianoNames in client.QueryPianoNamesAsync(ct)
            from banks in client.QueryBanksAtoPAsync(ct)
            from samp in client.QuerySampLibAsync(ct)
            from songs in client.QueryBanks1to8V1Async(ct)
            from synths in client.QueryBanks1to8V2Async(ct)
            select (pianos, pianoNames, banks, samp, songs, synths)
        ).ToEither();

        if (listsResult.IsLeft) return listsResult.Map(_ => unit);

        listsResult.IfRight(t =>
        {
            FillFromStrings(PianoCategories, "Piano category", t.pianos);
            FillFromStrings(ProgramBanks, "Program bank", t.banks); // initial bank names
            FillFromStrings(SampLibBanks, "Samp Lib", t.samp);
            FillFromStrings(SongBanks, "Song bank", t.songs);
            FillFromStrings(SynthBanks, "Synth bank", t.synths);
        });

        // Best-effort: replace bank names with rich per-item metadata if the device supports it.
        // Falls back silently to bank names already in ProgramBanks if the query fails.
        var programsResult = await client.QueryAllProgramsAsync(ct);
        var programQueryResult = programsResult.Map(programs =>
        {
            ProgramBanks.Clear();
            foreach (var p in programs)
            {
                var entry = new BankEntry($"Bank {p.BankLetter} · {p.Location:D2}", p.ItemIndex + 1, p.Name);
                if (p.PianoA is not null)
                    entry = entry with { Detail = $"Piano A: {p.PianoA}" };
                ProgramBanks.Add(entry);
            }
            return Unit.Default;
        });
        
        var pianoNamesResult = await client.QueryAllPianoNamesAsync(ct);
        var pianoNameQueryResult = pianoNamesResult.Map(pianoName =>
            {
                PianoCategories.Clear();
                foreach (var p in pianoName)
                {
                    var entry = new BankEntry($"Bank {p.Category} · {p.Location:D2}", p.Location + 1, p.Name);
                    if (p.Version is not null)
                        entry = entry with { Detail = $"Piano A: {p.Version}" };
                    PianoCategories.Add(entry);
                }
                return Unit.Default;
            });
        return programQueryResult.Bind(_ => pianoNameQueryResult);
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
    public string? Detail { get; init; }
    public override string ToString() => $"{Kind} {Index}: {Name}";
}