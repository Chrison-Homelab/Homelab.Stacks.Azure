using Topaz.Deploy.UniFi;
using Microsoft.Extensions.Logging;

namespace Topaz.Deploy.Reconcile.UniFi;

// Find-or-create the dedicated firewall zone the Azure-lab VLAN lands in. The
// zone starts default-deny; the network reconciler adds the VLAN to its
// networkIds and the policy reconcilers open the minimum allow-paths. ADD-ONLY:
// if a zone with the configured name already exists we emit a NoOp and never
// touch it (it may already carry other networks).
//
// Same plan shape as AccessReconciler: PlanAsync produces one ResourcePlan plus
// a deferred id provider the network/policy closures read at apply-time, so it
// resolves whether the zone was found (id known up-front) or created (id set in
// the zone apply).
public sealed class UniFiZoneReconciler
{
    private readonly UniFiClient _unifi;
    private readonly ILogger<UniFiZoneReconciler> _log;

    public UniFiZoneReconciler(UniFiClient unifi, ILogger<UniFiZoneReconciler> log)
    {
        _unifi = unifi;
        _log = log;
    }

    public async Task<(ResourcePlan Plan, Func<string> ZoneIdProvider)> PlanAsync(
        string siteId,
        string zoneName,
        CancellationToken ct)
    {
        var existing = (await _unifi.ListFirewallZonesAsync(siteId, ct))
            .FirstOrDefault(z => string.Equals(z.Name, zoneName, StringComparison.OrdinalIgnoreCase));

        // Deferred id provider — reads the live id at the time the network/policy
        // closures run. Rebound in the zone's apply when we create one.
        string? zoneId = existing?.Id;
        Func<string> zoneIdProvider = () => zoneId
            ?? throw new InvalidOperationException("Azure zone id not resolved — was the zone apply run?");

        if (existing is not null)
        {
            _log.LogInformation("UniFi firewall zone {Name} already exists ({Id})", zoneName, existing.Id);
            return (ResourcePlan.NoOp(
                "UniFiZone",
                zoneName,
                new[]
                {
                    FieldChange.Same("name", existing.Name),
                    FieldChange.Same("networkIds", existing.NetworkIds is { Count: > 0 } ? string.Join(", ", existing.NetworkIds) : "(none)"),
                },
                note: $"found existing zone id={existing.Id}"), zoneIdProvider);
        }

        var plan = ResourcePlan.Create(
            "UniFiZone",
            zoneName,
            new[]
            {
                FieldChange.Diff("name", null, zoneName),
                FieldChange.Diff("networkIds", null, "(none)"),
            },
            apply: async c =>
            {
                var zone = await _unifi.CreateFirewallZoneAsync(siteId, zoneName, Array.Empty<string>(), c);
                zoneId = zone.Id;
                _log.LogInformation("Created UniFi firewall zone {Name} ({Id})", zoneName, zone.Id);
            },
            note: "default-deny, networkIds added once the VLAN exists");

        return (plan, zoneIdProvider);
    }
}
