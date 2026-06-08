namespace NordSampleManager.Services;

public sealed record LibraryCatalogEntry(
    string  Title,
    string  Description,
    string? PreviewMp3Url,
    string? PianoDownloadsUrl,    // non-null for Piano Library entries
    string? AccordionItemsUrl,    // non-null for Sample Library categories
    string  CompatibleProductsUrl,
    string  LibraryType,          // "Piano" | "Sample"
    string[] Tags);

public sealed record PianoDownloadOption(
    string Size,           // "xl" | "l" | "m" | "s"
    string FileSize,       // e.g. "208.54 MB"
    string DownloadUrl,    // relative path on nordkeyboards.com
    string VersionName);   // e.g. "6.1"

public sealed record PianoDownloads(
    IReadOnlyList<PianoDownloadOption> Options,
    IReadOnlyList<CompatibleKeyboard>  CompatibleProducts);

public sealed record SampleInstrument(
    int     Id,
    string  Title,
    string  FileSize,
    string  Version,
    string  DownloadUrlTemplate,   // contains placeholder keyboard code — caller substitutes
    string? PreviewMp3Url,
    string  Category);             // parent category name

public sealed record CompatibleKeyboard(string Label, int Value);

public sealed record CompatibleProducts(
    IReadOnlyList<CompatibleKeyboard> Active,
    IReadOnlyList<CompatibleKeyboard> Legacy)
{
    public bool IsCompatible(int keyboardCode) =>
        Active.Concat(Legacy).Any(k => k.Value == keyboardCode);
}
