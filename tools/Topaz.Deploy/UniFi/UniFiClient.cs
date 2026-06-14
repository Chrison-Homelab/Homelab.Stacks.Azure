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

    // Create a GATEWAY-managed VLAN in firewall zone `zoneId` (required by the
    // gateway). dhcpStart/dhcpStop both non-empty enables a DHCP server with that
    // range; either blank → static-only (DHCP sub-object omitted entirely). Body
    // built here so the wire shape stays internal. Returns the created network so
    // the caller can read its id (needed to add it to the zone's networkIds).
    public async Task<UniFiNetwork> CreateNetworkAsync(
        string siteId,
        string name,
        int vlanId,
        string gateway,
        int prefixLength,
        string? dhcpStart,
        string? dhcpStop,
        string zoneId,
        CancellationToken ct = default)
    {
        var dhcpEnabled = !string.IsNullOrWhiteSpace(dhcpStart) && !string.IsNullOrWhiteSpace(dhcpStop);
        var body = new UniFiNetworkCreateBody
        {
            Name = name,
            VlanId = vlanId,
            ZoneId = zoneId,
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

    // ----- Firewall zones ------------------------------------------------

    public async Task<IReadOnlyList<UniFiZone>> ListFirewallZonesAsync(string siteId, CancellationToken ct = default)
        => await GetPageAsync<UniFiZone>($"v1/sites/{siteId}/firewall/zones", ct);

    // Create a firewall zone. networkIds may be empty (the VLAN gets added via a
    // follow-up PUT once it exists). Both fields are required on the wire.
    public async Task<UniFiZone> CreateFirewallZoneAsync(
        string siteId,
        string name,
        IReadOnlyList<string> networkIds,
        CancellationToken ct = default)
    {
        var body = new UniFiZoneCreateUpdateBody { Name = name, NetworkIds = networkIds.ToList() };
        return await PostAsync<UniFiZoneCreateUpdateBody, UniFiZone>($"v1/sites/{siteId}/firewall/zones", body, ct);
    }

    // Update a firewall zone — the only mutation we do to an existing zone, and
    // only ever our own just-created one (to add the new VLAN to its networkIds).
    public async Task<UniFiZone> UpdateFirewallZoneAsync(
        string siteId,
        string id,
        string name,
        IReadOnlyList<string> networkIds,
        CancellationToken ct = default)
    {
        var body = new UniFiZoneCreateUpdateBody { Name = name, NetworkIds = networkIds.ToList() };
        return await SendAsync<UniFiZoneCreateUpdateBody, UniFiZone>(HttpMethod.Put, $"v1/sites/{siteId}/firewall/zones/{id}", body, ct);
    }

    // ----- Firewall policies ---------------------------------------------

    public async Task<IReadOnlyList<UniFiPolicy>> ListFirewallPoliciesAsync(string siteId, CancellationToken ct = default)
        => await GetPageAsync<UniFiPolicy>($"v1/sites/{siteId}/firewall/policies", ct);

    // Create a zone-to-zone ALLOW policy from src zone to dst zone. Body built
    // here (like the network/DNS creates) so the wire shape stays internal; the
    // src/dst zone ids resolved at apply-time flow straight through. We never send
    // `index` — UniFi assigns it.
    // allowReturnTraffic=true makes UniFi auto-derive a companion (Return) policy
    // (connectionStateFilter RELATED,ESTABLISHED) so the reply path of an allowed
    // connection works. REQUIRED for internal→internal rules (e.g. Mgmt→Azure, or
    // the CT's SSH replies never get back). The API REJECTS true when the
    // destination is the External/WAN zone ("Return traffic can't be allowed") —
    // WAN egress return is handled by the gateway's NAT/conntrack anyway — so
    // egress-to-Internet rules must pass false.
    public async Task<UniFiPolicy> CreateFirewallPolicyAsync(
        string siteId,
        string name,
        string sourceZoneId,
        string destinationZoneId,
        bool allowReturnTraffic,
        CancellationToken ct = default)
    {
        var body = new UniFiPolicyCreateBody
        {
            Name = name,
            Action = new UniFiPolicyAction { Type = "ALLOW", AllowReturnTraffic = allowReturnTraffic },
            Source = new UniFiPolicyEndpoint { ZoneId = sourceZoneId },
            Destination = new UniFiPolicyEndpoint { ZoneId = destinationZoneId },
        };
        return await PostAsync<UniFiPolicyCreateBody, UniFiPolicy>($"v1/sites/{siteId}/firewall/policies", body, ct);
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

    // Fetches ALL pages. The integration API caps a page (~25 by default), so a
    // single GET silently truncates large collections (e.g. 174 firewall
    // policies) — which made find-or-create miss existing items and create
    // duplicates. Loop on offset until we've seen totalCount.
    private async Task<IReadOnlyList<T>> GetPageAsync<T>(string path, CancellationToken ct)
    {
        const int limit = 200;
        var all = new List<T>();
        var offset = 0;
        while (true)
        {
            var sep = path.Contains('?') ? '&' : '?';
            var page = await SendAsync<object, UniFiPage<T>>(
                HttpMethod.Get, $"{path}{sep}limit={limit}&offset={offset}", null, ct);
            var items = page?.Data ?? new List<T>();
            all.AddRange(items);
            offset += items.Count;
            if (items.Count == 0 || all.Count >= (page?.TotalCount ?? all.Count)) break;
        }
        return all;
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
