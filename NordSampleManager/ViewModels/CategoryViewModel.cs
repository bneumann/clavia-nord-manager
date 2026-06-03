using System.Collections.ObjectModel;
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

    [ObservableProperty]
    private BankEntry? selectedEntry;

    public CategoryViewModel(LibraryCategory category, string title, string subtitle, ObservableCollection<BankEntry> entries)
    {
        Category = category;
        Title = title;
        Subtitle = subtitle;
        Entries = entries;
    }
}
