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
    public Task ConnectAsync(CancellationToken ct = default) =>
        Task.Run(ConnectCore, ct);

    private void ConnectCore()
    {
        if (disposed) throw new NordException(new NordError.DeviceNotFound("Device is disposed."));
        if (IsConnected) return;

        context = new UsbContext();
        var found = context.Find(new UsbDeviceFinder { Vid = vid, Pid = pid });
        if (found is null)
            throw new NordException(new NordError.DeviceNotFound(
                $"USB device {vid:x4}:{pid:x4} not found. Is the Nord plugged in?"));

        found.Open();
        if (found is UsbDevice concrete)
            try { concrete.SetAutoDetachKernelDriver(true); } catch { /* not all platforms */ }

        if (!found.ClaimInterface(InterfaceNumber))
        {
            found.Close();
            throw new NordException(new NordError.InterfaceClaimFailed(
                $"Could not claim interface {InterfaceNumber}. Check udev rules (see README)."));
        }
        claimedInterface = true;

        var (outAddr, inAddr) = FindBulkEndpoints(found);
        bulkOutAddress = outAddr;
        bulkInAddress  = inAddr;
        writer = found.OpenEndpointWriter((WriteEndpointID)outAddr, EndpointType.Bulk);
        reader = found.OpenEndpointReader((ReadEndpointID)inAddr, 4096, EndpointType.Bulk);
        device = found;
    }

    public async Task SendAsync(ReadOnlyMemory<byte> frame, CancellationToken ct = default)
    {
        if (!IsConnected)
            throw new NordException(new NordError.DeviceDisconnected("Not connected."));
        var (err, _) = await writer!.WriteAsync(frame, DefaultTimeoutMs).WaitAsync(ct).ConfigureAwait(false);
        ThrowIfTransferError(err, sending: true);
    }

    public async Task<ReadOnlyMemory<byte>> ReceiveAsync(int maxLength, CancellationToken ct = default)
    {
        if (!IsConnected)
            throw new NordException(new NordError.DeviceDisconnected("Not connected."));
        var buffer = new byte[maxLength];
        var (err, transferred) = await reader!.ReadAsync(buffer, DefaultTimeoutMs).WaitAsync(ct).ConfigureAwait(false);
        ThrowIfTransferError(err, sending: false);
        return buffer.AsMemory(0, transferred);
    }

    private void ThrowIfTransferError(LibUsbError err, bool sending)
    {
        if (err == LibUsbError.Success) return;
        var op = sending ? "send" : "receive";
        if (err is LibUsbError.NoDevice or LibUsbError.Io or LibUsbError.NotFound)
        {
            RaiseDisconnect();
            throw new NordException(new NordError.DeviceDisconnected(
                $"Device disconnected during {op} (libusb {err})."));
        }
        if (err == LibUsbError.Timeout)
            throw new NordException(new NordError.TransferTimeout(
                $"USB {op} timed out after {DefaultTimeoutMs} ms — command may not be supported yet."));
        throw new NordException(new NordError.DeviceDisconnected($"USB {op} failed: {err}."));
    }

    private static (byte outAddr, byte inAddr) FindBulkEndpoints(IUsbDevice dev)
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
        throw new NordException(new NordError.NoEndpointsFound());
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
