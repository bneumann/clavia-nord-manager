namespace NordSampleManager.Protocol.Commands;

/// <summary>
/// Protocol constants — see README.md "Protokoll GET" for the canonical table.
/// </summary>
public static class NordCommands
{
    public const uint CmdInit = 0x00000007;
    public const uint CmdStatus = 0x00000006;
    public const uint CmdQuery = 0x0000000c;

    public const uint ParamQuery = 0x0000000a;

    // Param2 (query subtype) constants.
    public const uint QueryListBanks     = 0x00000002;
    public const uint QueryProgramOrSong = 0x00000028;
    public const uint QueryPianoDetail   = 0x0000001e;

    // Per-item query (Param2=0x21 host→device); payload = [0x00000000 | container | itemIndex].
    // Response flow: device may first send 0x1e (echo), then 0x1f (data) or 0x20 (end-of-container).
    public const uint QueryItem = 0x00000021;
    // 0x1e doubles as device→host echo in item queries (same value as QueryPianoDetail).
    public const uint ItemResponseData      = 0x0000001f;
    public const uint ItemResponseEndMarker = 0x00000020;

    // Program (ns3f) container layout: containers 0..13 = Banks A..N, up to 25 items each.
    public const int ProgramContainerCount    = 14;
    public const int ProgramItemsPerContainer = 25;

    // Payload selectors for the "list banks" query (Param2 = 0x02).
    public const uint ListPianoCategories = 0x00000001;
    public const uint ListBank1 = 0x00000002;
    public const uint ListBank2 = 0x00000003;
    public const uint ListBank3 = 0x00000004;
    public const uint ListSampLib = 0x00000005;
    public const uint ListBank4 = 0x00000006;
    public const uint ListBanksAtoP = 0x00000007;
    public const uint ListBanks1to8V1 = 0x00000008;
    public const uint ListBanks1to8V2 = 0x00000009;
}
