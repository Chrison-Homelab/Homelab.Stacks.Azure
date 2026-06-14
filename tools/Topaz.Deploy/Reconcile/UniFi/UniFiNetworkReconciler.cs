using Topaz.Deploy.Configuration;
using Topaz.Deploy.UniFi;
using Microsoft.Extensions.Logging;

namespace Topaz.Deploy.Reconcile.UniFi;

// Find-or-create the Azure-lab VLAN on the gateway. ADD-ONLY: if a network
// with the configured vlanId already exists we emit a NoOp and never touch it —
// the VLAN is shared, hand-managed infra. Keyed by vlanId (the natural key the
// gateway enforces unique); we surface the name as a diff field for context.
//
// Same plan shape as DnsRecordReconciler: PlanAsync produces one ResourcePlan
// whose Apply closure performs the POST only when not in dry-run.
public sealed class UniFiNetworkReconciler
{
    private readonly UniFiClient _unifi;
    private readonly ILogger<UniFiNetworkReconciler> _log;

    public UniFiNetworkReconciler(UniFiClient unifi, ILogger<UniFiNetworkReconciler> log)
    {
        _unifi = unifi;
        _log = log;
    }

    public async Task<ResourcePlan> PlanAsync(string siteId, NetworkSpec spec, CancellationToken ct)
    {
        var identity = $"VLAN {spec.VlanId} ({spec.Name})";

        var existing = (await _unifi.ListNetworksAsync(siteId, ct))
            .FirstOrDefault(n => n.VlanId == spec.VlanId);

        if (existing is not null)
        {
            _log.LogInformation("UniFi network VLAN {VlanId} already exists ({Id}, name={Name})",
                spec.VlanId, existing.Id, existing.Name);
            return ResourcePlan.NoOp(
                "UniFiNetwork",
                identity,
                new[]
                {
                    FieldChange.Same("vlanId", spec.VlanId.ToString()),
                    FieldChange.Same("name", existing.Name),
                    FieldChange.Same("management", existing.Management),
                },
                note: $"found existing network id={existing.Id}");
        }

        // DHCP is on by default — the client omits the sub-object only when both
        // range ends are blank, giving a static-only network (per the OpenAPI
        // schema, an absent dhcpConfiguration means hosts address statically).
        var dhcpEnabled = !string.IsNullOrWhiteSpace(spec.DhcpStart) && !string.IsNullOrWhiteSpace(spec.DhcpStop);

        var changes = new List<FieldChange>
        {
            FieldChange.Diff("management", null, "GATEWAY"),
            FieldChange.Diff("name", null, spec.Name),
            FieldChange.Diff("vlanId", null, spec.VlanId.ToString()),
            FieldChange.Diff("gateway", null, $"{spec.Gateway}/{spec.PrefixLength}"),
            FieldChange.Diff("dhcp", null, dhcpEnabled ? $"{spec.DhcpStart}–{spec.DhcpStop}" : "static-only"),
        };

        return ResourcePlan.Create(
            "UniFiNetwork",
            identity,
            changes,
            apply: async c =>
            {
                var net = await _unifi.CreateNetworkAsync(
                    siteId, spec.Name, spec.VlanId, spec.Gateway, spec.PrefixLength,
                    dhcpEnabled ? spec.DhcpStart : null,
                    dhcpEnabled ? spec.DhcpStop : null,
                    c);
                _log.LogInformation("Created UniFi network {Name} VLAN {VlanId} ({Id})", spec.Name, spec.VlanId, net.Id);
            },
            note: dhcpEnabled ? "GATEWAY-managed, DHCP server enabled" : "GATEWAY-managed, static-only");
    }
}
