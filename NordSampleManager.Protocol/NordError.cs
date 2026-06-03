namespace NordSampleManager.Protocol;

public abstract record NordError(string Message)
{
    public sealed record DeviceNotFound(string Message)          : NordError(Message);
    public sealed record InterfaceClaimFailed(string Message)    : NordError(Message);
    public sealed record NoEndpointsFound()
        : NordError("No bulk IN/OUT endpoint pair found on device");
    public sealed record TransferTimeout(string Message)         : NordError(Message);
    public sealed record DeviceDisconnected(string Message)      : NordError(Message);
    public sealed record ParseFailed(string Message)             : NordError(Message);
    public sealed record UnexpectedResponse(uint Param2, string Details)
        : NordError($"Unexpected Param2=0x{Param2:x8}: {Details}");
}
