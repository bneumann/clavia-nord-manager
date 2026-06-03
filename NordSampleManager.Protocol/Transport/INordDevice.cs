namespace NordSampleManager.Protocol.Transport;

public interface INordDevice : IDisposable
{
    bool IsConnected { get; }
    ushort VendorId { get; }
    ushort ProductId { get; }
    byte BulkOutEndpoint { get; }
    byte BulkInEndpoint { get; }

    event EventHandler? Disconnected;

    /// <exception cref="NordException">Thrown when the device cannot be opened or claimed.</exception>
    Task ConnectAsync(CancellationToken ct = default);

    /// <exception cref="NordException">Thrown on USB transfer failure.</exception>
    Task SendAsync(ReadOnlyMemory<byte> frame, CancellationToken ct = default);

    /// <exception cref="NordException">Thrown on USB transfer failure.</exception>
    Task<ReadOnlyMemory<byte>> ReceiveAsync(int maxLength, CancellationToken ct = default);
}
