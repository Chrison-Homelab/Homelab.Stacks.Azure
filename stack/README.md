# Azure stack — compose

The compose stack shipped to the LXC by the Fallout deploy in [`../`](../README.md).
Runs [Topaz](https://topaz.thecloudtheory.com) — a local Azure emulator (ARM,
Storage, Key Vault, Service Bus, Event Hub, Container Registry) — as a homelab
stack on its own **azure-lab VLAN**, with the web Portal exposed publicly through
a dedicated Cloudflare tunnel behind Access.

## How it's wired

```
chrison.dev (public)
└─ topaz.chrison.dev ──[tunnel]──> portal:8080      ← Cloudflare Access gates it
                                                       (single label → free Universal SSL)

azure-lab  VLAN 1050 / 10.50.0.0/16        LXC 2009 "topaz" @ 10.50.0.10 (desktop-01)
├─ *.topaz.local.dev ──[UniFi Local DNS]──> 10.50.0.10:<port>
│     ARM 443/8899 · Storage 8891 · Key Vault 8898 · Service Bus 8887/8889/5671
│     Event Hub 8897/8888 · ACR 8892 · CONNECT proxy 44380      (AMQP works — all LAN)
│     TLS: built-in *.topaz.local.dev self-signed cert (trust certs/topaz.crt once)
└─ portal also on http://10.50.0.10:8080 for LAN use
```

### Why the emulator stays on `topaz.local.dev`

The emulator's domain is **hardcoded** in the image (`GlobalSettings.cs`:
`TopazHostname`, `KeyVaultDnsSuffix`, the JWT issuer/scopes). The Key Vault
authorizer rejects any host not ending in `.vault.topaz.local.dev`, so the
service endpoints **cannot** be moved to `*.topaz.chrison.dev` without forking
the image. `chrison.dev` is therefore used only for the public Portal hostname.
The UCG Ultra's **Local DNS** gives the VLAN wildcard resolution for
`*.topaz.local.dev` — on the gateway, so it survives stack restarts/downtime.

## Deploy

The deploy is a **Fallout** build one level up at [`../`](../README.md), not shell
scripts — `./build.sh Provision --dry-run` then `./build.sh Up`. It reconciles the
Cloudflare tunnel + ingress + Access and SFTPs this stack to the LXC. The infra
below (VLAN, LXC, Local DNS) must exist first.

> Per the Homelab `CLAUDE.md`, `chrison.dev` is shared & hand-managed. The deploy
> is **add-only**: a dedicated tunnel `topaz`, one CNAME `topaz.chrison.dev`, and
> one Access app — nothing existing is touched, and `Provision --dry-run` shows the
> plan before any write.

## Apply (one-time infra)

**1. UniFi — the VLAN, and the Local DNS records:**
- **Network:** Settings → Networks → New. Name `Azure Lab`, VLAN **1050**, subnet
  **10.50.0.0/16**, gateway `10.50.0.1`, DHCP on. Still by hand — `converge-unifi`
  models port-forwards, DHCP reservations and static DNS, but not networks/VLANs yet.
- **Local DNS: not a manual step any more.** All twelve `topaz.local.dev` records are
  declared as IaC in the superproject's
  [`Infrastructure/unifi/network.yaml`](https://github.com/Chrison-Homelab/Homelab/blob/main/Infrastructure/unifi/network.yaml)
  under `staticDns` and reconciled by `converge-unifi` (Homelab#314). Nothing to click,
  and a rebuild restores them:

  ```bash
  # from the superproject
  homelab-infra converge-unifi Infrastructure/unifi/network.yaml           # dry run
  homelab-infra converge-unifi Infrastructure/unifi/network.yaml --apply
  ```

  They live there rather than in this repo because `staticDns` is a property of the
  superproject-global `UnifiNetwork` shape — these are gateway state, not stack state.

  **Why twelve records and not one wildcard:** UniFi's wildcard does match arbitrary
  labels, but only against the suffix it is written for, and the Azure SDKs derive
  endpoint hostnames per service family. Key Vault's authorizer specifically rejects any
  host not ending in `.vault.topaz.local.dev`, so the deep names are load-bearing rather
  than belt-and-braces. The leftmost label is the only dynamic part, so the set is
  complete. Verify with a deep name: `dig +short myacct.blob.storage.topaz.local.dev`.

> Local DNS lives on the gateway, so `*.topaz.local.dev` keeps resolving even when
> the stack is down. (If you'd rather not maintain static entries, a dnsmasq
> sidecar with `address=/topaz.local.dev/10.50.0.10` is the one-line alternative —
> but then DNS depends on the stack being up.)

**2. Proxmox — create the LXC.** `ProvisionHost` creates **CT 2009 `topaz`** on
desktop-01 via the Proxmox REST API from a **bare Debian 13 template**
(`local:vztmpl/debian-13-standard…`), unprivileged with `features=nesting=1,keyctl=1`,
NIC on `vmbr0` **VLAN tag 1050**, static **10.50.0.10/16** gw `.1` — then installs
Docker inside via `get.docker.com`. (Not the community-scripts Docker LXC: that's
interactive/on-node; the API path is declarative and upstream-decoupled for IaC.)
- Add an SSH alias `topaz-lxc` (user `chris`) so `../build.sh Up` can reach it, or
  set `Remote.Host` to `10.50.0.10` in `../azure-deploy.json`.

**3. Deploy the stack** via the Fallout build one level up (see [`../README.md`](../README.md)):

```sh
cd ..
export CLOUDFLARE_API_TOKEN=...   # scoped to chrison.dev
./build.sh Provision --dry-run    # review the Cloudflare plan
./build.sh Up                     # provision + ship + compose up
```

This provisions the tunnel + public CNAME + Access, SFTPs the files, writes
`stack.env`, pulls images, and brings the stack up. (The `*.topaz.local.dev`
wildcard DNS is the UniFi Local DNS from step 1 — not touched by deploy.)
Re-run anytime to roll forward.

## Use it

- **Portal (public):** https://topaz.chrison.dev → Cloudflare Access login → portal
  (seeded admin `topazadmin@topaz.local.dev` / `admin`; you'll set a new password
  on first sign-in).
- **Portal (LAN):** http://10.50.0.10:8080
- **Azure CLI / SDKs (LAN):** point at the `*.topaz.local.dev` endpoints (e.g.
  ARM `https://topaz.local.dev:8899`, Key Vault `https://<v>.vault.topaz.local.dev:8898`).
  **Trust the CA once** on each dev machine — copy `certs/topaz.crt` (it matches
  the running image) into the system/SDK trust store. AMQP (Service Bus / Event
  Hub) works because it's all on the LAN.

## Day 2

```sh
cd .. && ./build.sh Up          # update / re-apply (tags: --topaz-version / --portal-version)
ssh topaz-lxc 'cd ~/stacks/topaz && docker compose logs -f'
```

### Version matching

Host and portal are versioned independently but shipped together — keep both on
the same minor line (`--topaz-version` / `--portal-version`, written to `stack.env`
by the deploy). Defaults: host `v1.6.89-beta`, portal `v1.6.93-beta`.

## Files

| File | Purpose |
|------|---------|
| `compose.yml` | topaz + portal + cloudflared |
| `certs/topaz.crt` | built-in CA (fallback; deploy refreshes it from the running image) |
| `stack.env.example` | reference for the connector token + image tags the deploy writes |

> Deploy logic (tunnel + ingress + CNAME + Access + ship/up) lives in the Fallout
> build at [`../`](../README.md). Wildcard `*.topaz.local.dev` DNS is UniFi Local DNS (step 1).
