using Avalonia.Controls;
using Avalonia.Input;
using NordSampleManager.ViewModels;

namespace NordSampleManager.Views;

public partial class UploadDialog : Window
{
    public UploadDialog() => InitializeComponent();

    private void OnUpload(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Close(true);
    private void OnCancel(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Close(false);

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)  Close(true);
        if (e.Key == Key.Escape) Close(false);
    }
}
