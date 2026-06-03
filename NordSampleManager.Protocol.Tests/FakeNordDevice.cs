using NordSampleManager.Protocol;
using NordSampleManager.Protocol.Transport;

namespace NordSampleManager.Protocol.Tests;

/// <summary>
/// In-memory transport stub. Enqueue frames with <see cref="EnqueueReply"/> before calling any
/// NordClient method; inspect what was sent via <see cref="SentFrames"/>.
/// ReceiveAsync throws <see cref="NordException"/> when the reply queue is empty
/// (best-effort calls in NordClient silently swallow those, so tests only need to
/// enqueue replies for checked receives).
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

    public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task SendAsync(ReadOnlyMemory<byte> frame, CancellationToken ct = default)
    {
        SentFrames.Add(frame.ToArray());
        return Task.CompletedTask;
    }

    public Task<ReadOnlyMemory<byte>> ReceiveAsync(int maxLength, CancellationToken ct = default)
    {
        if (_replies.TryDequeue(out var reply))
            return Task.FromResult<ReadOnlyMemory<byte>>(reply);
        throw new NordException(new NordError.DeviceDisconnected("FakeNordDevice: no reply queued"));
    }

    public void Dispose() { }
}
