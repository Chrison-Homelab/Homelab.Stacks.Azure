using Topaz.Deploy.Configuration;
using Topaz.Deploy.Proxmox;
using Topaz.Deploy.Ssh;
using Microsoft.Extensions.Logging;

namespace Topaz.Deploy.Reconcile.Proxmox;

// Find-or-create the Topaz LXC and make sure Docker is installed in it.
// ADD-ONLY: if a CT with the configured vmid already exists we emit a NoOp for
// the create and never re-provision it — the host is shared infra. We still
// ensure it's started and Docker is present, because those are idempotent and
// the whole point is a reproducible host.
//
// PlanAsync emits up to three plans:
//   1. Lxc        — find-or-create the container by vmid.
//   2. LxcStart   — ensure it's running.
//   3. Docker     — install Docker over SSH (NoOp if `docker --version` works).
//
// The create body is form-encoded (Proxmox expects x-www-form-urlencoded for
// pct create), built here and handed to ProxmoxClient.CreateLxcAsync.
public sealed class ProxmoxLxcReconciler
{
    private readonly ProxmoxClient _pve;
    private readonly SshConnectionResolver _sshResolver;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<ProxmoxLxcReconciler> _log;

    public ProxmoxLxcReconciler(
        ProxmoxClient pve,
        SshConnectionResolver sshResolver,
        ILoggerFactory loggerFactory,
        ILogger<ProxmoxLxcReconciler> log)
    {
        _pve = pve;
        _sshResolver = sshResolver;
        _loggerFactory = loggerFactory;
        _log = log;
    }

