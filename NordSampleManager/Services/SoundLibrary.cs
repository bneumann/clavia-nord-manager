using System.Collections.ObjectModel;
using NordSampleManager.Protocol;
using NordSampleManager.Protocol.Records;
using ProgramCat = NordSampleManager.Protocol.Records.ProgramCategoryExtensions;
using SampCat = NordSampleManager.Protocol.Records.SampCategoryExtensions;
using SynthCat = NordSampleManager.Protocol.Records.SynthCategoryExtensions;

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

    public long PianoStorageUsedBytes => PianoCategories.Sum(e => e.SizeBytes);
    public long PianoStorageFreeBytes { get; private set; }

    public LibraryStorageInfo SampLibStorage { get; private set; }
    public LibraryStorageInfo SynthStorage { get; private set; }
    public LibraryStorageInfo SongStorage  { get; private set; }

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
        var banks = await client.QueryBanksAtoPAsync(ct);
        FillFromStrings(ProgramBanks, "Program bank", banks);

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
            PianoStorageFreeBytes = client.PianoStorage.FreeBytes;
            PianoCategories.Clear();
            foreach (var p in pianos)
            {
                PianoCategories.Add(new BankEntry(p.Category, p.Location + 1, p.Name)
                {
                    Detail    = $"Version: {p.Version}\nSize:    {p.SizeBytes / (1024.0 * 1024.0):F1} MiB",
                    SizeBytes = p.SizeBytes,
                });
            }
        }
        catch { /* keep placeholder data */ }

        try
        {
            var samps = await client.QueryAllSampLibAsync(ct);
            SampLibStorage = client.SampLibStorage;
            SampLibBanks.Clear();
            foreach (var s in samps)
            {
                var cat = SampCat.DisplayName(s.CategoryCode);
                SampLibBanks.Add(new BankEntry(cat, s.Location + 1, s.Name)
                {
                    Detail       = $"Category: {cat}\nVersion:  {s.Version}\nSize:     {s.SizeBytes / (1024.0 * 1024.0):F1} MiB",
                    CategoryCode = s.CategoryCode,
                    CategoryName = cat,
                    SizeBytes    = s.SizeBytes,
                });
            }
        }
        catch { /* keep placeholder data */ }

        try
        {
            var synths = await client.QueryAllSynthsAsync(ct);
            SynthStorage = client.SynthStorage;
            SynthBanks.Clear();
            foreach (var s in synths)
            {
                SynthBanks.Add(new BankEntry($"Bank {s.BankIndex + 1}", s.Location + 1, s.Name)
                {
                    Detail       = $"Version:  {s.Version}\nCategory: {SynthCat.DisplayName(s.CategoryCode)}",
                    CategoryCode = s.CategoryCode,
                    Ref          = new SoundRef(SoundItemType.Synth, s.BankIndex, s.Location),
                });
            }
        }
        catch { /* keep placeholder data */ }

        try
        {
            var songs = await client.QueryAllSongsAsync(ct);
            SongStorage = client.SongStorage;
            SongBanks.Clear();
            foreach (var s in songs)
            {
                SongBanks.Add(new BankEntry($"Bank {s.BankIndex + 1}", s.Location + 1, s.Name)
                {
                    Detail = BuildSongDetail(s),
                    Ref    = new SoundRef(SoundItemType.Song, s.BankIndex, s.Location),
                });
            }
        }
        catch { /* keep placeholder data */ }
    }

    public static string BuildSongDetail(SongInfo s)
    {
        var lines = new List<string> { $"Version: {s.Version}" };
        for (var i = 0; i < s.Programs.Count; i++)
            lines.Add($"Prog {i + 1}:  {s.Programs[i]}");
        return string.Join("\n", lines);
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
    public long      SizeBytes    { get; init; }
    public override string ToString() => $"{Kind} {Index}: {Name}";
}
