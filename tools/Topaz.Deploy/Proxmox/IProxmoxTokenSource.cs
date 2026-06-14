namespace Topaz.Deploy.Proxmox;

// Source of the Proxmox VE API token. Proxmox authenticates with a
// `PVEAPIToken=<tokenId>=<secret>` Authorization header, so a token is a
// (id, secret) pair rather than a single bearer string. The library doesn't
// care where either half came from, only that the caller can produce them.
public interface IProxmoxTokenSource
{
    string GetTokenId();
    string GetTokenSecret();
}

public sealed class InlineProxmoxTokenSource : IProxmoxTokenSource
{
    private readonly string _tokenId;
    private readonly string _tokenSecret;

    public InlineProxmoxTokenSource(string tokenId, string tokenSecret)
    {
        if (string.IsNullOrWhiteSpace(tokenId))
        {
            throw new ArgumentException("Proxmox token id must be a non-empty string.", nameof(tokenId));
        }
        if (string.IsNullOrWhiteSpace(tokenSecret))
        {
            throw new ArgumentException("Proxmox token secret must be a non-empty string.", nameof(tokenSecret));
        }
        _tokenId = tokenId;
        _tokenSecret = tokenSecret;
    }

    public string GetTokenId() => _tokenId;
    public string GetTokenSecret() => _tokenSecret;
}
