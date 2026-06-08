using System.Collections.ObjectModel;
using System.Collections.Specialized;
using CommunityToolkit.Mvvm.ComponentModel;
using NordSampleManager.Services;

namespace NordSampleManager.ViewModels;

public enum LibraryCategory { Piano, Program, SampLib, Song, Synth }

public sealed partial class CategoryViewModel : ObservableObject
{
    public LibraryCategory Category { get; }
    public string Title { get; }
    public string Subtitle { get; }
    public ObservableCollection<BankEntry> Entries { get; }
    public ObservableCollection<BankEntry> FilteredEntries { get; } = new();

    [ObservableProperty] private BankEntry? selectedEntry;
    [ObservableProperty] private string filterText = string.Empty;

    public int TotalCount => Entries.Count;

    public CategoryViewModel(LibraryCategory category, string title, string subtitle, ObservableCollection<BankEntry> entries)
    {
        Category = category;
        Title = title;
        Subtitle = subtitle;
        Entries = entries;
        Entries.CollectionChanged += OnEntriesChanged;
        RebuildFilter();
    }

    private void OnEntriesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        RebuildFilter();
        OnPropertyChanged(nameof(TotalCount));
    }

    partial void OnFilterTextChanged(string value) => RebuildFilter();

    private void RebuildFilter()
    {
        FilteredEntries.Clear();
        var f = FilterText.Trim();
        foreach (var e in Entries)
        {
            if (f.Length == 0
                || e.Kind.Contains(f, StringComparison.OrdinalIgnoreCase)
                || e.Name.Contains(f, StringComparison.OrdinalIgnoreCase)
                || (e.Detail?.Contains(f, StringComparison.OrdinalIgnoreCase) ?? false))
                FilteredEntries.Add(e);
        }
    }
}
