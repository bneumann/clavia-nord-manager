using System.Buffers.Binary;
using System.IO.Hashing;
using NordSampleManager.Protocol.Commands;
using NordSampleManager.Protocol.Framing;
using NordSampleManager.Protocol.Records;
using NordSampleManager.Protocol.Transport;

namespace NordSampleManager.Protocol;

/// <summary>
/// High-level façade over <see cref="INordDevice"/>. All public methods return
/// <see cref="Task"/> or <see cref="Task{T}"/> and throw <see cref="NordException"/>
/// on device or protocol errors.
/// Device access is serialized by a semaphore because the Nord protocol pairs every
/// send with exactly one reply.
/// </summary>
public sealed class NordClient : IDisposable
{
    private const int MaxResponseBytes = 16 * 1024;

    private readonly INordDevice device;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly bool ownsDevice;

    public NordClient(INordDevice device, bool ownsDevice = false)
    {
        this.device = device;
        this.ownsDevice = ownsDevice;
    }

    /// <summary>Last raw bytes returned by the device for CMD_INIT, for protocol debugging.</summary>
    public ReadOnlyMemory<byte> RawInitResponse { get; private set; }

    public bool IsConnected => device.IsConnected;

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        await device.ConnectAsync(ct).ConfigureAwait(false);
        var initFrame = MessageBuilder.Build(NordCommands.CmdInit, param1: 0, param2: 2, payload: []);
        await device.SendAsync(initFrame, ct).ConfigureAwait(false);
        var reply = await device.ReceiveAsync(MaxResponseBytes, ct).ConfigureAwait(false);
        RawInitResponse = reply.ToArray();
    }

    // ----------------------------------------------------------------
    // Confirmed-working list queries (Param2 = 0x02, varying payload).
    // ----------------------------------------------------------------

    public Task<IReadOnlyList<string>> QueryPianoCategoriesAsync(CancellationToken ct = default) =>
        ListQueryAsync(NordCommands.ListPianoCategories, ct);

    public Task<IReadOnlyList<string>> QueryPianoNamesAsync(CancellationToken ct = default) =>
        ListQueryAsync(NordCommands.PianoLibraryId, ct);

    public Task<IReadOnlyList<string>> QueryBank1Async(CancellationToken ct = default) =>
        ListQueryAsync(NordCommands.ListBank1, ct);

    public Task<IReadOnlyList<string>> QueryBank2Async(CancellationToken ct = default) =>
        ListQueryAsync(NordCommands.ListBank2, ct);

    public Task<IReadOnlyList<string>> QueryBank3Async(CancellationToken ct = default) =>
        ListQueryAsync(NordCommands.ListBank3, ct);

    public Task<IReadOnlyList<string>> QueryBank4Async(CancellationToken ct = default) =>
        ListQueryAsync(NordCommands.ListBank4, ct);

    public Task<IReadOnlyList<string>> QuerySampLibAsync(CancellationToken ct = default) =>
        ListQueryAsync(NordCommands.ListSampLib, ct);

    public Task<IReadOnlyList<string>> QueryBanksAtoPAsync(CancellationToken ct = default) =>
        ListQueryAsync(NordCommands.ListBanksAtoP, ct);

    public Task<IReadOnlyList<string>> QueryBanks1to8V1Async(CancellationToken ct = default) =>
        ListQueryAsync(NordCommands.ListBanks1to8V1, ct);

    public Task<IReadOnlyList<string>> QueryBanks1to8V2Async(CancellationToken ct = default) =>
        ListQueryAsync(NordCommands.ListBanks1to8V2, ct);

    private async Task<IReadOnlyList<string>> ListQueryAsync(uint payloadSelector, CancellationToken ct)
    {
        var payload = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(payload, payloadSelector);
        var raw = await SendAndReceiveAsync(NordCommands.CmdQuery, NordCommands.ParamQuery, NordCommands.QueryListBanks, payload, ct).ConfigureAwait(false);
        if (!MessageParser.TryParse(raw, out var msg))
            throw new NordException(new NordError.ParseFailed("Malformed list query response"));
        return MessageParser.ScanLengthPrefixedStrings(msg.Payload);
    }

    // ----------------------------------------------------------------
    // Per-item program enumeration (confirmed protocol, 2026-06-03).
    // ----------------------------------------------------------------

    public Task<IReadOnlyList<ProgramInfo>> QueryAllProgramsAsync(CancellationToken ct = default) =>
        QueryAllProgramsCoreAsync(ct);

    private async Task<IReadOnlyList<ProgramInfo>> QueryAllProgramsCoreAsync(CancellationToken ct)
    {
        await SetupLibraryIteratorAsync(NordCommands.ProgramLibraryId, ct).ConfigureAwait(false);

        var results = new List<ProgramInfo>();
        uint bankIndex = 0;
        bool allDone = false;

        while (!allDone)
        {
            var state = await OpenBankAsync(bankIndex, ct).ConfigureAwait(false);

            while (!state.IsEndOfBank)
            {
                var info = await FetchProgramItemAsync(state.Bank, state.NextItem, ct).ConfigureAwait(false);
                if (info is not null) results.Add(info);
                state = await AckItemAsync(state.Bank, state.NextItem, ct).ConfigureAwait(false);
            }

            bankIndex++;
            switch (bankIndex)
            {
                case > 0 when state.Bank < bankIndex - 1:
                case >= 16:
                    allDone = true;
                    break;
            }
        }

        try { await CloseIteratorAsync(ct).ConfigureAwait(false); } catch { }
        return results;
    }

    public Task<IReadOnlyList<Piano>> QueryAllPianoNamesAsync(CancellationToken ct = default) =>
        QueryAllPianoNamesCoreAsync(ct);

    private async Task<IReadOnlyList<Piano>> QueryAllPianoNamesCoreAsync(CancellationToken ct)
    {
        var categories = await ListQueryAsync(NordCommands.ListPianoCategories, ct).ConfigureAwait(false);
        await SetupLibraryIteratorAsync(NordCommands.PianoLibraryId, ct).ConfigureAwait(false);

        var results = new List<Piano>();
        uint bankIndex = 0;
        bool allDone = false;

        while (!allDone)
        {
            var state = await OpenBankAsync(bankIndex, ct).ConfigureAwait(false);

            while (!state.IsEndOfBank)
            {
                var piano = await FetchPianoItemAsync(state.Bank, state.NextItem, categories, ct).ConfigureAwait(false);
                if (piano is not null) results.Add(piano);
                state = await AckItemAsync(state.Bank, state.NextItem, ct).ConfigureAwait(false);
            }

            bankIndex++;
            switch (bankIndex)
            {
                case > 0 when state.Bank < bankIndex - 1:
                case >= 12:
                    allDone = true;
                    break;
            }
        }

        try { await CloseIteratorAsync(ct).ConfigureAwait(false); } catch { }
        return results;
    }

    private async Task<Piano?> FetchPianoItemAsync(
        uint bank, uint item, IReadOnlyList<string> categories, CancellationToken ct)
    {
        var payload = new byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(0, 4), bank);
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(4, 4), item);
        var categoryName = (int)bank < categories.Count ? categories[(int)bank] : $"Cat{bank}";

        var raw = await SendAndReceiveAsync(NordCommands.CmdQuery, NordCommands.ParamQuery, NordCommands.RequestItemBasic, payload, ct).ConfigureAwait(false);
        if (!MessageParser.TryParse(raw, out var msg) || !MessageParser.TryParseItemData(msg.Payload, out var data))
            return null;

        return new Piano(
            CategoryIndex: (int)bank,
            Category:      categoryName,
            Location:      (int)item,
            Name:          data.Name,
            Version:       $"{data.VersionMajor}.{data.VersionMinor:D2}",
            SizeBytes:     0,
            RawPayload:    []);
    }

    private async Task SetupLibraryIteratorAsync(uint libraryId, CancellationToken ct)
    {
        var libPayload = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(libPayload, libraryId);
        await SendAndReceiveAsync(NordCommands.CmdQuery, NordCommands.ParamQuery, NordCommands.LibrarySelect, libPayload, ct).ConfigureAwait(false);
        await SendAndReceiveAsync(NordCommands.CmdQuery, NordCommands.ParamQuery, NordCommands.LibraryInfo, libPayload, ct).ConfigureAwait(false);
    }

    private async Task<IteratorStateData> OpenBankAsync(uint bankIndex, CancellationToken ct)
    {
        var payload = new byte[12];
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(0, 4), bankIndex);
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(4, 4), 0xFFFF_FFFFu);
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(8, 4), 0u);
        var raw = await SendAndReceiveAsync(NordCommands.CmdQuery, NordCommands.ParamQuery, NordCommands.OpenIterator, payload, ct).ConfigureAwait(false);
        return RequireIteratorState(raw, "OpenBank");
    }

    private async Task<ProgramInfo?> FetchProgramItemAsync(uint bank, uint item, CancellationToken ct)
    {
        var payload = new byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(0, 4), bank);
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(4, 4), item);

        var basicRaw = await SendAndReceiveAsync(NordCommands.CmdQuery, NordCommands.ParamQuery, NordCommands.RequestItemBasic, payload, ct).ConfigureAwait(false);
        if (!MessageParser.TryParse(basicRaw, out var basicMsg) || !MessageParser.TryParseItemData(basicMsg.Payload, out var itemData))
            return null;

        string? pianoA = null;
        try
        {
            var detailRaw = await SendAndReceiveAsync(NordCommands.CmdQuery, NordCommands.ParamQuery, NordCommands.RequestItemDetail, payload, ct).ConfigureAwait(false);
            if (MessageParser.TryParse(detailRaw, out var detailMsg))
            {
                var detail = MessageParser.ParseProgramDetail(detailMsg.Payload);
                if (detail is { IsOccupied: true })
                    pianoA = detail.Name;
            }
        }
        catch { /* best-effort */ }

        return new ProgramInfo(
            BankLetter:   ((char)('A' + (int)bank)).ToString(),
            BankId:       (int)bank,
            ItemIndex:    (int)item,
            Name:         itemData.Name,
            CategoryCode: itemData.CategoryField,
            PianoA:       pianoA);
    }

    private async Task<IteratorStateData> AckItemAsync(uint bank, uint lastItem, CancellationToken ct)
    {
        var payload = new byte[12];
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(0, 4), bank);
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(4, 4), lastItem);
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(8, 4), 0u);
        var raw = await SendAndReceiveAsync(NordCommands.CmdQuery, NordCommands.ParamQuery, NordCommands.OpenIterator, payload, ct).ConfigureAwait(false);
        return RequireIteratorState(raw, "AckItem");
    }

    private static IteratorStateData RequireIteratorState(ReadOnlyMemory<byte> raw, string context)
    {
        if (!MessageParser.TryParse(raw, out var msg))
            throw new NordException(new NordError.ParseFailed($"Malformed {context} response"));
        return MessageParser.ParseIteratorState(msg.Payload)
            ?? throw new NordException(new NordError.ParseFailed($"{context}: IteratorState payload too short"));
    }

    private Task CloseIteratorAsync(CancellationToken ct) =>
        SendAndReceiveAsync(NordCommands.CmdQuery, NordCommands.ParamQuery, NordCommands.CloseIterator, ReadOnlyMemory<byte>.Empty, ct);

    // ----------------------------------------------------------------
    // Download — confirmed protocol from Upload Test2.pcapng, 2026-06-03.
    // ----------------------------------------------------------------

    public Task<byte[]> DownloadProgramAsync(int bankId, int itemIndex, CancellationToken ct = default) =>
        DownloadProgramCoreAsync(bankId, itemIndex, ct);

    private async Task<byte[]> DownloadProgramCoreAsync(int bankId, int itemIndex, CancellationToken ct)
    {
        var itemPayload = new byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(itemPayload.AsSpan(0, 4), (uint)bankId);
        BinaryPrimitives.WriteUInt32BigEndian(itemPayload.AsSpan(4, 4), (uint)itemIndex);
        var libPayload = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(libPayload, NordCommands.ProgramLibraryId);

        await SendAndReceiveAsync(NordCommands.CmdQuery, NordCommands.ParamQuery, NordCommands.LibrarySelect, libPayload, ct).ConfigureAwait(false);

        var basicRaw = await SendAndReceiveAsync(NordCommands.CmdQuery, NordCommands.ParamQuery, NordCommands.RequestItemBasic, itemPayload, ct).ConfigureAwait(false);
        if (!MessageParser.TryParse(basicRaw, out var basicMsg) || !MessageParser.TryParseItemData(basicMsg.Payload, out var meta))
            throw new NordException(new NordError.ParseFailed("Failed to parse ItemBasicData"));
        if (meta.DataSize == 0)
            throw new NordException(new NordError.ParseFailed("ItemBasicData reported DataSize=0"));

        await SendAndReceiveAsync(NordCommands.CmdQuery, NordCommands.ParamQuery, NordCommands.RequestDownload, itemPayload, ct).ConfigureAwait(false);

        var startPayload = new byte[16];
        BinaryPrimitives.WriteUInt32BigEndian(startPayload.AsSpan(0,  4), (uint)bankId);
        BinaryPrimitives.WriteUInt32BigEndian(startPayload.AsSpan(4,  4), (uint)itemIndex);
        BinaryPrimitives.WriteUInt32BigEndian(startPayload.AsSpan(8,  4), 0u);
        BinaryPrimitives.WriteUInt32BigEndian(startPayload.AsSpan(12, 4), meta.DataSize);

        var fileDataRaw = await SendAndReceiveAsync(NordCommands.CmdQuery, NordCommands.ParamQuery, NordCommands.StartTransfer, startPayload, ct).ConfigureAwait(false);
        if (!MessageParser.TryParse(fileDataRaw, out var fileMsg) || fileMsg.Param2 != NordCommands.FileData)
            throw new NordException(new NordError.ParseFailed($"Expected p2=0x{NordCommands.FileData:x}, got unexpected response"));
        var filePayload = fileMsg.Payload.Span;
        if (filePayload.Length < 20)
            throw new NordException(new NordError.ParseFailed("FileData payload too short"));
        var receivedSize = (int)BinaryPrimitives.ReadUInt32BigEndian(filePayload.Slice(16, 4));
        var rawData = filePayload.Slice(20, receivedSize).ToArray();

        await SendAndReceiveAsync(NordCommands.CmdQuery, NordCommands.ParamQuery, NordCommands.FinishTransfer, itemPayload, ct).ConfigureAwait(false);
        try { await SendAndReceiveAsync(NordCommands.CmdQuery, NordCommands.ParamQuery, NordCommands.LibraryInfo, libPayload, ct).ConfigureAwait(false); } catch { }
        try { await CloseIteratorAsync(ct).ConfigureAwait(false); } catch { }

        return BuildNs3fFile(bankId, itemIndex, meta, rawData);
    }

    private static byte[] BuildNs3fFile(int bankId, int itemIndex, ItemData meta, byte[] rawData)
    {
        var file = new byte[44 + rawData.Length];
        var span = file.AsSpan();
        System.Text.Encoding.ASCII.GetBytes("CBIN").CopyTo(span);
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(4,  4), 1u);
        System.Text.Encoding.ASCII.GetBytes(meta.FileType).CopyTo(span.Slice(8, 4));
        span[12] = (byte)bankId;
        span[14] = (byte)itemIndex;
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(16, 4), (uint)(meta.Name.Length + 1));
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(20, 4), (uint)(meta.VersionMajor * 100 + meta.VersionMinor));
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(24, 4), Crc32.HashToUInt32(rawData));
        rawData.CopyTo(span.Slice(44));
        return file;
    }

    // ----------------------------------------------------------------
    // Upload — confirmed protocol from Download Stevie Likes It To Nord.pcapng, 2026-06-03.
    // ----------------------------------------------------------------

    public Task UploadProgramAsync(
        int bankId, int itemIndex, string name, string fileType,
        byte[] rawData, uint categoryCode = 0, CancellationToken ct = default) =>
        UploadProgramCoreAsync(bankId, itemIndex, name, fileType, rawData, categoryCode, ct);

    private async Task UploadProgramCoreAsync(
        int bankId, int itemIndex, string name, string fileType,
        byte[] rawData, uint categoryCode, CancellationToken ct)
    {
        var libPayload = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(libPayload, NordCommands.ProgramLibraryId);
        var itemPayload = new byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(itemPayload.AsSpan(0, 4), (uint)bankId);
        BinaryPrimitives.WriteUInt32BigEndian(itemPayload.AsSpan(4, 4), (uint)itemIndex);

        await SendAndReceiveAsync(NordCommands.CmdQuery, NordCommands.ParamQuery, NordCommands.LibrarySelect, libPayload, ct).ConfigureAwait(false);

        var nameBytes   = System.Text.Encoding.ASCII.GetBytes(name);
        var metaPayload = new byte[28 + nameBytes.Length];
        BinaryPrimitives.WriteUInt32BigEndian(metaPayload.AsSpan(0,  4), (uint)bankId);
        BinaryPrimitives.WriteUInt32BigEndian(metaPayload.AsSpan(4,  4), (uint)itemIndex);
        BinaryPrimitives.WriteUInt32BigEndian(metaPayload.AsSpan(8,  4), (uint)rawData.Length);
        System.Text.Encoding.ASCII.GetBytes(fileType).CopyTo(metaPayload.AsSpan(12, 4));
        BinaryPrimitives.WriteUInt32BigEndian(metaPayload.AsSpan(16, 4), Crc32.HashToUInt32(rawData));
        BinaryPrimitives.WriteUInt32BigEndian(metaPayload.AsSpan(20, 4), categoryCode);
        BinaryPrimitives.WriteUInt32BigEndian(metaPayload.AsSpan(24, 4), (uint)nameBytes.Length);
        nameBytes.CopyTo(metaPayload.AsSpan(28));

        await SendAndReceiveAsync(NordCommands.CmdQuery, NordCommands.ParamQuery, NordCommands.UploadMetadata, metaPayload, ct).ConfigureAwait(false);

        var dataPayload = new byte[16 + rawData.Length];
        BinaryPrimitives.WriteUInt32BigEndian(dataPayload.AsSpan(0,  4), (uint)bankId);
        BinaryPrimitives.WriteUInt32BigEndian(dataPayload.AsSpan(4,  4), (uint)itemIndex);
        BinaryPrimitives.WriteUInt32BigEndian(dataPayload.AsSpan(8,  4), 0u);
        BinaryPrimitives.WriteUInt32BigEndian(dataPayload.AsSpan(12, 4), (uint)rawData.Length);
        rawData.CopyTo(dataPayload.AsSpan(16));

        await SendAndReceiveAsync(NordCommands.CmdQuery, NordCommands.ParamQuery, NordCommands.SendFileData, dataPayload, ct).ConfigureAwait(false);
        await SendAndReceiveAsync(NordCommands.CmdQuery, NordCommands.ParamQuery, NordCommands.FinishTransfer, itemPayload, ct).ConfigureAwait(false);
        try { await SendAndReceiveAsync(NordCommands.CmdQuery, NordCommands.ParamQuery, NordCommands.LibraryInfo, libPayload, ct).ConfigureAwait(false); } catch { }
        try { await CloseIteratorAsync(ct).ConfigureAwait(false); } catch { }
    }

    // ----------------------------------------------------------------
    // Delete — confirmed protocol from Delete Stevie Likes It.pcapng, 2026-06-03.
    // ----------------------------------------------------------------

    public Task DeleteProgramAsync(int bankId, int itemIndex, CancellationToken ct = default) =>
        DeleteProgramCoreAsync(bankId, itemIndex, ct);

    private async Task DeleteProgramCoreAsync(int bankId, int itemIndex, CancellationToken ct)
    {
        var libPayload = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(libPayload, NordCommands.ProgramLibraryId);
        var itemPayload = new byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(itemPayload.AsSpan(0, 4), (uint)bankId);
        BinaryPrimitives.WriteUInt32BigEndian(itemPayload.AsSpan(4, 4), (uint)itemIndex);

        await SendAndReceiveAsync(NordCommands.CmdQuery, NordCommands.ParamQuery, NordCommands.LibrarySelect, libPayload, ct).ConfigureAwait(false);

        var raw = await SendAndReceiveAsync(NordCommands.CmdQuery, NordCommands.ParamQuery, NordCommands.DeleteRequest, itemPayload, ct).ConfigureAwait(false);
        CheckStatusResponse(raw, "Delete");

        try { await SendAndReceiveAsync(NordCommands.CmdQuery, NordCommands.ParamQuery, NordCommands.LibraryInfo, libPayload, ct).ConfigureAwait(false); } catch { }
        try { await CloseIteratorAsync(ct).ConfigureAwait(false); } catch { }
    }

    // ----------------------------------------------------------------
    // Swap — confirmed protocol from Swap Bank N22 with N21.pcapng, 2026-06-03.
    // ----------------------------------------------------------------

    public Task SwapProgramsAsync(int bank1, int item1, int bank2, int item2, CancellationToken ct = default) =>
        SwapProgramsCoreAsync(bank1, item1, bank2, item2, ct);

    private async Task SwapProgramsCoreAsync(int bank1, int item1, int bank2, int item2, CancellationToken ct)
    {
        var libPayload = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(libPayload, NordCommands.ProgramLibraryId);
        var swapPayload = new byte[16];
        BinaryPrimitives.WriteUInt32BigEndian(swapPayload.AsSpan(0,  4), (uint)bank1);
        BinaryPrimitives.WriteUInt32BigEndian(swapPayload.AsSpan(4,  4), (uint)item1);
        BinaryPrimitives.WriteUInt32BigEndian(swapPayload.AsSpan(8,  4), (uint)bank2);
        BinaryPrimitives.WriteUInt32BigEndian(swapPayload.AsSpan(12, 4), (uint)item2);

        await SendAndReceiveAsync(NordCommands.CmdQuery, NordCommands.ParamQuery, NordCommands.LibrarySelect, libPayload, ct).ConfigureAwait(false);

        var raw = await SendAndReceiveAsync(NordCommands.CmdQuery, NordCommands.ParamQuery, NordCommands.SwapRequest, swapPayload, ct).ConfigureAwait(false);
        CheckStatusResponse(raw, "Swap");

        try { await SendAndReceiveAsync(NordCommands.CmdQuery, NordCommands.ParamQuery, NordCommands.LibraryInfo, libPayload, ct).ConfigureAwait(false); } catch { }
        try { await CloseIteratorAsync(ct).ConfigureAwait(false); } catch { }
    }

    // ----------------------------------------------------------------
    // Rename — confirmed protocol from pcapng RE, 2026-06-03.
    // ----------------------------------------------------------------

    public async Task RenameProgramAsync(
        int bankId, int itemIndex, string newName, uint categoryCode, CancellationToken ct = default)
    {
        var libPayload = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(libPayload, NordCommands.ProgramLibraryId);

        var catPayload = new byte[12];
        BinaryPrimitives.WriteUInt32BigEndian(catPayload.AsSpan(0, 4), (uint)bankId);
        BinaryPrimitives.WriteUInt32BigEndian(catPayload.AsSpan(4, 4), (uint)itemIndex);
        BinaryPrimitives.WriteUInt32BigEndian(catPayload.AsSpan(8, 4), categoryCode);

        var nameBytes   = System.Text.Encoding.ASCII.GetBytes(newName);
        var namePayload = new byte[12 + nameBytes.Length];
        BinaryPrimitives.WriteUInt32BigEndian(namePayload.AsSpan(0, 4), (uint)bankId);
        BinaryPrimitives.WriteUInt32BigEndian(namePayload.AsSpan(4, 4), (uint)itemIndex);
        BinaryPrimitives.WriteUInt32BigEndian(namePayload.AsSpan(8, 4), (uint)nameBytes.Length);
        nameBytes.CopyTo(namePayload.AsSpan(12));

        await SendAndReceiveAsync(NordCommands.CmdQuery, NordCommands.ParamQuery, NordCommands.LibrarySelect, libPayload, ct).ConfigureAwait(false);
        await SendAndWaitForAckAsync(NordCommands.CmdQuery, NordCommands.ParamQuery, NordCommands.EditItemOpen, catPayload, NordCommands.EditItemOpenAck, ct).ConfigureAwait(false);
        await SendAndWaitForAckAsync(NordCommands.CmdQuery, NordCommands.ParamQuery, NordCommands.WriteName, namePayload, NordCommands.WriteNameAck, ct).ConfigureAwait(false);
        await SendAndReceiveAsync(NordCommands.CmdQuery, NordCommands.ParamQuery, NordCommands.CloseIterator, ReadOnlyMemory<byte>.Empty, ct).ConfigureAwait(false);
    }

    // ----------------------------------------------------------------
    // Best-effort queries — protocol shape known, response parsing partial.
    // ----------------------------------------------------------------

    public async Task<NordMessage> QueryPianoDetailAsync(int categoryIndex, int location, CancellationToken ct = default)
    {
        var payload = new byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(0, 4), (uint)categoryIndex);
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(4, 4), (uint)location);
        var raw = await SendAndReceiveAsync(NordCommands.CmdQuery, NordCommands.ParamQuery, NordCommands.QueryPianoDetail, payload, ct).ConfigureAwait(false);
        if (!MessageParser.TryParse(raw, out var msg))
            throw new NordException(new NordError.ParseFailed("Malformed piano detail response"));
        return msg;
    }

    public async Task<NordMessage> QueryProgramAsync(int programNumber, CancellationToken ct = default)
    {
        var payload = new byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(0, 4), 0u);
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(4, 4), (uint)programNumber);
        var raw = await SendAndReceiveAsync(NordCommands.CmdQuery, NordCommands.ParamQuery, NordCommands.QueryProgramOrSong, payload, ct).ConfigureAwait(false);
        if (!MessageParser.TryParse(raw, out var msg))
            throw new NordException(new NordError.ParseFailed("Malformed program query response"));
        return msg;
    }

    public async Task<NordMessage> QuerySongAsync(int songNumber, CancellationToken ct = default)
    {
        var payload = new byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(0, 4), 1u);
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(4, 4), (uint)songNumber);
        var raw = await SendAndReceiveAsync(NordCommands.CmdQuery, NordCommands.ParamQuery, NordCommands.QueryProgramOrSong, payload, ct).ConfigureAwait(false);
        if (!MessageParser.TryParse(raw, out var msg))
            throw new NordException(new NordError.ParseFailed("Malformed song query response"));
        return msg;
    }

    // ----------------------------------------------------------------
    // Transport helpers — semaphore-serialized send + receive.
    // ----------------------------------------------------------------

    private async Task<ReadOnlyMemory<byte>> SendAndReceiveAsync(
        uint cmd, uint param1, uint param2, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        var frame = MessageBuilder.Build(cmd, param1, param2, payload.Span);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await device.SendAsync(frame, ct).ConfigureAwait(false);
            return await device.ReceiveAsync(MaxResponseBytes, ct).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private static void CheckStatusResponse(ReadOnlyMemory<byte> raw, string operation)
    {
        if (!MessageParser.TryParse(raw, out var msg) ||
            !MessageParser.ParseStatusResponse(msg.Payload.Span, out var status))
            throw new NordException(new NordError.ParseFailed($"Failed to parse {operation} response"));
        if (status != 0)
            throw new NordException(new NordError.ParseFailed($"{operation} failed: status=0x{status:x}"));
    }

    private async Task<NordMessage> SendAndWaitForAckAsync(
        uint cmd, uint param1, uint param2, ReadOnlyMemory<byte> payload, uint ackParam2, CancellationToken ct)
    {
        var frame = MessageBuilder.Build(cmd, param1, param2, payload.Span);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await device.SendAsync(frame, ct).ConfigureAwait(false);
            while (true)
            {
                var raw = await device.ReceiveAsync(MaxResponseBytes, ct).ConfigureAwait(false);
                if (!MessageParser.TryParse(raw, out var msg))
                    throw new NordException(new NordError.ParseFailed("Malformed ack frame during rename"));
                if (msg.Param2 == ackParam2) return msg;
                // p2=0x2c progress notification — keep reading
            }
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Send the session-close handshake and read the ack.
    /// Should be called before <see cref="Dispose"/>. Best-effort — callers should Dispose
    /// even if this throws.
    /// </summary>
    public async Task DisconnectAsync(CancellationToken ct = default)
    {
        var frame = MessageBuilder.Build(NordCommands.CmdStatus, param1: 1, param2: 2, payload: []);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await device.SendAsync(frame, ct).ConfigureAwait(false);
            await device.ReceiveAsync(MaxResponseBytes, ct).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    public void Dispose()
    {
        gate.Dispose();
        if (ownsDevice) device.Dispose();
    }
}
