using System.Buffers.Binary;
using NordSampleManager.Protocol.Commands;
using NordSampleManager.Protocol.Framing;
using NordSampleManager.Protocol.Records;
using NordSampleManager.Protocol.Transport;

namespace NordSampleManager.Protocol;

/// <summary>
/// High-level façade over <see cref="INordDevice"/>. One method per known query;
/// device access is serialized internally because the Nord protocol pairs every
/// host send with exactly one device reply.
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

    public async ValueTask ConnectAsync(CancellationToken ct = default)
    {
        await device.ConnectAsync(ct).ConfigureAwait(false);

        // CMD_INIT handshake. Response semantics are opaque (see CLAUDE.md / parsed_protocol.txt Msg 0+1).
        // We log it and proceed — the working read queries don't depend on parsing the reply.
        var initFrame = MessageBuilder.Build(NordCommands.CmdInit, param1: 0, param2: 2, payload: []);
        await device.SendAsync(initFrame, ct).ConfigureAwait(false);
        var reply = await device.ReceiveAsync(MaxResponseBytes, ct).ConfigureAwait(false);
        RawInitResponse = reply.ToArray();
    }

    // ------------------------------------------------------------
    // Confirmed-working list queries (Param2 = 0x02, varying payload).
    // ------------------------------------------------------------

    public ValueTask<IReadOnlyList<string>> QueryPianoCategoriesAsync(CancellationToken ct = default)
        => ListQueryAsync(NordCommands.ListPianoCategories, ct);

    public ValueTask<IReadOnlyList<string>> QueryBank1Async(CancellationToken ct = default)
        => ListQueryAsync(NordCommands.ListBank1, ct);

    public ValueTask<IReadOnlyList<string>> QueryBank2Async(CancellationToken ct = default)
        => ListQueryAsync(NordCommands.ListBank2, ct);

    public ValueTask<IReadOnlyList<string>> QueryBank3Async(CancellationToken ct = default)
        => ListQueryAsync(NordCommands.ListBank3, ct);

    public ValueTask<IReadOnlyList<string>> QueryBank4Async(CancellationToken ct = default)
        => ListQueryAsync(NordCommands.ListBank4, ct);

    public ValueTask<IReadOnlyList<string>> QuerySampLibAsync(CancellationToken ct = default)
        => ListQueryAsync(NordCommands.ListSampLib, ct);

    public ValueTask<IReadOnlyList<string>> QueryBanksAtoPAsync(CancellationToken ct = default)
        => ListQueryAsync(NordCommands.ListBanksAtoP, ct);

    public ValueTask<IReadOnlyList<string>> QueryBanks1to8V1Async(CancellationToken ct = default)
        => ListQueryAsync(NordCommands.ListBanks1to8V1, ct);

    public ValueTask<IReadOnlyList<string>> QueryBanks1to8V2Async(CancellationToken ct = default)
        => ListQueryAsync(NordCommands.ListBanks1to8V2, ct);

    private async ValueTask<IReadOnlyList<string>> ListQueryAsync(uint payloadSelector, CancellationToken ct)
    {
        var payload = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(payload, payloadSelector);
        var reply = await SendAndReceiveAsync(NordCommands.CmdQuery, NordCommands.ParamQuery, NordCommands.QueryListBanks, payload, ct).ConfigureAwait(false);
        if (!MessageParser.TryParse(reply, out var msg))
            return Array.Empty<string>();
        return MessageParser.ScanLengthPrefixedStrings(msg.Payload);
    }

    // ------------------------------------------------------------
    // Best-effort queries — protocol shape known, response parsing partial.
    // Methods return raw bytes so the UI can show "we got a reply" while RE continues.
    // ------------------------------------------------------------

    public async ValueTask<NordMessage?> QueryPianoDetailAsync(int categoryIndex, int location, CancellationToken ct = default)
    {
        var payload = new byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(0, 4), (uint)categoryIndex);
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(4, 4), (uint)location);
        var reply = await SendAndReceiveAsync(NordCommands.CmdQuery, NordCommands.ParamQuery, NordCommands.QueryPianoDetail, payload, ct).ConfigureAwait(false);
        return MessageParser.TryParse(reply, out var msg) ? msg : null;
    }

    public async ValueTask<NordMessage?> QueryProgramAsync(int programNumber, CancellationToken ct = default)
    {
        var payload = new byte[8];
        // Payload from README "## Program": 00000000 (= query program) then the program number.
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(0, 4), 0u);
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(4, 4), (uint)programNumber);
        var reply = await SendAndReceiveAsync(NordCommands.CmdQuery, NordCommands.ParamQuery, NordCommands.QueryProgramOrSong, payload, ct).ConfigureAwait(false);
        return MessageParser.TryParse(reply, out var msg) ? msg : null;
    }

    public async ValueTask<NordMessage?> QuerySongAsync(int songNumber, CancellationToken ct = default)
    {
        var payload = new byte[8];
        // Payload from README "## Songs": 00000001 (= query song) then the song number.
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(0, 4), 1u);
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(4, 4), (uint)songNumber);
        var reply = await SendAndReceiveAsync(NordCommands.CmdQuery, NordCommands.ParamQuery, NordCommands.QueryProgramOrSong, payload, ct).ConfigureAwait(false);
        return MessageParser.TryParse(reply, out var msg) ? msg : null;
    }

    // ------------------------------------------------------------

    private async ValueTask<ReadOnlyMemory<byte>> SendAndReceiveAsync(
        uint cmd, uint param1, uint param2, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var frame = MessageBuilder.Build(cmd, param1, param2, payload.Span);
            await device.SendAsync(frame, ct).ConfigureAwait(false);
            return await device.ReceiveAsync(MaxResponseBytes, ct).ConfigureAwait(false);
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
