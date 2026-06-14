using Topaz.Deploy.Configuration;
using Topaz.Deploy.UniFi;
using Microsoft.Extensions.Logging;

namespace Topaz.Deploy.Reconcile.UniFi;

// Find-or-create the Azure-lab VLAN on the gateway, in the dedicated firewall
// zone. ADD-ONLY: if a network with the configured vlanId already exists we emit
// a NoOp and never touch it — the VLAN is shared, hand-managed infra. Keyed by
// vlanId (the natural key the gateway enforces unique); we surface the name as a
// diff field for context.
//
// Two plans, mirroring AccessReconciler's app-then-policy shape: the network
// create, then a zone-membership update that PUTs our zone with the new network
// in its networkIds. Both apply closures read the zone id at call-time via the
// deferred provider, so they resolve whether the zone was found or just created.
// When the network already exists we emit a single NoOp and skip the membership
// PUT entirely.
public sealed class UniFiNetworkReconciler
{
    private readonly UniFiClient _unifi;
    private readonly ILogger<UniFiNetworkReconciler> _log;

    public UniFiNetworkReconciler(UniFiClient unifi, ILogger<UniFiNetworkReconciler> log)
    {
        _unifi = unifi;
        _log = log;
    }

    public async Task<IReadOnlyList<ResourcePlan>> PlanAsync(
        string siteId,
        NetworkSpec spec,
        string zoneName,
        Func<string> zoneIdProvider,
        CancellationToken ct)
    {
        var identity = $"VLAN {spec.VlanId} ({spec.Name})";

        var existing = (await _unifi.ListNetworksAsync(siteId, ct))
            .FirstOrDefault(n => n.VlanId == spec.VlanId);

        if (existing is not null)
        {
            _log.LogInformation("UniFi network VLAN {VlanId} already exists ({Id}, name={Name})",
                spec.VlanId, existing.Id, existing.Name);
            // ADD-ONLY: existing VLAN is left as-is, and we skip the zone-
            // membership PUT — its zone is the operator's to manage.
            return new[]
            {
                ResourcePlan.NoOp(
                    "UniFiNetwork",
                    identity,
                    new[]
                    {
                        FieldChange.Same("vlanId", spec.VlanId.ToString()),
                        FieldChange.Same("name", existing.Name),
                        FieldChange.Same("management", existing.Management),
                    },
                    note: $"found existing network id={existing.Id}"),
            };
        }

        // DHCP is on by default — the client omits the sub-object only when both
        // range ends are blank, giving a static-only network (per the OpenAPI
        // schema, an absent dhcpConfiguration means hosts address statically).
        var dhcpEnabled = !string.IsNullOrWhiteSpace(spec.DhcpStart) && !string.IsNullOrWhiteSpace(spec.DhcpStop);

        // Deferred network id — set in the create apply, read by the membership
        // PUT. Both run only on a real apply, so it's always resolved in order.
        string? createdNetworkId = null;

        var createPlan = ResourcePlan.Create(
            "UniFiNetwork",
            identity,
            new List<FieldChange>
            {
                FieldChange.Diff("management", null, "GATEWAY"),
                FieldChange.Diff("name", null, spec.Name),
                FieldChange.Diff("vlanId", null, spec.VlanId.ToString()),
                FieldChange.Diff("zone", null, zoneName),
                FieldChange.Diff("gateway", null, $"{spec.Gateway}/{spec.PrefixLength}"),
                FieldChange.Diff("dhcp", null, dhcpEnabled ? $"{spec.DhcpStart}–{spec.DhcpStop}" : "static-only"),
            },
            apply: async c =>
            {
                var net = await _unifi.CreateNetworkAsync(
                    siteId, spec.Name, spec.VlanId, spec.Gateway, spec.PrefixLength,
                    dhcpEnabled ? spec.DhcpStart : null,
                    dhcpEnabled ? spec.DhcpStop : null,
                    zoneIdProvider(),
                    c);
                createdNetworkId = net.Id;
                _log.LogInformation("Created UniFi network {Name} VLAN {VlanId} ({Id})", spec.Name, spec.VlanId, net.Id);
            },
            note: dhcpEnabled ? "GATEWAY-managed, DHCP server enabled" : "GATEWAY-managed, static-only");

        // Zone-membership: PUT our zone with the new network in its networkIds so
        // membership stays consistent. We re-read the zone at apply-time to keep
        // any networkIds it already carries (we only add, never replace).
        var membershipPlan = ResourcePlan.Update(
            "UniFiZone",
            $"{zoneName} membership",
            new[]
            {
                FieldChange.Diff("networkIds", "(none)", $"+ VLAN {spec.VlanId} ({spec.Name})"),
            },
            apply: async c =>
            {
                var zoneId = zoneIdProvider();
                var netId = createdNetworkId
                    ?? throw new InvalidOperationException("network id not resolved — was the network apply run?");
                var zone = (await _unifi.ListFirewallZonesAsync(siteId, c))
                    .FirstOrDefault(z => string.Equals(z.Id, zoneId, StringComparison.Ordinal));
                var networkIds = new List<string>(zone?.NetworkIds ?? new List<string>());
                if (!networkIds.Contains(netId, StringComparer.Ordinal)) networkIds.Add(netId);
                await _unifi.UpdateFirewallZoneAsync(siteId, zoneId, zoneName, networkIds, c);
                _log.LogInformation("Added network {NetId} to firewall zone {Name} ({ZoneId})", netId, zoneName, zoneId);
            },
            note: "add the new VLAN to the Azure zone");

        return new[] { createPlan, membershipPlan };
    }
}
