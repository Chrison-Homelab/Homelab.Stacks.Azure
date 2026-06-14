using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Topaz.Deploy.Proxmox;

// Thin typed client over the Proxmox VE REST API (/api2/json). Covers the LXC
// list/create/start + task-status endpoints the LXC reconciler hits.
//
// Auth: an `Authorization: PVEAPIToken=<id>=<secret>` header — Proxmox's API
// token scheme, NOT a bearer token. The (id, secret) pair comes from an
// IProxmoxTokenSource so neither half is baked into config and callers can mock.
//
// Writes (create/start) go as application/x-www-form-urlencoded — Proxmox's API
// expects form params, not a JSON body, for these endpoints. Reads come back in
// the usual { "data": ... } envelope which we project to T.
public sealed class ProxmoxClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly IProxmoxTokenSource _tokenSource;
    private readonly ILogger<ProxmoxClient> _log;

    public ProxmoxClient(HttpClient http, IProxmoxTokenSource tokenSource, ILogger<ProxmoxClient> log)
    {
        _http = http;
        _tokenSource = tokenSource;
        _log = log;
        // BaseAddress (PROXMOX_BASE_URL, e.g. https://host/api2/json) comes from
        // the factory; enforce a trailing slash so relative paths resolve under it.
        if (_http.BaseAddress is { } b && !b.AbsoluteUri.EndsWith('/'))
        {
            _http.BaseAddress = new Uri(b.AbsoluteUri + "/");
        }
    }

    public async Task<IReadOnlyList<PveLxc>> ListLxcAsync(string node, CancellationToken ct = default)
        => await GetAsync<List<PveLxc>>($"nodes/{node}/lxc", ct) ?? new List<PveLxc>();

    // Templates (and other content) on a storage. content=vztmpl lists CT templates.
    public async Task<IReadOnlyList<PveStorageContent>> ListStorageContentAsync(
        string node, string storage, string content, CancellationToken ct = default)
        => await GetAsync<List<PveStorageContent>>(
               $"nodes/{node}/storage/{storage}/content?content={Uri.EscapeDataString(content)}", ct)
           ?? new List<PveStorageContent>();

    // Download an appliance/system template from the pveam catalog onto a storage
    // (the API behind `pveam download <storage> <template>`). `template` is the
    // catalog filename, e.g. "debian-13-standard_13.1-2_amd64.tar.zst". Async →
    // returns the UPID to wait on.
    public async Task<string> DownloadTemplateAsync(string node, string storage, string template, CancellationToken ct = default)
        => await PostFormAsync<string>(
               $"nodes/{node}/aplinfo",
               new Dictionary<string, string> { ["storage"] = storage, ["template"] = template },
               ct)
           ?? throw new ProxmoxApiException("POST", $"nodes/{node}/aplinfo", null, "template download returned no UPID");

    // Returns the UPID task id Proxmox assigns to the (async) create job.
    public async Task<string> CreateLxcAsync(string node, IReadOnlyDictionary<string, string> body, CancellationToken ct = default)
        => await PostFormAsync<string>($"nodes/{node}/lxc", body, ct)
           ?? throw new ProxmoxApiException("POST", $"nodes/{node}/lxc", null, "create returned no UPID");

    public async Task<string> StartLxcAsync(string node, int vmid, CancellationToken ct = default)
        => await PostFormAsync<string>($"nodes/{node}/lxc/{vmid}/status/start", EmptyForm, ct)
           ?? throw new ProxmoxApiException("POST", $"nodes/{node}/lxc/{vmid}/status/start", null, "start returned no UPID");

    public async Task<PveLxcStatus> GetLxcStatusAsync(string node, int vmid, CancellationToken ct = default)
        => await GetAsync<PveLxcStatus>($"nodes/{node}/lxc/{vmid}/status/current", ct)
           ?? new PveLxcStatus { VmId = vmid, Status = "unknown" };

    public async Task<PveTaskStatus> GetTaskStatusAsync(string node, string upid, CancellationToken ct = default)
        => await GetAsync<PveTaskStatus>($"nodes/{node}/tasks/{Uri.EscapeDataString(upid)}/status", ct)
           ?? new PveTaskStatus();

    // Poll a task until it stops; throw if it ends with a non-OK exit status.
    public async Task WaitForTaskAsync(string node, string upid, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var deadline = DateTimeOffset.UtcNow + (timeout ?? TimeSpan.FromMinutes(5));
        while (true)
        {
            var status = await GetTaskStatusAsync(node, upid, ct);
            if (status.IsStopped)
            {
                if (!status.Succeeded)
                {
                    throw new ProxmoxApiException("GET", $"nodes/{node}/tasks/{upid}/status", null,
                        $"task {upid} finished with exitstatus '{status.ExitStatus}'");
                }
                return;
            }
            if (DateTimeOffset.UtcNow > deadline)
            {
                throw new ProxmoxApiException("GET", $"nodes/{node}/tasks/{upid}/status", null,
                    $"task {upid} did not stop within timeout");
            }
            await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
        }
    }

    // ----- Verb helpers --------------------------------------------------

    private static readonly IReadOnlyDictionary<string, string> EmptyForm =
        new Dictionary<string, string>();

    private async Task<T?> GetAsync<T>(string path, CancellationToken ct)
    {
        using var req = NewRequest(HttpMethod.Get, path);
        return await SendAsync<T>(req, HttpMethod.Get.Method, path, ct);
    }

    private async Task<T?> PostFormAsync<T>(string path, IReadOnlyDictionary<string, string> form, CancellationToken ct)
    {
        using var req = NewRequest(HttpMethod.Post, path);
        req.Content = new FormUrlEncodedContent(form);
        return await SendAsync<T>(req, HttpMethod.Post.Method, path, ct);
    }

    private HttpRequestMessage NewRequest(HttpMethod method, string path)
    {
        var req = new HttpRequestMessage(method, path);
        // PVEAPIToken=<id>=<secret> — note the API token scheme uses '=' as the
        // separator inside the value, not a space.
        req.Headers.TryAddWithoutValidation(
            "Authorization",
            $"PVEAPIToken={_tokenSource.GetTokenId()}={_tokenSource.GetTokenSecret()}");
        req.Headers.TryAddWithoutValidation("Accept", "application/json");
        return req;
    }

    private async Task<T?> SendAsync<T>(HttpRequestMessage req, string method, string path, CancellationToken ct)
    {
        _log.LogDebug("pve {Method} {Path}", method, path);

        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        var status = (int)resp.StatusCode;
        var raw = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!resp.IsSuccessStatusCode)
        {
            throw new ProxmoxApiException(method, path, status,
                $"Proxmox API {method} {path} failed (HTTP {status}): {Truncate(raw, 300)}");
        }

        if (string.IsNullOrWhiteSpace(raw))
        {
            return default;
        }

        try
        {
            var env = JsonSerializer.Deserialize<PveEnvelope<T>>(raw, Json);
            return env is null ? default : env.Data;
        }
        catch (JsonException jex)
        {
            throw new ProxmoxApiException(method, path, status,
                $"non-JSON response (HTTP {status}): {Truncate(raw, 200)}", jex);
        }
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
