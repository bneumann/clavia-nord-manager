using LibUsbDotNet;
using LibUsbDotNet.LibUsb;
using LibUsbDotNet.Main;

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

    public ValueTask ConnectAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (IsConnected) return ValueTask.CompletedTask;

        context = new UsbContext();
        var found = context.Find(new UsbDeviceFinder { Vid = vid, Pid = pid })
            ?? throw new NordDeviceDisconnectedException(
                $"USB device {vid:x4}:{pid:x4} not found. Is the Nord plugged in?");

        found.Open();

        if (found is UsbDevice concrete)
        {
            try { concrete.SetAutoDetachKernelDriver(true); } catch { /* not all platforms */ }
        }

        if (!found.ClaimInterface(InterfaceNumber))
        {
            found.Close();
            throw new NordDeviceDisconnectedException(
                $"Could not claim interface {InterfaceNumber}. Check udev rules (see README).");
        }
        claimedInterface = true;

        (bulkOutAddress, bulkInAddress) = FindBulkEndpoints(found);

        writer = found.OpenEndpointWriter(
            (WriteEndpointID)bulkOutAddress,
            EndpointType.Bulk);
        reader = found.OpenEndpointReader(
            (ReadEndpointID)bulkInAddress,
            readBufferSize: 4096,
            EndpointType.Bulk);

        device = found;
        return ValueTask.CompletedTask;
    }

    public async ValueTask SendAsync(ReadOnlyMemory<byte> frame, CancellationToken ct = default)
    {
        EnsureConnected();
        var (err, _) = await writer!.WriteAsync(frame, DefaultTimeoutMs).WaitAsync(ct).ConfigureAwait(false);
        ThrowIfTransferFailed(err, sending: true);
    }

    public async ValueTask<ReadOnlyMemory<byte>> ReceiveAsync(int maxLength, CancellationToken ct = default)
    {
        EnsureConnected();
        var buffer = new byte[maxLength];
        var (err, transferred) = await reader!.ReadAsync(buffer, DefaultTimeoutMs).WaitAsync(ct).ConfigureAwait(false);
        ThrowIfTransferFailed(err, sending: false);
        return buffer.AsMemory(0, transferred);
    }

    private void ThrowIfTransferFailed(Error err, bool sending)
    {
        if (err == Error.Success) return;
        if (err is Error.NoDevice or Error.Io or Error.NotFound)
        {
            RaiseDisconnect();
            throw new NordDeviceDisconnectedException(
                $"Device disconnected during {(sending ? "send" : "receive")} (libusb {err}).");
        }
        throw new InvalidOperationException(
            $"libusb transfer failed during {(sending ? "send" : "receive")}: {err}");
    }

    private void EnsureConnected()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!IsConnected)
            throw new InvalidOperationException("Device not connected. Call ConnectAsync first.");
    }

    private void RaiseDisconnect() => Disconnected?.Invoke(this, EventArgs.Empty);

    private static (byte outAddr, byte inAddr) FindBulkEndpoints(IUsbDevice dev)
    {
        // Mirror nord_api.py: scan interface 0's endpoints for the bulk OUT and bulk IN pair.
        // Expected on the Nord Stage 3: 0x03 OUT, 0x82 IN.
        foreach (var cfg in dev.Configs)
        {
            foreach (var intf in cfg.Interfaces)
            {
                byte? outAddr = null;
                byte? inAddr = null;
                foreach (var ep in intf.Endpoints)
                {
                    // Attributes low 2 bits: 0=control, 1=iso, 2=bulk, 3=interrupt.
                    var isBulk = (ep.Attributes & 0x03) == 0x02;
                    if (!isBulk) continue;
                    var addr = ep.EndpointAddress;
                    var isIn = (addr & 0x80) != 0;
                    if (isIn) inAddr ??= addr;
                    else outAddr ??= addr;
                }
                if (outAddr.HasValue && inAddr.HasValue)
                    return (outAddr.Value, inAddr.Value);
            }
        }
        throw new NordDeviceDisconnectedException("No bulk IN/OUT endpoint pair found on device.");
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        try
        {
            if (device is not null)
            {
                if (claimedInterface)
                {
                    try { device.ReleaseInterface(InterfaceNumber); } catch { /* best effort */ }
                }
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
