using System.Text.Json.Serialization;

namespace Topaz.Deploy.Proxmox;

// Typed models over the Proxmox VE REST API (/api2/json). Like Cloudflare,
// Proxmox wraps every response in { "data": ... }; we project `data` into T.
// Reads are JSON; writes (LXC create/start) go as application/x-www-form-
// urlencoded, which is built by the client rather than serialised from a POCO.

internal sealed class PveEnvelope<T>
{
    [JsonPropertyName("data")] public T? Data { get; set; }
}

public sealed class PveLxc
{
    // vmid comes back as a number in the JSON; bind loosely so a string form
    // wouldn't trip us up.
    [JsonPropertyName("vmid")] public int VmId { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("status")] public string? Status { get; set; }
}

public sealed class PveLxcStatus
{
    [JsonPropertyName("vmid")] public int VmId { get; set; }
    [JsonPropertyName("status")] public string? Status { get; set; }
}

// One item from a storage's content listing (content=vztmpl). `volid` is the
// full volume id, e.g. "local:vztmpl/debian-13-standard_13.1-2_amd64.tar.zst".
public sealed class PveStorageContent
{
    [JsonPropertyName("volid")] public string? VolId { get; set; }
    [JsonPropertyName("format")] public string? Format { get; set; }
}

// One poll of a task's status. `status` flips to "stopped" when the task is
// done; `exitstatus` is "OK" on success or an error string otherwise.
public sealed class PveTaskStatus
{
    [JsonPropertyName("status")] public string? Status { get; set; }
    [JsonPropertyName("exitstatus")] public string? ExitStatus { get; set; }
    [JsonPropertyName("upid")] public string? Upid { get; set; }

    public bool IsStopped => string.Equals(Status, "stopped", StringComparison.OrdinalIgnoreCase);
    public bool Succeeded => string.Equals(ExitStatus, "OK", StringComparison.OrdinalIgnoreCase);
}
