using Topaz.Deploy.UniFi;
using Microsoft.Extensions.Logging;

namespace Topaz.Deploy.Reconcile.UniFi;

// Find-or-create one Local DNS A record per configured domain. ADD-ONLY: if an
// A_RECORD policy already exists for the domain we emit a NoOp and never touch
// it. Keyed by `domain`. Wildcard domains (e.g. *.topaz.local.dev) are sent
// verbatim — the gateway accepts them as wildcard A records.
//
// Mirrors DnsRecordReconciler's plan shape but find-or-create only (no update
// path): the record's target is owned by us, so an existing record is left as
// the operator set it.
public sealed class UniFiDnsReconciler
{
    private const int DesiredTtl = 300;

    private readonly UniFiClient _unifi;
    private readonly ILogger<UniFiDnsReconciler> _log;

    public UniFiDnsReconciler(UniFiClient unifi, ILogger<UniFiDnsReconciler> log)
    {
        _unifi = unifi;
        _log = log;
    }

    // One PlanAsync call covers every domain; we read the policy list once and
    // produce a plan per domain so the render shows each record's status.
    public async Task<IReadOnlyList<ResourcePlan>> PlanAsync(
        string siteId,
        string dnsTarget,
        IReadOnlyList<string> domains,
        CancellationToken ct)
    {
        var existingPolicies = await _unifi.ListDnsPoliciesAsync(siteId, ct);
        var plans = new List<ResourcePlan>(domains.Count);

        foreach (var domain in domains)
        {
            var existing = existingPolicies.FirstOrDefault(p =>
                string.Equals(p.Type, "A_RECORD", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(p.Domain, domain, StringComparison.OrdinalIgnoreCase));

            if (existing is not null)
            {
                _log.LogInformation("UniFi Local DNS A {Domain} already exists ({Id} → {Ip})",
                    domain, existing.Id, existing.Ipv4Address);
                plans.Add(ResourcePlan.NoOp(
                    "UniFiDns",
                    $"A {domain}",
                    new[]
                    {
                        FieldChange.Same("domain", domain),
                        FieldChange.Same("ipv4Address", existing.Ipv4Address),
                    },
                    note: $"found existing id={existing.Id}"));
                continue;
            }

            plans.Add(ResourcePlan.Create(
                "UniFiDns",
                $"A {domain}",
                new[]
                {
                    FieldChange.Diff("domain", null, domain),
                    FieldChange.Diff("ipv4Address", null, dnsTarget),
                    FieldChange.Diff("ttlSeconds", null, DesiredTtl.ToString()),
                },
                apply: async c =>
                {
                    var rec = await _unifi.CreateDnsARecordAsync(siteId, domain, dnsTarget, DesiredTtl, c);
                    _log.LogInformation("Created UniFi Local DNS A {Domain} → {Ip} ({Id})", domain, dnsTarget, rec.Id);
                }));
        }

        return plans;
    }
}
