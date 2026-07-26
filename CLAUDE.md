# CLAUDE.md — Homelab.Stacks.Azure

Guidance for Claude Code working in this repo.

## What this is

A **stack submodule** of [`Chrison-Homelab/Homelab`](https://github.com/Chrison-Homelab/Homelab),
mounted there at `stacks/Azure` (meta-repo model, ADR-0008). A local **Azure** environment
emulated by [Topaz](https://topaz.thecloudtheory.com), with the Portal exposed through a dedicated
Cloudflare tunnel behind Access.

> **Read the superproject's [`CLAUDE.md`](https://github.com/Chrison-Homelab/Homelab/blob/main/CLAUDE.md) first.**
> It carries the rules that apply here and are *not* repeated in this file: the PR-only git
> workflow + merge strategy (ADR-0010), the worktree rule for parallel sessions, the shared
> external-account guardrails (**add-only** — never touch Cloudflare resources we didn't create),
> and `secrets.env` / Bitwarden Secrets Manager handling.

## This stack is the exception: it deploys itself

**Unlike every other stack, this one is NOT applied by the superproject's C# converge engine.** It
carries its own **Fallout** build (the NUKE hard-fork, `Fallout.Common` from nuget.org) and its own
reconcile engine, so it runs identically locally and in CI.

```
build/Build.cs            Provision / ProvisionNetwork / ProvisionHost / Up / Bootstrap targets
tools/Topaz.Deploy/       reconcile engine (Cloudflare / UniFi / Proxmox) + SSH deployer
azure-deploy.json         desired state: zone, tunnel, ingress, Access, VLAN, LXC
stack/                    the compose stack shipped to the LXC (host + portal + cloudflared)
```

- **Guest:** LXC **2009**, hostname `topaz`, node `desktop-01`
- **Deploy:** `./build.sh <Target>` (or `build.cmd` on Windows) — C# targets, not shell
- **Desired state lives in `azure-deploy.json`**, not in a `*.lxc.yaml` shape

So: do **not** try to converge this stack from the superproject, and do not add a `.lxc.yaml`
expecting the engine to pick it up.

## Gotchas specific to this stack

- **`Topaz.Deploy`'s `ProxmoxLxcReconciler` documents the keyctl seam** that the whole homelab
  relies on: a Proxmox **API token cannot set `keyctl`** on an unprivileged LXC. Anything needing
  keyctl must be created over **root SSH / pct**, not the token path. This file is the canonical
  note on that — the superproject's podman work (ADR-0009 / #284) cites it.
- **This stack binds `:443` on its host** and has rootless-443 caveats already noted in-file. It is
  therefore **deliberately out of scope for the Podman migration** (ADR-0009), alongside Pangolin.
- **It reconciles Cloudflare AND UniFi AND Proxmox.** The add-only guardrail matters more here than
  anywhere else — this is the one stack with its own write path to shared external accounts. Read
  current state before adding, and never repurpose an existing tunnel/record/VLAN.
- **Fallout comes from the `Fallout-build` GitHub Packages feed**, which needs `GITHUB_PACKAGES_PAT`
  (read:packages). `nuget.config` maps `Fallout.*` to that feed and everything else to nuget.org.
