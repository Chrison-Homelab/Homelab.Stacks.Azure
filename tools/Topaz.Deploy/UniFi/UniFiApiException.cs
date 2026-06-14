namespace Topaz.Deploy.UniFi;

public sealed class UniFiApiException : Exception
{
    public string Method { get; }
    public string Path { get; }
    public int? HttpStatus { get; }

    public UniFiApiException(
        string method,
        string path,
        int? httpStatus,
        string message,
        Exception? inner = null)
        : base(message, inner)
    {
        Method = method;
        Path = path;
        HttpStatus = httpStatus;
    }
}