    // vlanId is the UniFi VLAN the CT's net0 tags into — it lives on UniFiOptions,
    // not LxcSpec, so the caller threads it through to keep the two configs aligned.
    public async Task<IReadOnlyList<ResourcePlan>> PlanAsync(LxcSpec spec, int vlanId, CancellationToken ct)
    {
        var plans = new List<ResourcePlan>();
        var identity = $"CT {spec.VmId} ({spec.Hostname})";

        // The container IP without the CIDR suffix — what we SSH into and bind
        // net0's ip= to (with the suffix). e.g. "10.50.0.10/16" → "10.50.0.10".
        var bareIp = spec.Ip.Split('/', 2)[0];

        // 0. Ensure the CT template is on the node so this works on bare metal.
        //    Parse the configured volid "<storage>:vztmpl/<file>" into its parts;
        //    NoOp if already downloaded, else `pveam download` it (catalog must
        //    list this exact version — bump Lxc.Template in config if it's aged out).
        var (tplStorage, tplFile) = ParseTemplateVolId(spec.Template);
        var haveTemplate = (await _pve.ListStorageContentAsync(spec.Node, tplStorage, "vztmpl", ct))
            .Any(c => string.Equals(c.VolId, spec.Template, StringComparison.OrdinalIgnoreCase));
        if (haveTemplate)
        {
            _log.LogInformation("CT template {Tpl} already on {Storage}", tplFile, tplStorage);
            plans.Add(ResourcePlan.NoOp(
                "Template",
                tplFile,
                new[] { FieldChange.Same("volid", spec.Template) },
                note: $"present on {tplStorage}"));
        }
        else
        {
            plans.Add(ResourcePlan.Create(
                "Template",
                tplFile,
                new[]
                {
                    FieldChange.Diff("storage", null, tplStorage),
                    FieldChange.Diff("template", null, tplFile),
                },
                apply: async c =>
                {
                    var upid = await _pve.DownloadTemplateAsync(spec.Node, tplStorage, tplFile, c);
                    _log.LogInformation("Downloading CT template {Tpl} to {Storage} (task {Upid})", tplFile, tplStorage, upid);
                    await _pve.WaitForTaskAsync(spec.Node, upid, TimeSpan.FromMinutes(15), c);
                    _log.LogInformation("Template {Tpl} downloaded", tplFile);
                },
                note: "pveam download to the node's template storage"));
        }

        var existing = (await _pve.ListLxcAsync(spec.Node, ct))
            .FirstOrDefault(c => c.VmId == spec.VmId);

        if (existing is not null)
        {
            _log.LogInformation("Proxmox LXC {VmId} already exists on {Node} (status={Status})",
                spec.VmId, spec.Node, existing.Status);
            plans.Add(ResourcePlan.NoOp(
                "Lxc",
                identity,
                new[]
                {
                    FieldChange.Same("vmid", spec.VmId.ToString()),
                    FieldChange.Same("hostname", existing.Name),
                },
                note: "found existing CT (left untouched)"));
        }
        else
        {
            var body = BuildCreateBody(spec, vlanId);
            plans.Add(ResourcePlan.Create(
                "Lxc",
                identity,
                new[]
                {
                    FieldChange.Diff("vmid", null, spec.VmId.ToString()),
                    FieldChange.Diff("hostname", null, spec.Hostname),
                    FieldChange.Diff("template", null, spec.Template),
                    FieldChange.Diff("rootfs", null, $"{spec.RootfsStorage}:{spec.DiskGb}"),
                    FieldChange.Diff("cores/memory", null, $"{spec.Cores}c / {spec.MemoryMb}MB"),
                    FieldChange.Diff("net0", null, $"eth0@{spec.Bridge} tag={vlanId} ip={spec.Ip} gw={spec.Gateway}"),
                    FieldChange.Diff("features", null, "nesting=1"),
                },
                apply: async c =>
                {
                    var upid = await _pve.CreateLxcAsync(spec.Node, body, c);
                    _log.LogInformation("Creating LXC {VmId} on {Node} (task {Upid})", spec.VmId, spec.Node, upid);
                    await _pve.WaitForTaskAsync(spec.Node, upid, ct: c);
                    _log.LogInformation("Created LXC {VmId}", spec.VmId);
                },
                note: "unprivileged, nesting=1 for Docker"));
        }

        // Ensure running. When the CT is brand-new we can't read its status yet,
        // so plan a start unconditionally; for an existing CT, NoOp if running.
        var willStart = true;
        string? statusNote = "will start after create";
        if (existing is not null)
        {
            var st = await _pve.GetLxcStatusAsync(spec.Node, spec.VmId, ct);
            willStart = !string.Equals(st.Status, "running", StringComparison.OrdinalIgnoreCase);
            statusNote = willStart ? $"current status={st.Status}" : "already running";
        }

        if (willStart)
        {
            plans.Add(ResourcePlan.Create(
                "LxcStart",
                identity,
                new[] { FieldChange.Diff("status", existing?.Status, "running") },
                apply: async c =>
                {
                    var upid = await _pve.StartLxcAsync(spec.Node, spec.VmId, c);
                    await _pve.WaitForTaskAsync(spec.Node, upid, ct: c);
                    _log.LogInformation("Started LXC {VmId}", spec.VmId);
                },
                note: statusNote));
        }
        else
        {
            plans.Add(ResourcePlan.NoOp(
                "LxcStart",
                identity,
                new[] { FieldChange.Same("status", "running") },
                note: statusNote));
        }

        // Docker install over SSH. We can't probe a not-yet-running container in
        // dry-run, so this is always planned as a Create with the install closure
        // that NoOps internally if `docker --version` already succeeds.
        plans.Add(ResourcePlan.Create(
            "Docker",
            $"root@{bareIp}",
            new[]
            {
                FieldChange.Diff("install", null, "curl get.docker.com | sh"),
                FieldChange.Diff("service", null, "systemctl enable --now docker"),
            },
            // SshDeployer is synchronous (blocking SSH/SFTP); run it off-thread so
            // the apply closure honours its Task contract without blocking a pool
            // thread mid-await.
            apply: c => Task.Run(() => InstallDocker(spec, bareIp), c),
            note: "idempotent — NoOp if docker already present"));

        return plans;
    }

    // Proxmox `pct create` parameters as a form body. ssh-public-keys carries
    // the operator's pubkey so we can SSH in for the Docker install. features
    // nesting=1 is what Docker needs in an unprivileged LXC; keyctl is omitted
    // because the Proxmox API rejects it for non-root@pam tokens ("changing
    // feature flags (except nesting) is only allowed for root@pam") and Docker
    // runs fine without it for this stack.
    private static IReadOnlyDictionary<string, string> BuildCreateBody(LxcSpec spec, int vlanId)
    {
        var net0 = $"name=eth0,bridge={spec.Bridge},tag={vlanId},ip={spec.Ip},gw={spec.Gateway}";
        var form = new Dictionary<string, string>
        {
            ["vmid"] = spec.VmId.ToString(),
            ["hostname"] = spec.Hostname,
            ["ostemplate"] = spec.Template,
            ["storage"] = spec.RootfsStorage,
            ["rootfs"] = $"{spec.RootfsStorage}:{spec.DiskGb}",
            ["cores"] = spec.Cores.ToString(),
            ["memory"] = spec.MemoryMb.ToString(),
            ["swap"] = "512",
            ["unprivileged"] = "1",
            ["features"] = "nesting=1",
            ["net0"] = net0,
            ["onboot"] = "1",
        };

        var pubKey = ReadPublicKey(spec.SshPublicKeyFile);
        if (pubKey is not null)
        {
            form["ssh-public-keys"] = pubKey;
        }
        return form;
    }

