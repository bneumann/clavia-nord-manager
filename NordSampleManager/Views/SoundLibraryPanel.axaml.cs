using Avalonia.Controls;

namespace NordSampleManager.Views;

public partial class SoundLibraryPanel : UserControl
{
    public SoundLibraryPanel() => InitializeComponent();

    // Raised when "Install…" is clicked so the parent window can open the dialog.
    public event EventHandler? InstallRequested;

    private void OnInstallClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        InstallRequested?.Invoke(this, EventArgs.Empty);
}
