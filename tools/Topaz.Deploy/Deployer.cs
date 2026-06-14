using Topaz.Deploy.Configuration;
using Topaz.Deploy.Ssh;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Spectre.Console;

namespace Topaz.Deploy;

public sealed record DeployRequest(
    DeployOptions Options,
    string ConnectorToken,
    string TopazVersion,
    string PortalVersion,
    string ComposeSourceDir,
    bool DryRun);

public sealed record DeployResult(int ExitCode);

// Owns the SSH/SFTP half of the deploy.
//
// What it replaces (deploy.ps1, lines 76–110):
//   - ssh "$user@$host" "mkdir -p '$remoteDir/certs'"      → SftpClient mkdir-p
//   - scp compose.yml + certs/topaz.crt                     → SftpClient.UploadFile (binary-safe)
//   - cat > stack.env && chmod 600                          → SftpClient.UploadFile + ChangePermissions (THE BUG FIX)
//   - ssh "$user@$host" "docker compose pull && up -d …"    → SshClient.RunCommand
//
// The stack.env step is the original failure mode. PowerShell piped a string
// body through ssh, where the remote shell re-parsed it, mangling lines that
// contained `$`, double-quotes, or newlines depending on terminal/locale.
// SFTP writes the bytes directly to the remote filesystem — no shell parses
// the content. Quoting goes from a multi-layered nightmare to a non-issue.
//
// ingress.json is NOT shipped: the tunnel ingress is now API-pushed by Provision.
public sealed class Deployer
{
    private readonly SshConnectionResolver _resolver;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<Deployer> _log;

    public Deployer(SshConnectionResolver resolver, ILoggerFactory? loggerFactory = null)
    {
        _resolver = resolver;
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        _log = _loggerFactory.CreateLogger<Deployer>();
    }

    public static Deployer Create(ILoggerFactory? loggerFactory = null)
    {
        loggerFactory ??= NullLoggerFactory.Instance;
        return new Deployer(
            new SshConnectionResolver(loggerFactory.CreateLogger<SshConnectionResolver>()),
            loggerFactory);
    }

    public DeployResult Run(DeployRequest req)
    {
        var remote = req.Options.Remote;
        var conn = _resolver.Resolve(remote);
        var stackEnv = StackEnvBuilder.Build(req.ConnectorToken, req.TopazVersion, req.PortalVersion);
        var uploads = BuildUploads(req.ComposeSourceDir, remote.StackDir, stackEnv);

        // Pre-flight: complain about missing source files before touching SSH.
        foreach (var u in uploads)
        {
            // Length 0 is suspicious for compose.yml/topaz.crt; stack.env is
            // fine to be small but never zero — we always write at least
            // TUNNEL_TOKEN=… and TOPAZ_VERSION=…
            if (u.Size == 0)
            {
                AnsiConsole.MarkupLineInterpolated($"[red]Refusing to upload empty file:[/] {u.RemotePath}");
                return new DeployResult(2);
            }
        }

        if (req.DryRun)
        {
            RenderPlan(conn, uploads, remote.StackDir, redact: true);
            return new DeployResult(0);
        }

        RenderPlan(conn, uploads, remote.StackDir, redact: true);

        AnsiConsole.MarkupLine("[cyan]→[/] connecting + uploading");
        using var deployer = SshDeployer.Connect(conn, _loggerFactory.CreateLogger<SshDeployer>());
        deployer.UploadFiles(uploads, remote.StackDir);

        // Deploy ordering matters: the portal mounts certs/topaz.crt and must
        // trust the EXACT cert the running host image presents. So we bring up
        // the host alone, copy its live cert out, then bring up the rest.
        //   1. pull every image
        //   2. up the topaz host only
        //   3. refresh certs/topaz.crt from the running host image
        //   4. up the rest (portal + cloudflared)
        var commands = new[]
        {
            $"cd '{remote.StackDir}' && docker compose --env-file stack.env pull",
            $"cd '{remote.StackDir}' && docker compose --env-file stack.env up -d topaz",
            $"sleep 3 && docker cp topaz:/app/topaz.crt '{remote.StackDir}/certs/topaz.crt'",
            $"cd '{remote.StackDir}' && docker compose --env-file stack.env up -d",
            // Plain (non-interpolated) string: Docker's Go template needs literal
            // double braces ({{.Names}}). In a C# $"" interpolated string `{{`
            // collapses to a single `{`, so the remote `docker ps` printed the
            // raw template instead of values. No interpolation needed here.
            "docker ps --filter name=topaz --format 'table {{.Names}}\t{{.Status}}\t{{.Ports}}'",
        };

        foreach (var cmd in commands)
        {
            AnsiConsole.MarkupLineInterpolated($"[green]→ remote[/] {cmd}");
            var r = deployer.Run(cmd, TimeSpan.FromMinutes(5));
            if (!string.IsNullOrWhiteSpace(r.StdOut))
            {
                AnsiConsole.WriteLine(r.StdOut.TrimEnd());
            }
            if (r.ExitStatus != 0)
            {
                AnsiConsole.MarkupLineInterpolated($"[red]remote command failed[/] (exit={r.ExitStatus}): {r.StdErr.TrimEnd()}");
                return new DeployResult(r.ExitStatus);
            }
        }

        AnsiConsole.MarkupLine("[green]✓[/] deploy complete");
        return new DeployResult(0);
    }

