using LanguageExt;
using NordSampleManager.Protocol;
using NordSampleManager.Protocol.Transport;

namespace NordSampleManager.Services;

public enum ConnectionState { Disconnected, Connecting, Connected, Failed }

public sealed class DeviceService : IDisposable
{
    private LibUsbNordDevice? device;
    private NordClient? client;

    public ConnectionState State { get; private set; } = ConnectionState.Disconnected;
    public NordClient? Client => client;
    public string? LastError { get; private set; }

    public ushort VendorId => device?.VendorId ?? 0;
    public ushort ProductId => device?.ProductId ?? 0;
    public byte BulkOut => device?.BulkOutEndpoint ?? 0;
    public byte BulkIn => device?.BulkInEndpoint ?? 0;

    public event EventHandler? StateChanged;

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        if (State == ConnectionState.Connecting) return;
        SetState(ConnectionState.Connecting);

        var newDevice = new LibUsbNordDevice();
        var newClient = new NordClient(newDevice);

        var result = await newClient.ConnectAsync(ct).ToEither();
        result.Match(
            Right: _ =>
            {
                device = newDevice;
                client = newClient;
                newDevice.Disconnected += OnDeviceDisconnected;
                LastError = null;
                SetState(ConnectionState.Connected);
            },
            Left: err =>
            {
                LastError = err.Message;
                newClient.Dispose();
                newDevice.Dispose();
                SetState(ConnectionState.Failed);
            });
    }

    private void OnDeviceDisconnected(object? sender, EventArgs e)
    {
        LastError = "Device disconnected.";
        Disconnect();
    }

    public void Disconnect()
    {
        if (device is not null) device.Disconnected -= OnDeviceDisconnected;

        // Send session-close handshake before releasing USB. Best-effort: cap at 1 s.
        if (client is not null)
            try { Task.Run(() => client.DisconnectAsync().ToEither()).Wait(TimeSpan.FromSeconds(1)); }
            catch { /* ignored */ }

        client?.Dispose();
        device?.Dispose();
        client = null;
        device = null;
        SetState(ConnectionState.Disconnected);
    }

    private void SetState(ConnectionState state)
    {
        State = state;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose() => Disconnect();
}
