using Topaz.Deploy.Cloudflare;
using Topaz.Deploy.Configuration;
using Topaz.Deploy.Reconcile;
using Topaz.Deploy.Reconcile.Cloudflare;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Spectre.Console;

namespace Topaz.Deploy;

public sealed record ProvisionRequest(
    DeployOptions Options,
    bool DryRun,
    OutputFormat Output);

public enum OutputFormat
{
    Text,
    Json,
}

public sealed record TunnelOutput(string Name, string Id, string ConnectorToken);

public sealed record ProvisionResult(
    int ExitCode,
    IReadOnlyList<ResourcePlan> Plans,
    IReadOnlyList<TunnelOutput> Tunnels);

// Mirrors what provision.ps1 did, minus the shell. Pure logic over a
// CloudflareClient + reconcilers; emits to AnsiConsole for the human path and
// returns the result for the caller to optionally serialize.
public sealed class Provisioner
{
    private readonly CloudflareClient _cf;
    private readonly TunnelReconciler _tunnels;
    private readonly DnsRecordReconciler _dns;
    private readonly IngressReconciler _ingress;
    private readonly AccessReconciler _access;
    private readonly ILogger<Provisioner> _log;

    public Provisioner(
        CloudflareClient cf,
        TunnelReconciler tunnels,
        DnsRecordReconciler dns,
        IngressReconciler ingress,
        AccessReconciler access,
        ILogger<Provisioner>? log = null)
    {
        _cf = cf;
        _tunnels = tunnels;
        _dns = dns;
        _ingress = ingress;
        _access = access;
        _log = log ?? NullLogger<Provisioner>.Instance;
    }

    // Convenience constructor for the Fallout build target — wires up its own
    // graph from token strings and a DeployOptions POCO.
    //
    // Two token slots: `cloudflareApiToken` drives the tunnel/ingress/DNS
    // reconcile, `accessApiToken` drives the Cloudflare Access reconcile. They may
    // be the same value today (injected twice) and split into separately-scoped,
    // dedicated tokens later — the reconcilers don't care which they're handed.
    public static Provisioner Create(string cloudflareApiToken, string accessApiToken, ILoggerFactory? loggerFactory = null)
    {
        loggerFactory ??= NullLoggerFactory.Instance;
        var http = new HttpClient();
        var cf = new CloudflareClient(http, new InlineCloudflareTokenSource(cloudflareApiToken), loggerFactory.CreateLogger<CloudflareClient>());
        var cfAccess = new CloudflareClient(http, new InlineCloudflareTokenSource(accessApiToken), loggerFactory.CreateLogger<CloudflareClient>());
        return new Provisioner(
            cf,
            new TunnelReconciler(cf, loggerFactory.CreateLogger<TunnelReconciler>()),
            new DnsRecordReconciler(cf, loggerFactory.CreateLogger<DnsRecordReconciler>()),
            new IngressReconciler(cf, loggerFactory.CreateLogger<IngressReconciler>()),
            new AccessReconciler(cfAccess, loggerFactory.CreateLogger<AccessReconciler>()),
            loggerFactory.CreateLogger<Provisioner>());
    }