    // SSH in and install Docker. Idempotent: skip if `docker --version` works.
    // Retries the connect a few times because a freshly-booted CT may not have
    // sshd up yet.
    private void InstallDocker(LxcSpec spec, string bareIp)
    {
        var remote = new RemoteOptions
        {
            Host = bareIp,
            User = "root",
            IdentityFile = DerivePrivateKey(spec.SshPublicKeyFile),
        };
        var conn = _sshResolver.Resolve(remote);

        using var ssh = ConnectWithRetry(conn, attempts: 10, delay: TimeSpan.FromSeconds(3));

        var check = ssh.Run("docker --version", TimeSpan.FromSeconds(20));
        if (check.ExitStatus == 0)
        {
            _log.LogInformation("Docker already present in CT {VmId}: {Version}", spec.VmId, check.StdOut.Trim());
            return;
        }

        _log.LogInformation("Installing Docker in CT {VmId} via get.docker.com", spec.VmId);
        // The Debian "standard" template ships without curl, and a bare
        // `curl … | sh` would swallow that failure (pipe exit = sh's). Install
        // curl+ca-certificates first, and `set -o pipefail` so a download failure
        // actually fails the step instead of silently running nothing.
        var install = ssh.Run(
            "set -o pipefail; export DEBIAN_FRONTEND=noninteractive; " +
            "apt-get update -qq && apt-get install -y -qq curl ca-certificates && " +
            "curl -fsSL https://get.docker.com | sh",
            TimeSpan.FromMinutes(8));
        if (install.ExitStatus != 0)
        {
            throw new InvalidOperationException(
                $"Docker install failed in CT {spec.VmId} (exit={install.ExitStatus}): {install.StdErr.TrimEnd()}");
        }

        var enable = ssh.Run("systemctl enable --now docker", TimeSpan.FromMinutes(1));
        if (enable.ExitStatus != 0)
        {
            throw new InvalidOperationException(
                $"`systemctl enable --now docker` failed in CT {spec.VmId} (exit={enable.ExitStatus}): {enable.StdErr.TrimEnd()}");
        }
        _log.LogInformation("Docker installed + enabled in CT {VmId}", spec.VmId);
    }

    private SshDeployer ConnectWithRetry(ResolvedSshConnection conn, int attempts, TimeSpan delay)
    {
        Exception? last = null;
        for (var i = 1; i <= attempts; i++)
        {
            try
            {
                return SshDeployer.Connect(conn, _loggerFactory.CreateLogger<SshDeployer>());
            }
            catch (Exception ex)
            {
                last = ex;
                _log.LogDebug("SSH to {Display} not ready (attempt {Attempt}/{Max}): {Message}",
                    conn.Display, i, attempts, ex.Message);
                Thread.Sleep(delay);
            }
        }
        throw new InvalidOperationException(
            $"Could not SSH to {conn.Display} after {attempts} attempts — is the CT booted and sshd up?", last);
    }

    // The reconciler installs over SSH using the private key that pairs with the
    // configured public key; we strip a trailing ".pub" to find it. When the
    // file isn't a ".pub" we leave IdentityFile null and let SshConnectionResolver
    // fall back to its default candidate set.
    private static string? DerivePrivateKey(string sshPublicKeyFile)
    {
        var path = ExpandHome(sshPublicKeyFile);
        return path.EndsWith(".pub", StringComparison.OrdinalIgnoreCase) ? path[..^4] : null;
    }

    // "local:vztmpl/debian-13-standard_13.1-2_amd64.tar.zst"
    //   → ("local", "debian-13-standard_13.1-2_amd64.tar.zst")
    private static (string Storage, string File) ParseTemplateVolId(string volId)
    {
        var colon = volId.IndexOf(':');
        if (colon < 0)
        {
            throw new InvalidOperationException(
                $"Lxc.Template '{volId}' is not a storage volid (expected '<storage>:vztmpl/<file>').");
        }
        var storage = volId[..colon];
        var rest = volId[(colon + 1)..];            // "vztmpl/<file>"
        var slash = rest.LastIndexOf('/');
        var file = slash >= 0 ? rest[(slash + 1)..] : rest;
        return (storage, file);
    }

    private static string? ReadPublicKey(string sshPublicKeyFile)
    {
        var path = ExpandHome(sshPublicKeyFile);
        return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
    }

    private static string ExpandHome(string path)
        => path.StartsWith("~/")
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[2..])
            : path;
}
