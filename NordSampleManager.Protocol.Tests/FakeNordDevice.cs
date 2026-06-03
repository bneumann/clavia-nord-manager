using LanguageExt;
using static LanguageExt.Prelude;
using NordSampleManager.Protocol.Transport;

namespace NordSampleManager.Protocol.Tests;

/// <summary>
/// In-memory transport stub. Enqueue frames with <see cref="EnqueueReply"/> before calling any
/// NordClient method; record what was sent via <see cref="SentFrames"/>.
/// ReceiveAsync returns Left when the reply queue is empty (best-effort calls in NordClient
/// silently swallow those errors, so tests only need to enqueue replies for checked receives).
/// </summary>
internal sealed class FakeNordDevice : INordDevice
{
    private readonly Queue<byte[]> _replies = new();

    public List<byte[]> SentFrames { get; } = [];
    public bool IsConnected => true;
    public ushort VendorId => 0x0ffc;
    public ushort ProductId => 0x0026;
    public byte BulkOutEndpoint => 0x03;
    public byte BulkInEndpoint => 0x82;
    public event EventHandler? Disconnected { add { } remove { } }

    public void EnqueueReply(byte[] frame) => _replies.Enqueue(frame);

    public EitherAsync<NordError, Unit> ConnectAsync(CancellationToken ct = default) =>
        EitherAsync<NordError, Unit>.Right(unit);

    public EitherAsync<NordError, Unit> SendAsync(ReadOnlyMemory<byte> frame, CancellationToken ct = default)
    {
        SentFrames.Add(frame.ToArray());
        return EitherAsync<NordError, Unit>.Right(unit);
    }

    public EitherAsync<NordError, ReadOnlyMemory<byte>> ReceiveAsync(int maxLength, CancellationToken ct = default)
    {
        if (_replies.TryDequeue(out var reply))
            return EitherAsync<NordError, ReadOnlyMemory<byte>>.Right(reply);
        return EitherAsync<NordError, ReadOnlyMemory<byte>>.Left(
            new NordError.DeviceDisconnected("FakeNordDevice: no reply queued"));
    }

    public void Dispose() { }
}
