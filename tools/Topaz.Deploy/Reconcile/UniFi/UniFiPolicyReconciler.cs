using Topaz.Deploy.UniFi;
using Microsoft.Extensions.Logging;

namespace Topaz.Deploy.Reconcile.UniFi;

// Find-or-create the minimum allow-policies that make the Azure zone usable
// while it stays default-deny. ADD-ONLY: a policy with the configured name is
// left as the operator set it (NoOp). Keyed by `name`.
//
//   "Azure → Internet"      Azure → External   (egress: image pulls, cloudflared)
//   "<MgmtZone> → Azure"    MgmtZone → Azure    (ingress: deploy box SSHs the LXC)
//
// Same plan shape as AccessReconciler: one plan per policy, the create apply
// reads the Azure zone id at call-time via the deferred provider so it resolves
// whether the zone was found or created. External / MgmtZone ids are resolved by
// name from the live zone list up-front; a missing named zone fails loudly.
public sealed class UniFiPolicyReconciler
{
    private const string ExternalZoneName = "External";

    private readonly UniFiClient _unifi;
    private readonly ILogger<UniFiPolicyReconciler> _log;

    public UniFiPolicyReconciler(UniFiClient unifi, ILogger<UniFiPolicyReconciler> log)
    {
        _unifi = unifi;
        _log = log;
    }

    public async Task<IReadOnlyList<ResourcePlan>> PlanAsync(
        string siteId,
        string azureZoneName,
        string mgmtZoneName,
        Func<string> azureZoneIdProvider,
        CancellationToken ct)
    {
        var zones = await _unifi.ListFirewallZonesAsync(siteId, ct);
        var externalZone = ResolveZone(zones, ExternalZoneName);
        var mgmtZone = ResolveZone(zones, mgmtZoneName);

        var existingPolicies = await _unifi.ListFirewallPoliciesAsync(siteId, ct);
        var plans = new List<ResourcePlan>(2);

        // Egress: Azure → External (source resolved at apply-time).
        plans.Add(PlanPolicy(
            siteId,
            name: $"{azureZoneName} → Internet",
            existingPolicies,
            srcZoneIdProvider: azureZoneIdProvider,
            srcDescription: azureZoneName,
            dstZoneIdProvider: () => externalZone.Id,
            dstDescription: ExternalZoneName,
            ct));

        // Ingress: MgmtZone → Azure (destination resolved at apply-time).
        plans.Add(PlanPolicy(
            siteId,
            name: $"{mgmtZoneName} → {azureZoneName}",
            existingPolicies,
            srcZoneIdProvider: () => mgmtZone.Id,
            srcDescription: mgmtZoneName,
            dstZoneIdProvider: azureZoneIdProvider,
            dstDescription: azureZoneName,
            ct));

        return plans;
    }

    private ResourcePlan PlanPolicy(
        string siteId,
        string name,
        IReadOnlyList<UniFiPolicy> existingPolicies,
        Func<string> srcZoneIdProvider,
        string srcDescription,
        Func<string> dstZoneIdProvider,
        string dstDescription,
        CancellationToken ct)
    {
        var existing = existingPolicies.FirstOrDefault(p =>
            string.Equals(p.Name, name, StringComparison.Ordinal));

        if (existing is not null)
        {
            _log.LogInformation("UniFi firewall policy {Name} already exists ({Id})", name, existing.Id);
            return ResourcePlan.NoOp(
                "UniFiPolicy",
                name,
                new[]
                {
                    FieldChange.Same("name", existing.Name),
                    FieldChange.Same("action", existing.Action?.Type),
                },
                note: $"found existing policy id={existing.Id}");
        }

        return ResourcePlan.Create(
            "UniFiPolicy",
            name,
            new[]
            {
                FieldChange.Diff("action", null, "ALLOW (allowReturnTraffic=true)"),
                FieldChange.Diff("source", null, srcDescription),
                FieldChange.Diff("destination", null, dstDescription),
                FieldChange.Diff("ipProtocolScope", null, "IPV4_AND_IPV6"),
            },
            apply: async c =>
            {
                var policy = await _unifi.CreateFirewallPolicyAsync(siteId, name, srcZoneIdProvider(), dstZoneIdProvider(), c);
                _log.LogInformation("Created UniFi firewall policy {Name} ({Id})", name, policy.Id);
            },
            note: $"ALLOW {srcDescription} → {dstDescription}");
    }

    private static UniFiZone ResolveZone(IReadOnlyList<UniFiZone> zones, string name)
        => zones.FirstOrDefault(z => string.Equals(z.Name, name, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(
                $"UniFi firewall zone '{name}' not found. Zones seen: {string.Join(", ", zones.Select(z => z.Name))}.");
}
