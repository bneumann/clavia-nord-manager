using CommunityToolkit.Mvvm.ComponentModel;
using NordSampleManager.Protocol.Records;

namespace NordSampleManager.ViewModels;

public sealed record BankItem(int Id, string Label);
public sealed record SlotItem(int ItemIndex, string Label);

public sealed partial class UploadDialogViewModel : ObservableObject
{
    private readonly IReadOnlyDictionary<(int bank, int loc), string> _occupied;

    [ObservableProperty] private string name;
    [ObservableProperty] private CategoryItem? selectedCategoryItem;
    [ObservableProperty] private BankItem?     selectedBank;
    [ObservableProperty] private SlotItem?     selectedSlot;

    public IReadOnlyList<CategoryItem> Categories { get; } =
        ProgramCategoryExtensions.AllCategories
            .Select(c => new CategoryItem(c, c.DisplayName()))
            .ToArray();

    public IReadOnlyList<BankItem> Banks { get; } =
        Enumerable.Range(0, 16)
            .Select(i => new BankItem(i, $"Bank {(char)('A' + i)}"))
            .ToArray();

    public IReadOnlyList<SlotItem> Slots { get; } =
        Enumerable.Range(0, 50)
            .Select(i => new SlotItem(i, $"{(i / 5 + 1) * 10 + (i % 5 + 1)}"))
            .ToArray();

    public bool IsOverwrite =>
        SelectedBank is not null && SelectedSlot is not null &&
        _occupied.ContainsKey((SelectedBank.Id, SelectedSlot.ItemIndex));

    public string OverwriteWarning =>
        IsOverwrite
            ? $"Will overwrite '{_occupied[(SelectedBank!.Id, SelectedSlot!.ItemIndex)]}'"
            : string.Empty;

    public string ConfirmLabel => IsOverwrite ? "Overwrite" : "Upload";

    public uint CategoryCode => (uint)(SelectedCategoryItem?.Cat ?? ProgramCategory.Undefined);
    public int  BankId       => SelectedBank?.Id          ?? 0;
    public int  ItemIndex    => SelectedSlot?.ItemIndex   ?? 0;

    public UploadDialogViewModel(
        string defaultName,
        int initialBankId,
        int initialItemIndex,
        IReadOnlyDictionary<(int bank, int loc), string> occupied)
    {
        _occupied = occupied;
        name      = defaultName.Length > 16 ? defaultName[..16] : defaultName;

        selectedCategoryItem = Categories.FirstOrDefault(c => c.Cat == ProgramCategory.Acoustic);
        selectedBank = Banks.FirstOrDefault(b => b.Id == initialBankId) ?? Banks[0];
        selectedSlot = Slots.FirstOrDefault(s => s.ItemIndex == initialItemIndex) ?? Slots[0];
    }

    partial void OnSelectedBankChanged(BankItem? value)
    {
        OnPropertyChanged(nameof(IsOverwrite));
        OnPropertyChanged(nameof(OverwriteWarning));
        OnPropertyChanged(nameof(ConfirmLabel));
    }

    partial void OnSelectedSlotChanged(SlotItem? value)
    {
        OnPropertyChanged(nameof(IsOverwrite));
        OnPropertyChanged(nameof(OverwriteWarning));
        OnPropertyChanged(nameof(ConfirmLabel));
    }
}
