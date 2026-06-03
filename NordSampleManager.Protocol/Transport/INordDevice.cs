namespace NordSampleManager.Protocol.Transport;

public interface INordDevice : IDisposable
{
    bool IsConnected { get; }

    ushort VendorId { get; }
    ushort ProductId { get; }

    byte BulkOutEndpoint { get; }
    byte BulkInEndpoint { get; }

    event EventHandler? Disconnected;

    ValueTask ConnectAsync(CancellationToken ct = default);

    ValueTask SendAsync(ReadOnlyMemory<byte> frame, CancellationToken ct = default);

    ValueTask<ReadOnlyMemory<byte>> ReceiveAsync(int maxLength, CancellationToken ct = default);
}
