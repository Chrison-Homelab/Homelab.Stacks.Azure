# Topaz (Azure emulator) — container setup

Runs [Topaz](https://topaz.thecloudtheory.com) — a local Azure emulator (Storage,
Key Vault, Service Bus, Event Hub, Container Registry, ARM) — as a container
instead of the local `topaz-host` binary.

The Homebrew `topaz` CLI still works as the client; it talks to whichever
`topaz-host` is listening on localhost — now the container.

## Prerequisites

- **Stop the local host first** so it doesn't fight the container for ports.
  If you ran `topaz-host` from Homebrew, kill that process. The CLI just needs
  *something* on those ports.
- An engine: **OrbStack/Docker** (recommended locally — handles port 443) or
  **podman** (`podman machine start` first; see the 443 note below).

## Local dev (OrbStack)

```bash
docker compose up -d          # start
docker compose logs -f topaz  # watch boot
topaz health                  # or: curl -k https://localhost:8899/health
docker compose down           # stop (state kept in the topaz-data volume)
```

Healthy response: `HTTP 200 {"status":"Healthy", ...}`.

## Pointing tools at the emulator

- **SDKs**: use the emulator endpoints — ARM `https://localhost:8899`,
  Key Vault SDK `https://localhost:8898`, Storage `https://localhost:8891`.
  Certs are self-signed, so disable TLS validation in dev (`curl -k`, or the
  SDK's "allow untrusted cert" / `ServerCertificateValidationCallback` option).
- **Azure CLI auth** (ROPC `az login -u … -p …`) needs the CONNECT proxy:

  ```bash
  export HTTPS_PROXY=http://127.0.0.1:44380
  az login -u <user> -p <pass>
  ```

See the docs' Azure CLI / SDK integration pages for per-service connection strings.

## Portal

Web UI at **http://localhost:8900**. Log in with the seeded admin:
`topazadmin@topaz.local.dev` / `admin` (you'll be asked to set a new password
on first sign-in).

The portal makes a **server-side** token call to the host over HTTPS, so it must
trust the host's self-signed CA — otherwise login spins forever with
`UntrustedRoot` in the portal logs. `certs/topaz.crt` is that CA, extracted from
the host image (`/app/topaz.crt`; CN/SAN `*.topaz.local.dev`, baked in and stable
for this tag). It's mounted into the portal with `SSL_CERT_FILE=/certs/topaz.crt`.

If you bump the host image tag, re-extract the cert in case it changed:

```bash
cid=$(docker create thecloudtheory/topaz-host:<tag>)
docker cp "$cid:/app/topaz.crt" certs/topaz.crt
docker rm "$cid"
docker compose up -d
```

## Reusing your existing local state (optional)

You already have state in `~/.topaz`. To keep using it instead of a fresh
named volume, swap the volume line in `compose.yaml`:

```yaml
    volumes:
      - ${HOME}/.topaz:/app/.topaz
```

## Homelab notes

The same `compose.yaml` runs there. Two things to watch:

- **Port 443 under rootless podman**: rootless containers can't bind host ports
  below 1024 by default. Either run the engine rootful, lower
  `net.ipv4.ip_unprivileged_port_start=443` (sysctl, inside the podman VM/host),
  or front Topaz with a reverse proxy that owns 443. The Azure CLI hardcodes
  443, so you can't simply remap it to a high port for CLI scenarios.
- For a systemd-managed homelab, generate a **Quadlet** unit
  (`podman generate systemd` / a `.container` file) from this service so it
  starts on boot.

## Updating

```bash
docker compose pull && docker compose up -d
```

Bump the pinned tag in `compose.yaml` when you want a specific version; tags
track Git releases (`latest` ≈ newest beta).
