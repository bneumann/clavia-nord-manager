using Avalonia.Controls;

namespace NordSampleManager.Views;

public partial class SoundLibraryPanel : UserControl
{
    public SoundLibraryPanel() => InitializeComponent();

    /// Raised when "Transfer to Instrument" is clicked (install to next empty slot).
    public event EventHandler? TransferRequested;

    /// Raised when "Substitute Selected Sound" is clicked (replace the currently selected device sound).
    public event EventHandler? SubstituteRequested;

    private void OnTransferClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        TransferRequested?.Invoke(this, EventArgs.Empty);

    private void OnSubstituteClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        SubstituteRequested?.Invoke(this, EventArgs.Empty);
}
