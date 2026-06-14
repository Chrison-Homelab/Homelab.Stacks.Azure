using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Topaz.Deploy.UniFi;

// Thin typed client over the UniFi Network *integration* API. Covers the site
// discovery + network + Local DNS endpoints the network/DNS reconcilers hit.
//
// Auth: an X-API-KEY header carrying the integration API key (NOT a bearer
// token) — mirrors UnifiApiKeyAuthenticationProvider in the UnifiSharp
// reference vendor. The key comes from an IUniFiApiKeySource so it never gets
// baked into options/config and callers can mock it.
//
// Unlike Cloudflare, the integration API returns bare objects (no
// success/errors envelope); list endpoints wrap items in a pagination object
// we project down to its `data` array.
public sealed class UniFiClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly IUniFiApiKeySource _keySource;
    private readonly ILogger<UniFiClient> _log;

    public UniFiClient(HttpClient http, IUniFiApiKeySource keySource, ILogger<UniFiClient> log)
    {
        _http = http;
        _keySource = keySource;
        _log = log;
        // BaseAddress comes from UNIFI_BASE_URL via the factory; we just make
        // sure relative paths resolve under it by enforcing a trailing slash.
        if (_http.BaseAddress is { } b && !b.AbsoluteUri.EndsWith('/'))
        {
            _http.BaseAddress = new Uri(b.AbsoluteUri + "/");
        }
    }

    // GET /v1/sites — every site visible to the key. Caller picks by name.
    public async Task<IReadOnlyList<UniFiSite>> ListSitesAsync(CancellationToken ct = default)
        => await GetPageAsync<UniFiSite>("v1/sites", ct);

    public async Task<IReadOnlyList<UniFiNetwork>> ListNetworksAsync(string siteId, CancellationToken ct = default)
        => await GetPageAsync<UniFiNetwork>($"v1/sites/{siteId}/networks", ct);

    // Create a GATEWAY-managed VLAN. dhcpStart/dhcpStop both non-empty enables a
    // DHCP server with that range; either blank → static-only (DHCP sub-object
    // omitted entirely). Body built here so the wire shape stays internal.
    public async Task<UniFiNetwork> CreateNetworkAsync(
        string siteId,
        string name,
        int vlanId,
        string gateway,
        int prefixLength,
        string? dhcpStart,
        string? dhcpStop,
        CancellationToken ct = default)
    {
        var dhcpEnabled = !string.IsNullOrWhiteSpace(dhcpStart) && !string.IsNullOrWhiteSpace(dhcpStop);
        var body = new UniFiNetworkCreateBody
        {
            Name = name,
            VlanId = vlanId,
            Ipv4Configuration = new UniFiIpv4Configuration
            {
                HostIpAddress = gateway,
                PrefixLength = prefixLength,
                DhcpConfiguration = dhcpEnabled
                    ? new UniFiDhcpServerConfiguration
                    {
                        IpAddressRange = new UniFiIpAddressRange { Start = dhcpStart!, Stop = dhcpStop! },
                    }
                    : null,
            },
        };
        return await PostAsync<UniFiNetworkCreateBody, UniFiNetwork>($"v1/sites/{siteId}/networks", body, ct);
    }

    public async Task<IReadOnlyList<UniFiDnsPolicy>> ListDnsPoliciesAsync(string siteId, CancellationToken ct = default)
        => await GetPageAsync<UniFiDnsPolicy>($"v1/sites/{siteId}/dns/policies", ct);

    // Create an A_RECORD Local DNS policy. Wildcard domains go on the wire verbatim.
    public async Task<UniFiDnsPolicy> CreateDnsARecordAsync(
        string siteId,
        string domain,
        string ipv4Address,
        int ttlSeconds,
        CancellationToken ct = default)
    {
        var body = new UniFiDnsARecordCreateBody { Domain = domain, Ipv4Address = ipv4Address, TtlSeconds = ttlSeconds };
        return await PostAsync<UniFiDnsARecordCreateBody, UniFiDnsPolicy>($"v1/sites/{siteId}/dns/policies", body, ct);
    }

    // ----- Verb helpers --------------------------------------------------

    private async Task<IReadOnlyList<T>> GetPageAsync<T>(string path, CancellationToken ct)
    {
        var page = await SendAsync<object, UniFiPage<T>>(HttpMethod.Get, path, null, ct);
        return page?.Data ?? new List<T>();
    }

    private Task<TOut> PostAsync<TIn, TOut>(string path, TIn body, CancellationToken ct)
        => SendAsync<TIn, TOut>(HttpMethod.Post, path, body, ct);

    private async Task<TOut> SendAsync<TIn, TOut>(HttpMethod method, string path, TIn? body, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(method, path);
        req.Headers.TryAddWithoutValidation("X-API-KEY", _keySource.GetApiKey());
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (body is not null && method != HttpMethod.Get)
        {
            req.Content = JsonContent.Create(body, options: Json);
        }

        _log.LogDebug("unifi {Method} {Path}", method.Method, path);

        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        var status = (int)resp.StatusCode;
        var raw = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!resp.IsSuccessStatusCode)
        {
            throw new UniFiApiException(method.Method, path, status,
                $"UniFi API {method.Method} {path} failed (HTTP {status}): {Truncate(raw, 300)}");
        }

        if (string.IsNullOrWhiteSpace(raw))
        {
            throw new UniFiApiException(method.Method, path, status, $"empty response body (HTTP {status})");
        }

        try
        {
            return JsonSerializer.Deserialize<TOut>(raw, Json)
                ?? throw new UniFiApiException(method.Method, path, status, "response deserialised to null");
        }
        catch (JsonException jex)
        {
            throw new UniFiApiException(method.Method, path, status,
                $"non-JSON response (HTTP {status}): {Truncate(raw, 200)}", jex);
        }
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
