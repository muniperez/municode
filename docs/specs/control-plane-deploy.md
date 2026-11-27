# Spec: deploy a new release to an existing control plane

Status: proposed. Tracking: see the epic issue linked from the
[issue breakdown](#issue-breakdown).

## Problem

`workspaces seed` installs a control plane once. After that, no supported path
moves an installation to a newer release. Operators upgrade by hand: they copy a
bundle over SSH, merge `.env`, run `pg_dumpall`, pause OpenBao to archive its
volume, restart services from the new tree, and roll back by hand. Every hosted
upgrade so far (for example `personal.1` to `storage.1` on the personal instance
and `acceptance.5` to `acceptance.6` on cloud3) used one-off scripts. Those
scripts skipped database migrations (`compose up --no-deps`), hard-coded host
names and release paths, and depended on an agent to reconcile drift.

An operator must be able to run, from the same workstation that seeded the
instance:

```sh
workspaces deploy plan --instance dev --release latest --prerelease
workspaces deploy apply <operation> --approve <digest>
```

and end with the instance serving the exact target release. Data, secrets,
operator policy, the pinned SSH identity and the installation ID must be
preserved. If the deploy fails, the instance must return to the previous release.

## Goals

- One CLI workflow (`plan`, `apply`, `status`, `resume`, `rollback`) upgrades an
  existing single-VPS control plane in place, with bounded downtime.
- It uses the same trust model as seed: exact release identity, an approval
  digest bound to what will change, pinned SSH host identity, an
  interruption-safe journal, and no secrets in output.
- Database, identity and secret-store state are backed up on the host before any
  schema change. Restore is automatic when the failure happens before public
  traffic returns.
- Operator configuration (`.env` operator keys, policy JSON, Compose overlays)
  survives upgrades without manual merging.
- Works for instances created by `seed` and for adopted manual installations.

## Non-goals

- Zero-downtime or multi-host deploys. A deploy has a short maintenance window
  (target: under 5 minutes, excluding image pulls, which happen before the window).
- PostgreSQL major-version upgrades. Plan refuses them, and they need a separate
  procedure.
- Workspace VM image releases, which keep their own qualification and capture flow.
- Provider API calls. Deploy needs no provider credential.
- Changing the seed trust model. Seed still accepts only signed releases.

## User experience

```text
$ workspaces instance adopt --seed-operation 18c3…f9 --name dev
Instance dev: dev.muni.dev / api.dev.muni.dev, host 46.225.68.165 (pinned SHA256:…)
Layout: seed (/opt/municode/workspaces), release workspaces-v0.1.0-storage.1 (723df64)

$ workspaces deploy plan --instance dev --release latest --prerelease
Resolved latest → workspaces-v0.1.0-acceptance.7 (published 2026-11-24, source 1a2b…)
Verified Sigstore identity, manifest, checksums and component digests.
Staged on host (no service changed).
Current:   workspaces-v0.1.0-storage.1 723df64
Target:    workspaces-v0.1.0-acceptance.7 1a2b3c4
Migrations: 4 pending (Postgres): AddBudgetPeriodsAndScopes, …
Images:    api/worker/gateway payload changed; kratos, openbao, postgres, caddy unchanged
Env:       +WORKSPACES_NEW_SETTING (release default), no operator input required
Overlays:  /etc/municode/workspaces/compose.d/10-runtime-policy.yml (validated)
Work:      0 nonterminal workspace operations; 3 workspace VMs keep running
Unseal:    seed recovery record present
Downtime:  API, dashboard and gateway unavailable for about 2–4 minutes
Backup:    /var/lib/municode/workspaces/backups/<operation> (≈180 MB, 31 GB free)
Approval digest: 9f0c…
Run: workspaces deploy apply 5be1… --approve 9f0c…

$ workspaces deploy apply 5be1… --approve 9f0c…
[prepare]   pulled 0 images                           ✓
[drain]     maintenance on, 0 nonterminal operations   ✓
[backup]    matched snapshot (postgres, openbao, caddy, secrets, config) ✓
[migrate]   kratos ✓  workspaces (4) ✓
[start]     openbao unsealed ✓  internal /health/ready ✓
[open]      https://api.dev.muni.dev/health ✓  dashboard ✓  version 1a2b3c4 ✓
[resume]    worker started, maintenance off            ✓
Deployed workspaces-v0.1.0-acceptance.7. Previous release kept for rollback.
```

Commands (all accept `--json`):

| Command | Effect |
| --- | --- |
| `workspaces instance list` / `show <name>` | Local registry only. |
| `workspaces instance adopt --seed-operation <id> [--name <n>]` | Registers a completed seed instance using its pinned SSH material. Runs a read-only host probe. |
| `workspaces instance add --name <n> --host <ipv4> --ssh-key <path> --confirm-host-fingerprint SHA256:… --dashboard-hostname … --api-hostname … --gateway-domain …` | Registers a manual installation. Uses the same independent-fingerprint rule as seed. |
| `workspaces deploy plan --instance <n> (--release <tag> \| --release latest [--prerelease] \| --bundle-dir <dir> --trust local-build)` | Verifies, uploads and stages the bundle. Runs a read-only preflight. Prints the diff and an approval digest. |
| `workspaces deploy apply <op> --approve <digest> [--unseal-key-file <path>] [--drain-timeout <dur>] [--offhost-backup <dir>]` | Runs the host deploy agent and follows its progress. |
| `workspaces deploy status <op>` | Local journal plus a live host-agent state read over pinned SSH. |
| `workspaces deploy resume <op>` | Reattaches after a lost connection, or continues an interrupted agent. |
| `workspaces deploy rollback <op> [--restore-database --accept-data-loss-since <timestamp>] [--accept-provider-reconciliation]` | Returns to the previous release. See [rollback](#failure-and-rollback). |

Seed completion registers its instance automatically. Its default name is the
dashboard hostname.

## Design

### Components

```text
workstation                              control-plane VPS (root over pinned SSH)
───────────                              ────────────────────────────────────────
workspaces CLI                           /opt/municode/releases/<release-id>/   immutable trees
 ├─ instance registry                    /opt/municode/workspaces → releases/<active>  (symlink)
 ├─ release source (signed | latest |    /etc/municode/workspaces/secrets/      unchanged
 │   local-build)                        /etc/municode/workspaces/config/       operator-owned
 ├─ deploy journal (<config>/deploy/<op>)     .env.operator, workspaces.json, compose.d/*.yml
 └─ SSH transport (seed pinned options)  /var/lib/municode/workspaces/
                                             active-release.json, deploy.lock,
                                             deploys/<op>/state.json, log, plan.json,
                                             backups/<op>/
                                         deploy_host.py  (shipped in the *target* bundle,
                                                          run under systemd-run)
```

### Release sources

1. **Signed tag** (`--release workspaces-vX.Y.Z[-pre]`). This reuses
   `SeedRelease.VerifyAsync`: Sigstore identity, manifest, checksum inventory,
   component digests and dependency digests. Trust is recorded as `github-sigstore`.
2. **Latest** (`--release latest [--prerelease]`). The CLI reads the GitHub
   releases API for `muniperez/municode`, keeps non-draft `workspaces-v*` tags
   (prereleases only with `--prerelease`), and picks the most recently
   *published* one. It doesn't use semver precedence, because prerelease labels
   such as `acceptance` and `storage` aren't a linear series. The resolved exact
   tag is then verified as in (1). Only the exact tag is journaled and approved,
   so nothing installs from a mutable reference.
3. **Local build** (`--bundle-dir <dir> --trust local-build`). This deploys
   output from `scripts/delivery/build-release.sh --ref <git-ref>`. That script
   builds the README's package commands in a clean `git worktree` at an exact
   commit and writes `release-manifest.json`, the bundle and `checksums.txt`
   (no Sigstore bundle). The CLI verifies the inventory and component digests,
   requires a clean exact source revision, and records trust `local-build`. Seed
   still refuses local builds. This source supports the workflow where each
   operator deploys their own instance from their own machine without waiting
   for a tagged CI release.

**CLI version coupling.** Seed requires the CLI and release source revisions to
match. Deploy drops that requirement because the host logic ships in the
*target* bundle (`deploy_host.py`). The CLI is the transport and orchestrator. It
checks `release-manifest.json` `deployProtocol` against the versions it
supports, and refuses with "use the CLI from the target release (`cli/<rid>/`)"
when the target needs a newer protocol.

### Release manifest additions (`deployProtocol: 1`)

`scripts/delivery/package.py` adds:

- `deployProtocol`: integer contract between the CLI and `deploy_host.py`.
- `migrations`: ordered EF migration IDs per provider (`postgres`, `sqlite`),
  produced at package time by a new API command
  (`Workspaces.Api migrations list --json`). The command reads them from the
  real `WorkspaceDbContext` model, whose migrations live in `Workspaces.Common`.
  This list is informational. Preflight decides with the live database (below).
- `envContract`: generated from a new `packaging/bundle/env-contract.json`. It
  assigns every `.env` key an owner:
  - `release`: image digests, `WORKSPACES_SOURCE_URL`, `WORKSPACES_RELEASE_*`.
    Always taken from the target.
  - `installer`: `WORKSPACES_SECRETS_DIR`, `WORKSPACES_WORKER_SSH_PUBLIC_KEY`.
    Carried forward unchanged.
  - `operator`: host names, installation ID, passkeys and any new tunables.
    Carried forward, and a new key needs either a declared default or explicit
    operator input.

  Keys the contract doesn't know, such as variables read by operator overlays,
  are kept verbatim in `.env.operator` and listed in the plan. They are never
  dropped silently. A key the target release removes is reported. A delivery
  test fails when `.env.example` and `env-contract.json` disagree.
- `compose.yml` reads the policy file from `${WORKSPACES_POLICY_FILE:-./workspaces.json}`
  for `api`, `api-migrate` and `worker`. The agent sets it to
  `/etc/municode/workspaces/config/workspaces.json`, so operator policy stays
  in force across releases instead of falling back to the target's
  admission-disabled default.

### Running release identity

The installer writes the release-owned keys `WORKSPACES_RELEASE_TAG`,
`WORKSPACES_SOURCE_REVISION` and `WORKSPACES_RELEASE_TRUST` into `.env`. The API
exposes them at anonymous `GET /health/version` as
`{tag, sourceRevision, trust}`. The response contains no secrets or host details.
Deploy verification requires the public endpoint to report the target revision.
This closes the gap where an upgrade "succeeds" but stale containers keep
serving.

### Host layout and adoption

Canonical layout:

- `/opt/municode/releases/<release-id>/` holds one extracted, verified, read-only
  bundle per release. `<release-id>` is the tag, or `local-<rev12>-<bundle12>`
  for local builds.
- `/opt/municode/workspaces` is a symlink to the active release, kept for
  operator convenience and existing docs. The agent always runs Compose with
  `--project-name municode-workspaces --project-directory <resolved release path>`,
  so bind mounts point at an immutable tree. Volumes are keyed by project name
  and therefore carry over between releases.
- `/etc/municode/workspaces/config/` holds operator-owned files: `.env.operator`
  (operator keys only), `workspaces.json` (policy) and `compose.d/*.yml`
  (overlays, applied in lexical order). The agent renders
  `<release>/.env` from target release keys, installer keys and `.env.operator`.
- `/var/lib/municode/workspaces/active-release.json` records
  `{releaseId, tag, sourceRevision, bundleSha256, trust, activatedAt, previous}`.
- The host wrapper `/usr/local/sbin/municode-compose` runs Compose against the
  active release with all overlays, for manual operations.

The first deploy (or `instance adopt`) detects and converts existing layouts:

| Detected layout | Evidence | Conversion |
| --- | --- | --- |
| Seed | `/opt/municode/workspaces` is a directory. `<secrets>/seed-install-complete` exists, and `<secrets>.seed-owner` reads `<installation-id> <tag> complete`. | Verify the tree with its `release-manifest.json` and `verify_bundle.py`, move it to `releases/<tag>`, and create the symlink. Split `.env` into `.env.operator` by the contract. Copy the installed `workspaces.json` to `config/`, so the effective policy is preserved even when unchanged. |
| Manual `install.sh` (operator guide) | `/opt/municode/workspaces` is a directory with `release-manifest.json` and `.env`, no seed marker. Running containers carry the matching `com.municode.workspaces.installation-id` label. | Same as seed. |
| Manual releases (the cloud3 style) | `/opt/municode/releases/<tag>` plus `/var/lib/municode/active-release`, and the running containers' `com.docker.compose.project.working_dir` matches it. | Import the record into `active-release.json` and import overlays named in the containers' `com.docker.compose.project.config_files` label into `compose.d/`. |
| Anything else | | Refuse with the exact discrepancy. Never adopt or delete unowned paths. |

OpenBao auto-unseal is possible only when the seed recovery record
(`<secrets>/openbao-seed-init.json`) exists. Manual installations must supply
an unseal key file at `apply` time (see the `migrate` phase).

Conversion happens in `apply` while services are stopped (see `drain` below).
Plan only reports it, so plan never changes running services. The original
directory is kept, renamed, until the next successful deploy.

### Host deploy agent (`packaging/bundle/deploy_host.py`)

The agent is Python 3 with stdlib only (already a host prerequisite), unit-tested
with a fake `docker` like `scripts/delivery/test_seed_start.py`. Its commands:

- `stage`: verify the uploaded archive digest, extract into
  `releases/.staging-<op>`, run `verify_bundle.py`, then rename into
  `releases/<release-id>` (idempotent when the same digest already exists).
- `preflight --plan plan.json`: read-only, returns JSON:
  - the current release (from `active-release.json` or detected layout) and its drift
  - the rendered env diff and missing operator keys
  - `docker compose config --quiet` for the target plus overlays
  - images to pull
  - migrations: the *target* API's new read-only command
    `Workspaces.Api migrations status --json` runs as a one-off container
    against the live database. It reads `public."__WorkspacesMigrationsHistory"`
    through the real `WorkspaceDbContext` and reports applied, pending and
    unknown-applied IDs. Unknown-applied IDs mean a downgrade, which is refused.
    A missing history table is distinguished from an empty one.
  - PostgreSQL major-version change (refused)
  - unseal availability: the seed recovery record is present, or `apply` needs
    `--unseal-key-file`. Every deploy recreates OpenBao, because its
    `openbao.hcl` bind source moves with the release directory. So unseal is
    always required, not only when the image changes.
  - nonterminal workspace operations (any status other than completed, failed
    or cancelled), and whether the current release supports maintenance mode
  - OpenBao seal state
  - free disk space against the estimated backup and image size
  - service health
- `run --plan plan.json`: the state machine below. It runs under
  `systemd-run --unit municode-deploy-<op> --collect` so it survives SSH loss.
  It holds `flock` on `deploy.lock` and writes `deploys/<op>/state.json`
  atomically *before* each side effect (intent) and after it (outcome). Every
  step is idempotent and resumable. Recovery decisions use the intent markers,
  so a crash between a side effect and its outcome record is treated
  pessimistically.
- `rollback --plan plan.json [--restore-database]`: see below.
- `status`: prints the state file and the unit state.

Logs go to `deploys/<op>/log` and contain only step names, exit codes and fixed
messages. Container output is never copied (gateway logs can contain bearer
capabilities).

### Apply state machine

All Compose calls use `--project-name municode-workspaces`, the resolved
release directory and every overlay. Jobs run as
`docker compose run --rm --no-deps <job>`, one at a time with each exit status
checked, and services start with `docker compose up -d --no-deps <explicit list>`.
`--abort-on-container-exit` is never used, because it stops sibling containers
when a dependency job exits, and dependency chains are never left to `up`.

| Phase | Actions | On failure |
| --- | --- | --- |
| `prepare` | Pull target image digests. Render target `.env`. Under the deploy lock, recompute the preflight and effective-config hash (see [approval](#approval-digest)) and require them to match the approved plan. | Nothing changed, so stop. |
| `drain` | Enter maintenance mode. Wait, bounded by `--drain-timeout` (default 15 min), until no workspace operation is nonterminal while the worker keeps running. Then stop `caddy`, `worker`, `workspace-gateway`, `api` and `kratos`, so PostgreSQL has no clients, and convert the layout if needed. On the legacy path without maintenance mode, stop `caddy` and `api` first, then require zero nonterminal operations, otherwise abort. | Exit maintenance and restart the previous release's stopped services (*auto-rollback A*). |
| `backup` | Write a matched snapshot to `backups/<op>/`: `pg_dumpall --globals-only`, plus `pg_dump -Fc` of `workspaces` and of `kratos`. Pause `openbao` and archive its volume, with unpause guaranteed in `finally` and on resume. Archive `caddy-data`, the secrets directory, `config/` and `active-release.json`. Write `backup-manifest.json` with SHA-256s and test-list each archive. With `--offhost-backup`, the CLI downloads the backup over pinned SSH into a protected local directory and verifies the hashes. | Auto-rollback A. |
| `migrate` | Record `migrationsStarted`. Run `database-initialize`, `kratos-migrate` and `api-migrate` from the target, in that order. | Restore the matched snapshot, then start the previous release (*auto-rollback B*). |
| `start` | Start the target's `openbao`, unseal it from the seed recovery record or from `--unseal-key-file` (streamed over SSH stdin and never stored), and wait for its health check. Then start `openbao-agent`, `kratos`, `api` and `workspace-gateway`. Probe `http://api:8080/health/ready` from the private network with the pinned Caddy image. Readiness requires no pending migrations. | Auto-rollback B. |
| `open` | Record `trafficMayHaveOpened` durably, *then* start `caddy`. The CLI verifies public HTTPS `/health`, `/health/ready`, `/health/version` (target revision), `/api/account/capabilities` and the dashboard `runtime-config.json`. | No automatic restore: users may have written data. Keep the target running and report `rollback` options. |
| `resume` | Record `workerResumed`, start `worker` and exit maintenance. | Same as `open`. |
| `commit` | Write `active-release.json` (previous becomes `previous`), point the symlink at the target, and prune releases (keep the active and previous) and backups (keep 3). | Retry is idempotent. |

**Maintenance mode** is a small API and worker feature delivered separately.
`Workspaces.Api maintenance enter|exit|status` sets a database flag. While it
is set, the API returns `503` with `Retry-After` for requests that would create
workspace operations. The worker keeps processing accepted operations until
they reach a terminal state, because operations release their lease and go back
to `pending` between steps. Zero leases therefore doesn't mean the work
finished, and an operation that crosses a release boundary could meet changed
step or evidence semantics. The drain waits for terminal states, not for lease
expiry.

Workspace VMs keep running throughout. Open T3 and preview sessions disconnect
while the gateway is down and reconnect afterwards.

### Failure and rollback

The backup is one matched snapshot: PostgreSQL databases, the OpenBao volume and
configuration are restored together, never PostgreSQL alone. Otherwise a
restored database could point at OpenBao secrets deleted after the backup, such
as a deleted provider account's credential.

Restore procedure: stop every database client, then
`DROP DATABASE … WITH (FORCE)` for `workspaces` and `kratos`. Recreate both with
the owners from `init-databases.sh` (roles and passwords are unchanged because
secrets are preserved, and the step verifies that). Run
`pg_restore --exit-on-error --single-transaction`, then restore the OpenBao
volume and config archives.

- **Auto-rollback A** (before `migrationsStarted`): start the previous release's
  services from its own project directory, unsealing OpenBao if it was
  recreated, and exit maintenance. No data changed.
- **Auto-rollback B** (`migrationsStarted` set, `trafficMayHaveOpened` not set):
  no user and no worker has touched the target, so the restore is lossless.
  Restore the snapshot, start the previous release and exit maintenance.
- **Manual rollback** (`trafficMayHaveOpened` set, including a crash during
  `open`): `deploy rollback <op>` restarts the previous release.
  - If migrations were applied, the old code can't run against the new schema.
    The command then refuses unless the operator passes
    `--restore-database --accept-data-loss-since <open timestamp>`.
  - If `workerResumed` is set, it also refuses until the operator passes
    `--accept-provider-reconciliation`. Before restoring, it exports the target
    database's operations and VMs created after the backup, because a restore
    would forget provider resources the worker created. The export goes to
    `backups/<op>/post-open-operations.json` so the operator can reconcile
    billable VMs by hand.
  - Rolling forward with a fixed release is the recommended path.

The local journal records every phase, so an interrupted CLI reattaches with
`resume`. The host agent remains the source of truth. A changed host
fingerprint, instance identity or plan digest stops the deploy before any host
command runs.

### Approval digest

The digest is SHA-256 over:

- the instance name and installation ID, and the pinned host fingerprint
- the current release identity, the target release identity and trust, and the
  agent `deployProtocol`
- the normalized preflight result: env diff (including preserved unknown keys),
  applied and pending migrations, image set and layout conversion
- the **effective-config hash**: the normalized `docker compose config` output
  for the target plus overlays, and the SHA-256 of every host file it
  bind-mounts or reads from outside the release tree (for example
  `config/workspaces.json` and policy files that overlays mount from `/etc`),
  with secret file *contents* excluded

`apply` recomputes everything under the deploy lock right before `drain`. Any
change since `plan` (an edited policy file, a new overlay, a migration applied
elsewhere, or a different staged digest) makes it refuse and print the new
digest, as seed does.

### Security

- The deploy uses only the instance's pinned SSH material, with seed's strict
  options (`StrictHostKeyChecking=yes`, pinned `known_hosts`, no agent
  forwarding).
- No secret reaches the workstation unless `--offhost-backup` is requested. In
  that case the backup lands in an owner-only directory (`SeedFiles` rules) and
  is never printed.
- The agent rejects symlinks in releases, verifies every component digest, and
  never deletes paths it doesn't own (`active-release.json`, staged trees and
  backups that carry its operation ID).
- `--trust local-build` is explicit. It is recorded on the host and shown in
  `instance show` and `/health/version`.

## Issue breakdown

Each item is a GitHub issue under the epic. Dependencies are listed.

1. **Release manifest deploy contract**: `deployProtocol`, `migrations`,
   `env-contract.json`, the `WORKSPACES_POLICY_FILE` mount, installer-written
   release keys, and `Workspaces.Api migrations list|status --json`.
   *(Medium.)*
2. **Running release identity**: `GET /health/version`. Depends on 1.
   *(Small.)*
3. **Maintenance mode**: API and worker drain flag plus the `maintenance`
   command. Independent. *(Medium.)*
4. **Local instance registry**: `instance list|show|adopt|add`, and seed
   registers on completion. Independent. *(Medium.)*
5. **Host deploy agent: stage, preflight and layout adoption**. Depends on 1.
   *(Large / high.)*
6. **Host deploy agent: run, rollback and backups**. Depends on 5. Uses 3
   when present. *(Large / high.)*
7. **CLI `workspaces deploy`**: release sources, orchestration, journal and
   public verification. Depends on 1, 2, 4, 5 and 6. `plan` and `status` can
   land first against 5. *(Large / high.)*
8. **Local release build script**: `scripts/delivery/build-release.sh`.
   Depends on 1. *(Medium.)*
9. **Operator docs and live validation**: an operator guide section, a live
   checklist on a disposable seeded instance, then the personal instance.
   Depends on 1–8. *(Medium, live operations.)*
