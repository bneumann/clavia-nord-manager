namespace NordSampleManager.Protocol.Records;

public sealed record SongInfo(
    int BankIndex,     // 0–7
    int Location,      // 0–49
    string Name,
    string Version,
    uint CategoryCode,
    string FileType,   // "ns3s"
    IReadOnlyList<string> Programs);  // up to 5 program names from the detail query
