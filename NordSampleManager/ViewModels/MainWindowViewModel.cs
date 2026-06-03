using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NordSampleManager.Protocol.Records;
using NordSampleManager.Services;
using NordSampleManager.Views;
using Avalonia.Platform.Storage;
using ProgramCat = NordSampleManager.Protocol.Records.ProgramCategoryExtensions;

namespace NordSampleManager.ViewModels;

public partial class MainWindowViewModel : ObservableObject
{
    private readonly DeviceService deviceService;
    private readonly SoundLibrary library;

    public ObservableCollection<CategoryViewModel> Categories { get; }

    [ObservableProperty] private CategoryViewModel? selectedCategory;
    [ObservableProperty] private string statusText = "Disconnected";
    [ObservableProperty] private bool canConnect = true;
    [ObservableProperty] private bool canReload;
    [ObservableProperty] private bool canRename;
    [ObservableProperty] private bool canExport;
    [ObservableProperty] private bool canDelete;
    [ObservableProperty] private bool canUpload;
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private string? detailHeader;
    [ObservableProperty] private string? detailBody;

    partial void OnIsBusyChanged(bool value) => UpdateStatus();

    public MainWindowViewModel(DeviceService deviceService, SoundLibrary library)
    {
        this.deviceService = deviceService;
        this.library = library;
        deviceService.StateChanged += (_, _) => UpdateStatus();

        Categories = new ObservableCollection<CategoryViewModel>
        {
            new(LibraryCategory.Piano,   "Pianos",         "Categories on device", library.PianoCategories),
            new(LibraryCategory.Program, "Programs",       "Banks A–N",            library.ProgramBanks),
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
        UpdateStatus();

        if (sender is not CategoryViewModel cat || cat.SelectedEntry is null)
        {
            DetailHeader = null;
            DetailBody = null;
            return;
        }
        var entry = cat.SelectedEntry;
        DetailHeader = entry.Name;
        DetailBody = entry.Detail is not null
            ? $"Kind:     {entry.Kind}\n" +
              $"Name:     {entry.Name}\n\n" +
              entry.Detail
            : $"Kind:     {entry.Kind}\n" +
              $"Index:    {entry.Index}\n" +
              $"Name:     {entry.Name}\n\n" +
              "Per-item detail requires the Param2=0x1e / 0x28 queries — not yet decoded.";
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
        var connected = !IsBusy && deviceService.State == ConnectionState.Connected;
        CanConnect = !IsBusy && deviceService.State is ConnectionState.Disconnected or ConnectionState.Failed;
        CanReload  = connected;
        CanRename  = connected && SelectedCategory?.SelectedEntry?.Ref?.ItemType == SoundItemType.Program;
        CanExport  = CanRename;
        CanDelete  = CanRename;
        CanUpload  = connected;
    }

    [RelayCommand]
    private async Task ConnectAndLoadAsync()
    {
        IsBusy = true;
        try
        {
            await deviceService.ConnectAsync();
            if (deviceService.State != ConnectionState.Connected || deviceService.Client is null) return;
            await LoadLibraryAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ReloadAsync()
    {
        if (deviceService.Client is null) return;
        IsBusy = true;
        try
        {
            await LoadLibraryAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task RenameAsync()
    {
        var entry = SelectedCategory?.SelectedEntry;
        if (entry?.Ref is not { ItemType: SoundItemType.Program } ref_) return;
        if (deviceService.Client is null) return;

        var vm = new RenameDialogViewModel(entry.Name, entry.CategoryCode);
        var dialog = new RenameDialog { DataContext = vm };

        Avalonia.Controls.Window? owner = null;
        if (Avalonia.Application.Current?.ApplicationLifetime is
            Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime dt)
            owner = dt.MainWindow;
        if (owner is null) return;

        var confirmed = await dialog.ShowDialog<bool>(owner);
        if (!confirmed) return;

        IsBusy = true;
        try
        {
            var result = await deviceService.Client
                .RenameProgramAsync(ref_.Bank, ref_.Location, vm.Name, vm.CategoryCode)
                .ToEither();

            result.Match(
                Right: _ =>
                {
                    // Update entry in-place — no full reload needed.
                    var collection = library.ProgramBanks;
                    var idx = collection.IndexOf(entry);
                    if (idx >= 0)
                    {
                        var newCode = vm.CategoryCode;
                        collection[idx] = entry with
                        {
                            Name         = vm.Name,
                            CategoryCode = newCode,
                            CategoryName = ProgramCat.FromCode(newCode).DisplayName(),
                            Detail       = $"Category: {ProgramCat.FromCode(newCode).DisplayName()}"
                                         + (entry.Detail?.Contains("Piano A:") == true
                                             ? "\n" + entry.Detail.Split('\n').FirstOrDefault(l => l.StartsWith("Piano A:"))
                                             : string.Empty),
                        };
                    }
                    // Refresh detail pane.
                    DetailHeader = vm.Name;
                },
                Left: err => StatusText = $"Rename failed: {err.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ExportAsync()
    {
        var entry = SelectedCategory?.SelectedEntry;
        if (entry?.Ref is not { ItemType: SoundItemType.Program } ref_) return;
        if (deviceService.Client is null) return;

        Avalonia.Controls.Window? owner = null;
        if (Avalonia.Application.Current?.ApplicationLifetime is
            Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime dt)
            owner = dt.MainWindow;
        if (owner is null) return;

        var topLevel = Avalonia.Controls.TopLevel.GetTopLevel(owner);
        if (topLevel is null) return;

        var file = await topLevel.StorageProvider.SaveFilePickerAsync(
            new Avalonia.Platform.Storage.FilePickerSaveOptions
            {
                Title = "Export program",
                SuggestedFileName = $"{entry.Name}.ns3f",
                FileTypeChoices =
                [
                    new Avalonia.Platform.Storage.FilePickerFileType("Nord Stage 3 Program")
                    {
                        Patterns = ["*.ns3f"],
                    },
                ],
            });
        if (file is null) return;

        IsBusy = true;
        try
        {
            var result = await deviceService.Client
                .DownloadProgramAsync(ref_.Bank, ref_.Location)
                .ToEither();

            await result.Match<System.Threading.Tasks.Task>(
                Right: async bytes =>
                {
                    await using var stream = await file.OpenWriteAsync();
                    await stream.WriteAsync(bytes);
                    StatusText = $"Exported: {entry.Name}.ns3f";
                },
                Left: err =>
                {
                    StatusText = $"Export failed: {err.Message}";
                    return System.Threading.Tasks.Task.CompletedTask;
                });
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task UploadAsync()
    {
        if (deviceService.Client is null) return;

        Avalonia.Controls.Window? owner = null;
        if (Avalonia.Application.Current?.ApplicationLifetime is
            Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime dt)
            owner = dt.MainWindow;
        if (owner is null) return;

        var topLevel = Avalonia.Controls.TopLevel.GetTopLevel(owner);
        if (topLevel is null) return;

        // File open picker — .ns3f only.
        var picks = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open program file",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Nord Stage 3 Program") { Patterns = ["*.ns3f"] },
            ],
        });
        if (picks.Count == 0) return;

        byte[] fileBytes;
        try
        {
            await using var stream = await picks[0].OpenReadAsync();
            using var ms = new System.IO.MemoryStream();
            await stream.CopyToAsync(ms);
            fileBytes = ms.ToArray();
        }
        catch (Exception ex)
        {
            StatusText = $"Failed to read file: {ex.Message}";
            return;
        }

        if (!CbinFile.TryParse(fileBytes, out var cbin))
        {
            StatusText = "Not a valid CBIN/ns3f file.";
            return;
        }

        // Default name = filename without extension (capped at 16 chars).
        var defaultName = System.IO.Path.GetFileNameWithoutExtension(picks[0].Name);

        // Build a lookup of occupied (bank, location) → name so the dialog can warn on overwrite.
        var occupied = library.ProgramBanks
            .Where(e => e.Ref.HasValue)
            .ToDictionary(
                e => (e.Ref!.Value.Bank, e.Ref!.Value.Location),
                e => e.Name);

        var vm = new UploadDialogViewModel(defaultName, cbin.BankId, cbin.ItemIndex, occupied);
        var dialog = new UploadDialog { DataContext = vm };

        var confirmed = await dialog.ShowDialog<bool>(owner);
        if (!confirmed) return;

        IsBusy = true;
        try
        {
            var result = await deviceService.Client
                .UploadProgramAsync(vm.BankId, vm.ItemIndex, vm.Name, cbin.FileType, cbin.RawData, vm.CategoryCode)
                .ToEither();

            result.Match(
                Right: _ => StatusText = $"Uploaded: {vm.Name}",
                Left:  err => StatusText = $"Upload failed: {err.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task DeleteAsync()
    {
        var entry = SelectedCategory?.SelectedEntry;
        if (entry?.Ref is not { ItemType: SoundItemType.Program } ref_) return;
        if (deviceService.Client is null) return;

        Avalonia.Controls.Window? owner = null;
        if (Avalonia.Application.Current?.ApplicationLifetime is
            Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime dt)
            owner = dt.MainWindow;
        if (owner is null) return;

        var vm = new ConfirmDialogViewModel(
            title:        "Delete Program",
            message:      $"Delete '{entry.Name}'? This cannot be undone.",
            confirmLabel: "Delete");
        var dialog = new Views.ConfirmDialog { DataContext = vm };

        var confirmed = await dialog.ShowDialog<bool>(owner);
        if (!confirmed) return;

        IsBusy = true;
        try
        {
            var result = await deviceService.Client
                .DeleteProgramAsync(ref_.Bank, ref_.Location)
                .ToEither();

            result.Match(
                Right: _ =>
                {
                    library.ProgramBanks.Remove(entry);
                    DetailHeader = null;
                    DetailBody   = null;
                    StatusText   = $"Deleted: {entry.Name}";
                },
                Left: err => StatusText = $"Delete failed: {err.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task LoadLibraryAsync()
    {
        var loadResult = await library.LoadAsync(deviceService.Client!);
        loadResult.IfLeft(err => StatusText = $"Load error: {err.Message}");
        SelectedCategory ??= Categories[0];
        if (loadResult.IsRight) UpdateStatus();
    }
}
