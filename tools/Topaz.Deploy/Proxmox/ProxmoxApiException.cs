namespace Topaz.Deploy.Proxmox;

public sealed class ProxmoxApiException : Exception
{
    public string Method { get; }
    public string Path { get; }
    public int? HttpStatus { get; }

    public ProxmoxApiException(
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
