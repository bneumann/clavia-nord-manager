using Avalonia.Controls;
using Avalonia.Input;
using NordSampleManager.ViewModels;

namespace NordSampleManager.Views;

public partial class RenameDialog : Window
{
    public RenameDialog() => InitializeComponent();

    public RenameDialogViewModel? Result { get; private set; }

    private void OnSave(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        Result = DataContext as RenameDialogViewModel;
        Close(true);
    }

    private void OnCancel(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        Close(false);

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)  { Result = DataContext as RenameDialogViewModel; Close(true); }
        if (e.Key == Key.Escape) Close(false);
    }
}
