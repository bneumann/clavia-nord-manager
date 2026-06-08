using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using NordSampleManager.Services;

namespace NordSampleManager.ViewModels;

public sealed record InstallSlotResult(
    string  DownloadUrl,
    int     CategoryIndex,  // piano: 0-5; sample: 0
    int     Slot,
    bool    DeleteExisting,
    string  Name,
    uint    CategoryCode = 0);  // sample: device category code; piano: unused (0xffffffff set in NordClient)

public sealed partial class InstallSlotDialogViewModel : ObservableObject
{
    // Piano category names in order (indices 0-5 match PianoLibraryId bank numbers)
    public static readonly string[] PianoCategories = ["Grand", "Upright", "Electric", "Clav/Hps", "Digital", "Layer"];

    private readonly IReadOnlyList<PianoDownloadOption>? pianoOptions;
    private readonly IReadOnlyDictionary<(int cat, int slot), string> occupiedPiano;
    private readonly IReadOnlyDictionary<int, string> occupiedSample;

    public bool IsPiano { get; }

    // --- Piano mode ---
    public IReadOnlyList<string> CategoryNames { get; } = PianoCategories;
    [ObservableProperty] private int     selectedCategoryIndex;
    [ObservableProperty] private decimal selectedPianoSlot;   // 0-19

    public IReadOnlyList<PianoDownloadOption> DownloadOptions { get; }
    [ObservableProperty] private PianoDownloadOption? selectedOption;

    // --- Sample mode ---
    [ObservableProperty] private decimal selectedSampleSlot;  // 0-399

    // --- Common ---
    [ObservableProperty] private string soundName;
    [ObservableProperty] private string overwriteWarning = "";
    [ObservableProperty] private bool   isOverwrite;

    public InstallSlotDialogViewModel(
        string name,
        IReadOnlyList<PianoDownloadOption> pianoOptions,
        IReadOnlyDictionary<(int cat, int slot), string> occupiedPiano)
    {
        IsPiano = true;
        soundName = TruncateName(name);
        this.pianoOptions = pianoOptions;
        this.occupiedPiano = occupiedPiano;
        this.occupiedSample = new Dictionary<int, string>();
        DownloadOptions = pianoOptions;
        selectedOption = pianoOptions.FirstOrDefault(o => o.Size == "l") ?? pianoOptions.FirstOrDefault();
        UpdateOverwrite();
    }

    public InstallSlotDialogViewModel(
        string name,
        IReadOnlyDictionary<int, string> occupiedSample)
    {
        IsPiano = false;
        soundName = TruncateName(name);
        pianoOptions = null;
        this.occupiedPiano = new Dictionary<(int, int), string>();
        this.occupiedSample = occupiedSample;
        DownloadOptions = [];
        UpdateOverwrite();
    }

    partial void OnSelectedCategoryIndexChanged(int value)     { UpdateOverwrite(); OnPropertyChanged(nameof(ConfirmLabel)); }
    partial void OnSelectedPianoSlotChanged(decimal value)     { UpdateOverwrite(); OnPropertyChanged(nameof(ConfirmLabel)); }
    partial void OnSelectedSampleSlotChanged(decimal value)    { UpdateOverwrite(); OnPropertyChanged(nameof(ConfirmLabel)); }

    public string ConfirmLabel => IsOverwrite ? "Replace" : "Install";

    private void UpdateOverwrite()
    {
        if (IsPiano)
        {
            var key = (SelectedCategoryIndex, (int)SelectedPianoSlot);
            IsOverwrite = occupiedPiano.TryGetValue(key, out var existing);
            OverwriteWarning = IsOverwrite ? $"Will replace '{existing}'" : "";
        }
        else
        {
            IsOverwrite = occupiedSample.TryGetValue((int)SelectedSampleSlot, out var existing);
            OverwriteWarning = IsOverwrite ? $"Will replace '{existing}'" : "";
        }
    }

    public InstallSlotResult? BuildResult(string downloadUrl) =>
        new(downloadUrl,
            IsPiano ? SelectedCategoryIndex : 0,
            IsPiano ? (int)SelectedPianoSlot : (int)SelectedSampleSlot,
            IsOverwrite,
            SoundName);

    private static string TruncateName(string name) =>
        name.Length > 16 ? name[..16] : name;
}
