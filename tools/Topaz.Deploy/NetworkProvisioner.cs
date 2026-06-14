using System.Net.Http;
using Topaz.Deploy.Configuration;
using Topaz.Deploy.Reconcile;
using Topaz.Deploy.Reconcile.UniFi;
using Topaz.Deploy.UniFi;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Spectre.Console;

namespace Topaz.Deploy;

public sealed record NetworkProvisionRequest(
    DeployOptions Options,
    bool DryRun,
    OutputFormat Output);

public sealed record NetworkProvisionResult(
    int ExitCode,
    IReadOnlyList<ResourcePlan> Plans);

// Reconciles the UniFi VLAN + Local DNS for the Topaz stack. Mirrors
// Provisioner's shape: pure logic over a UniFiClient + reconcilers, emits to
// AnsiConsole for the human path, honours dry-run. ADD-ONLY throughout.
public sealed class NetworkProvisioner
{
    private readonly UniFiClient _unifi;
    private readonly UniFiZoneReconciler _zone;
    private readonly UniFiNetworkReconciler _network;
    private readonly UniFiPolicyReconciler _policy;
    private readonly UniFiDnsReconciler _dns;
    private readonly ILogger<NetworkProvisioner> _log;

    public NetworkProvisioner(
        UniFiClient unifi,
        UniFiZoneReconciler zone,
        UniFiNetworkReconciler network,
        UniFiPolicyReconciler policy,
        UniFiDnsReconciler dns,
        ILogger<NetworkProvisioner>? log = null)
    {
        _unifi = unifi;
        _zone = zone;
        _network = network;
        _policy = policy;
        _dns = dns;
        _log = log ?? NullLogger<NetworkProvisioner>.Instance;
    }

    // Convenience constructor for the Fallout build target. baseUrl is
    // UNIFI_BASE_URL; verifyTls=false skips cert validation (self-signed gateway).
    public static NetworkProvisioner Create(
        string apiKey,
        string baseUrl,
        bool verifyTls,
        ILoggerFactory? loggerFactory = null)
    {
        loggerFactory ??= NullLoggerFactory.Instance;
        var http = HttpClientFactory.Create(baseUrl, verifyTls);
        var key = new InlineUniFiApiKeySource(apiKey);
        var unifi = new UniFiClient(http, key, loggerFactory.CreateLogger<UniFiClient>());
        return new NetworkProvisioner(
            unifi,
            new UniFiZoneReconciler(unifi, loggerFactory.CreateLogger<UniFiZoneReconciler>()),
            new UniFiNetworkReconciler(unifi, loggerFactory.CreateLogger<UniFiNetworkReconciler>()),
            new UniFiPolicyReconciler(unifi, loggerFactory.CreateLogger<UniFiPolicyReconciler>()),
            new UniFiDnsReconciler(unifi, loggerFactory.CreateLogger<UniFiDnsReconciler>()),
            loggerFactory.CreateLogger<NetworkProvisioner>());
    }

    public async Task<NetworkProvisionResult> RunAsync(NetworkProvisionRequest req, CancellationToken ct = default)
    {
        var unifi = req.Options.UniFi;
        var human = req.Output == OutputFormat.Text;

        // 1. Discover the site by name.
        if (human) AnsiConsole.MarkupLine("[cyan]→[/] discovering UniFi site");
        var sites = await _unifi.ListSitesAsync(ct);
        if (sites.Count == 0)
        {
            AnsiConsole.MarkupLine("[red]UniFi key has access to zero sites. Check the integration API key.[/]");
            return new NetworkProvisionResult(2, Array.Empty<ResourcePlan>());
        }
        var site = sites.FirstOrDefault(s => string.Equals(s.Name, unifi.Site, StringComparison.OrdinalIgnoreCase));
        if (site is null)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]UniFi site '{unifi.Site}' not found. Sites seen: {string.Join(", ", sites.Select(s => s.Name))}.[/]");
            return new NetworkProvisionResult(2, Array.Empty<ResourcePlan>());
        }
        _log.LogDebug("unifi site={SiteId} ({Name})", site.Id, site.Name);

        // 2. Build plan list. Order matters for the deferred-id chain: the zone
        //    must plan first (its id provider feeds the network + policies), then
        //    the VLAN (+ the zone-membership PUT), then the allow-policies, then
        //    the Local DNS records. Every closure resolves ids at apply-time, so
        //    dry-run renders without any of them firing.
        var (zonePlan, zoneIdProvider) = await _zone.PlanAsync(site.Id, unifi.Network.Zone, ct);
        var plans = new List<ResourcePlan> { zonePlan };
        plans.AddRange(await _network.PlanAsync(site.Id, unifi.Network, unifi.Network.Zone, zoneIdProvider, ct));
        plans.AddRange(await _policy.PlanAsync(site.Id, unifi.Network.Zone, unifi.MgmtZone, zoneIdProvider, ct));
        plans.AddRange(await _dns.PlanAsync(site.Id, unifi.DnsTarget, unifi.DnsDomains, ct));

        // 3. Render.
        if (human) PlanRenderer.RenderHuman(plans);

        // 4. Dry-run exits here.
        if (req.DryRun)
        {
            if (!human) AnsiConsole.WriteLine(PlanRenderer.RenderJson(plans, new { dry_run = true }));
            return new NetworkProvisionResult(0, plans);
        }

        // 5. Apply.
        foreach (var plan in plans)
        {
            if (plan.Apply is null) continue;
            if (human) AnsiConsole.MarkupLineInterpolated($"[green]→ applying[/] {plan.Resource} {plan.Identity} ({plan.Action})");
            await plan.Apply(ct);
        }

        if (human) AnsiConsole.MarkupLine("[green]✓[/] network provision complete.");
        else AnsiConsole.WriteLine(PlanRenderer.RenderJson(plans));
        return new NetworkProvisionResult(0, plans);
    }
}
