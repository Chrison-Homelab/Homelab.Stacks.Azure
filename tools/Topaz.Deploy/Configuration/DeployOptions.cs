namespace Topaz.Deploy.Configuration;

public sealed class DeployOptions
{
    public CloudflareOptions Cloudflare { get; init; } = new();
    public AccessOptions Access { get; init; } = new();
    public RemoteOptions Remote { get; init; } = new();

    // Infrastructure provisioning (fresh-hardware path). UniFi = the VLAN +
    // Local DNS the stack lives behind; Lxc = the Proxmox container that hosts
    // the compose stack. Both are reconciled ADD-ONLY (find-or-create).
    public UniFiOptions UniFi { get; init; } = new();
    public LxcSpec Lxc { get; init; } = new();
}

public sealed class CloudflareOptions
{
    public string Zone { get; init; } = string.Empty;
    public List<TunnelSpec> Tunnels { get; init; } = new();
}

public sealed class TunnelSpec
{
    public string Name { get; init; } = string.Empty;
    public List<HostnameSpec> Hostnames { get; init; } = new();
    public string Catchall { get; init; } = "http_status:404";
}

public sealed class HostnameSpec
{
    public string Hostname { get; init; } = string.Empty;

    /// <summary>
    /// Optional Cloudflare ingress path matcher (regex), e.g. <c>/api/me</c>.
    /// When set the rule only matches that path on the hostname. Null matches
    /// the whole hostname. Order matters: list more-specific path rules BEFORE
    /// the bare-hostname rule (Cloudflare evaluates top-down).
    /// </summary>
    public string? Path { get; init; }

    public string Service { get; init; } = string.Empty;
}

// Cloudflare Access (Zero Trust). The Topaz portal is an admin UI with a weak
// default login, so it's gated by Access — never exposed raw through the tunnel.
public sealed class AccessOptions
{
    public List<AccessAppSpec> Apps { get; init; } = new();
}

public sealed class AccessAppSpec
{
    // The self-hosted app's natural key (find-or-create by domain).
    public string Hostname { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string SessionDuration { get; init; } = "24h";

    // Allow policy. PolicyName is the policy's natural key on the app; emails
    // are not secret, so they live in azure-deploy.json.
    public string PolicyName { get; init; } = "topaz-allow";
    public List<string> AllowEmails { get; init; } = new();
}

// UniFi Network integration. Site is the human site name (find-or-create
// reconcilers resolve it to its id). Network is the VLAN the stack lives on;
// DnsTarget + DnsDomains are the Local DNS A records that point the stack's
// hostnames at the LXC. All reconciled ADD-ONLY.
public sealed class UniFiOptions
{
    public string Site { get; init; } = "Default";
    public NetworkSpec Network { get; init; } = new();
    public string DnsTarget { get; init; } = string.Empty;
    public List<string> DnsDomains { get; init; } = new();

    // The source firewall zone allowed inbound to the VLAN's zone — where the
    // deploy/mgmt workstation lives, so it can SSH the LXC. Resolved by name.
    public string MgmtZone { get; init; } = "Internal";
}

// A GATEWAY-managed VLAN. Keyed by VlanId (the gateway's natural unique key).
// Gateway is the network's host (gateway) IP; PrefixLength its subnet size.
// DhcpStart/DhcpStop bound the DHCP pool — leave both blank for a static-only
// network (the reconciler then omits the DHCP sub-object entirely).
public sealed class NetworkSpec
{
    public string Name { get; init; } = string.Empty;

    // The firewall zone the VLAN lands in (find-or-create by name). The zone
    // starts default-deny; the deploy opens only the allow-paths it needs.
    public string Zone { get; init; } = "Azure";
    public int VlanId { get; init; }
    public string Gateway { get; init; } = string.Empty;
    public int PrefixLength { get; init; } = 24;
    public string DhcpStart { get; init; } = string.Empty;
    public string DhcpStop { get; init; } = string.Empty;
}

// A Proxmox LXC container hosting the compose stack. Keyed by VmId on Node.
// Ip carries the CIDR suffix (e.g. 10.50.0.10/16); the reconciler strips it
// for the SSH target. SshPublicKeyFile is injected at create time so the
// Docker-install SSH step can authenticate; ~ expands to the home dir.
public sealed class LxcSpec
{
    public string Node { get; init; } = string.Empty;
    public int VmId { get; init; }
    public string Hostname { get; init; } = string.Empty;
    public string Template { get; init; } = string.Empty;
    public string RootfsStorage { get; init; } = "local-lvm";
    public int DiskGb { get; init; } = 8;
    public int Cores { get; init; } = 2;
    public int MemoryMb { get; init; } = 2048;
    public string Ip { get; init; } = string.Empty;
    public string Gateway { get; init; } = string.Empty;
    public string Bridge { get; init; } = "vmbr0";
    public string SshPublicKeyFile { get; init; } = "~/.ssh/id_ed25519.pub";
}

public sealed class RemoteOptions
{
    // Host is the SSH config alias OR a raw hostname. SshConnectionResolver
    // shells out to `ssh -G <Host>` and picks up Hostname/Port/User/IdentityFile
    // from ~/.ssh/config. Explicit values below override what ssh -G resolves.
    public string Host { get; init; } = "topaz-lxc";

    // Optional overrides. When null/0, the value resolved from `ssh -G` wins.
    public int? Port { get; init; }
    public string? User { get; init; }
    public string? IdentityFile { get; init; }

    public string StackDir { get; init; } = "/home/chris/stacks/topaz";
}
