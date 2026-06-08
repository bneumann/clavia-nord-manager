using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NordSampleManager.Services;

namespace NordSampleManager.ViewModels;

public partial class SoundLibraryViewModel : ObservableObject
{
    private readonly NordLibraryClient client;

    public ObservableCollection<LibraryCatalogEntry> PianoCatalog { get; } = new();
    public ObservableCollection<LibraryCatalogEntry> SampleCategories { get; } = new();
    public ObservableCollection<SampleInstrument>    CategoryInstruments { get; } = new();

    [ObservableProperty] private bool   isLoadingCatalog;
    [ObservableProperty] private bool   isLoadingInstruments;
    [ObservableProperty] private string filterText = "";
    [ObservableProperty] private string selectedLibraryType = "Piano";  // "Piano" | "Sample"
    [ObservableProperty] private LibraryCatalogEntry? selectedCategory;
    [ObservableProperty] private object? selectedItem;  // LibraryCatalogEntry (piano) | SampleInstrument
    [ObservableProperty] private string? statusMessage;

    public SoundLibraryViewModel(NordLibraryClient client)
    {
        this.client = client;
    }

    partial void OnSelectedCategoryChanged(LibraryCatalogEntry? value)
    {
        if (value?.AccordionItemsUrl is not null)
            _ = LoadCategoryInstrumentsCommand.ExecuteAsync(value);
    }

    partial void OnFilterTextChanged(string value) => RefreshFilter();
    partial void OnSelectedLibraryTypeChanged(string value)
    {
        SelectedItem = null;
        RefreshFilter();
    }

    public IReadOnlyList<LibraryCatalogEntry> FilteredPianos =>
        string.IsNullOrWhiteSpace(FilterText)
            ? PianoCatalog
            : PianoCatalog.Where(p => p.Title.Contains(FilterText, StringComparison.OrdinalIgnoreCase)
                                    || p.Description.Contains(FilterText, StringComparison.OrdinalIgnoreCase)
                                    || p.Tags.Any(t => t.Contains(FilterText, StringComparison.OrdinalIgnoreCase)))
                          .ToList();

    public IReadOnlyList<SampleInstrument> FilteredInstruments =>
        string.IsNullOrWhiteSpace(FilterText)
            ? CategoryInstruments
            : CategoryInstruments.Where(i => i.Title.Contains(FilterText, StringComparison.OrdinalIgnoreCase))
                                 .ToList();

    [RelayCommand]
    private async Task LoadCatalogAsync(CancellationToken ct = default)
    {
        IsLoadingCatalog = true;
        StatusMessage = "Loading catalog…";
        try
        {
            // Fetch piano and sample catalogs in parallel.
            var pianoTask  = client.GetPianoCatalogAsync(ct);
            var sampleTask = client.GetSampleCatalogAsync(ct);
            await Task.WhenAll(pianoTask, sampleTask).ConfigureAwait(false);
            var pianos  = pianoTask.Result;
            var samples = sampleTask.Result;

            PianoCatalog.Clear();
            foreach (var p in pianos) PianoCatalog.Add(p);

            SampleCategories.Clear();
            foreach (var s in samples) SampleCategories.Add(s);

            StatusMessage = $"{pianos.Count} pianos, {samples.Count} sample categories loaded.";
            RefreshFilter();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to load catalog: {ex.Message}";
        }
        finally
        {
            IsLoadingCatalog = false;
        }
    }

    [RelayCommand]
    private async Task LoadCategoryInstrumentsAsync(LibraryCatalogEntry category, CancellationToken ct = default)
    {
        if (category.AccordionItemsUrl is null) return;
        IsLoadingInstruments = true;
        CategoryInstruments.Clear();
        try
        {
            var instruments = await client.GetSampleInstrumentsAsync(category.AccordionItemsUrl, category.Title, ct).ConfigureAwait(false);
            foreach (var i in instruments) CategoryInstruments.Add(i);
            RefreshFilter();
        }
        finally
        {
            IsLoadingInstruments = false;
        }
    }

    private void RefreshFilter()
    {
        OnPropertyChanged(nameof(FilteredPianos));
        OnPropertyChanged(nameof(FilteredInstruments));
    }
}
