using System.Text.Json;
using Topaz.Deploy;
using Topaz.Deploy.Configuration;
using Fallout.Common;
using Fallout.Common.CI.GitHubActions;
using Fallout.Common.IO;
using Serilog;

// ReSharper disable AllUnderscoreLocalParameterName

/// <summary>
/// Topaz homelab deploy pipeline.
///
/// Local usage:
///   ./build.ps1               (default target — Up)
///   ./build.sh Provision --dry-run
///   ./build.sh Bootstrap --dry-run        (full fresh-hardware path)
///   ./build.cmd Up
///
/// Replaces the old bin/provision.ps1 + deploy.ps1 with a
/// Fallout (NUKE hard-fork) build, so the deploy logic lives in C# and runs
/// identically locally and on CI. There's no app to compile here — this repo is
/// just the compose stack plus its IaC.
///
/// Target graph:
///   Bootstrap → ProvisionHost → ProvisionNetwork   (fresh-hardware infra path)
///   Up        → Provision                          (Cloudflare + compose ship)
/// Bootstrap chains the infra targets and then the Cloudflare Provision + Up so
/// a bare Proxmox host reaches a running stack in one invocation. Provision and
/// Up still run standalone exactly as before; the default target stays Up.
/// </summary>
class Build : FalloutBuild
{
    public static int Main() => Execute<Build>(x => x.Up);

    // -------------------------------------------------------------------------
    // Deploy parameters (consumed by the targets below).
    //
    // [Secret] makes Fallout prompt for the value (masked) when not supplied via
    // env var or command line, and keeps it out of logs. Locally we expect the
    // matching env var set (e.g. CLOUDFLARE_API_TOKEN, UNIFI_API_KEY) before the
    // run; in CI they come from GitHub Actions secrets.
    // -------------------------------------------------------------------------
    [Parameter("Cloudflare API token. Scopes: Account · Cloudflare Tunnel · Edit; Account · Access: Apps and Policies · Edit; Zone · DNS · Edit; Account · Account Settings · Read.")]
    [Secret]
    readonly string CloudflareApiToken = null!;

    // ----- UniFi (ProvisionNetwork) -----
    [Parameter("UniFi Network integration API key (X-API-KEY). Generate in the console: Settings → Control Plane → Integrations.", Name = "UNIFI_API_KEY")]
    [Secret]
    readonly string UnifiApiKey = null!;

    [Parameter("UniFi integration API base URL, e.g. https://HOST/proxy/network/integration.", Name = "UNIFI_BASE_URL")]
    readonly string UnifiBaseUrl = null!;

    [Parameter("Verify the UniFi gateway's TLS cert. Set to false for a self-signed gateway.", Name = "UNIFI_VERIFY_TLS")]
    readonly bool UnifiVerifyTls = true;

    // ----- Proxmox (ProvisionHost) -----
    [Parameter("Proxmox API token secret (the secret half of the PVEAPIToken pair).", Name = "PROXMOX_TOKEN_SECRET")]
    [Secret]
    readonly string ProxmoxTokenSecret = null!;

    [Parameter("Proxmox API token id, e.g. user@pam!tokenname.", Name = "PROXMOX_TOKEN_ID")]
    readonly string ProxmoxTokenId = null!;

    [Parameter("Proxmox API base URL, e.g. https://HOST/api2/json.", Name = "PROXMOX_BASE_URL")]
    readonly string ProxmoxBaseUrl = null!;

    [Parameter("Verify the Proxmox node's TLS cert. Set to false for a self-signed node.", Name = "PROXMOX_VERIFY_TLS")]
    readonly bool ProxmoxVerifyTls = true;

    [Parameter("Plan changes without writing them back to Cloudflare/UniFi/Proxmox (Provision*) or the LXC (Up).")]
    readonly bool DryRun;

    [Parameter("Output format for the provision targets: text (human, default) or json.")]
    readonly string DeployOutput = "text";

    [Parameter("Image tag for the topaz emulator host. Default: v1.6.89-beta.")]
    readonly string TopazVersion = "v1.6.89-beta";

