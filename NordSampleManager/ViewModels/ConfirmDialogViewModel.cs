namespace NordSampleManager.ViewModels;

public sealed class ConfirmDialogViewModel
{
    public string Title        { get; }
    public string Message      { get; }
    public string ConfirmLabel { get; }

    public ConfirmDialogViewModel(string title, string message, string confirmLabel = "OK")
    {
        Title        = title;
        Message      = message;
        ConfirmLabel = confirmLabel;
    }
}
