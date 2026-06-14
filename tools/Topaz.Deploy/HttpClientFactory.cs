using System.Net.Http;
using System.Net.Security;

namespace Topaz.Deploy;

// Builds an HttpClient bound to a base URL, optionally skipping TLS cert
// validation. Both the UniFi gateway and Proxmox commonly present self-signed
// certs on the LAN, so the *_VERIFY_TLS env flags ("false" → skip) flow through
// here. We only ever bypass validation when explicitly told to.
internal static class HttpClientFactory
{
    public static HttpClient Create(string baseUrl, bool verifyTls)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            throw new ArgumentException("Base URL must be a non-empty string.", nameof(baseUrl));
        }

        var handler = new HttpClientHandler();
        if (!verifyTls)
        {
            handler.ServerCertificateCustomValidationCallback =
                (_, _, _, _) => true;
        }

        return new HttpClient(handler) { BaseAddress = new Uri(baseUrl) };
    }
}
