using System.Buffers.Binary;
using LanguageExt;
using static LanguageExt.Prelude;
using NordSampleManager.Protocol.Commands;
using NordSampleManager.Protocol.Framing;
using NordSampleManager.Protocol.Records;
using NordSampleManager.Protocol.Transport;

namespace NordSampleManager.Protocol;

/// <summary>
/// High-level façade over <see cref="INordDevice"/>. All public methods return
/// <see cref="EitherAsync{NordError,T}"/> — Left propagates device/parse errors,
/// Right carries the decoded result.
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

    public EitherAsync<NordError, Unit> ConnectAsync(CancellationToken ct = default)
    {
        var initFrame = MessageBuilder.Build(NordCommands.CmdInit, param1: 0, param2: 2, payload: []);
        return
            from _conn  in device.ConnectAsync(ct)
            from _send  in device.SendAsync(initFrame, ct)
            from reply  in device.ReceiveAsync(MaxResponseBytes, ct)
            select StoreInitResponse(reply);
    }

    private Unit StoreInitResponse(ReadOnlyMemory<byte> reply)
    {
        RawInitResponse = reply.ToArray();
        return unit;
    }

    // ----------------------------------------------------------------
    // Confirmed-working list queries (Param2 = 0x02, varying payload).
    // ----------------------------------------------------------------

    public EitherAsync<NordError, IReadOnlyList<string>> QueryPianoCategoriesAsync(CancellationToken ct = default)
        => ListQueryAsync(NordCommands.ListPianoCategories, ct);

    public EitherAsync<NordError, IReadOnlyList<string>> QueryPianoNamesAsync(CancellationToken ct = default)
        => ListQueryAsync(NordCommands.PianoLibraryId, ct);
    
    public EitherAsync<NordError, IReadOnlyList<string>> QueryBank1Async(CancellationToken ct = default)
        => ListQueryAsync(NordCommands.ListBank1, ct);

    public EitherAsync<NordError, IReadOnlyList<string>> QueryBank2Async(CancellationToken ct = default)
        => ListQueryAsync(NordCommands.ListBank2, ct);

    public EitherAsync<NordError, IReadOnlyList<string>> QueryBank3Async(CancellationToken ct = default)
        => ListQueryAsync(NordCommands.ListBank3, ct);

    public EitherAsync<NordError, IReadOnlyList<string>> QueryBank4Async(CancellationToken ct = default)
        => ListQueryAsync(NordCommands.ListBank4, ct);

    public EitherAsync<NordError, IReadOnlyList<string>> QuerySampLibAsync(CancellationToken ct = default)
        => ListQueryAsync(NordCommands.ListSampLib, ct);

    public EitherAsync<NordError, IReadOnlyList<string>> QueryBanksAtoPAsync(CancellationToken ct = default)
        => ListQueryAsync(NordCommands.ListBanksAtoP, ct);

    public EitherAsync<NordError, IReadOnlyList<string>> QueryBanks1to8V1Async(CancellationToken ct = default)
        => ListQueryAsync(NordCommands.ListBanks1to8V1, ct);

    public EitherAsync<NordError, IReadOnlyList<string>> QueryBanks1to8V2Async(CancellationToken ct = default)
        => ListQueryAsync(NordCommands.ListBanks1to8V2, ct);

    private EitherAsync<NordError, IReadOnlyList<string>> ListQueryAsync(uint payloadSelector, CancellationToken ct)
    {
        var payload = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(payload, payloadSelector);
        return
            from raw in SendAndReceiveAsync(NordCommands.CmdQuery, NordCommands.ParamQuery, NordCommands.QueryListBanks, payload, ct)
            from msg in MessageParser.Parse(raw).ToAsync()
            select MessageParser.ScanLengthPrefixedStrings(msg.Payload);
    }

    // ----------------------------------------------------------------
    // Per-item program enumeration (confirmed protocol, 2026-06-03).
    // ----------------------------------------------------------------

    /// <summary>
    /// Enumerate all occupied program slots across all banks.
    /// Uses the confirmed iterator protocol: LibrarySelect → LibraryInfo → open banks → iterate.
    /// Returns Left on transport error; empty list if no programs found.
    /// </summary>
    public EitherAsync<NordError, IReadOnlyList<ProgramInfo>> QueryAllProgramsAsync(CancellationToken ct = default) =>
        EitherAsync<NordError, Unit>.Right(unit)
            .BindAsync<IReadOnlyList<ProgramInfo>>(async _ =>
            {
                var result = await QueryAllProgramsCoreAsync(ct).ConfigureAwait(false);
                return result.ToAsync();
            });

    private async Task<Either<NordError, IReadOnlyList<ProgramInfo>>> QueryAllProgramsCoreAsync(CancellationToken ct)
    {
        // Phase 1: Select library and fetch count.
        var setupResult = await SetupLibraryIteratorAsync(NordCommands.ProgramLibraryId, ct).ToEither().ConfigureAwait(false);
        if (setupResult.IsLeft) return setupResult.Map(_ => (IReadOnlyList<ProgramInfo>)System.Array.Empty<ProgramInfo>());

        var results = new List<ProgramInfo>();
        uint bankIndex = 0;
        bool allDone = false;

        while (!allDone)
        {
            // Open (or advance to) next bank.
            var openResult = await OpenBankAsync(bankIndex, ct).ToEither().ConfigureAwait(false);
            if (openResult.IsLeft) return openResult.Map(_ => (IReadOnlyList<ProgramInfo>)results);

            IteratorStateData state = default;
            openResult.IfRight(s => state = s);

            // Iterate items in this bank until end-of-bank.
            while (!state.IsEndOfBank)
            {
                var itemResult = await FetchProgramItemAsync(state.Bank, state.NextItem, ct).ToEither().ConfigureAwait(false);
                if (itemResult.IsLeft) return itemResult.Map(_ => (IReadOnlyList<ProgramInfo>)results);

                Option<ProgramInfo> maybeInfo = None;
                itemResult.IfRight(opt => maybeInfo = opt);
                maybeInfo.IfSome(info => results.Add(info));

                // ACK item and get next state.
                var ackResult = await AckItemAsync(state.Bank, state.NextItem, ct).ToEither().ConfigureAwait(false);
                if (ackResult.IsLeft) return ackResult.Map(_ => (IReadOnlyList<ProgramInfo>)results);
                ackResult.IfRight(s => state = s);
            }

            bankIndex++;

            switch (bankIndex)
            {
                // Heuristic: if the device starts returning bank IDs from 0 again, we've wrapped.
                case > 0 when state.Bank < bankIndex - 1:
                // Programs A–P = 16 banks max.
                case >= 16:
                    allDone = true;
                    break;
            }
        }

        // Phase 3: Close iterator.
        await CloseIteratorAsync(ct).ToEither().ConfigureAwait(false);

        return Right<NordError, IReadOnlyList<ProgramInfo>>(results);
    }

    public EitherAsync<NordError, IReadOnlyList<Piano>> QueryAllPianoNamesAsync(CancellationToken ct = default) => 
        QueryAllPianoNamesCoreAsync(ct).ToAsync();

    private async Task<Either<NordError, IReadOnlyList<Piano>>> QueryAllPianoNamesCoreAsync(CancellationToken ct)
    {
        // Fetch category names first so we can label each bank (0=Grand, 1=Upright, …).
        var catResult = await ListQueryAsync(NordCommands.ListPianoCategories, ct).ToEither().ConfigureAwait(false);
        if (catResult.IsLeft) return catResult.Map(_ => (IReadOnlyList<Piano>)System.Array.Empty<Piano>());
        IReadOnlyList<string> categories = new List<string>();
        catResult.IfRight(c => categories = c);

        var setupResult = await SetupLibraryIteratorAsync(NordCommands.PianoLibraryId, ct).ToEither().ConfigureAwait(false);
        if (setupResult.IsLeft) return setupResult.Map(_ => (IReadOnlyList<Piano>)System.Array.Empty<Piano>());

        var results = new List<Piano>();
        uint bankIndex = 0;
        bool allDone = false;

        while (!allDone)
        {
            var openResult = await OpenBankAsync(bankIndex, ct).ToEither().ConfigureAwait(false);
            if (openResult.IsLeft) return openResult.Map(_ => (IReadOnlyList<Piano>)results);

            IteratorStateData state = default;
            openResult.IfRight(s => state = s);

            while (!state.IsEndOfBank)
            {
                var itemResult = await FetchPianoItemAsync(state.Bank, state.NextItem, categories, ct).ToEither().ConfigureAwait(false);
                if (itemResult.IsLeft) return itemResult.Map(_ => (IReadOnlyList<Piano>)results);

                itemResult.IfRight(opt => opt.IfSome(p => results.Add(p)));

                var ackResult = await AckItemAsync(state.Bank, state.NextItem, ct).ToEither().ConfigureAwait(false);
                if (ackResult.IsLeft) return ackResult.Map(_ => (IReadOnlyList<Piano>)results);
                ackResult.IfRight(s => state = s);
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

        await CloseIteratorAsync(ct).ToEither().ConfigureAwait(false);
        return Right<NordError, IReadOnlyList<Piano>>(results);
    }

    private EitherAsync<NordError, Option<Piano>> FetchPianoItemAsync(
        uint bank, uint item, IReadOnlyList<string> categories, CancellationToken ct)
    {
        var payload = new byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(0, 4), bank);
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(4, 4), item);

        var categoryName = (int)bank < categories.Count ? categories[(int)bank] : $"Cat{bank}";

        return EitherAsync<NordError, Unit>.Right(unit)
            .BindAsync<Option<Piano>>(async _ =>
            {
                var basicResult = await SendAndReceiveAsync(
                    NordCommands.CmdQuery, NordCommands.ParamQuery, NordCommands.RequestItemBasic, payload, ct)
                    .ToEither().ConfigureAwait(false);
                if (basicResult.IsLeft)
                    return basicResult.Map(_ => Option<Piano>.None).ToAsync();

                Either<NordError, Option<Piano>> decoded = Right<NordError, Option<Piano>>(None);
                basicResult.IfRight(raw =>
                {
                    if (!MessageParser.TryParse(raw, out var msg)) return;
                    if (!MessageParser.TryParseItemData(msg.Payload, out var data)) return;
                    decoded = Right<NordError, Option<Piano>>(Some(new Piano(
                        CategoryIndex: (int)bank,
                        Category:      categoryName,
                        Location:      (int)item,
                        Name:          data.Name,
                        Version:       $"{data.VersionMajor}.{data.VersionMinor:D2}",
                        SizeBytes:     0,
                        RawPayload:    [])));
                });
                return decoded.ToAsync();
            });
    }

    private EitherAsync<NordError, Unit> SetupLibraryIteratorAsync(uint libraryId, CancellationToken ct)
    {
        var libPayload = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(libPayload, libraryId);
        return
            from _sel  in SendAndReceiveAsync(NordCommands.CmdQuery, NordCommands.ParamQuery, NordCommands.LibrarySelect, libPayload, ct)
            from _info in SendAndReceiveAsync(NordCommands.CmdQuery, NordCommands.ParamQuery, NordCommands.LibraryInfo, libPayload, ct)
            select unit;
    }

    private EitherAsync<NordError, IteratorStateData> OpenBankAsync(uint bankIndex, CancellationToken ct)
    {
        var payload = new byte[12];
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(0, 4), bankIndex);
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(4, 4), 0xFFFF_FFFFu);
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(8, 4), 0u);
        return
            from raw in SendAndReceiveAsync(NordCommands.CmdQuery, NordCommands.ParamQuery, NordCommands.OpenIterator, payload, ct)
            from msg in MessageParser.Parse(raw).ToAsync()
            from state in MessageParser.ParseIteratorState(msg.Payload)
                              .ToEither<NordError>(new NordError.ParseFailed("IteratorState payload too short"))
                              .ToAsync()
            select state;
    }

    private EitherAsync<NordError, Option<ProgramInfo>> FetchProgramItemAsync(uint bank, uint item, CancellationToken ct)
    {
        var basicPayload = new byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(basicPayload.AsSpan(0, 4), bank);
        BinaryPrimitives.WriteUInt32BigEndian(basicPayload.AsSpan(4, 4), item);

        return EitherAsync<NordError, Unit>.Right(unit)
            .BindAsync<Option<ProgramInfo>>(async _ =>
            {
                // Step 1: p2=0x1e → p2=0x1f. Patch name is at offset 36 (uint32 length at 32).
                var basicResult = await SendAndReceiveAsync(
                    NordCommands.CmdQuery, NordCommands.ParamQuery, NordCommands.RequestItemBasic, basicPayload, ct)
                    .ToEither().ConfigureAwait(false);
                if (basicResult.IsLeft)
                    return basicResult.Map(_ => Option<ProgramInfo>.None).ToAsync();

                ItemData itemData = default;
                bool hasItemData = false;
                basicResult.IfRight(raw =>
                {
                    if (MessageParser.TryParse(raw, out var msg))
                        hasItemData = MessageParser.TryParseItemData(msg.Payload, out itemData);
                });
                if (!hasItemData)
                    return Right<NordError, Option<ProgramInfo>>(None).ToAsync();

                // Step 2: p2=0x28 → p2=0x29. Piano A name at offset 33 (byte length at 32),
                // is_occupied=1 means Piano A layer is active in this program.
                var detailResult = await SendAndReceiveAsync(
                    NordCommands.CmdQuery, NordCommands.ParamQuery, NordCommands.RequestItemDetail, basicPayload, ct)
                    .ToEither().ConfigureAwait(false);

                string? pianoA = null;
                detailResult.IfRight(raw =>
                {
                    if (MessageParser.TryParse(raw, out var msg))
                        MessageParser.ParseProgramDetail(msg.Payload)
                            .Filter(d => d.IsOccupied)
                            .IfSome(d => pianoA = d.Name);
                });

                var info = new ProgramInfo(
                    BankLetter:   ((char)('A' + (int)bank)).ToString(),
                    BankId:       (int)bank,
                    ItemIndex:    (int)item,
                    Name:         itemData.Name,
                    CategoryCode: itemData.CategoryField,
                    PianoA:       pianoA);

                return Right<NordError, Option<ProgramInfo>>(Some(info)).ToAsync();
            });
    }

    private EitherAsync<NordError, IteratorStateData> AckItemAsync(uint bank, uint lastItem, CancellationToken ct)
    {
        var payload = new byte[12];
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(0, 4), bank);
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(4, 4), lastItem);
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(8, 4), 0u);
        return
            from raw in SendAndReceiveAsync(NordCommands.CmdQuery, NordCommands.ParamQuery, NordCommands.OpenIterator, payload, ct)
            from msg in MessageParser.Parse(raw).ToAsync()
            from state in MessageParser.ParseIteratorState(msg.Payload)
                              .ToEither<NordError>(new NordError.ParseFailed("IteratorState payload too short"))
                              .ToAsync()
            select state;
    }

    private EitherAsync<NordError, Unit> CloseIteratorAsync(CancellationToken ct) =>
        from raw in SendAndReceiveAsync(NordCommands.CmdQuery, NordCommands.ParamQuery, NordCommands.CloseIterator, ReadOnlyMemory<byte>.Empty, ct)
        select unit;

    // ----------------------------------------------------------------
    // Rename — confirmed protocol from pcapng RE, 2026-06-03.
    // ----------------------------------------------------------------

    /// <summary>
    /// Rename a program and/or change its category. Both changes are sent in a single
    /// edit session; pass the current CategoryCode unchanged if only renaming.
    /// </summary>
    public EitherAsync<NordError, Unit> RenameProgramAsync(
        int bankId, int itemIndex, string newName, uint categoryCode, CancellationToken ct = default)
    {
        var libPayload = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(libPayload, NordCommands.ProgramLibraryId);

        var catPayload = new byte[12];
        BinaryPrimitives.WriteUInt32BigEndian(catPayload.AsSpan(0, 4), (uint)bankId);
        BinaryPrimitives.WriteUInt32BigEndian(catPayload.AsSpan(4, 4), (uint)itemIndex);
        BinaryPrimitives.WriteUInt32BigEndian(catPayload.AsSpan(8, 4), categoryCode);

        var nameBytes = System.Text.Encoding.ASCII.GetBytes(newName);
        var namePayload = new byte[12 + nameBytes.Length];
        BinaryPrimitives.WriteUInt32BigEndian(namePayload.AsSpan(0, 4), (uint)bankId);
        BinaryPrimitives.WriteUInt32BigEndian(namePayload.AsSpan(4, 4), (uint)itemIndex);
        BinaryPrimitives.WriteUInt32BigEndian(namePayload.AsSpan(8, 4), (uint)nameBytes.Length);
        nameBytes.CopyTo(namePayload.AsSpan(12));

        return
            from _sel  in SendAndReceiveAsync(NordCommands.CmdQuery, NordCommands.ParamQuery, NordCommands.LibrarySelect, libPayload, ct)
            from _cat  in SendAndWaitForAckAsync(NordCommands.CmdQuery, NordCommands.ParamQuery, NordCommands.EditItemOpen, catPayload, NordCommands.EditItemOpenAck, ct)
            from _name in SendAndWaitForAckAsync(NordCommands.CmdQuery, NordCommands.ParamQuery, NordCommands.WriteName, namePayload, NordCommands.WriteNameAck, ct)
            from _cls  in SendAndReceiveAsync(NordCommands.CmdQuery, NordCommands.ParamQuery, NordCommands.CloseIterator, ReadOnlyMemory<byte>.Empty, ct)
            select unit;
    }

    private EitherAsync<NordError, NordMessage> SendAndWaitForAckAsync(
        uint cmd, uint param1, uint param2, ReadOnlyMemory<byte> payload,
        uint ackParam2, CancellationToken ct)
    {
        var frame = MessageBuilder.Build(cmd, param1, param2, payload.Span);
        return SendAndWaitForAckCoreAsync(frame, ackParam2, ct).ToAsync();
    }

    private async Task<Either<NordError, NordMessage>> SendAndWaitForAckCoreAsync(
        ReadOnlyMemory<byte> frame, uint ackParam2, CancellationToken ct)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var sendResult = await device.SendAsync(frame, ct).ToEither().ConfigureAwait(false);
            if (sendResult.IsLeft) return sendResult.Map(_ => default(NordMessage));

            while (true)
            {
                var rawResult = await device.ReceiveAsync(MaxResponseBytes, ct).ToEither().ConfigureAwait(false);
                if (rawResult.IsLeft) return rawResult.Map(_ => default(NordMessage));

                ReadOnlyMemory<byte> raw = default;
                rawResult.IfRight(r => raw = r);

                if (!MessageParser.TryParse(raw, out var msg))
                    return Left<NordError, NordMessage>(new NordError.ParseFailed("Malformed ack frame during rename"));

                if (msg.Param2 == ackParam2)
                    return Right<NordError, NordMessage>(msg);
                // p2=0x2c progress notification — keep reading
            }
        }
        finally
        {
            gate.Release();
        }
    }

    // ----------------------------------------------------------------
    // Best-effort queries — protocol shape known, response parsing partial.
    // ----------------------------------------------------------------

    public EitherAsync<NordError, NordMessage> QueryPianoDetailAsync(int categoryIndex, int location, CancellationToken ct = default)
    {
        var payload = new byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(0, 4), (uint)categoryIndex);
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(4, 4), (uint)location);
        return
            from raw in SendAndReceiveAsync(NordCommands.CmdQuery, NordCommands.ParamQuery, NordCommands.QueryPianoDetail, payload, ct)
            from msg in MessageParser.Parse(raw).ToAsync()
            select msg;
    }

    public EitherAsync<NordError, NordMessage> QueryProgramAsync(int programNumber, CancellationToken ct = default)
    {
        var payload = new byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(0, 4), 0u);
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(4, 4), (uint)programNumber);
        return
            from raw in SendAndReceiveAsync(NordCommands.CmdQuery, NordCommands.ParamQuery, NordCommands.QueryProgramOrSong, payload, ct)
            from msg in MessageParser.Parse(raw).ToAsync()
            select msg;
    }

    public EitherAsync<NordError, NordMessage> QuerySongAsync(int songNumber, CancellationToken ct = default)
    {
        var payload = new byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(0, 4), 1u);
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(4, 4), (uint)songNumber);
        return
            from raw in SendAndReceiveAsync(NordCommands.CmdQuery, NordCommands.ParamQuery, NordCommands.QueryProgramOrSong, payload, ct)
            from msg in MessageParser.Parse(raw).ToAsync()
            select msg;
    }

    // ----------------------------------------------------------------
    // Transport helpers — semaphore-serialized send + receive.
    // The gate is managed inside BindAsync so release is guaranteed.
    // ----------------------------------------------------------------

    private EitherAsync<NordError, ReadOnlyMemory<byte>> SendAndReceiveAsync(
        uint cmd, uint param1, uint param2, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        var frame = MessageBuilder.Build(cmd, param1, param2, payload.Span);
        return EitherAsync<NordError, Unit>.Right(unit)
            .BindAsync<ReadOnlyMemory<byte>>(async _ =>
            {
                await gate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    var result = await (
                        from _send in device.SendAsync(frame, ct)
                        from reply in device.ReceiveAsync(MaxResponseBytes, ct)
                        select reply
                    ).ToEither().ConfigureAwait(false);
                    return result.ToAsync();
                }
                finally
                {
                    gate.Release();
                }
            });
    }

    /// <summary>
    /// Send the session-close handshake (cmd=0x06 / p1=0x01 / p2=0x02) and read the ack.
    /// Should be called before <see cref="Dispose"/>. Best-effort — callers should Dispose
    /// even if this returns Left.
    /// </summary>
    public EitherAsync<NordError, Unit> DisconnectAsync(CancellationToken ct = default)
    {
        var frame = MessageBuilder.Build(NordCommands.CmdStatus, param1: 1, param2: 2, payload: []);
        return EitherAsync<NordError, Unit>.Right(unit)
            .BindAsync<Unit>(async _ =>
            {
                await gate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    var result = await (
                        from _send in device.SendAsync(frame, ct)
                        from _ack  in device.ReceiveAsync(MaxResponseBytes, ct)
                        select unit
                    ).ToEither().ConfigureAwait(false);
                    return result.ToAsync();
                }
                finally
                {
                    gate.Release();
                }
            });
    }

    public void Dispose()
    {
        gate.Dispose();
        if (ownsDevice) device.Dispose();
    }
}
