using LanguageExt;
using static LanguageExt.Prelude;
using LibUsbDotNet;
using LibUsbDotNet.LibUsb;
using LibUsbDotNet.Main;
using LibUsbError = LibUsbDotNet.Error;

namespace NordSampleManager.Protocol.Transport;

public sealed class LibUsbNordDevice : INordDevice
{
    private const int DefaultTimeoutMs = 5000;
    private const int InterfaceNumber = 0;

    private readonly ushort vid;
    private readonly ushort pid;

    private UsbContext? context;
    private IUsbDevice? device;
    private UsbEndpointWriter? writer;
    private UsbEndpointReader? reader;
    private bool claimedInterface;
    private byte bulkOutAddress;
    private byte bulkInAddress;
    private bool disposed;

    public LibUsbNordDevice(ushort vendorId = NordDeviceIds.VendorId, ushort productId = NordDeviceIds.ProductId)
    {
        vid = vendorId;
        pid = productId;
    }

    public bool IsConnected => device is { IsOpen: true } && writer is not null && reader is not null;
    public ushort VendorId => vid;
    public ushort ProductId => pid;
    public byte BulkOutEndpoint => bulkOutAddress;
    public byte BulkInEndpoint => bulkInAddress;

    public event EventHandler? Disconnected;

    // ConnectAsync is synchronous internally; Task.Run keeps it off the UI thread.
    public EitherAsync<NordError, Unit> ConnectAsync(CancellationToken ct = default) =>
        EitherAsync<NordError, Unit>.Right(unit)
            .BindAsync<Unit>(async _ =>
            {
                var result = await Task.Run(ConnectCore, ct).ConfigureAwait(false);
                return result.ToAsync();
            });

    private Either<NordError, Unit> ConnectCore()
    {
        if (disposed) return new NordError.DeviceNotFound("Device is disposed.");
        if (IsConnected) return unit;

        context = new UsbContext();
        var found = context.Find(new UsbDeviceFinder { Vid = vid, Pid = pid });
        if (found is null)
            return new NordError.DeviceNotFound(
                $"USB device {vid:x4}:{pid:x4} not found. Is the Nord plugged in?");

        found.Open();
        if (found is UsbDevice concrete)
            try { concrete.SetAutoDetachKernelDriver(true); } catch { /* not all platforms */ }

        if (!found.ClaimInterface(InterfaceNumber))
        {
            found.Close();
            return new NordError.InterfaceClaimFailed(
                $"Could not claim interface {InterfaceNumber}. Check udev rules (see README).");
        }
        claimedInterface = true;

        return FindBulkEndpoints(found).Match(
            Right: endpoints =>
            {
                (bulkOutAddress, bulkInAddress) = endpoints;
                writer = found.OpenEndpointWriter((WriteEndpointID)bulkOutAddress, EndpointType.Bulk);
                reader = found.OpenEndpointReader((ReadEndpointID)bulkInAddress, 4096, EndpointType.Bulk);
                device = found;
                return Right<NordError, Unit>(unit);
            },
            Left: err => Left<NordError, Unit>(err));
    }

    public EitherAsync<NordError, Unit> SendAsync(ReadOnlyMemory<byte> frame, CancellationToken ct = default)
    {
        if (!IsConnected)
            return LeftAsync<NordError, Unit>(new NordError.DeviceDisconnected("Not connected."));
        return EitherAsync<NordError, Unit>.Right(unit)
            .BindAsync<Unit>(async _ =>
            {
                var result = await SendCoreAsync(frame, ct).ConfigureAwait(false);
                return result.ToAsync();
            });
    }

    private async Task<Either<NordError, Unit>> SendCoreAsync(ReadOnlyMemory<byte> frame, CancellationToken ct)
    {
        var (err, _) = await writer!.WriteAsync(frame, DefaultTimeoutMs).WaitAsync(ct).ConfigureAwait(false);
        return MapTransferError(err, sending: true)
            .Match(Some: e => Left<NordError, Unit>(e), None: () => Right<NordError, Unit>(unit));
    }

    public EitherAsync<NordError, ReadOnlyMemory<byte>> ReceiveAsync(int maxLength, CancellationToken ct = default)
    {
        if (!IsConnected)
            return LeftAsync<NordError, ReadOnlyMemory<byte>>(new NordError.DeviceDisconnected("Not connected."));
        return EitherAsync<NordError, Unit>.Right(unit)
            .BindAsync<ReadOnlyMemory<byte>>(async _ =>
            {
                var result = await ReceiveCoreAsync(maxLength, ct).ConfigureAwait(false);
                return result.ToAsync();
            });
    }

    private async Task<Either<NordError, ReadOnlyMemory<byte>>> ReceiveCoreAsync(int maxLength, CancellationToken ct)
    {
        var buffer = new byte[maxLength];
        var (err, transferred) = await reader!.ReadAsync(buffer, DefaultTimeoutMs).WaitAsync(ct).ConfigureAwait(false);
        return MapTransferError(err, sending: false)
            .Match(
                Some: e => Left<NordError, ReadOnlyMemory<byte>>(e),
                None: () => Right<NordError, ReadOnlyMemory<byte>>(buffer.AsMemory(0, transferred)));
    }

    private Option<NordError> MapTransferError(LibUsbError err, bool sending)
    {
        if (err == LibUsbError.Success) return None;
        var op = sending ? "send" : "receive";
        if (err is LibUsbError.NoDevice or LibUsbError.Io or LibUsbError.NotFound)
        {
            RaiseDisconnect();
            return Some<NordError>(new NordError.DeviceDisconnected(
                $"Device disconnected during {op} (libusb {err})."));
        }
        if (err == LibUsbError.Timeout)
            return Some<NordError>(new NordError.TransferTimeout(
                $"USB {op} timed out after {DefaultTimeoutMs} ms — command may not be supported yet."));
        return Some<NordError>(new NordError.DeviceDisconnected($"USB {op} failed: {err}."));
    }

    private static Either<NordError, (byte outAddr, byte inAddr)> FindBulkEndpoints(IUsbDevice dev)
    {
        foreach (var cfg in dev.Configs)
            foreach (var intf in cfg.Interfaces)
            {
                byte? outAddr = null, inAddr = null;
                foreach (var ep in intf.Endpoints)
                {
                    if ((ep.Attributes & 0x03) != 0x02) continue;
                    var addr = ep.EndpointAddress;
                    if ((addr & 0x80) != 0) inAddr ??= addr;
                    else outAddr ??= addr;
                }
                if (outAddr.HasValue && inAddr.HasValue)
                    return (outAddr.Value, inAddr.Value);
            }
        return new NordError.NoEndpointsFound();
    }

    private void RaiseDisconnect() => Disconnected?.Invoke(this, EventArgs.Empty);

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        try
        {
            if (device is not null)
            {
                if (claimedInterface)
                    try { device.ReleaseInterface(InterfaceNumber); } catch { /* best effort */ }
                try { device.Close(); } catch { /* best effort */ }
            }
        }
        finally
        {
            context?.Dispose();
            context = null;
            device = null;
            writer = null;
            reader = null;
        }
    }
}
