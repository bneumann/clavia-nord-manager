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
    // Per-item queries (Param2=0x21, RE hypothesis 2026-06-03).
    // ----------------------------------------------------------------

    /// <summary>
    /// Enumerate all program slots across Banks A–N (containers 0–13, up to 25 items each).
    /// Returns Left on transport error; returns an empty or partial list if items time out (best-effort).
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
        var results = new List<ProgramInfo>();
        for (var container = 0; container < NordCommands.ProgramContainerCount; container++)
        {
            bool containerDone = false;
            for (var item = 0; item < NordCommands.ProgramItemsPerContainer && !containerDone; item++)
            {
                var itemResult = await QueryItemAsync(container, item, ct).ToEither().ConfigureAwait(false);

                if (itemResult.IsLeft)
                    return itemResult.Map(_ => (IReadOnlyList<ProgramInfo>)results);

                Option<ProgramInfo> maybeInfo = Option<ProgramInfo>.None;
                itemResult.IfRight(opt => maybeInfo = opt);
                maybeInfo.IfSome(info => results.Add(info));
                containerDone = maybeInfo.IsNone;
            }
        }
        return Right<NordError, IReadOnlyList<ProgramInfo>>(results);
    }

    private EitherAsync<NordError, Option<ProgramInfo>> QueryItemAsync(int container, int item, CancellationToken ct)
    {
        var payload = new byte[12];
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(0, 4), 0u);
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(4, 4), (uint)container);
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(8, 4), (uint)item);
        return
            from raw in SendAndReceiveItemAsync(NordCommands.QueryItem, payload, ct)
            from msg in MessageParser.Parse(raw).ToAsync()
            select MessageParser.IsEndMarker(msg) ? Option<ProgramInfo>.None : DecodeItemResponse(msg, container, item);
    }

    private static Option<ProgramInfo> DecodeItemResponse(NordMessage msg, int container, int item)
    {
        if (msg.Param2 != NordCommands.ItemResponseData) return Option<ProgramInfo>.None;
        return MessageParser.ParseItemData(msg.Payload)
            .Map(data => new ProgramInfo(
                BankLetter:    ((char)('A' + container)).ToString(),
                Container:     container,
                ItemIndex:     item,
                Name:          data.Name,
                FileType:      data.FileType,
                VersionMajor:  data.VersionMajor,
                VersionMinor:  data.VersionMinor,
                CategoryField: data.CategoryField));
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
    /// Variant for item queries that handles the optional 0x1e echo the device sends
    /// before the actual 0x1f data response. Both reads happen inside the same gate window.
    /// </summary>
    private EitherAsync<NordError, ReadOnlyMemory<byte>> SendAndReceiveItemAsync(
        uint param2, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        var frame = MessageBuilder.Build(NordCommands.CmdQuery, NordCommands.ParamQuery, param2, payload.Span);
        return EitherAsync<NordError, Unit>.Right(unit)
            .BindAsync<ReadOnlyMemory<byte>>(async _ =>
            {
                await gate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    var result = await (
                        from _send in device.SendAsync(frame, ct)
                        from raw   in device.ReceiveAsync(MaxResponseBytes, ct)
                        from final in ResolveEcho(raw, ct)
                        select final
                    ).ToEither().ConfigureAwait(false);
                    return result.ToAsync();
                }
                finally
                {
                    gate.Release();
                }
            });
    }

    private EitherAsync<NordError, ReadOnlyMemory<byte>> ResolveEcho(ReadOnlyMemory<byte> raw, CancellationToken ct)
    {
        // Device may send a 0x1e echo before the actual 0x1f data; if so, consume it.
        if (MessageParser.TryParse(raw, out var peek) && peek.Param2 == NordCommands.QueryPianoDetail)
            return device.ReceiveAsync(MaxResponseBytes, ct);
        return RightAsync<NordError, ReadOnlyMemory<byte>>(Task.FromResult(raw));
    }

    public void Dispose()
    {
        gate.Dispose();
        if (ownsDevice) device.Dispose();
    }
}
