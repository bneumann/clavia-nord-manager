using Avalonia.Controls;

namespace NordSampleManager.Views;

public partial class ConfirmDialog : Window
{
    public ConfirmDialog() => InitializeComponent();

    private void OnConfirm(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Close(true);
    private void OnCancel(object? sender,  Avalonia.Interactivity.RoutedEventArgs e) => Close(false);
}
