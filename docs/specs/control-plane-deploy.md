# Spec: deploy a new release to an existing control plane

Status: proposed. Municode has no external users yet, so this design carries
no backwards compatibility: existing instances are reinstalled, not migrated.
Tracking: epic #168, see the [issue breakdown](#issue-breakdown).

## Problem

`municode seed` installs a control plane once. After that, no supported path
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
municode deploy plan --instance dev --release latest --prerelease
municode deploy apply <operation> --approve <digest>
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
- Operator configuration (`operator.env` and the policy JSON) survives upgrades
  without manual merging.
- `seed` and `install.sh` install the deployable layout directly, so the first
  release and every later one go through the same host layout.

## Non-goals

- Zero-downtime or multi-host deploys. A deploy has a short maintenance window
  (target: under 5 minutes, excluding image pulls, which happen before the window).
- PostgreSQL major-version upgrades. Plan refuses them, and they need a separate
  procedure.
- Workspace VM image releases, which keep their own qualification and capture flow.
- Provider API calls. Deploy needs no provider credential.
- Changing the seed trust model. Seed still accepts only signed releases.
- Migrating instances installed before this feature, legacy layouts, operator
  Compose overlays, or releases without maintenance mode. Such instances
  (today dev.muni.dev and cloud3) are reinstalled once, by `seed` or by
  `install.sh`, with a release that has this feature.

## User experience

```text
$ municode instance show dev.muni.dev
Instance dev.muni.dev: api.dev.muni.dev, host 46.225.68.165 (pinned SHA256:…)
Release municode-v0.2.0-storage.2 (723df64), installed by seed

$ municode deploy plan --instance dev.muni.dev --release latest --prerelease
Resolved latest → municode-v0.2.0-acceptance.7 (published 2026-11-24, source 1a2b…)
Verified Sigstore identity, manifest, checksums and component digests.
Staged on host (no service changed).
Current:   municode-v0.2.0-storage.2 723df64
Target:    municode-v0.2.0-acceptance.7 1a2b3c4
Migrations: 4 pending (Postgres): AddBudgetPeriodsAndScopes, …
Images:    api/worker/gateway payload changed; kratos, openbao, postgres, caddy unchanged
Env:       +MUNICODE_NEW_SETTING (release default), no operator input required
Policy:    /etc/municode/config/municode.json (unchanged)
Work:      0 nonterminal workspace operations; 3 workspace VMs keep running
Unseal:    host unseal key present
Downtime:  API, dashboard and gateway unavailable for about 2–4 minutes
Backup:    /var/lib/municode/backups/<operation> (≈180 MB, 31 GB free)
Approval digest: 9f0c…
Run: municode deploy apply 5be1… --approve 9f0c…

$ municode deploy apply 5be1… --approve 9f0c…
[prepare]   pulled 0 images                           ✓
[drain]     maintenance on, 0 nonterminal operations   ✓
[backup]    matched snapshot (postgres, openbao, caddy, secrets, config) ✓
[migrate]   kratos ✓  municode (4) ✓
[start]     openbao unsealed ✓  internal /health/ready ✓
[open]      https://api.dev.muni.dev/health ✓  dashboard ✓  version 1a2b3c4 ✓
[resume]    worker started, maintenance off            ✓
Deployed municode-v0.2.0-acceptance.7. Previous release kept for rollback.
```

Commands (all accept `--json`):

