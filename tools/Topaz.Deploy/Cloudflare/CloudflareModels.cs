using System.Text.Json.Serialization;

namespace Topaz.Deploy.Cloudflare;

// Cloudflare's API wraps every response in { success, errors, messages, result }.
// We project `result` into a typed `T` and surface success/errors at the boundary.

internal sealed class CfEnvelope<T>
{
    [JsonPropertyName("success")] public bool Success { get; set; }
    [JsonPropertyName("errors")] public List<CfMessage>? Errors { get; set; }
    [JsonPropertyName("messages")] public List<CfMessage>? Messages { get; set; }
    [JsonPropertyName("result")] public T? Result { get; set; }
}

internal sealed class CfMessage
{
    [JsonPropertyName("code")] public int Code { get; set; }
    [JsonPropertyName("message")] public string Message { get; set; } = string.Empty;
}

public sealed class CfAccount
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
}

public sealed class CfZone
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
}

public sealed class CfTunnel
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("config_src")] public string? ConfigSrc { get; set; }
    [JsonPropertyName("deleted_at")] public DateTimeOffset? DeletedAt { get; set; }
}

internal sealed class CfTunnelCreateBody
{
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("tunnel_secret")] public string TunnelSecret { get; set; } = string.Empty;
    [JsonPropertyName("config_src")] public string ConfigSrc { get; set; } = "cloudflare";
}

public sealed class CfIngressConfig
{
    [JsonPropertyName("config")] public CfIngressInner Config { get; set; } = new();
}

public sealed class CfIngressInner
{
    [JsonPropertyName("ingress")] public List<CfIngressRule> Ingress { get; set; } = new();
}

public sealed class CfIngressRule
{
    [JsonPropertyName("hostname")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Hostname { get; set; }

    [JsonPropertyName("path")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Path { get; set; }

    [JsonPropertyName("service")] public string Service { get; set; } = string.Empty;
}

public sealed class CfDnsRecord
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("type")] public string Type { get; set; } = string.Empty;
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("content")] public string Content { get; set; } = string.Empty;
    [JsonPropertyName("proxied")] public bool Proxied { get; set; }
    [JsonPropertyName("ttl")] public int Ttl { get; set; }
}

internal sealed class CfDnsRecordUpsertBody
{
    [JsonPropertyName("type")] public string Type { get; set; } = "CNAME";
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("content")] public string Content { get; set; } = string.Empty;
    [JsonPropertyName("proxied")] public bool Proxied { get; set; } = true;
    [JsonPropertyName("ttl")] public int Ttl { get; set; } = 1;
}

// ----- Cloudflare Access (Zero Trust) ------------------------------------
//
// A self-hosted Access application gates a public hostname behind the Access
// login. The app is keyed by its `domain`; each app carries one or more
// policies that decide who gets through. We find-or-create both by their
// natural key (domain for the app, name for the policy) so re-runs don't
// duplicate. Mirrors what provision.ps1 step 7 did against the same endpoints.

public sealed class CfAccessApp
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("domain")] public string Domain { get; set; } = string.Empty;
    [JsonPropertyName("type")] public string? Type { get; set; }
    [JsonPropertyName("session_duration")] public string? SessionDuration { get; set; }
}

internal sealed class CfAccessAppCreateBody
{
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("domain")] public string Domain { get; set; } = string.Empty;
    [JsonPropertyName("type")] public string Type { get; set; } = "self_hosted";
    [JsonPropertyName("session_duration")] public string SessionDuration { get; set; } = "24h";
    [JsonPropertyName("app_launcher_visible")] public bool AppLauncherVisible { get; set; } = true;
}

public sealed class CfAccessPolicy
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("decision")] public string? Decision { get; set; }
    [JsonPropertyName("include")] public List<CfAccessInclude>? Include { get; set; }
}

internal sealed class CfAccessPolicyUpsertBody
{
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("decision")] public string Decision { get; set; } = "allow";
    [JsonPropertyName("include")] public List<CfAccessInclude> Include { get; set; } = new();
}

// Access include rules are a tagged union; we only use the email rule
// ({ "email": { "email": "you@example.com" } }). Other rule kinds (groups,
// IP ranges, …) stay null and are omitted on the wire.
public sealed class CfAccessInclude
{
    [JsonPropertyName("email")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CfAccessEmailRule? Email { get; set; }
}

public sealed class CfAccessEmailRule
{
    [JsonPropertyName("email")] public string Email { get; set; } = string.Empty;
}