    private static IReadOnlyList<RemoteFileUpload> BuildUploads(string sourceDir, string stackDir, byte[] stackEnv)
    {
        var composeYml = Path.Combine(sourceDir, "compose.yml");
        var topazCrt = Path.Combine(sourceDir, "certs", "topaz.crt");

        if (!File.Exists(composeYml) || !File.Exists(topazCrt))
        {
            throw new FileNotFoundException(
                $"Compose stack source not found under {sourceDir}. " +
                $"Expected compose.yml and certs/topaz.crt.");
        }

        // Renci.SshNet's ChangePermissions takes the octal digits as a decimal
        // integer (not the actual POSIX bitfield) — i.e. pass 644, not 0b110_100_100.
        // Their validator does Math.DivRem(mode, 1000) and checks each digit ∈ [0,7].
        const short Mode644 = 644;
        const short Mode600 = 600;
        return new List<RemoteFileUpload>
        {
            new($"{stackDir}/compose.yml",      File.ReadAllBytes(composeYml), Mode644),
            // The cert is refreshed in-place from the running image by step 3 of
            // the deploy; we still seed it so the portal has something to mount
            // on the very first up before the refresh runs.
            new($"{stackDir}/certs/topaz.crt",  File.ReadAllBytes(topazCrt),   Mode644),
            // stack.env is 0600 — it carries the cloudflared connector secret.
            new($"{stackDir}/stack.env",        stackEnv,                      Mode600),
        };
    }

    private static void RenderPlan(
        ResolvedSshConnection conn,
        IReadOnlyList<RemoteFileUpload> uploads,
        string stackDir,
        bool redact)
    {
        var t = new Table().Border(TableBorder.Rounded);
        t.AddColumn("Target");
        t.AddColumn("Detail");
        t.AddRow("connect", Markup.Escape(conn.Display));
        t.AddRow("stackDir", Markup.Escape(stackDir));
        foreach (var u in uploads)
        {
            var preview = u.RemotePath.EndsWith("stack.env") && redact
                ? "(redacted — contains TUNNEL_TOKEN)"
                : $"{u.Size} bytes, mode 0{u.Mode}";
            t.AddRow("upload " + Markup.Escape(Path.GetFileName(u.RemotePath)), Markup.Escape(preview));
        }
        t.AddRow("compose", "pull + up topaz + cert refresh + up -d (--env-file stack.env)");
        AnsiConsole.Write(t);
    }
}
