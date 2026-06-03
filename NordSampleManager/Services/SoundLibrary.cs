using System.Collections.ObjectModel;
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
    public ObservableCollection<BankEntry> ProgramBanks { get; } = new();
    public ObservableCollection<BankEntry> SampLibBanks { get; } = new();
    public ObservableCollection<BankEntry> SongBanks { get; } = new();
    public ObservableCollection<BankEntry> SynthBanks { get; } = new();

    public void Clear()
    {
        PianoCategories.Clear();
        ProgramBanks.Clear();
        SampLibBanks.Clear();
        SongBanks.Clear();
        SynthBanks.Clear();
    }

    public async Task LoadAsync(NordClient client, CancellationToken ct = default)
    {
        Clear();
        FillFromStrings(PianoCategories, "Piano category", await client.QueryPianoCategoriesAsync(ct));
        // Programs occupy Banks A-P on the device (Param2=0x02, payload=0x07).
        FillFromStrings(ProgramBanks, "Program bank", await client.QueryBanksAtoPAsync(ct));
        // Samp Lib is exposed as a single bank (payload=0x05).
        FillFromStrings(SampLibBanks, "Samp Lib", await client.QuerySampLibAsync(ct));
        // Songs use Banks 1-8 (payload=0x08).
        FillFromStrings(SongBanks, "Song bank", await client.QueryBanks1to8V1Async(ct));
        // Synths use Banks 1-8 (payload=0x09).
        FillFromStrings(SynthBanks, "Synth bank", await client.QueryBanks1to8V2Async(ct));
    }

    private static void FillFromStrings(ObservableCollection<BankEntry> target, string label, IReadOnlyList<string> names)
    {
        for (var i = 0; i < names.Count; i++)
            target.Add(new BankEntry(label, i + 1, names[i]));
    }
}

public sealed record BankEntry(string Kind, int Index, string Name)
{
    public override string ToString() => $"{Kind} {Index}: {Name}";
}