    [Parameter("Image tag for the topaz portal. Default: v1.6.93-beta.")]
    readonly string PortalVersion = "v1.6.93-beta";

    // Populated by Provision when it applies; read by Up. Empty when Provision
    // ran in dry-run, in which case Up uses a placeholder token for its own
    // dry-run output and refuses to proceed if asked to apply for real.
    IReadOnlyList<TunnelOutput> _connectorTokens = Array.Empty<TunnelOutput>();

    GitHubActions GitHubActions => GitHubActions.Instance;

    // Shared helpers ----------------------------------------------------------

    AbsolutePath ConfigPath => RootDirectory / "azure-deploy.json";

    // Every target loads the same azure-deploy.json. Centralised so the
    // not-found / parse-failure handling lives in one place.
    DeployOptions LoadOptions()
    {
        if (!ConfigPath.FileExists())
        {
            throw new InvalidOperationException($"Deploy config not found at {ConfigPath}");
        }
        return JsonSerializer.Deserialize<DeployOptions>(
                   ConfigPath.ReadAllText(),
                   new JsonSerializerOptions(JsonSerializerDefaults.Web))
               ?? throw new InvalidOperationException($"Failed to parse {ConfigPath}");
    }

    OutputFormat Output => string.Equals(DeployOutput, "json", StringComparison.OrdinalIgnoreCase)
        ? OutputFormat.Json
        : OutputFormat.Text;

    // The compose stack lives in a sibling sub-folder of the repo root.
    AbsolutePath ComposeSource => RootDirectory / "stack";

    // The Cloudflare reconcile body, reused by Provision (standalone) and
    // Bootstrap (chained). Returns the exit code; sets _connectorTokens.
    async Task RunProvisionAsync()
    {
        var options = LoadOptions();
        var provisioner = Provisioner.Create(CloudflareApiToken);
        var result = await provisioner.RunAsync(new ProvisionRequest(options, DryRun, Output));
        if (result.ExitCode != 0)
        {
            throw new Exception($"Provision failed with exit code {result.ExitCode}.");
        }
        _connectorTokens = result.Tunnels;
        Log.Information("Provision complete ({TunnelCount} tunnel(s), DryRun={DryRun}).", result.Tunnels.Count, DryRun);
    }

    // The compose-ship body, reused by Up (standalone) and Bootstrap (chained).
    void RunUp()
    {
        var options = LoadOptions();

        string token;
        if (DryRun)
        {
            // Provision ran in dry-run too, so there's no real token.
            token = "<dry-run-placeholder>";
        }
        else
        {
            if (_connectorTokens.Count == 0)
            {
                throw new InvalidOperationException(
                    "No connector token from Provision — did Provision actually apply? " +
                    "If running Up standalone (not via DependsOn), set --dry-run first to verify, " +
                    "then full apply.");
            }
            // Single-tunnel today; if/when multi-tunnel lands, deploy needs to
            // know which token to write into stack.env.
            token = _connectorTokens[0].ConnectorToken;
        }

        var deployer = Deployer.Create();
        var result = deployer.Run(new DeployRequest(
            Options:          options,
            ConnectorToken:   token,
            TopazVersion:     TopazVersion,
            PortalVersion:    PortalVersion,
            ComposeSourceDir: ComposeSource,
            DryRun:           DryRun));

        if (result.ExitCode != 0)
        {
            throw new Exception($"Up failed with exit code {result.ExitCode}.");
        }
        Log.Information("Up complete (TopazVersion={TopazVersion}, PortalVersion={PortalVersion}, DryRun={DryRun}).", TopazVersion, PortalVersion, DryRun);
    }

