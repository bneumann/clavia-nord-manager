namespace NordSampleManager.Services;

/// <summary>
/// Maps USB product IDs to nordkeyboards.com API keyboard codes.
/// Extend this table when adding support for other Nord keyboards.
/// </summary>
public static class KeyboardRegistry
{
    private static readonly Dictionary<ushort, int> Map = new()
    {
        { 0x0026, 54 },  // Nord Stage 3
    };

    public static int? TryGetApiCode(ushort usbProductId) =>
        Map.TryGetValue(usbProductId, out var code) ? code : null;
}