| Command | Effect |
| --- | --- |
| `municode instance add <name> --ssh <destination> [--identity-file <path>] [--confirm-host-fingerprint SHA256:…]` | Registers any instance the workstation can reach over SSH: seeded, installed by `install.sh`, or on any provider. Pins the host key and probes the host read-only. |
| `municode instance list` / `show <name>` / `remove <name>` | Manages the local registry. `show` also probes the host read-only over pinned SSH. |
| `municode deploy plan --instance <n> (--release <tag> \| --release latest [--prerelease] \| --bundle-dir <dir> --trust local-build)` | Verifies, uploads and stages the bundle. Runs a read-only preflight. Prints the diff and an approval digest. |
| `municode deploy apply <op> --approve <digest> [--unseal-key-file <path>] [--drain-timeout <dur>] [--offhost-backup <dir>]` | Runs the host deploy agent and follows its progress. |
| `municode deploy status <op>` | Local journal plus a live host-agent state read over pinned SSH. |
| `municode deploy resume <op>` | Reattaches after a lost connection, or continues an interrupted agent. |
| `municode deploy rollback <op> [--restore-database --accept-data-loss-since <timestamp>] [--accept-provider-reconciliation]` | Returns to the previous release. See [rollback](#failure-and-rollback). |

Any instance is deployable. The only requirement is an SSH connection from the
workstation, as `root` or as a user with passwordless `sudo`, to a host whose
control plane was installed by a release with this feature (see
[host layout](#host-layout)). Seed is one way to get there, and seed completion
calls `instance add` for you with its own key and pinned host key, named after
the dashboard hostname (`--instance-name` overrides it).

`instance add`:

- `--ssh <destination>` is anything `ssh` accepts: `user@host`, `host:port`, or
  a `Host` alias from `~/.ssh/config`. Authentication uses the operator's normal
  SSH setup (agent, config, `--identity-file`). The CLI stores no private key for
  added instances, only the destination and the identity-file path if one was given.
- The host key is pinned at registration into the instance's own `known_hosts`.
  If `--confirm-host-fingerprint` is given it must match. Otherwise the CLI
  shows the fingerprint and asks for interactive confirmation (refused
  non-interactively), as seed does for its independent fingerprint check. From
  then on every connection uses `StrictHostKeyChecking=yes` against that file, so
  a changed host key stops the deploy.
- The read-only probe confirms sudo access, reads `active-release.json` and the
  installation ID, and reads the dashboard, API and gateway host names from
  `operator.env`, so the operator doesn't type them. A host without
  `active-release.json` is rejected with "reinstall with `install.sh` from a
  release that supports deploy".

## Design

### Naming: Municode everywhere

The product is **Municode**. "Workspaces" survives only as the domain noun for
development workspaces (the coding VMs). Before the deploy work starts, the
repository is renamed so that the names in this spec are the real ones. There is
no backwards compatibility: old names aren't aliased, old releases aren't
deployable, and existing instances and workspace images are reinstalled.

Rule: a name changes when "Workspaces" is used as the *product* or *component*
prefix. It stays when it means development workspaces.

| Area | Before | After |
| --- | --- | --- |
| .NET projects, assemblies, namespaces | `Workspaces.Api`, `.Cli`, `.Common`, `.Gateway`, `.Worker`, `Workspaces.*.Tests`, `Workspaces.Tests` | `Municode.Api`, `.Cli`, `.Common`, `.Gateway`, `.Worker`, `Municode.*.Tests`, `Municode.Tests` |
| Solution | `Workspaces.slnx` | `Municode.slnx` |
| Environment variables | `WORKSPACES_*` (`.env`, tests, scripts) | `MUNICODE_*` |
| .NET config keys | `ConnectionStrings__Workspaces` | `ConnectionStrings__Municode` |
| Database | `workspaces` database and role, `__WorkspacesMigrationsHistory` | `municode`, `__MunicodeMigrationsHistory` |
| Policy file | `workspaces.json` | `municode.json` |
| Host paths | `/opt/municode/workspaces`, `/etc/municode/workspaces/{secrets,…}`, `/etc/workspaces/` | `/opt/municode/{releases,current}`, `/etc/municode/{secrets,config}`, `/var/lib/municode/` |
| Compose | project `municode-workspaces`, labels `com.municode.workspaces.*` | project `municode`, labels `com.municode.*` |
| Images and archives | `ghcr.io/muniperez/municode-workspaces-{api,worker,gateway}`, `municode-workspaces-X.Y.Z-*.tar.gz` | `ghcr.io/muniperez/municode-{api,worker,gateway}`, `municode-X.Y.Z-*.tar.gz` |
| Release tags and workflows | `workspaces-vX.Y.Z`, `workspaces-release.yml`, `workspaces-images.yml` | `municode-vX.Y.Z`, `municode-release.yml`, `municode-images.yml` (Sigstore identity follows) |
| CLI | `municode` binary plus a `workspaces` wrapper, `municode workspaces seed` spelling | `municode` only. `municode workspaces …` stays as the development-workspace command group |
| HTTP headers and cookies | `X-Workspaces-*`, `__Host-workspaces-*` | `X-Municode-*`, `__Host-municode-*` |
| Workspace VM image | `workspaces-*.service`, `workspaceswork.slice`, `/opt/workspaces-*`, `/var/lib/workspaces-*`, `/run/workspaces-*`, `/etc/workspaces-*`, `.workspaces-volume.json`, `/usr/share/workspaces` | `municode-*` equivalents, `municodework.slice` |
| Docs and site prose | "Municode Workspaces" | "Municode" |

Kept: `DevelopmentWorkspace*` types, `development_workspaces` tables,
`/api/development-workspaces`, the `/workspaces` directory inside VMs, the
`templates/development-workspaces` directory, the `workspace-gateway` service,
the dashboard's Workspaces pages, and `municode workspaces …` commands.

Version: the first release after the rename is `municode-v0.2.0-<label>.1`.
`--release latest` considers only `municode-v*` tags.

Commands are top-level verbs of the `municode` executable, next to `seed`,
`login` and `workspaces`. The release's `workspaces` wrapper passes arguments
through, so `workspaces deploy` works too.

### Components

```text
workstation                              control-plane host (root or sudo over pinned SSH)
───────────                              ────────────────────────────────────────
municode CLI                             /opt/municode/releases/<release-id>/   immutable trees
 ├─ instance registry                    /opt/municode/current → releases/<active>     (symlink)
 ├─ release source (signed | latest |    /etc/municode/secrets/      unchanged
 │   local-build)                        /etc/municode/config/       operator-owned
 ├─ deploy journal (<config>/deploy/<op>)     operator.env, municode.json
 └─ SSH transport (pinned host key)      /var/lib/municode/
                                             active-release.json, deploy.lock,
                                             deploys/<op>/state.json, log, plan.json,
                                             backups/<op>/
                                         deploy_host.py  (shipped in the *target* bundle,
                                                          run under systemd-run)
```

### Release sources

1. **Signed tag** (`--release municode-vX.Y.Z[-pre]`). This reuses
   `SeedRelease.VerifyAsync`: Sigstore identity, manifest, checksum inventory,
   component digests and dependency digests. Trust is recorded as `github-sigstore`.
2. **Latest** (`--release latest [--prerelease]`). The CLI reads the GitHub
   releases API for `muniperez/municode`, keeps non-draft `municode-v*` tags
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
*target* bundle (`deploy_host.py`), and the CLI is only the transport and
orchestrator. The CLI requires the target's `release-manifest.json`
`deployProtocol` to equal its own, and otherwise refuses with "use the CLI from
the target release (`cli/<rid>/`)". There is one protocol version at a time, and
no negotiation.

### Release manifest additions (`deployProtocol: 1`)

`scripts/delivery/package.py` adds:

- `deployProtocol`: integer contract between the CLI and `deploy_host.py`.
- `migrations`: ordered EF migration IDs per provider (`postgres`, `sqlite`),
  produced at package time by a new API command
  (`Municode.Api migrations list --json`). The command reads them from the
  real `WorkspaceDbContext` model, whose migrations live in `Municode.Common`.
  This list is informational. Preflight decides with the live database (below).
- `envContract`: generated from a new `packaging/bundle/env-contract.json`. It
  assigns every `.env.example` key an owner:
  - `release`: image digests, `MUNICODE_SOURCE_URL`, `MUNICODE_RELEASE_*`.
    Always taken from the target.
  - `installer`: `MUNICODE_SECRETS_DIR`, `MUNICODE_WORKER_SSH_PUBLIC_KEY`.
    Written once at install, carried forward.
  - `operator`: host names, installation ID, passkeys and tunables. Stored in
    `operator.env`. A new key needs a declared default or explicit operator
    input. A key that isn't in the target contract is an error, not carried.

  A delivery test fails when `.env.example` and `env-contract.json` disagree.
- `compose.yml` mounts the policy from
  `/etc/municode/config/municode.json` (via
  `MUNICODE_POLICY_FILE`) for `api`, `api-migrate` and `worker`, instead of
  `./workspaces.json` (now `./municode.json`) from the release tree. The bundled `municode.json`
  becomes the template that install copies into `config/` once.

### Running release identity

The installer writes the release-owned keys `MUNICODE_RELEASE_TAG`,
`MUNICODE_RELEASE_SOURCE_REVISION` and `MUNICODE_RELEASE_TRUST` into `.env`. The API
exposes them at anonymous `GET /health/version` as
`{tag, sourceRevision, trust}`. The response contains no secrets or host details.
Deploy verification requires the public endpoint to report the target revision.
This closes the gap where an upgrade "succeeds" but stale containers keep
serving.

### Host layout

`seed` (through `seed-start.sh`) and `install.sh` write this layout from the
first install. Deploy only ever sees it.

- `/opt/municode/releases/<release-id>/` holds one extracted, verified, read-only
  bundle per release. `<release-id>` is the tag, or `local-<rev12>-<bundle12>`
  for local builds.
- `/opt/municode/current` is a symlink to the active release, for operator
  convenience. The agent always runs Compose with
  `--project-name municode --project-directory <resolved release path>`,
  so bind mounts point at an immutable tree. Volumes are keyed by project name
  and therefore carry over between releases.
- `/etc/municode/config/` holds the operator-owned `operator.env` and
  `municode.json`. The agent renders `<release>/.env` from the target's
  release keys, the installer keys and `operator.env`. Operator Compose overlays
  aren't supported. Settings that need one become product configuration.
- `/etc/municode/secrets/` is unchanged.
- `/var/lib/municode/active-release.json` records
  `{releaseId, tag, sourceRevision, bundleSha256, trust, activatedAt, previous}`.
  `install.sh` writes the first record.
- The host wrapper `/usr/local/sbin/municode-compose` runs Compose against the
  active release, for manual operations.

OpenBao is unsealed with `<secrets>/openbao-unseal-key` when it exists (seed
writes it). If the operator moved unseal material offline, as
`docs/operator.md` recommends, `apply` needs `--unseal-key-file`.

The agent refuses to run unless `active-release.json` exists and matches the
running containers' `com.docker.compose.project.working_dir` and
installation-ID labels. It never deletes paths it doesn't own.

### Host deploy agent (`packaging/bundle/deploy_host.py`)

The agent is Python 3 with stdlib only (already a host prerequisite), unit-tested
with a fake `docker` like `scripts/delivery/test_seed_start.py`. Its commands:

- `stage`: verify the uploaded archive digest, extract into
  `releases/.staging-<op>`, run `verify_bundle.py`, then rename into
  `releases/<release-id>` (idempotent when the same digest already exists).
- `preflight --plan plan.json`: read-only, returns JSON:
  - the current release from `active-release.json`, checked against the running containers
  - the rendered env diff and missing operator keys
  - `docker compose config --quiet` for the target
  - images to pull
  - migrations: the *target* API's new read-only command
    `Municode.Api migrations status --json` runs as a one-off container
    against the live database. It reads `public."__MunicodeMigrationsHistory"`
    through the real `WorkspaceDbContext` and reports applied, pending and
    unknown-applied IDs. Unknown-applied IDs mean a downgrade, which is refused.
    A missing history table is distinguished from an empty one.
  - PostgreSQL major-version change (refused)
  - unseal availability: `<secrets>/openbao-unseal-key` is present, or `apply`
    needs `--unseal-key-file`. Every deploy recreates OpenBao, because its
    `openbao.hcl` bind source moves with the release directory. So unseal is
    always required, not only when the image changes.
  - nonterminal workspace operations (any status other than completed, failed
    or cancelled)
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

All Compose calls use `--project-name municode` and the resolved
release directory. Jobs run as
`docker compose run --rm --no-deps <job>`, one at a time with each exit status
checked, and services start with `docker compose up -d --no-deps <explicit list>`.
`--abort-on-container-exit` is never used, because it stops sibling containers
when a dependency job exits, and dependency chains are never left to `up`.

| Phase | Actions | On failure |
| --- | --- | --- |
| `prepare` | Pull target image digests. Render target `.env`. Under the deploy lock, recompute the preflight and effective-config hash (see [approval](#approval-digest)) and require them to match the approved plan. | Nothing changed, so stop. |
| `drain` | Enter maintenance mode. Wait, bounded by `--drain-timeout` (default 15 min), until no workspace operation is nonterminal while the worker keeps running. Then stop `caddy`, `worker`, `workspace-gateway`, `api` and `kratos`, so PostgreSQL has no clients. | Exit maintenance and restart the previous release's stopped services (*auto-rollback A*). |
| `backup` | Write a matched snapshot to `backups/<op>/`: `pg_dumpall --globals-only`, plus `pg_dump -Fc` of `municode` and of `kratos`. Pause `openbao` and archive its volume, with unpause guaranteed in `finally` and on resume. Archive `caddy-data`, the secrets directory, `config/` and `active-release.json`. Write `backup-manifest.json` with SHA-256s and test-list each archive. With `--offhost-backup`, the CLI downloads the backup over pinned SSH into a protected local directory and verifies the hashes. | Auto-rollback A. |
| `migrate` | Record `migrationsStarted`. Run `database-initialize`, `kratos-migrate` and `api-migrate` from the target, in that order. | Restore the matched snapshot, then start the previous release (*auto-rollback B*). |
| `start` | Start the target's `openbao`, unseal it from `<secrets>/openbao-unseal-key` or from `--unseal-key-file` (streamed over SSH stdin and never stored), and wait for its health check. Then start `openbao-agent`, `kratos`, `api` and `workspace-gateway`. Probe `http://api:8080/health/ready` from the private network with the pinned Caddy image. Readiness requires no pending migrations. | Auto-rollback B. |
| `open` | Record `trafficMayHaveOpened` durably, *then* start `caddy`. The CLI verifies public HTTPS `/health`, `/health/ready`, `/health/version` (target revision), `/api/account/capabilities` and the dashboard `runtime-config.json`. | No automatic restore: users may have written data. Keep the target running and report `rollback` options. |
| `resume` | Record `workerResumed`, start `worker` and exit maintenance. | Same as `open`. |
| `commit` | Write `active-release.json` (previous becomes `previous`), point the symlink at the target, and prune releases (keep the active and previous) and backups (keep 3). | Retry is idempotent. |

**Maintenance mode** is a small API and worker feature delivered separately.
`Municode.Api maintenance enter|exit|status` sets a database flag. While it
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
`DROP DATABASE … WITH (FORCE)` for `municode` and `kratos`. Recreate both with
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
  applied and pending migrations and the image set
- the **effective-config hash**: the normalized `docker compose config` output
  for the target, and the SHA-256 of `config/municode.json` and
  `operator.env`

`apply` recomputes everything under the deploy lock right before `drain`. Any
change since `plan` (an edited policy file, a migration applied elsewhere, or a different staged digest) makes it refuse and print the new
digest, as seed does.

### Security

- The deploy uses the instance's pinned `known_hosts` with seed's strict
  options (`StrictHostKeyChecking=yes`, no agent forwarding). Seeded instances
  use the seed key. Added instances use the operator's own SSH authentication.
- On a non-root user, every host command runs through `sudo -n`. Interactive
  sudo prompts aren't supported.
- Uploads go to a fresh `mktemp -d` directory owned by the SSH user. `stage`
  (run as root) verifies the archive digest before extracting it, so the upload
  location isn't trusted.
- No secret reaches the workstation unless `--offhost-backup` is requested. In
  that case the backup lands in an owner-only directory (`SeedFiles` rules) and
  is never printed.
- The agent rejects symlinks in releases, verifies every component digest, and
  never deletes paths it doesn't own (`active-release.json`, staged trees and
  backups that carry its operation ID).
- `--trust local-build` is explicit. It is recorded on the host and shown in
  `instance show` and `/health/version`.

## Issue breakdown

Each item is a GitHub issue under epic #168. Dependencies are listed. The
rename lands first, so later issues use the new names.

- **R1. .NET identity rename** (#179): projects, assemblies, namespaces,
  solution, Dockerfiles and project paths. No dependencies. *(Medium,
  mechanical.)*
- **R2. Control-plane and release contract rename** (#180): env vars, config
  keys, database, policy file, host paths, Compose, images, tags, workflows,
  CLI wrapper, control-plane headers, docs and site. Depends on R1. *(Large.)*
- **R3. Workspace VM image contract rename** (#181): units, slice, VM paths,
  volume markers, VM-facing headers and cookies, with their worker and gateway
  constants. Depends on R1. Runs in parallel with R2. *(Medium.)*

Deploy issues 1, 2, 5, 6, 7, 8, 9 and 10 depend on R2 (and R3 for 9). Issues 3
and 4 depend only on R1.


1. **Release manifest deploy contract** (#169): `deployProtocol`, `migrations`,
   `env-contract.json`, the policy mount from `config/`, installer-written
   release keys, and `Municode.Api migrations list|status --json`.
   *(Medium.)*
2. **Running release identity** (#170): `GET /health/version`. Depends on 1.
   *(Small.)*
3. **Maintenance mode** (#171): API and worker drain flag plus the
   `maintenance` command. Independent. *(Medium.)*
4. **Local instance registry** (#172): `instance add|list|show|remove` for any
   SSH-reachable host, and seed registers on completion. Independent.
   *(Medium.)*
5. **Host deploy agent: stage and preflight** (#173). Depends on 1 and 10.
   *(Medium.)*
6. **Host deploy agent: run, rollback and backups** (#174). Depends on 3 and
   5. *(Large / high.)*
7. **CLI `municode deploy`** (#175): release sources, orchestration, journal
   and public verification. Depends on 1, 2, 4, 5 and 6. *(Large / high.)*
8. **Local release build script** (#176): `scripts/delivery/build-release.sh`.
   Depends on 1. *(Medium.)*
9. **Operator docs and live validation** (#177): deploy to a seeded and to an
   `install.sh` instance registered with `instance add`. Reinstall the personal
   instance with a release that has this feature, then deploy a newer one to it.
   Depends on 1–8 and 10. *(Medium, live operations.)*
10. **Deployable host layout in seed and `install.sh`** (#178): install
    into `releases/<id>`, the symlink, `config/` and `active-release.json`.
    Depends on 1. *(Medium.)*
