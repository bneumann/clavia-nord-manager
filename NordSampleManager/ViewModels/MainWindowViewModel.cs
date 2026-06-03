using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NordSampleManager.Services;

namespace NordSampleManager.ViewModels;

public partial class MainWindowViewModel : ObservableObject
{
    private readonly DeviceService deviceService;
    private readonly SoundLibrary library;

    public ObservableCollection<CategoryViewModel> Categories { get; }

    [ObservableProperty] private CategoryViewModel? selectedCategory;
    [ObservableProperty] private string statusText = "Disconnected";
    [ObservableProperty] private bool canConnect = true;
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private string? detailHeader;
    [ObservableProperty] private string? detailBody;

    public MainWindowViewModel(DeviceService deviceService, SoundLibrary library)
    {
        this.deviceService = deviceService;
        this.library = library;
        deviceService.StateChanged += (_, _) => UpdateStatus();

        Categories = new ObservableCollection<CategoryViewModel>
        {
            new(LibraryCategory.Piano,   "Pianos",         "Categories on device", library.PianoCategories),
            new(LibraryCategory.Program, "Programs",       "Banks A–P",            library.ProgramBanks),
            new(LibraryCategory.SampLib, "Sample Library", "Samp Lib banks",       library.SampLibBanks),
            new(LibraryCategory.Song,    "Songs",          "Banks 1–8",            library.SongBanks),
            new(LibraryCategory.Synth,   "Synths",         "Banks 1–8",            library.SynthBanks),
        };

        foreach (var cat in Categories)
            cat.PropertyChanged += OnCategoryPropertyChanged;

        SelectedCategory = Categories[0];
        UpdateStatus();
    }

    private void OnCategoryPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(CategoryViewModel.SelectedEntry)) return;
        if (sender is not CategoryViewModel cat || cat.SelectedEntry is null)
        {
            DetailHeader = null;
            DetailBody = null;
            return;
        }
        DetailHeader = cat.SelectedEntry.Name;
        DetailBody =
            $"Kind:     {cat.SelectedEntry.Kind}\n" +
            $"Index:    {cat.SelectedEntry.Index}\n" +
            $"Name:     {cat.SelectedEntry.Name}\n\n" +
            "Per-item detail (description, version, size, cross-references) " +
            "requires the Param2=0x1e / 0x28 queries — not yet decoded.";
    }

    private void UpdateStatus()
    {
        StatusText = deviceService.State switch
        {
            ConnectionState.Disconnected => "Disconnected",
            ConnectionState.Connecting   => "Connecting…",
            ConnectionState.Connected    => $"Connected: {deviceService.VendorId:x4}:{deviceService.ProductId:x4}  OUT 0x{deviceService.BulkOut:x2}  IN 0x{deviceService.BulkIn:x2}",
            ConnectionState.Failed       => $"Connection failed: {deviceService.LastError}",
            _ => "Unknown",
        };
        CanConnect = deviceService.State is ConnectionState.Disconnected or ConnectionState.Failed;
    }

    [RelayCommand]
    private async Task ConnectAndLoadAsync()
    {
        IsBusy = true;
        try
        {
            await deviceService.ConnectAsync();
            if (deviceService.State != ConnectionState.Connected || deviceService.Client is null) return;
            await library.LoadAsync(deviceService.Client);
            SelectedCategory ??= Categories[0];
        }
        finally
        {
            IsBusy = false;
        }
    }
}
