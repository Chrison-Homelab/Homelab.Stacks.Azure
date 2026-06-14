using Topaz.Deploy.Cloudflare;
using Topaz.Deploy.Configuration;
using Microsoft.Extensions.Logging;

namespace Topaz.Deploy.Reconcile.Cloudflare;

// Reconciles a Cloudflare Access self-hosted application + its allow policy for
// one app spec. New for Topaz: the portal is an admin UI with a weak default
// login, so it's gated by Access — never exposed raw through the tunnel.
//
// Same plan pattern as the tunnel reconciler: PlanAsync produces TWO plans (the
// app, then the policy) and a deferred id provider. The app is keyed by its
// `domain`; the policy by its `name` on that app. Both are find-or-create so
// re-runs don't duplicate. Mirrors provision.ps1 step 7 over the same endpoints:
//   GET/POST /accounts/{acct}/access/apps
//   GET/POST/PUT /accounts/{acct}/access/apps/{appId}/policies
public sealed class AccessReconciler
{
    private readonly CloudflareClient _cf;
    private readonly ILogger<AccessReconciler> _log;

    public AccessReconciler(CloudflareClient cf, ILogger<AccessReconciler> log)
    {
        _cf = cf;
        _log = log;
    }

    // Returns the app plan first, then the policy plan. The policy's apply reads
    // the app id at call-time via the deferred provider, so it resolves whether
    // the app was found (id known up-front) or created (id set in the app apply).
    public async Task<IReadOnlyList<ResourcePlan>> PlanAsync(
        string accountId,
        AccessAppSpec spec,
        CancellationToken ct)
    {
        var plans = new List<ResourcePlan>();

        var existingApps = await _cf.ListAccessAppsAsync(accountId, ct);
        var existingApp = existingApps.FirstOrDefault(a =>
            string.Equals(a.Domain, spec.Hostname, StringComparison.OrdinalIgnoreCase));

        // Deferred id provider — reads the live id at the time the policy
        // closures run. Rebound in the app's apply when we create one.
        string? createdAppId = existingApp?.Id;
        var appWillBeCreated = existingApp is null;
        Func<string> appIdProvider = () => createdAppId
            ?? throw new InvalidOperationException("Access app id not resolved — was the app apply run?");

        if (existingApp is null)
        {
            plans.Add(ResourcePlan.Create(
                "AccessApp",
                spec.Hostname,
                new[]
                {
                    FieldChange.Diff("name", null, spec.Name),
                    FieldChange.Diff("domain", null, spec.Hostname),
                    FieldChange.Diff("type", null, "self_hosted"),
                    FieldChange.Diff("session_duration", null, spec.SessionDuration),
                },
                apply: async c =>
                {
                    var app = await _cf.CreateAccessAppAsync(accountId, spec.Name, spec.Hostname, spec.SessionDuration, c);
                    createdAppId = app.Id;
                    _log.LogInformation("Created Access app {Name} for {Domain} ({Id})", spec.Name, spec.Hostname, app.Id);
                },
                note: "self_hosted, app_launcher_visible=true"));
        }
        else
        {
            _log.LogInformation("Access app for {Domain} already exists ({Id})", spec.Hostname, existingApp.Id);
            plans.Add(ResourcePlan.NoOp(
                "AccessApp",
                spec.Hostname,
                new[]
                {
                    FieldChange.Same("id", existingApp.Id),
                    FieldChange.Same("name", existingApp.Name),
                },
                note: "found existing app"));
        }

        // Policy — find-or-create by name on the app. When the app is brand-new
        // there are no existing policies to diff against, so skip the GET.
        var include = spec.AllowEmails
            .Select(e => new CfAccessInclude { Email = new CfAccessEmailRule { Email = e } })
            .ToList();
        var emailSummary = string.Join(", ", spec.AllowEmails);

        CfAccessPolicy? existingPolicy = null;
        if (!appWillBeCreated)
        {
            var policies = await _cf.ListAccessPoliciesAsync(accountId, appIdProvider(), ct);
            existingPolicy = policies.FirstOrDefault(p =>
                string.Equals(p.Name, spec.PolicyName, StringComparison.Ordinal));
        }

        if (existingPolicy is null)
        {
            plans.Add(ResourcePlan.Create(
                "AccessPolicy",
                spec.PolicyName,
                new[]
                {
                    FieldChange.Diff("decision", null, "allow"),
                    FieldChange.Diff("include", null, emailSummary),
                },
                apply: async c =>
                {
                    await _cf.CreateAccessPolicyAsync(accountId, appIdProvider(), spec.PolicyName, include, c);
                    _log.LogInformation("Created Access allow policy {Name} ({Count} email(s))", spec.PolicyName, include.Count);
                },
                note: $"allow {include.Count} email(s)"));
        }
        else
        {
            // Always PUT the policy on a re-run — the include set may have
            // gained/lost emails, and the PUT is idempotent for an unchanged set.
            var beforeEmails = string.Join(", ",
                (existingPolicy.Include ?? new List<CfAccessInclude>())
                    .Select(i => i.Email?.Email)
                    .Where(e => !string.IsNullOrEmpty(e)));
            var policyId = existingPolicy.Id;
            plans.Add(ResourcePlan.Update(
                "AccessPolicy",
                spec.PolicyName,
                new[]
                {
                    FieldChange.Same("decision", "allow"),
                    FieldChange.Diff("include", beforeEmails, emailSummary)
                        with { IsChange = !string.Equals(beforeEmails, emailSummary, StringComparison.Ordinal) },
                },
                apply: async c =>
                {
                    await _cf.UpdateAccessPolicyAsync(accountId, appIdProvider(), policyId, spec.PolicyName, include, c);
                    _log.LogInformation("Updated Access allow policy {Name} ({Count} email(s))", spec.PolicyName, include.Count);
                },
                note: $"allow {include.Count} email(s), id={existingPolicy.Id}"));
        }

        return plans;
    }
}