    public async Task<ProvisionResult> RunAsync(ProvisionRequest req, CancellationToken ct = default)
    {
        var deploy = req.Options;
        var human = req.Output == OutputFormat.Text;
        if (deploy.Cloudflare.Tunnels.Count == 0)
        {
            AnsiConsole.MarkupLine("[red]Cloudflare.Tunnels is empty — nothing to provision.[/]");
            return new ProvisionResult(2, Array.Empty<ResourcePlan>(), Array.Empty<TunnelOutput>());
        }

        // 1. Discover account + zone.
        if (human) AnsiConsole.MarkupLine("[cyan]→[/] discovering account + zone");
        var accounts = await _cf.ListAccountsAsync(ct);
        if (accounts.Count == 0)
        {
            AnsiConsole.MarkupLine("[red]Cloudflare token has access to zero accounts. Check token scopes.[/]");
            return new ProvisionResult(2, Array.Empty<ResourcePlan>(), Array.Empty<TunnelOutput>());
        }
        if (accounts.Count > 1 && human)
        {
            AnsiConsole.MarkupLineInterpolated($"[yellow]warning:[/] token sees {accounts.Count} accounts; using first ({accounts[0].Name}).");
        }
        var accountId = accounts[0].Id;

        var zones = await _cf.FindZoneByNameAsync(deploy.Cloudflare.Zone, ct);
        if (zones.Count == 0)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]zone '{deploy.Cloudflare.Zone}' not found.[/]");
            return new ProvisionResult(2, Array.Empty<ResourcePlan>(), Array.Empty<TunnelOutput>());
        }
        var zoneId = zones[0].Id;
        _log.LogDebug("account={AccountId} zone={ZoneId}", accountId, zoneId);

        // 2. Build plan list.
        var plans = new List<ResourcePlan>();
        var tunnelResults = new List<TunnelReconcileResult>();
        foreach (var spec in deploy.Cloudflare.Tunnels)
        {
            var tr = await _tunnels.PlanAsync(accountId, spec.Name, ct);
            var trIndex = tunnelResults.Count;
            tunnelResults.Add(tr);
            plans.Add(tr.Plan);

            // Deferred id provider — reads the LIST SLOT (not the captured `tr`)
            // at call time. TunnelReconcileResult is an immutable record, so the
            // post-create refresh below does `tunnelResults[idx] = … with { Id }`,
            // replacing the slot; a closure over `tr` would keep returning the
            // pre-create "<pending>" id (the bug that 405'd the ingress PUT).
            Func<string> idProvider = () => tunnelResults[trIndex].Id;

            plans.Add(await _ingress.PlanAsync(accountId, idProvider, tr.WillCreate, spec, ct));
            foreach (var h in spec.Hostnames)
            {
                plans.Add(await _dns.PlanCnameAsync(zoneId, h.Hostname, idProvider, tr.WillCreate, ct));
            }
        }

        // 2b. Access apps + policies (after ingress/DNS). The portal is gated by
        // Access — find-or-create the self-hosted app + allow policy.
        foreach (var app in deploy.Access.Apps)
        {
            plans.AddRange(await _access.PlanAsync(accountId, app, ct));
        }

        // 3. Render plan.
        if (human)
        {
            PlanRenderer.RenderHuman(plans);
        }

        // 4. Dry-run exits here.
        if (req.DryRun)
        {
            if (!human)
            {
                AnsiConsole.WriteLine(PlanRenderer.RenderJson(plans, new { dry_run = true }));
            }
            return new ProvisionResult(0, plans, Array.Empty<TunnelOutput>());
        }

        // 5. Apply.
        foreach (var plan in plans)
        {
            if (plan.Apply is null) continue;
            if (human)
            {
                AnsiConsole.MarkupLineInterpolated($"[green]→ applying[/] {plan.Resource} {plan.Identity} ({plan.Action})");
            }
            await plan.Apply(ct);

            // Post-tunnel-create: refresh the id so DNS/ingress closures resolve.
            if (plan.Resource == "Tunnel" && plan.Action == PlanAction.Create)
            {
                var refreshed = await _cf.FindTunnelByNameAsync(accountId, plan.Identity, ct);
                if (refreshed.Count == 0)
                {
                    AnsiConsole.MarkupLineInterpolated($"[red]post-create lookup failed for tunnel {plan.Identity}.[/]");
                    return new ProvisionResult(1, plans, Array.Empty<TunnelOutput>());
                }
                var idx = tunnelResults.FindIndex(t => t.Name == plan.Identity);
                if (idx >= 0)
                {
                    tunnelResults[idx] = tunnelResults[idx] with { Id = refreshed[0].Id, WillCreate = false };
                }
            }
        }

        // 6. Pull connector tokens.
        var tokens = new List<TunnelOutput>();
        foreach (var tr in tunnelResults)
        {
            var token = await tr.ConnectorTokenAsync(ct);
            tokens.Add(new TunnelOutput(tr.Name, tr.Id, token));
        }

        if (human)
        {
            AnsiConsole.MarkupLineInterpolated($"[green]✓[/] provision complete ({tokens.Count} tunnel(s)).");
        }
        else
        {
            AnsiConsole.WriteLine(PlanRenderer.RenderJson(plans, new
            {
                tunnels = tokens.Select(t => new { tunnel = t.Name, tunnel_id = t.Id, connector_token = t.ConnectorToken })
            }));
        }
        return new ProvisionResult(0, plans, tokens);
    }
}