    // -------------------------------------------------------------------------
    // ProvisionNetwork: reconcile the UniFi VLAN + Local DNS the stack lives
    // behind. ADD-ONLY (find-or-create); the VLAN/DNS are shared, hand-managed
    // infra so an existing match is left untouched. Consumes azure-deploy.json.
    // -------------------------------------------------------------------------
    Target ProvisionNetwork => _ => _
        .Description("Reconcile the UniFi VLAN + Local DNS A records for the Topaz stack (find-or-create). Pass --dry-run to plan without writes.")
        .Requires(() => UnifiApiKey)
        .Executes(async () =>
        {
            var options = LoadOptions();
            var provisioner = NetworkProvisioner.Create(UnifiApiKey, UnifiBaseUrl, UnifiVerifyTls);
            var result = await provisioner.RunAsync(new NetworkProvisionRequest(options, DryRun, Output));
            if (result.ExitCode != 0)
            {
                throw new Exception($"ProvisionNetwork failed with exit code {result.ExitCode}.");
            }
            Log.Information("ProvisionNetwork complete ({PlanCount} plan(s), DryRun={DryRun}).", result.Plans.Count, DryRun);
        });

    // -------------------------------------------------------------------------
    // ProvisionHost: find-or-create the Proxmox LXC, start it, install Docker.
    // DependsOn(ProvisionNetwork) so the VLAN exists before the CT joins it.
    // ADD-ONLY (existing CT by vmid left untouched). Consumes azure-deploy.json.
    // -------------------------------------------------------------------------
    Target ProvisionHost => _ => _
        .Description("Find-or-create the Proxmox LXC + install Docker. DependsOn(ProvisionNetwork). Pass --dry-run to plan without writes.")
        .Requires(() => ProxmoxTokenSecret)
        .DependsOn(ProvisionNetwork)
        .Executes(async () =>
        {
            var options = LoadOptions();
            var provisioner = HostProvisioner.Create(ProxmoxTokenId, ProxmoxTokenSecret, ProxmoxBaseUrl, ProxmoxVerifyTls);
            var result = await provisioner.RunAsync(new HostProvisionRequest(options, DryRun, Output));
            if (result.ExitCode != 0)
            {
                throw new Exception($"ProvisionHost failed with exit code {result.ExitCode}.");
            }
            Log.Information("ProvisionHost complete ({PlanCount} plan(s), DryRun={DryRun}).", result.Plans.Count, DryRun);
        });

    // -------------------------------------------------------------------------
    // Deploy: reconcile Cloudflare tunnel + ingress + DNS + Access for the Topaz
    // stack. Consumes azure-deploy.json. Replaces bin/provision.ps1.
    // -------------------------------------------------------------------------
    Target Provision => _ => _
        .Description("Reconcile Cloudflare tunnel + ingress + DNS + Access for the Topaz stack. Pass --dry-run to plan without writes.")
        .Requires(() => CloudflareApiToken)
        .Executes(RunProvisionAsync);

    // -------------------------------------------------------------------------
    // Up: ship the compose stack to the LXC and bring it forward.
    //
    // Replaces deploy.ps1 lines 76–110. Uses Renci.SshNet for SFTP + remote
    // exec so the stack.env body is written as raw bytes — no remote shell
    // ever re-parses the connector token line, which is the bug deploy.ps1
    // couldn't shake.
    //
    // DependsOn(Provision): every Up runs the Cloudflare reconcile first so
    // the connector token is fresh. Idempotent for both halves.
    // -------------------------------------------------------------------------
    Target Up => _ => _
        .Description("Ship compose.yml + certs/topaz.crt + stack.env to the LXC and `docker compose up -d`. DependsOn(Provision).")
        .DependsOn(Provision)
        .Executes(RunUp);

    // -------------------------------------------------------------------------
    // Bootstrap: the whole fresh-hardware path in one target. Chains the infra
    // provisioning (network → host) and then the Cloudflare reconcile + compose
    // ship. DependsOn(ProvisionHost) wires the infra half via the target graph;
    // the Provision + Up bodies run inline here (reusing the shared helpers) so
    // they execute after the host exists rather than before it.
    // -------------------------------------------------------------------------
    Target Bootstrap => _ => _
        .Description("Fresh-hardware path: UniFi VLAN/DNS → Proxmox LXC + Docker → Cloudflare reconcile → compose up. Pass --dry-run to plan the whole chain.")
        .DependsOn(ProvisionHost)
        .Executes(async () =>
        {
            await RunProvisionAsync();
            RunUp();
            Log.Information("Bootstrap complete (DryRun={DryRun}).", DryRun);
        });
}
