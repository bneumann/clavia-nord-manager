using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NordSampleManager.Protocol;
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
    private readonly NordLibraryClient libraryClient;

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
    [ObservableProperty] private bool   storageVisible;
    [ObservableProperty] private double storageUsedFraction;
    [ObservableProperty] private string storageUsedText = "";
    [ObservableProperty] private string storageFreeText = "";
    [ObservableProperty] private bool   soundLibraryVisible;

    public SoundLibraryViewModel SoundLibrary { get; }
    public ushort DeviceProductId => deviceService.ProductId;

    partial void OnIsBusyChanged(bool value) => UpdateStatus();

    public MainWindowViewModel(DeviceService deviceService, SoundLibrary library,
        NordLibraryClient libraryClient, SoundLibraryViewModel soundLibrary)
    {
        this.deviceService = deviceService;
        this.library = library;
        this.libraryClient = libraryClient;
        SoundLibrary = soundLibrary;
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

    partial void OnSelectedCategoryChanged(CategoryViewModel? value) => UpdateStorageInfo();

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
              "No per-item detail (device not connected, or loading failed).";
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
    private void ToggleSoundLibrary() => SoundLibraryVisible = !SoundLibraryVisible;

    /// <summary>
    /// "Transfer to Instrument": installs the selected online sound into the next available
    /// empty slot. Fails early if there is not enough storage space.
    /// </summary>
    public async Task TransferToInstrumentAsync(object? selectedItem, int keyboardCode, CancellationToken ct = default)
    {
        if (deviceService.Client is null || selectedItem is null) return;

        if (selectedItem is LibraryCatalogEntry pianoEntry && pianoEntry.LibraryType == "Piano")
        {
            var downloads = await FetchPianoDownloadsOrError(pianoEntry, ct);
            if (downloads is null) return;

            var owner = GetMainWindow();
            if (owner is null) return;

            // Let user pick size and see available empty slots only
            var occupied = PianoOccupiedMap();
            var vm = new InstallSlotDialogViewModel(pianoEntry.Title, downloads.Options, occupied);
            var dialog = new Views.InstallSlotDialog { DataContext = vm };
            if (!await dialog.ShowDialog<bool>(owner)) return;
            if (vm.SelectedOption is null) return;

            // Space check before downloading
            var needed = NordLibraryClient.ParseFileSizeBytes(vm.SelectedOption.FileSize);
            if (needed > 0 && needed > library.PianoStorageFreeBytes)
            {
                StatusText = $"Not enough space: need {needed / (1024.0 * 1024):F0} MiB, " +
                             $"{library.PianoStorageFreeBytes / (1024.0 * 1024):F0} MiB free.";
                return;
            }

            await RunInstallAsync(vm.BuildResult(vm.SelectedOption.DownloadUrl)!, isPiano: true, keyboardCode, ct);
        }
        else if (selectedItem is SampleInstrument instrument)
        {
            // For samples: find first empty slot
            var allSlots = Enumerable.Range(0, 400).ToHashSet();
            var usedSlots = library.SampLibBanks.Where(e => e.Ref.HasValue).Select(e => e.Ref!.Value.Location).ToHashSet();
            var emptySlot = allSlots.Except(usedSlots).Cast<int?>().FirstOrDefault();
            if (emptySlot is null)
            {
                StatusText = "Sample library is full — no empty slots available.";
                return;
            }

            var url = NordLibraryClient.SubstituteKeyboardCode(instrument.DownloadUrlTemplate, keyboardCode);
            var categoryCode = Protocol.Records.SampCategoryExtensions.FromWebCategoryTitle(instrument.Category);
            var result = new InstallSlotResult(url, 0, emptySlot.Value, false, TruncateName(instrument.Title), categoryCode);
            await RunInstallAsync(result, isPiano: false, keyboardCode, ct);
        }
    }

    /// <summary>
    /// "Substitute Selected Sound": replaces the device sound currently selected in the main
    /// panel with the chosen online sound. Fails early if not enough space.
    /// </summary>
    public async Task SubstituteSelectedSoundAsync(object? selectedItem, int keyboardCode, CancellationToken ct = default)
    {
        if (deviceService.Client is null || selectedItem is null) return;
        var owner = GetMainWindow();
        if (owner is null) return;

        var deviceEntry = SelectedCategory?.SelectedEntry;
        if (deviceEntry?.Ref is null)
        {
            StatusText = "Select a sound on the device first.";
            return;
        }

        if (selectedItem is LibraryCatalogEntry pianoEntry && pianoEntry.LibraryType == "Piano"
            && SelectedCategory?.Category == LibraryCategory.Piano)
        {
            var downloads = await FetchPianoDownloadsOrError(pianoEntry, ct);
            if (downloads is null) return;

            var vm = new InstallSlotDialogViewModel(pianoEntry.Title, downloads.Options, PianoOccupiedMap());
            // Pre-select the device entry's category/slot
            vm.SelectedCategoryIndex = deviceEntry.Ref.Value.Bank;
            vm.SelectedPianoSlot    = deviceEntry.Ref.Value.Location;

            var dialog = new Views.InstallSlotDialog { DataContext = vm };
            if (!await dialog.ShowDialog<bool>(owner)) return;
            if (vm.SelectedOption is null) return;

            var needed = NordLibraryClient.ParseFileSizeBytes(vm.SelectedOption.FileSize);
            if (needed > 0 && needed > library.PianoStorageFreeBytes + deviceEntry.SizeBytes)
            {
                StatusText = $"Not enough space after freeing the replaced piano.";
                return;
            }

            await RunInstallAsync(vm.BuildResult(vm.SelectedOption.DownloadUrl)!, isPiano: true, keyboardCode, ct);
        }
        else if (selectedItem is SampleInstrument instrument
                 && SelectedCategory?.Category == LibraryCategory.SampLib)
        {
            var slot         = deviceEntry.Ref.Value.Location;
            var url          = NordLibraryClient.SubstituteKeyboardCode(instrument.DownloadUrlTemplate, keyboardCode);
            var categoryCode = Protocol.Records.SampCategoryExtensions.FromWebCategoryTitle(instrument.Category);
            var result = new InstallSlotResult(url, 0, slot, true, TruncateName(instrument.Title), categoryCode);
            await RunInstallAsync(result, isPiano: false, keyboardCode, ct);
        }
        else
        {
            StatusText = "Select a matching sound type on the device (Piano→Piano, Sample→Sample).";
        }
    }

    private async Task<PianoDownloads?> FetchPianoDownloadsOrError(LibraryCatalogEntry entry, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(entry.PianoDownloadsUrl))
        {
            StatusText = "No download URL for this piano.";
            return null;
        }
        var downloads = await libraryClient.GetPianoDownloadsAsync(
            NordLibraryClient.ExtractProductCode(entry.PianoDownloadsUrl), ct);
        if (downloads is null || downloads.Options.Count == 0)
        {
            StatusText = "Could not fetch download options.";
            return null;
        }
        return downloads;
    }

    private Dictionary<(int cat, int slot), string> PianoOccupiedMap() =>
        library.PianoCategories
               .Where(e => e.Ref.HasValue)
               .ToDictionary(e => (e.Ref!.Value.Bank, e.Ref!.Value.Location), e => e.Name);

    private static string TruncateName(string name) => name.Length > 16 ? name[..16] : name;

    private async Task RunInstallAsync(InstallSlotResult result, bool isPiano, int keyboardCode, CancellationToken ct)
    {
        if (deviceService.Client is null) return;
        IsBusy = true;
        var progress = new Progress<(long received, long total)>(p =>
        {
            if (p.total > 0)
                StatusText = $"Downloading… {p.received / (1024.0 * 1024):F1} / {p.total / (1024.0 * 1024):F1} MB";
        });
        try
        {
            StatusText = "Downloading…";
            var rawBytes = await libraryClient.DownloadFileAsync(result.DownloadUrl, progress, ct);

            // Piano files from the Nord CDN are CBIN-wrapped; sample files may or may not be.
            byte[] rawData;
            if (Protocol.Records.CbinFile.TryParse(rawBytes, out var cbin))
            {
                rawData = cbin.RawData;
            }
            else if (!isPiano)
            {
                rawData = rawBytes;  // raw NSMP without CBIN wrapper — use as-is
            }
            else
            {
                StatusText = "Downloaded piano file is not valid CBIN format.";
                return;
            }

            StatusText = $"Installing '{result.Name}'…";
            var uploadProgress = new Progress<(int done, int total)>(p =>
                StatusText = $"Uploading… {p.done}/{p.total} chunks");

            if (isPiano)
            {
                await deviceService.Client.InstallPianoAsync(
                    result.CategoryIndex, result.Slot, result.Name, rawData,
                    result.DeleteExisting, uploadProgress, ct);
                // Refresh piano section
                try
                {
                    var pianos = await deviceService.Client.QueryAllPianoNamesAsync(ct);
                    library.PianoStorageFreeBytes = deviceService.Client.PianoStorage.FreeBytes;
                    library.PianoCategories.Clear();
                    foreach (var p in pianos)
                        library.PianoCategories.Add(new Services.BankEntry(p.Category, p.Location + 1, p.Name)
                        {
                            Detail    = $"Version: {p.Version}\nSize:    {p.SizeBytes / (1024.0 * 1024.0):F1} MiB",
                            SizeBytes = p.SizeBytes,
                            Ref       = new Protocol.Records.SoundRef(Protocol.Records.SoundItemType.Piano, p.CategoryIndex, p.Location),
                        });
                }
                catch { }
            }
            else
            {
                await deviceService.Client.InstallSampleAsync(
                    result.Slot, result.Name, rawData, result.CategoryCode,
                    result.DeleteExisting, uploadProgress, ct);
                // Refresh sample section
                try
                {
                    var samps = await deviceService.Client.QueryAllSampLibAsync(ct);
                    library.SampLibBanks.Clear();
                    foreach (var s in samps)
                    {
                        var cat = Protocol.Records.SampCategoryExtensions.DisplayName(s.CategoryCode);
                        library.SampLibBanks.Add(new Services.BankEntry(cat, s.Location + 1, s.Name)
                        {
                            Detail       = $"Category: {cat}\nVersion:  {s.Version}\nSize:     {s.SizeBytes / (1024.0 * 1024.0):F1} MiB",
                            CategoryCode = s.CategoryCode,
                            CategoryName = cat,
                            SizeBytes    = s.SizeBytes,
                            Ref          = new Protocol.Records.SoundRef(Protocol.Records.SoundItemType.SampLib, 0, s.Location),
                        });
                    }
                }
                catch { }
            }

            StatusText = $"Installed: {result.Name}";
            UpdateStorageInfo();
        }
        catch (Exception ex)
        {
            StatusText = $"Install failed: {ex.Message}";
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

        Avalonia.Controls.Window? owner = GetMainWindow();
        if (owner is null) return;

        var confirmed = await dialog.ShowDialog<bool>(owner);
        if (!confirmed) return;

        IsBusy = true;
        try
        {
            await deviceService.Client.RenameProgramAsync(ref_.Bank, ref_.Location, vm.Name, vm.CategoryCode);

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
            DetailHeader = vm.Name;
        }
        catch (NordException ex)
        {
            StatusText = $"Rename failed: {ex.Message}";
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

        var owner = GetMainWindow();
        if (owner is null) return;

        var topLevel = Avalonia.Controls.TopLevel.GetTopLevel(owner);
        if (topLevel is null) return;

        var file = await topLevel.StorageProvider.SaveFilePickerAsync(
            new FilePickerSaveOptions
            {
                Title = "Export program",
                SuggestedFileName = $"{entry.Name}.ns3f",
                FileTypeChoices =
                [
                    new FilePickerFileType("Nord Stage 3 Program") { Patterns = ["*.ns3f"] },
                ],
            });
        if (file is null) return;

        IsBusy = true;
        try
        {
            var bytes = await deviceService.Client.DownloadProgramAsync(ref_.Bank, ref_.Location);
            await using var stream = await file.OpenWriteAsync();
            await stream.WriteAsync(bytes);
            StatusText = $"Exported: {entry.Name}.ns3f";
        }
        catch (NordException ex)
        {
            StatusText = $"Export failed: {ex.Message}";
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

        var owner = GetMainWindow();
        if (owner is null) return;

        var topLevel = Avalonia.Controls.TopLevel.GetTopLevel(owner);
        if (topLevel is null) return;

        var picks = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open program file",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Nord Stage 3 Program") { Patterns = ["*.ns3f"] }],
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

        var defaultName = System.IO.Path.GetFileNameWithoutExtension(picks[0].Name);
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
            await deviceService.Client.UploadProgramAsync(vm.BankId, vm.ItemIndex, vm.Name, cbin.FileType, cbin.RawData, vm.CategoryCode);
            StatusText = $"Uploaded: {vm.Name}";
        }
        catch (NordException ex)
        {
            StatusText = $"Upload failed: {ex.Message}";
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

        var owner = GetMainWindow();
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
            await deviceService.Client.DeleteProgramAsync(ref_.Bank, ref_.Location);
            library.ProgramBanks.Remove(entry);
            DetailHeader = null;
            DetailBody   = null;
            StatusText   = $"Deleted: {entry.Name}";
        }
        catch (NordException ex)
        {
            StatusText = $"Delete failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task SwapAsync(BankEntry source, BankEntry target)
    {
        if (source.Ref is not { ItemType: SoundItemType.Program } src) return;
        if (target.Ref is not { ItemType: SoundItemType.Program } tgt) return;
        if (deviceService.Client is null) return;

        var owner = GetMainWindow();
        if (owner is null) return;

        var vm = new ConfirmDialogViewModel(
            "Swap Programs",
            $"Swap '{source.Name}' ↔ '{target.Name}'?",
            "Swap");
        var dialog = new Views.ConfirmDialog { DataContext = vm };
        var confirmed = await dialog.ShowDialog<bool>(owner);
        if (!confirmed) return;

        IsBusy = true;
        try
        {
            await deviceService.Client.SwapProgramsAsync(src.Bank, src.Location, tgt.Bank, tgt.Location);

            var idx1 = library.ProgramBanks.IndexOf(source);
            var idx2 = library.ProgramBanks.IndexOf(target);
            if (idx1 >= 0 && idx2 >= 0)
            {
                library.ProgramBanks[idx1] = source with
                {
                    Name         = target.Name,
                    CategoryCode = target.CategoryCode,
                    CategoryName = target.CategoryName,
                    Detail       = target.Detail,
                };
                library.ProgramBanks[idx2] = target with
                {
                    Name         = source.Name,
                    CategoryCode = source.CategoryCode,
                    CategoryName = source.CategoryName,
                    Detail       = source.Detail,
                };
            }
            StatusText = $"Swapped: {source.Name} ↔ {target.Name}";
        }
        catch (NordException ex)
        {
            StatusText = $"Swap failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task LoadLibraryAsync()
    {
        try
        {
            await library.LoadAsync(deviceService.Client!);
            SelectedCategory ??= Categories[0];
            UpdateStatus();
            UpdateStorageInfo();
        }
        catch (NordException ex)
        {
            StatusText = $"Load error: {ex.Message}";
        }
    }

    private void UpdateStorageInfo()
    {
        if (SelectedCategory?.Category == LibraryCategory.Piano
            && library.PianoStorageFreeBytes > 0)
        {
            var used  = library.PianoStorageUsedBytes;
            var free  = library.PianoStorageFreeBytes;
            var total = used + free;
            StorageUsedFraction = total > 0 ? (double)used / total : 0;
            StorageUsedText = $"Used: {used  / (1024.0 * 1024 * 1024):F1} GiB";
            StorageFreeText = $"Free: {free  / (1024.0 * 1024 * 1024):F1} GiB";
            StorageVisible  = true;
        }
        else if (SelectedCategory?.Category == LibraryCategory.Synth
                 && library.SynthStorage.TotalBytes > 0)
        {
            StorageUsedFraction = library.SynthStorage.UsedFraction;
            StorageUsedText = $"{library.SynthStorage.UsedBytes} / {library.SynthStorage.TotalBytes} slots used";
            StorageFreeText = $"{library.SynthStorage.FreeBytes} free";
            StorageVisible  = true;
        }
        else if (SelectedCategory?.Category == LibraryCategory.Song
                 && library.SongStorage.TotalBytes > 0)
        {
            StorageUsedFraction = library.SongStorage.UsedFraction;
            StorageUsedText = $"{library.SongStorage.UsedBytes} / {library.SongStorage.TotalBytes} slots used";
            StorageFreeText = $"{library.SongStorage.FreeBytes} free";
            StorageVisible  = true;
        }
        else if (SelectedCategory?.Category == LibraryCategory.SampLib
                 && library.SampLibStorage.TotalBytes > 0)
        {
            StorageUsedFraction = library.SampLibStorage.UsedFraction;
            StorageUsedText = $"Used: {library.SampLibStorage.UsedBytes / (1024.0 * 1024 * 1024):F1} GiB";
            StorageFreeText = $"Free: {library.SampLibStorage.FreeBytes / (1024.0 * 1024 * 1024):F1} GiB";
            StorageVisible  = true;
        }
        else
        {
            StorageVisible = false;
        }
    }

    private static Avalonia.Controls.Window? GetMainWindow() =>
        Avalonia.Application.Current?.ApplicationLifetime is
            Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime dt
            ? dt.MainWindow : null;
}
