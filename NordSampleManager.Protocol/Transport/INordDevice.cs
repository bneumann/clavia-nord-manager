using LanguageExt;

namespace NordSampleManager.Protocol.Transport;

public interface INordDevice : IDisposable
{
    bool IsConnected { get; }

    ushort VendorId { get; }
    ushort ProductId { get; }

    byte BulkOutEndpoint { get; }
    byte BulkInEndpoint { get; }

    event EventHandler? Disconnected;

    EitherAsync<NordError, Unit> ConnectAsync(CancellationToken ct = default);

    EitherAsync<NordError, Unit> SendAsync(ReadOnlyMemory<byte> frame, CancellationToken ct = default);

    EitherAsync<NordError, ReadOnlyMemory<byte>> ReceiveAsync(int maxLength, CancellationToken ct = default);
}
