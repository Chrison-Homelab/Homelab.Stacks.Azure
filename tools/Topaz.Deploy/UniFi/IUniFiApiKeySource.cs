namespace Topaz.Deploy.UniFi;

// Source of the UniFi Network integration API key. Same shape as
// ICloudflareTokenSource — the library doesn't care where the key came from
// (env var, prompt, Bitwarden, GitHub Actions secret), only that the caller
// can produce one. UniFi's integration API authenticates with an X-API-KEY
// header rather than a bearer token (see UnifiApiKeyAuthenticationProvider in
// the UnifiSharp reference vendor).
public interface IUniFiApiKeySource
{
    string GetApiKey();
}

public sealed class InlineUniFiApiKeySource : IUniFiApiKeySource
{
    private readonly string _apiKey;

    public InlineUniFiApiKeySource(string apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new ArgumentException("UniFi API key must be a non-empty string.", nameof(apiKey));
        }
        _apiKey = apiKey;
    }

    public string GetApiKey() => _apiKey;
}
