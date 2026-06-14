using System.Text.Json.Serialization;

namespace Topaz.Deploy.UniFi;

// Typed models over the UniFi Network *integration* API
// (https://HOST/proxy/network/integration/v1). Field names mirror
// schema/openapi.10.4.57.json in the UnifiSharp reference vendor exactly, so
// the create-bodies serialise to what the gateway expects.
//
// List endpoints wrap their items in a pagination envelope
// { offset, limit, count, totalCount, data: [...] }. We only ever read `data`.

internal sealed class UniFiPage<T>
{
    [JsonPropertyName("offset")] public long Offset { get; set; }
    [JsonPropertyName("limit")] public int Limit { get; set; }
    [JsonPropertyName("count")] public int Count { get; set; }
    [JsonPropertyName("totalCount")] public long TotalCount { get; set; }
    [JsonPropertyName("data")] public List<T>? Data { get; set; }
}

// ----- Sites ---------------------------------------------------------------

public sealed class UniFiSite
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("internalReference")] public string? InternalReference { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
}

// ----- Networks ------------------------------------------------------------
//
// A GET on /networks returns each network with its `management` discriminator,
// id, name and vlanId — enough to find-or-create by vlanId. We don't model the
// full ipv4Configuration on the read side; we only need identity for matching.
public sealed class UniFiNetwork
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("management")] public string? Management { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("vlanId")] public int? VlanId { get; set; }
}

// Create body for a GATEWAY-managed VLAN. Discriminated by `management`.
// Mirrors IntegrationGatewayManagedNetworkCreateUpdateDto + the nested
// Gateway Managed IPv4 (Server) Configuration schemas.
internal sealed class UniFiNetworkCreateBody
{
    [JsonPropertyName("management")] public string Management { get; set; } = "GATEWAY";
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
    [JsonPropertyName("vlanId")] public int VlanId { get; set; }
    [JsonPropertyName("isolationEnabled")] public bool IsolationEnabled { get; set; }
    [JsonPropertyName("cellularBackupEnabled")] public bool CellularBackupEnabled { get; set; }
    [JsonPropertyName("internetAccessEnabled")] public bool InternetAccessEnabled { get; set; } = true;

    // The firewall zone this VLAN lands in. Required by the gateway — a create
    // without it fails api.network.validation.missing-zone-id.
    [JsonPropertyName("zoneId")] public string ZoneId { get; set; } = string.Empty;
    [JsonPropertyName("ipv4Configuration")] public UniFiIpv4Configuration Ipv4Configuration { get; set; } = new();
}

internal sealed class UniFiIpv4Configuration
{
    [JsonPropertyName("autoScaleEnabled")] public bool AutoScaleEnabled { get; set; }
    [JsonPropertyName("hostIpAddress")] public string HostIpAddress { get; set; } = string.Empty;
    [JsonPropertyName("prefixLength")] public int PrefixLength { get; set; }

    // Omitted entirely for a static-only network — the schema says a null/absent
    // dhcpConfiguration means DHCP is off and hosts address statically.
    [JsonPropertyName("dhcpConfiguration")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public UniFiDhcpServerConfiguration? DhcpConfiguration { get; set; }
}

// Discriminated by `mode`; SERVER hands out leases from `ipAddressRange`.
// leaseTimeSeconds + pingConflictDetectionEnabled are required alongside the
// range per the OpenAPI schema, so we set sensible defaults.
internal sealed class UniFiDhcpServerConfiguration
{
    [JsonPropertyName("mode")] public string Mode { get; set; } = "SERVER";
    [JsonPropertyName("ipAddressRange")] public UniFiIpAddressRange IpAddressRange { get; set; } = new();
    [JsonPropertyName("leaseTimeSeconds")] public int LeaseTimeSeconds { get; set; } = 86400;
    [JsonPropertyName("pingConflictDetectionEnabled")] public bool PingConflictDetectionEnabled { get; set; }
}

internal sealed class UniFiIpAddressRange
{
    [JsonPropertyName("start")] public string Start { get; set; } = string.Empty;
    [JsonPropertyName("stop")] public string Stop { get; set; } = string.Empty;
}

// ----- Local DNS policies --------------------------------------------------
//
// The integration API models Local DNS as "DNS policies" discriminated by
// `type`. We only create A records. A GET returns each policy with its id +
// type + domain so we can find-or-create by domain.
public sealed class UniFiDnsPolicy
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("type")] public string? Type { get; set; }
    [JsonPropertyName("enabled")] public bool Enabled { get; set; }
    [JsonPropertyName("domain")] public string? Domain { get; set; }
    [JsonPropertyName("ipv4Address")] public string? Ipv4Address { get; set; }
    [JsonPropertyName("ttlSeconds")] public int? TtlSeconds { get; set; }
}

