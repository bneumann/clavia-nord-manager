namespace NordSampleManager.Protocol.Transport;

public sealed class NordDeviceDisconnectedException : Exception
{
    public NordDeviceDisconnectedException(string message) : base(message) { }
    public NordDeviceDisconnectedException(string message, Exception inner) : base(message, inner) { }
}
