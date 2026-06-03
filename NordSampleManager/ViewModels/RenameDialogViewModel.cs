using CommunityToolkit.Mvvm.ComponentModel;
using NordSampleManager.Protocol.Records;

namespace NordSampleManager.ViewModels;

public sealed record CategoryItem(ProgramCategory Cat, string Label);

public sealed partial class RenameDialogViewModel : ObservableObject
{
    [ObservableProperty] private string name;
    [ObservableProperty] private CategoryItem? selectedCategoryItem;

    public string OriginalName { get; }
    public CategoryItem? OriginalCategoryItem { get; }

    public IReadOnlyList<CategoryItem> Categories { get; } =
        ProgramCategoryExtensions.AllCategories
            .Select(c => new CategoryItem(c, c.DisplayName()))
            .ToArray();

    public uint CategoryCode => (uint)(SelectedCategoryItem?.Cat ?? ProgramCategory.Undefined);

    public RenameDialogViewModel(string currentName, uint currentCategoryCode)
    {
        OriginalName = currentName;
        name         = currentName;

        var cat = ProgramCategoryExtensions.FromCode(currentCategoryCode);
        selectedCategoryItem = Categories.FirstOrDefault(c => c.Cat == cat)
            ?? Categories.FirstOrDefault(c => c.Cat == ProgramCategory.Undefined);
        OriginalCategoryItem = selectedCategoryItem;
    }
}