// Create body for an A record. Discriminated by `type` = A_RECORD. Mirrors
// IntegrationDnsARecordCreateUpdateDto. Wildcard domains (e.g. *.topaz.local.dev)
// go on the wire verbatim.
internal sealed class UniFiDnsARecordCreateBody
{
    [JsonPropertyName("type")] public string Type { get; set; } = "A_RECORD";
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
    [JsonPropertyName("domain")] public string Domain { get; set; } = string.Empty;
    [JsonPropertyName("ipv4Address")] public string Ipv4Address { get; set; } = string.Empty;
    [JsonPropertyName("ttlSeconds")] public int TtlSeconds { get; set; } = 300;
}

// ----- Firewall zones ------------------------------------------------------
//
// Zone-based firewall: every network sits in a zone, and policies allow/deny
// traffic between zones. A network create now needs a zoneId, so we find-or-
// create our own zone and add the VLAN to its networkIds. A GET returns each
// zone with its id + name + the networks bound to it. name + networkIds are
// both required on create/update (networkIds may be empty).
public sealed class UniFiZone
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("networkIds")] public List<string>? NetworkIds { get; set; }
}

internal sealed class UniFiZoneCreateUpdateBody
{
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("networkIds")] public List<string> NetworkIds { get; set; } = new();
}

// ----- Firewall policies ---------------------------------------------------
//
// A zone-to-zone allow/deny rule. We only ever create ALLOW policies (the zone
// starts default-deny). A GET returns each policy with its id + name so we can
// find-or-create by name. UniFi assigns `index`, so we never send it.
public sealed class UniFiPolicy
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("enabled")] public bool Enabled { get; set; }
    [JsonPropertyName("action")] public UniFiPolicyAction? Action { get; set; }
    [JsonPropertyName("source")] public UniFiPolicyEndpoint? Source { get; set; }
    [JsonPropertyName("destination")] public UniFiPolicyEndpoint? Destination { get; set; }
    [JsonPropertyName("ipProtocolScope")] public UniFiPolicyIpProtocolScope? IpProtocolScope { get; set; }
    [JsonPropertyName("loggingEnabled")] public bool LoggingEnabled { get; set; }
}

public sealed class UniFiPolicyAction
{
    [JsonPropertyName("type")] public string Type { get; set; } = "ALLOW";
    [JsonPropertyName("allowReturnTraffic")] public bool AllowReturnTraffic { get; set; } = true;
}

// Source/destination both reference a zone by id. The wire schema is the same
// shape for either end, so we share one record.
public sealed class UniFiPolicyEndpoint
{
    [JsonPropertyName("zoneId")] public string ZoneId { get; set; } = string.Empty;
}

public sealed class UniFiPolicyIpProtocolScope
{
    [JsonPropertyName("ipVersion")] public string IpVersion { get; set; } = "IPV4_AND_IPV6";
}

// Create body for an ALLOW policy. Mirrors UniFiPolicy on the write side minus
// `id`/`index` (UniFi assigns the index). All seven fields below are required.
internal sealed class UniFiPolicyCreateBody
{
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("action")] public UniFiPolicyAction Action { get; set; } = new();
    [JsonPropertyName("source")] public UniFiPolicyEndpoint Source { get; set; } = new();
    [JsonPropertyName("destination")] public UniFiPolicyEndpoint Destination { get; set; } = new();
    [JsonPropertyName("ipProtocolScope")] public UniFiPolicyIpProtocolScope IpProtocolScope { get; set; } = new();
    [JsonPropertyName("loggingEnabled")] public bool LoggingEnabled { get; set; }
}
