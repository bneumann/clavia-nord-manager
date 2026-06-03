namespace NordSampleManager.Protocol;

public sealed class NordException : Exception
{
    public NordError Error { get; }
    public NordException(NordError error) : base(error.Message) => Error = error;
}
