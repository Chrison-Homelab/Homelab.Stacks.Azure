using Topaz.Deploy.Configuration;
using Topaz.Deploy.Proxmox;
using Topaz.Deploy.Reconcile;
using Topaz.Deploy.Reconcile.Proxmox;
using Topaz.Deploy.Ssh;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Spectre.Console;

namespace Topaz.Deploy;

public sealed record HostProvisionRequest(
    DeployOptions Options,
    bool DryRun,
    OutputFormat Output);

public sealed record HostProvisionResult(
    int ExitCode,
    IReadOnlyList<ResourcePlan> Plans);

// Reconciles the Proxmox LXC + Docker install for the Topaz stack. Mirrors
// Provisioner's shape: pure logic over a ProxmoxClient + reconciler, emits to
// AnsiConsole for the human path, honours dry-run. ADD-ONLY (find-or-create CT).
public sealed class HostProvisioner
{
    private readonly ProxmoxLxcReconciler _lxc;
    private readonly ILogger<HostProvisioner> _log;

    public HostProvisioner(
        ProxmoxLxcReconciler lxc,
        ILogger<HostProvisioner>? log = null)
    {
        _lxc = lxc;
        _log = log ?? NullLogger<HostProvisioner>.Instance;
    }

    // Convenience constructor for the Fallout build target. baseUrl is
    // PROXMOX_BASE_URL; verifyTls=false skips cert validation (self-signed PVE).
    public static HostProvisioner Create(
        string tokenId,
        string tokenSecret,
        string baseUrl,
        bool verifyTls,
        ILoggerFactory? loggerFactory = null)
    {
        loggerFactory ??= NullLoggerFactory.Instance;
        var http = HttpClientFactory.Create(baseUrl, verifyTls);
        var tokens = new InlineProxmoxTokenSource(tokenId, tokenSecret);
        var pve = new ProxmoxClient(http, tokens, loggerFactory.CreateLogger<ProxmoxClient>());
        var reconciler = new ProxmoxLxcReconciler(
            pve,
            new SshConnectionResolver(loggerFactory.CreateLogger<SshConnectionResolver>()),
            loggerFactory,
            loggerFactory.CreateLogger<ProxmoxLxcReconciler>());
        return new HostProvisioner(reconciler, loggerFactory.CreateLogger<HostProvisioner>());
    }

    public async Task<HostProvisionResult> RunAsync(HostProvisionRequest req, CancellationToken ct = default)
    {
        var human = req.Output == OutputFormat.Text;

        if (human) AnsiConsole.MarkupLine("[cyan]→[/] reconciling Proxmox LXC + Docker");
        // net0 tags into the same VLAN ProvisionNetwork created — sourced from
        // UniFi.Network.VlanId so the host lands on the lab network.
        var plans = (await _lxc.PlanAsync(req.Options.Lxc, req.Options.UniFi.Network.VlanId, ct)).ToList();

        if (human) PlanRenderer.RenderHuman(plans);

        if (req.DryRun)
        {
            if (!human) AnsiConsole.WriteLine(PlanRenderer.RenderJson(plans, new { dry_run = true }));
            return new HostProvisionResult(0, plans);
        }

        foreach (var plan in plans)
        {
            if (plan.Apply is null) continue;
            if (human) AnsiConsole.MarkupLineInterpolated($"[green]→ applying[/] {plan.Resource} {plan.Identity} ({plan.Action})");
            await plan.Apply(ct);
        }

        if (human) AnsiConsole.MarkupLine("[green]✓[/] host provision complete.");
        else AnsiConsole.WriteLine(PlanRenderer.RenderJson(plans));
        return new HostProvisionResult(0, plans);
    }
}
