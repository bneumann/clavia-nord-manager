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

    // Per-item iterator protocol (confirmed from pcapng RE, 2026-06-03).
    // All of these are p2 values within CmdQuery / ParamQuery.
    public const uint LibrarySelect     = 0x00000004;  // H→D: payload=[library_id]
    public const uint LibrarySelectAck  = 0x00000005;  // D→H
    public const uint LibraryInfo       = 0x00000008;  // H→D: payload=[library_id]
    public const uint LibraryInfoAck    = 0x00000009;  // D→H: payload=[0, total_count, ...]
    public const uint OpenIterator      = 0x00000020;  // H→D: [bank_index, 0xffffffff, 0] to open bank; [0, item, 0] to ACK item
    public const uint IteratorState     = 0x00000021;  // D→H: [counter, bank, next_item]; counter=1 means end-of-bank
    public const uint RequestItemBasic  = 0x0000001e;  // H→D: [bank, item] — same numeric value as QueryPianoDetail
    public const uint ItemBasicData     = 0x0000001f;  // D→H: basic ns3f header (file type, version, id)
    public const uint RequestItemDetail = 0x00000028;  // H→D: [bank, item] — same numeric value as QueryProgramOrSong
    public const uint ItemDetailData    = 0x00000029;  // D→H: full detail with name and slot flags
    public const uint CloseIterator     = 0x00000006;  // H→D: empty payload; ends the iterator session
    public const uint CloseIteratorAck  = 0x00000007;  // D→H
    public const uint ProgramLibraryId  = 0x00000007;  // library_id that selects Programs A-P (ns3f)
    public const uint PianoLibraryId    = 0x00000001;  // library_id that selects Pianos (npno); banks = categories
    public const uint SynthLibraryId    = 0x00000008;  // library_id that selects Synths (ns3y); 8 banks × 50 slots
    public const uint SongLibraryId     = 0x00000009;  // library_id that selects Songs  (ns3s); 8 banks × 50 slots

    // Download commands (confirmed from Upload Test2.pcapng, 2026-06-03).
    public const uint RequestDownload   = 0x0000000c;  // H→D: [bank, item]
    public const uint DownloadReady     = 0x0000000d;  // D→H: [0, bank, item]
    public const uint StartTransfer     = 0x00000012;  // H→D: [bank, item, offset, dataSize]
    public const uint FileData          = 0x00000013;  // D→H: [0, bank, item, 0, dataSize, ...rawData]
    public const uint FinishTransfer    = 0x0000000e;  // H→D: [bank, item]
    public const uint FinishTransferAck = 0x0000000f;  // D→H: [0, bank, item]

    // Upload commands (confirmed from Download Stevie Likes It To Nord.pcapng, 2026-06-03).
    public const uint UploadMetadata    = 0x0000000a;  // H→D: [bank, item, size, type, crc32, category, nameLen, name]
    public const uint UploadMetadataAck = 0x0000000b;  // D→H: [status, bank, item]
    public const uint SendFileData      = 0x00000010;  // H→D: [bank, item, offset, size, rawData]
    public const uint SendFileDataAck   = 0x00000011;  // D→H: [status, bank, item]

    // Delete commands (confirmed from Delete Stevie Likes It.pcapng, 2026-06-03).
    public const uint DeleteRequest  = 0x00000014;  // H→D: [bank, item]
    public const uint DeleteResponse = 0x00000015;  // D→H: [status, bank, item]

    // Swap commands (confirmed from Swap Bank N22 with N21.pcapng, 2026-06-03).
    public const uint SwapRequest  = 0x0000001a;  // H→D: [bank1, item1, bank2, item2]
    public const uint SwapResponse = 0x0000001b;  // D→H: [status]

    // Rename / write commands (confirmed from Rename N11 pcapng, 2026-06-03).
    public const uint EditItemOpen    = 0x00000033;  // H→D: [bank_id, item_index, new_category_code]
    public const uint EditItemOpenAck = 0x00000034;  // D→H
    public const uint WriteName       = 0x0000001c;  // H→D: [bank_id, item_index, name_len, name_bytes]
    public const uint WriteNameAck    = 0x0000001d;  // D→H
    public const uint ProgressNotify  = 0x0000002c;  // D→H progress notification (emitted 2-3× before real ack)

    // Payload selectors for the "list banks" query (Param2 = 0x02).
    // Capacities from captures: 0x02=100, 0x03=9, 0x04=400, 0x06=100 — all return name "Bank 1".
    // 0x03/0x04/0x06 purposes inferred from capacity; not confirmed from captures.
    public const uint ListPianoCategories  = 0x00000001;  // 6 categories, capacity 20 each
    public const uint ListSongsFlat        = 0x00000002;  // 1 bank "Bank 1", capacity 100
    public const uint ListLiveBuffers      = 0x00000003;  // 1 bank "Bank 1", capacity 9 (Stage 3 has 9 Live slots)
    public const uint ListProgramsFlat     = 0x00000004;  // 1 bank "Bank 1", capacity 400 (16×25)
    public const uint ListSampLib          = 0x00000005;  // 1 bank "Samp Lib", capacity 400
    public const uint ListSongsV2          = 0x00000006;  // 1 bank "Bank 1", capacity 100 (purpose unclear)
    public const uint ListBanksAtoP        = 0x00000007;  // 16 banks A–P, capacity 25 each (Programs)
    public const uint ListBanks1to8V1      = 0x00000008;  // 8 banks, capacity 50 each (Synths, ns3y)
    public const uint ListBanks1to8V2      = 0x00000009;  // 8 banks, capacity 50 each (Songs, ns3s)
}
