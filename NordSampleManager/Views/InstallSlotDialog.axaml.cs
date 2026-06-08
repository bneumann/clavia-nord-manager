using Avalonia.Controls;

namespace NordSampleManager.Views;

public partial class InstallSlotDialog : Window
{
    public InstallSlotDialog() => InitializeComponent();

    private void OnInstall(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Close(true);
    private void OnCancel(object? sender, Avalonia.Interactivity.RoutedEventArgs e)  => Close(false);
}
