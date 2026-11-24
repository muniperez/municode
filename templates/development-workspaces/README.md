# Portable development VM runtime (protocol 1)

Opt-in issue #110 implementation, **not a published/qualified provider image**. Python 3.12,
Ubuntu 24.04, linux/amd64, systemd/cgroup v2, existing ext4. Default tests need only Python
and Git; they never mount disks, contact providers, install tools, or start T3.

```sh
templates/development-workspaces/test.sh
```

Prepared images seed the supported T3 clone/add-folder default to
`/workspace/repos` without overwriting an explicit user choice. Gateway sessions
grant `source-control:write` for browser Git URL cloning and `filesystem:read`
for destination browsing, alongside the existing terminal and orchestration
read/operate scopes. This does not change named New Project's hard-coded parent
in pinned T3. The project defaults use the retained workspace root.

The root-only guest issuer allowlists these six scopes (subsets remain valid for
read-only qualification fixtures). T3 v0.1.9 enforces RPC scopes server-side:
clone requires source-control write; browsing/metadata requires filesystem read;
adding projects requires orchestration operate. Its Clone action already explains
missing write/add-project permission, separately from destination and Git failures.
No T3 fork or filesystem write, settings, provider, access-management or relay
scope is needed. Source-control write also authorizes other Git/PR mutations
using credentials available to the guest; it is not a public-clone-only grant.

Launch, redemption and relay resolution still require `WriteWorkspaces`:
owner/admin/developer membership or a workspace-write API key. Viewers and
read-only keys cannot launch T3. The dashboard uses the same role boundary.
The gateway injects the generation-bound guest bearer, not a caller-supplied scope
list, over the private tunnel. T3 runs as `coding` inside the workspace VM with
`NoNewPrivileges`, a read-only system, writable workspace/scratch and private temp.
Filesystem read is not a `/workspace/repos` sandbox: it permits browsing other
guest paths readable by `coding`, including user files. It cannot traverse out of
the VM into the control plane or another VM; guest root-only runtime/SSH secrets
remain protected by Unix permissions. Existing terminal operate already exposes
the same coding-user files and guest credentials. Keep operator credentials out
of the guest; this change adds no credential or VM-boundary bypass.

Outputs: ignored `.local/artifacts/runtime/`. Fixtures use two real independent
clones and a local bare remote; injected process/mount/encryption/transport adapters test
failure handling. This is not a real ext4, systemd-freezer, age, or live restore qualification.

### Safe native qualification diagnostics

If a disposable native qualification stops at `T3_RESTART_LEFT_UNKNOWN_WORK`, retain only
the probe/source identity and the short allowlisted work-inventory reason code. Do not retain
runtime logs, process arguments, executable or cgroup paths, SQL, usernames, or secrets.
The native probe polls only this advisory signal for a bounded interval and rechecks VM,
T3, retained-volume, and Ready identities on every observation. Repeat a clean native
qualification before capturing an image.

Optional [T3 Connect enrollment](../../docs/setup/runtime.md#runtime-interfaces) uses the existing
primary VM and managed T3 unit, a private memory-only approval page on loopback
774, and a distinct root-only management credential. Connect adds no relay scopes
to normal gateway sessions. Pending approval and activation fence shutdown. Activation results
are generation-bound root-owned state, survive the managed restart and page refresh,
and report only allowlisted diagnostics. Both T3 and the enrollment CLI explicitly
resolve the image's pinned cloudflared executable. Its updated native probe and
hosted cross-device/lifecycle acceptance remain unqualified; never run the
upstream service installer or show device approval in T3 terminal history.

## Prepared image

`image/manifest.lock.json` pins the Ubuntu cloud-image **serial and SHA256**, immutable
Ubuntu apt snapshot, Node 24.11.1, .NET SDK 10.0.100, Codex 0.169.0 (npm integrity lock),
the coding agents listed under [Coding agents](#coding-agents), GitHub CLI **2.83.1** and GitLab CLI **1.74.0** (official linux/amd64 release
archives, SHA256-verified before copying only their executables to `/usr/local/bin`),
T3 with its exact release, upstream commit and artifact digests, and Chromium shared libraries. The default supplies the coding environment, not an
application stack: it installs no application database server/client, creates no
application database/roles, and ships no emulator or preview canary application.
A browser executable or additional application SDK is intentionally not
silently downloaded; register an explicit pinned workload extension if needed.

### Retrieving the pinned Ubuntu base image

The Noble `20251001` release URL can redirect to Canonical's public archive over
HTTP; the archive's HTTPS listener can time out. Canonical's released
[simplestreams metadata](https://cloud-images.ubuntu.com/releases/streams/v1/com.ubuntu.cloud:released:download.json)
and archive `SHA256SUMS` independently report the lock's SHA-256
`02dc186dc491514254df58df29a5877c93ca90ac790cc91a164ea90ed3a0bb05`;
simplestreams also identifies a **620695552-byte QCOW2**. Do not change the source
serial or digest, substitute another image, or treat a redirect/HTML body as an image.

Run this in a download directory with `curl` and GNU `sha256sum`. It first tries
the real release URL and archive over HTTPS with normal certificate validation,
HTTPS-only redirects and bounded timeouts. If both fail, it explicitly fetches
only this public artifact from Canonical's HTTP archive, without following
redirects or sending credentials. This is not a general TLS-policy relaxation:
never use HTTP for secrets, authenticated downloads or other build inputs.

```sh
(
  set -eu
  image=ubuntu-24.04-server-cloudimg-amd64.img
  release=https://cloud-images.ubuntu.com/releases/noble/release-20251001
  archive=cloud-images-archive.ubuntu.com/releases/noble/release-20251001
  digest=02dc186dc491514254df58df29a5877c93ca90ac790cc91a164ea90ed3a0bb05
  test ! -e "$image" || { echo "Refusing to overwrite $image" >&2; exit 1; }
  tmp=$(mktemp -d ./ubuntu-download.XXXXXX)
  trap 'rm -f "$tmp/image.part"; rmdir "$tmp"' EXIT
  if ! curl --disable --fail --show-error --location --proto '=https' --proto-redir '=https' \
      --connect-timeout 15 --max-time 900 --output "$tmp/image.part" "$release/$image"; then
    if ! curl --disable --fail --show-error --location --proto '=https' --proto-redir '=https' \
        --connect-timeout 15 --max-time 900 --output "$tmp/image.part" "https://$archive/$image"; then
      echo "HTTPS unavailable; fetching only the pinned public Canonical artifact over HTTP" >&2
      curl --disable --fail --show-error --proto '=http' --connect-timeout 15 --max-time 900 \
        --output "$tmp/image.part" "http://$archive/$image"
    fi
  fi
  printf '%s  %s\n' "$digest" "$tmp/image.part" | sha256sum --check --strict -
  test "$(wc -c < "$tmp/image.part")" -eq 620695552 ||
    { echo "Unexpected image size; refusing disk use" >&2; exit 1; }
  mv "$tmp/image.part" "$image"
  printf 'Verified QCOW2: %s\n' "$image"
)
```

**SHA-256 verification against the existing lock is mandatory before any
decompression, conversion, import, boot or disk write.** A checksum/size failure
stops the example and removes the temporary download; never bypass it or adopt
a digest from the HTTP response. The `.img` is already QCOW2, not a compressed
archive. Retrieval alone does not qualify an image: re-run native image
qualification on the disposable builder before resolving #16/#18 or capture.

An operator must download and verify the specified base image, then boot it in a
**disposable isolated builder VM without workspace volumes**. No build script allocates
provider resources. Stage only this subtree at `/opt/workspaces-image-source` inside that
VM. These commands are **not for a development workstation/control-plane/shared host**:

```sh
# INSIDE THE DISPOSABLE BUILDER ONLY, as root:
printf '%s\n' DESTROY-DISPOSABLE-IMAGE-BUILDER > /etc/workspaces-disposable-image-builder
cd /opt/workspaces-image-source
python3 -B image/build.py install \
  --acknowledgement DESTROY-DISPOSABLE-IMAGE-BUILDER \
  --source-revision <exact-40-character-workspaces-commit> --builder-identity <operator-build-id>
python3 -B image/build.py generalize \
  --acknowledgement DESTROY-DISPOSABLE-IMAGE-BUILDER
# Power off via the builder's management console, then explicitly capture/register the image.
```

The destructive guard requires all of: exact acknowledgement, root-owned builder marker,
Ubuntu 24.04, actual `/etc/cloud/build.info` serial matching the pinned `20251001` **before
any package mutation**, x86_64 VM, no enrolled runtime, no mounted workspace or provider volume.
Generalization erases root/user homes, agent logins, build source, workspace and T3 data,
enrollment/runtime state, cloud-init seed/state/logs, machine IDs, SSH host keys and logs.
First boot regenerates identity/host keys through systemd/cloud-init. Do not execute more
interactive build commands after generalization. Capture only after power-off.

### Provider images (Hetzner, DigitalOcean, Vultr)

The same recipe produces the image for every supported provider; only base-image
import, capture and registration differ. Build in the provider account that will run
the workspaces. A provider's stock Ubuntu 24.04 image is accepted only if its
`/etc/cloud/build.info` serial matches the lock. The guard refuses any other serial
and a missing file, and Vultr's stock image has no `build.info`. DigitalOcean and
Vultr builders therefore boot an import of the **verified pinned cloud image**.

| Provider | Base image input | Builder | Capture | `ProviderImageId` |
| --- | --- | --- | --- | --- |
| Hetzner | Stock Ubuntu 24.04 x64 image only when its serial matches the lock | Server without volumes | Server snapshot after power-off | numeric image ID |
| DigitalOcean | Verified pinned QCOW2 imported as a custom image (`distribution` Ubuntu) | Droplet from that custom image, without volumes | Droplet snapshot after power-off | numeric image ID of the snapshot |
| Vultr | Pinned RAW derived from the verified QCOW2, imported as a snapshot from URL | Instance from that snapshot, without block storage | Instance snapshot after power-off | snapshot UUID |

#### Verify and derive the provider import file

First run the [pinned download](#retrieving-the-pinned-ubuntu-base-image). Then, on the
operator workstation, in this directory (Python 3.11+, and `qemu-img` from `qemu-utils`
for Vultr), check the file against the lock before any upload:

```sh
python3 -B image/base_image.py verify-qcow2 ubuntu-24.04-server-cloudimg-amd64.img
# Vultr only: derive the RAW disk. About 3.5 GiB, sparse on most filesystems.
python3 -B image/base_image.py raw ubuntu-24.04-server-cloudimg-amd64.img ubuntu-24.04-20251001-amd64.raw
```

`base_image.py` makes no provider or network calls. It refuses a symlink, a size or
SHA-256 mismatch with `os.size`/`os.sha256`, a non-QCOW2 header, a QCOW2 backing file
or encryption, a virtual size other than `os.raw.size`, and an existing output. The RAW
is converted in a private staging directory next to the output and published only if
it matches `os.raw.size` and `os.raw.sha256`. That digest is the guest-visible disk of
the pinned QCOW2. It was derived with `qemu-img convert` and confirmed by an independent
QCOW2 decoder, so it does not depend on the `qemu-img` version. Each command prints
a JSON attestation with the serial and both digests. Keep that attestation with the
build record. Never edit the lock to match a different result.

#### Import the base image

Upload only the verified file to HTTPS object storage that you control. Prefer a
short-lived pre-signed URL. Treat that URL as a secret: keep it out of issues, logs and
shell history, and delete the object once the import finishes. Providers do not check
the digest during import. Don't import from the Canonical URL, because it can redirect
to plain HTTP. The builder's serial guard is the in-guest check that the pinned image
booted.

**DigitalOcean.** Import the QCOW2 in the region that will build and run workspaces,
for example
`doctl compute image create t3-base-20251001 --image-url "$URL" --region <region> --image-distribution Ubuntu`,
or upload it from the control panel. Wait for the image status `available`. Create the
builder droplet from it with an SSH key and no volumes. DigitalOcean requires an SSH key
for custom images and documents that NoCloud listed before ConfigDrive breaks droplets.
Before running `build.py`, confirm on the builder that `cloud-init status --long`
completed, `cloud-id` reports `digitalocean` or `configdrive`, and the key was injected.
Otherwise stop and destroy the builder. Delete the base custom image and the uploaded
object when the workspace snapshot is registered.

**Vultr.** Run
`vultr-cli snapshot create-url --url "$URL" --description t3-base-20251001` and omit
`--uefi`. Wait for the status `complete`. Create the builder instance from that snapshot
without block storage. Before running `build.py`, confirm on the builder that
`cloud-init status --long` completed, `cloud-id` reports `vultr`, and
`/etc/cloud/build.info` reports serial `20251001`. Otherwise stop and destroy the
builder. Delete the base snapshot and the uploaded object when the workspace snapshot
is registered.

Then run the disposable-builder commands above, power off, and capture.

#### Register the captured image

Before registration, confirm that the provider reports the captured image as an
available x86 Ubuntu snapshot. The worker refuses anything else. On DigitalOcean,
`doctl compute image get <id>` must show the distribution `Ubuntu`. On Vultr, the
snapshot's `os_id` from `vultr-cli snapshot get <id>` must map to `family` `ubuntu`
and `arch` `x64` in `vultr-cli os list`. If an imported lineage reports any other OS
or an unknown one, stop. Don't relabel or spoof it. Live qualification of both import
paths is tracked in #32.

Each `DevelopmentWorkspaces:Images:<image-key>` entry names exactly one
`ProviderAccountId` and `ProviderImageId`. Register one image key per provider account
(for example `t3-connect-2026-11-hetzner`, `…-digitalocean`, `…-vultr`) with the same
`ManifestSha256`, and list the regions where the image is available in `AllowedRegions`.
DigitalOcean custom images and snapshots are regional; transfer them first if needed.
Vultr snapshots span all regions. Copy the
installed manifest's `agents` array into the entry's `Agents` so the API, CLI and
dashboard show what the image contains:

```json
"Agents": [
  {"Id": "codex", "Name": "Codex", "Version": "0.169.0"},
  {"Id": "claude", "Name": "Claude Code", "Version": "2.1.328"}
]
```

Provider differences that affect isolation and operations:

* **Volumes.** Hetzner and DigitalOcean create ext4 volumes; the worker only probes them.
  Vultr block storage is created raw, so the worker formats it with `bootstrap.py
  volume-format` **only** when the volume was created by the same operation and no
  filesystem is present; an existing volume without a filesystem blocks instead of being
  formatted. Stable devices are `scsi-0HC_Volume_<id>` (Hetzner), `scsi-0DO_Volume_<name>`
  (DigitalOcean) and `virtio-<first 20 characters of the mount ID>` (Vultr).
* **Firewalls.** Hetzner and DigitalOcean firewalls allow inbound SSH only from the
  management addresses and restrict egress to HTTP(S), DNS, NTP and Cloudflare
  Tunnel's TCP/UDP port 7844 (HTTP/2/QUIC). Port 443 alone cannot carry the
  relay tunnel. Existing firewalls are not automatically migrated: rollout needs
  an explicitly authorized firewall update or a newly provisioned disposable VM.
  Vultr firewall
  groups are **inbound only**: the same SSH restriction applies, but egress is not
  provider-filtered. Treat Vultr workspaces as having weaker network isolation.
* **Root password e-mail.** DigitalOcean e-mails a root password when no provider SSH
  key is attached. The image's SSH configuration disables password authentication, and
  management access uses only the cloud-init key.

Installed manifest: `/opt/workspaces-workspace/image-manifest.json`, including actual dpkg
versions, source revision, builder identity, artifact integrity and runtime CLI checks.
The builder also writes `provenance.installedImmutableInputs.files`: canonical root-owned
installed paths and their actual SHA-256 hashes for the curated runtime files, storage
inputs, systemd units, npm locks, pinned executable entry points and T3 telemetry helper.
Source-managed copies must match the staged source bytes exactly before the manifest is
written. This evidence is checked by native qualification, not a qualification pass.
This is a reproducible **input/toolchain recipe**, not a claim of byte-identical cloud
snapshot disks. Package auto-updates are masked: patch by building/registering a new pinned
image, not by silently changing an existing image. Toolchain installation happens only
in the builder, never on a workspace boot.

The installed binaries and `systemd/` units use UID/GID **2000:2000** (`coding`, no SSH login).
The workspace root is explicitly traversable (0755). User-created application paths
and data belong to the user; no database-engine directories or accounts are provisioned.
No recursive permission changes occur.
The pinned T3 empty Foundation database is recognized only with zero events, projections
and outbox entries. Native x86-64 stdin-only `pselect6` additionally requires reading and
rechecking the actual fd set; a freezer-obscured wait requires its cgroup's frozen proof.
Neither adapter enables terminal-input observation or qualifies the production gateway.
The pinned T3 read-only resource monitor is recognized separately only by its exact
release digest, immutable root-owned code, argument-free invocation, direct T3 parent,
registered service cgroup and stdio IPC, with no TTY or children. Other
children, agents and helpers remain unregistered work. Tool archives install root-owned.
The pinned T3 CLI's graceful interrupted exit (130) is a successful systemd stop;
other nonzero exits still fail the clean-stop fence.
Only root key-based SSH is admitted, with no agent forwarding. Provider firewalls/network
rules **must** deny lateral VM/sibling/control-plane-private access and public service
ports; this filesystem runtime cannot prove network isolation. Never forward a worker SSH
agent/key or provider/control-plane credentials into coding processes.

## GitHub Copilot CLI in T3

The recipe pins the official Linux x64 Copilot CLI **1.0.114** release archive and SHA-256
alongside the existing Codex installation. `/usr/local/bin/copilot` is a root-owned launcher
for `/opt/workspaces-copilot/copilot`, with automatic updates disabled so a user cache cannot
silently replace the qualified version. Terminal commands and the T3 service use this same
executable and coding user's home, `/workspace/home`. No login is included in the image.

Before each T3 start, `copilot_provider.py configure` runs as `coding`. It adds
`workspaces-copilot` to `/workspace/t3/userdata/settings.json`, using the pinned T3
`acpRegistry` driver with `source: local`, `commandPath: /usr/local/bin/copilot`, and
literal `commandArgs: ["--acp", "--stdio"]`. This is the documented **Local ACP command**
path, not an invented Copilot driver. The card is named **GitHub Copilot CLI** under
**Settings → Providers**. Available models come from the agent after sign-in; none are
hard-coded or promised before authentication. Its only environment override is the nonsecret
`COPILOT_AUTO_UPDATE=false`, also applied by T3 to the agent's sign-in command.

Setup preserves unrelated settings, Codex, native Copilot credentials, and T3 threads.
Reapplying it leaves an existing local Copilot ACP instance (including a custom ID, home,
or disabled state) untouched instead of duplicating it. Invalid settings produce a sanitized
setup error in the service journal. Otherwise each provider is decided on its own and logs one
`T3 provider <id> (<driver>): …` line to the journal (`journalctl -u workspaces-t3`): `seeded`,
`already configured as <id>`, `skipped, missing binary <path>`, or `error, <reason>`. A
conflicting managed ID or an unusable agent binary fails only that provider; the others are
still seeded, and setup exits nonzero so the failure is visible. No pre-start failure prevents
Codex/T3 from starting. Repair the settings or ID before retrying setup.
Do not run `configure` against a live T3 settings writer: use the managed service's next
start, after safely stopping active work. Existing workspaces require an explicit prepared
image rollout preserving their workspace volume; this recipe does not patch running hosts.

### Headless sign-in and diagnostics

Use an authorized, **unrecorded management SSH terminal**, not a CI log or a recorded T3
terminal (T3 retains terminal output). Do not use the provider's in-app terminal sign-in
for this private flow. On the workspace host:

```sh
# As the workspace operator; no tokens in command arguments or environment.
umask 077
runuser -u coding -- env -i HOME=/workspace/home PATH=/usr/local/bin:/usr/bin:/bin \
  copilot login --device-code
# Separately check the actual pinned stdio transport, without opening a thread:
runuser -u coding -- env -i HOME=/workspace/home PATH=/usr/local/bin:/usr/bin:/bin \
  python3 -B /opt/workspaces-workspace/copilot_provider.py probe
```

Open the displayed GitHub URL on your own browser and enter the short-lived code privately.
An existing login in the coding user's home is reused; do **not** log out, delete
`~/.copilot`, replace the home, or sign in as root to enable the provider. On a headless
host without a credential store, Copilot may store its token under `~/.copilot`; treat the
entire home and workspace backups as sensitive. Never capture device codes/tokens in build
logs, screenshots, support bundles or exported provider configuration.

The probe reports `Compatible` only after an actual ACP v1 `initialize` response identifies
the pinned Copilot version. It explicitly reports authentication as **NotChecked**: it does
not send prompts, create sessions, export credentials, or assert model access. Missing,
incompatible, malformed and unresponsive installations report actionable `SetupRequired`;
rebuild/roll out the pinned image instead of installing an unverified latest CLI. When
signed out, the pinned agent advertises `copilot-login` terminal authentication; T3's ACP
provider health/sign-in state remains authoritative. Complete the private management-terminal
login above and retry/reconnect the provider before choosing a model.

Automatic managed setup needs no T3 bearer. Normal gateway session scopes remain unchanged;
do not grant settings-management access to routine coding sessions. Any manual provider
configuration must use a separately authorized T3 settings session.

Qualification references:
[pinned T3 local-command guide](https://github.com/pingdotgg/t3code/blob/24e77473ecf41bc1f7142529c7911c1c1aae1d76/docs/user/providers-acp.md#add-a-local-command),
its `packages/provider-acp-registry/src/settings.ts` and `packages/contracts/src/providerInstance.ts`,
and [GitHub ACP stdio reference](https://docs.github.com/en/copilot/reference/copilot-cli-reference/acp-server).
The issue's guide URL omits `c1` from the commit; the manifest's full 40-character SHA is used.

Deterministic tests cover seeding/reapplication, existing settings/login/thread preservation,
transport/version errors and artifact installation. CLI 1.0.114 `--acp --stdio` initialization
was also exercised locally against the installed release. The opt-in disposable-image native
harness checks seeded settings and stdio initialization as UID 2000, but **has not been run
for this recipe**. No new provider image is qualified by these tests.

**Remaining hosted acceptance:** build/generalize/register the image, start a fresh workspace,
use a sanctioned authenticated remote browser to confirm the provider card and sign-in state,
select an agent-reported model, send a project prompt and a follow-up in the same thread.
Also verify Codex chat, then reapply setup/restart safely on an already signed-in volume and
confirm both providers and existing threads still work. No authenticated browser prompt,
follow-up, or hosted Codex result is claimed without that session.

## Coding agents

All agents are pinned in `image/manifest.lock.json` and installed root-owned with
self-update disabled; patch them by building a new image. The manifest's `agents` array
records the IDs, names and versions shown by the API, CLI and dashboard.

| Agent | Version | Install | T3 provider |
| --- | --- | --- | --- |
| Codex | 0.169.0 | npm lockfile integrity (`tools/package-lock.json`) | built-in `codex` |
| GitHub Copilot CLI | 1.0.114 | release archive SHA-256 | `workspaces-copilot` (local ACP, above) |
| Claude Code | 2.1.328 | native `@anthropic-ai/claude-code-linux-x64` package, npm lockfile integrity; `/usr/local/bin/claude` sets `DISABLE_AUTOUPDATER=1` | built-in `claudeAgent` (enabled by default, uses `claude` from `PATH`) |
| Antigravity | 1.4.5 | ACP server zip SHA-256 and size; only `agy_acp_server.par` and `localharness_external` are copied to `/opt/workspaces-antigravity` | `workspaces-antigravity` (`antigravity` driver, `binaryPath` set) |
| Muse Code | 1.4.24-R9131.1 | release binary SHA-256 and size at `/opt/workspaces-muse/muse`; `/usr/local/bin/muse` sets `MUSE_NO_AUTO_UPDATE=1` | `workspaces-muse` (`muse` driver, `binaryPath` set) |

T3 0.1.9 supports all of these natively. Its `antigravity` and `muse` drivers are disabled
by default, so `copilot_provider.py configure` adds an enabled instance for each one whose
pinned executable is a regular file the `coding` user can execute. A missing executable is
skipped and logged with its path; an unreadable install directory or non-executable binary is
logged as an error for that agent only. Any existing instance of the same driver, including a
disabled one, is the user's choice and is left alone. An occupied instance ID is reported in the
setup result without blocking T3 or the other providers. The build creates
`/opt/workspaces-antigravity` and `/opt/workspaces-muse` root-owned with mode 0755 regardless
of its umask, like `/opt/workspaces-copilot`. The `copilot-stdio-provider-setup` native probe
requires the Copilot, Antigravity and Muse instances to be enabled in T3 settings and their
binaries to be executable by `coding`. The Claude Code build verifies `claude --version`.
Antigravity and Muse are verified by digest and size only, because their version commands
need a signed-in home.

The image includes no agent logins. Sign in privately as `coding` (HOME=/workspace/home)
from an unrecorded management terminal, as for Copilot: `claude` (then `/login`) for Claude
Code and `muse login` for Muse. Choose the Antigravity sign-in method in **Settings →
Providers**. The three agent binaries add about 1.8 GB to the image.

## GitHub and GitLab source control

The T3 service explicitly uses `PATH=/usr/local/bin:/usr/bin:/bin` and
`HOME=/workspace/home`; its terminals inherit access to `gh` and `glab` as UID 2000.
No provider sign-in or Git credential setup runs during image build/bootstrap.
No provider tokens, account configurations, device codes, or test-repository data
belong in the image, manifest, native reports, CI output, or screenshots. Generalize
after qualification and before capture; never capture a workspace after user login.

On a fresh workspace, open **T3 → Settings → Source control** and refresh discovery.
Both providers should be available and signed out, not missing/unknown. From its terminal,
run `gh --version` and `glab --version`. Use the provider's sign-in action if offered by
the pinned T3 configuration. Otherwise an operator can provide a sanctioned,
**non-recorded interactive management session** running the CLI as the workspace user.
T3 persists terminal output, so do **not** display browser/device codes or enter credentials
in its recorded terminal. From the approved session, the operator launches one of:

```sh
setpriv --reuid=2000 --regid=2000 --clear-groups env -i \
  HOME=/workspace/home PATH=/usr/local/bin:/usr/bin:/bin \
  gh auth login --hostname github.com --git-protocol https --web
setpriv --reuid=2000 --regid=2000 --clear-groups env -i \
  HOME=/workspace/home PATH=/usr/local/bin:/usr/bin:/bin \
  glab auth login --hostname gitlab.com
```

The user completes the flow for their own account; never sign in as root or use an
operator/provider account. Follow the displayed browser/device instructions on your own browser; do not capture
the terminal/session or paste tokens into commands, chat, logs, or shell history.
Use only user-authorized accounts and minimal access to a designated test repository.
For headless GitLab, follow `glab auth login --help` for the supported interactive
browser/token choices; enter any token only into its hidden prompt, never an argument.
Refresh Source control and confirm the account/authentication state. Without printing
credentials, verify read access with `gh repo view OWNER/TEST-REPO` and
`glab repo view GROUP/TEST-REPO`. Do not push/create repositories as a qualification step.

When GitHub asks whether to authenticate **Git**, decline. Do not run
`gh auth setup-git`, change global `credential.helper`, or replace Workspaces's staged
repository-bound Git credentials. CLI sign-in is separate from the root-managed
`git-credential.py` helper, short-lived token expiry, configured repository allowlist,
push authorization, and Git-save gates; none are broadened by installing these tools.
CLI credentials can grant additional access as that user, so sign-in requires explicit
user authorization. The headless image has no assumed OS keyring: CLI credentials may
be stored in private files under `/workspace/home/.config/{gh,glab-cli}`. Treat retained
home/backup data as sensitive; do not copy it to another user/workspace or image.
Ephemeral workspaces do not retain these sign-ins after deletion.

The native qualification now requires missing-binary/version checks as coding UID 2000
using the actual running T3 service PATH, execution of both version commands through a
real T3 terminal, and the authenticated `server.discoverSourceControl` RPC reporting both
providers available and unauthenticated with matching versions. This is the RPC behind
the pinned Settings page: upstream commit above, `packages/contracts/src/rpc.ts`,
`packages/source-control-{github,gitlab}/src/server/*SourceControlProvider.ts`, and
`apps/web/src/components/settings/SourceControlSettings.tsx`. It never performs sign-in,
supplies provider credentials, or reports raw auth output. A missing provider blocks the
native candidate. Source tests use synthetic archives/RPC fixtures, not a hosted login.
**The updated image/native probes and browser/account/repository checks have not been
run in a sanctioned hosted environment.** `sourceControlUserSignInQualified` remains
false; source tests are not evidence of provider authentication or browser qualification.

### Qualify the latest stable T3 release

The local candidate tool pins the upstream commit, verifies the Linux x64 archive
against GitHub's published SHA-256, and pins the resource monitor from its archive
entry. Run from the repository root with Python 3.11+ and network access:

```sh
mkdir -p .local/artifacts/t3-release
python3 -B templates/development-workspaces/image/t3-candidate.py \
  --output .local/artifacts/t3-release/manifest.lock.json
```

Review the release notes and use the candidate lockfile in the exact source tree
for the [disposable builder](#prepared-image). On it, run `image/native.py plan`
with the runtime SHA-256, then `image/native.py run` with the same digest and the
root-owned short-lived authorization from the operator. Hosted operators can use
their guarded builder qualification and preflight helpers with the exact source
revision and runtime SHA-256. Retain the installed manifest and native report before generalizing the builder. Use the exact installed
version and protocol in the qualified image registration's `T3Version` and
`T3EnvironmentProtocol`. Protocol 2 is currently supported.

Native qualification also loads the installed renderer from guest loopback: the
document, its module scripts, stylesheets and splash logo, and every chunk in the entry
module's Vite preload list must return 200 with the expected MIME type and a nonempty
body. A missing chunk served as the HTML fallback keeps the browser on the splash
screen, so it blocks the candidate. This checks delivery only (`browserUiVerified: false`);
it is not a browser rendering pass.

For hosted acceptance, start a disposable workspace on that exact registered
image. Follow the [T3 Connect runtime contract](../../docs/setup/runtime.md#runtime-interfaces) to verify the
authenticated gateway, WebSocket, owner enrollment, relay and desktop discovery.
Use synthetic data and a verified backup to test clean stop, retained-volume
reattachment and rollback before moving existing workspaces. Publish the
qualified image as the default only after the native and hosted checks pass.

### Rolling out to existing workspaces

Build, qualify, generalize, power off, capture/register a **new immutable image revision**;
retain the previous image and its exact policy registration until all running
generations that use it are stopped. Keep it available for rollback. Update the approved image selection for new
workspaces. Changing a catalog/default image alone does not update an existing VM.
Do not install packages ad hoc on retained workspaces or change the manifest in place.

For each retained primary, use the authorized lifecycle to fence admissions, finish
active work, quiesce, obtain/validate the clean-stop receipt, and verify a restorable
encrypted off-host backup before deleting old compute. Record the volume identity,
filesystem UUID, owner claim, generation, and old image. Prove old compute is absent
and exclusive volume ownership before attaching that **same retained volume** to a
replacement VM on the new image. Re-enroll with a new generation and
`initializeOwner:false`; never format/reinitialize the volume or clone over its files.
Preserve all of `/workspace`, including repos (tracked/untracked/ignored files), T3 state,
home/CLI sign-ins, transcripts, config, artifacts, and user application data. Verify
the bootstrap receipt, versions, Source control, repository access and retained data
before reopening admissions. This is an operator procedure, not an automatic migration;
use the previous image and the same exclusive-ownership/generation fences for rollback.
For ephemeral workspaces, first complete the normal verified Git-save or explicitly
authorized discard flow; then create a new workspace on the new image. Root-disk,
ignored files and local credentials are not retained by that flow.

## Bootstrap and volume contract (parent worker)

The image installs:

* `/opt/workspaces-workspace/{runtime.py,bootstrap.py,issue-t3-session.py,git-credential.py,copilot_provider.py,storage_tiers.py,scratch.env}`
* `/etc/systemd/system/workspaces{work.slice,-workspace.service,-workspace-mount.service,-t3.service}`
* `/etc/tmpfiles.d/workspaces-scratch.conf` and `/etc/profile.d/workspaces-scratch.sh` (see
  [Storage tiers](#storage-tiers))
* `/opt/workspaces-workspace/image-manifest.json`

Upload root-owned mode-0600 configuration to `/etc/workspaces-workspace/runtime.json`.
First-boot cloud-init must explicitly enable key-only root access (`disable_root:false`)
with only the selected management public key; the image's SSH allowlist admits root only.
Example **primary** (replace synthetic values; root fields outside this schema fail closed):

```json
{
  "vmId": "vm-example", "generation": 7, "kind": "primary",
  "uid": 2000, "gid": 2000, "workspace": "/workspace",
  "volume": {
    "device": "/dev/disk/by-id/scsi-0HC_Volume_123",
    "providerDeviceId": "scsi-0HC_Volume_123", "providerVolumeId": "123",
    "filesystemUuid": "UUID-READ-FROM-THE-ACTUAL-FORMATTED-VOLUME",
    "initializeOwner": false,
    "owner": {
      "organizationId": "org-example", "workspaceId": "workspace-example",
      "providerAccountId": "account-example", "location": "region-example",
      "providerVolumeId": "123"
    }
  },
  "infrastructureUnits": ["workspaces-t3.service"],
  "stopUnits": ["workspaces-t3.service"]
}
```

The examples use a Hetzner device. On DigitalOcean the device is
`/dev/disk/by-id/scsi-0DO_Volume_<volume-name>`, and on Vultr it is
`/dev/disk/by-id/virtio-<first 20 characters of the block mount ID>`. Vultr volumes are
created raw. For a new Vultr volume only, the worker first runs `bootstrap.py volume-format`
with the same `--device` and `--provider-device-id`, which refuses any other filesystem or
signature and returns the existing ext4 volume on retry; it then probes.

For a **newly provider-formatted empty volume only**, the trusted worker first probes:

```sh
python3 -B /opt/workspaces-workspace/bootstrap.py volume-probe \
  --device /dev/disk/by-id/scsi-0HC_Volume_123 \
  --provider-device-id scsi-0HC_Volume_123
```

This returns `{"ok":true,"result":{"schemaVersion":1,"type":"VolumeProbe","device":"…",
"providerDeviceId":"…","filesystemUuid":"…","filesystem":"ext4","formatted":false}}`.
Persist the UUID in the control plane **before** admitting a writer. Set `initializeOwner:true`
only for that new, empty, separately provisioned volume. It creates a root-owned
`.workspaces-volume.json` exactly equal to `owner`. Existing content without a matching
ownership marker blocks; resume never initializes/rewrites claims or formats anything.
`blkid`, stable provider by-id device, ext4 UUID, mounted source/UUID, and ownership marker
are checked. Unexpected mounts/devices/filesystems/claims, missing devices or nonempty local
fallback directories block. The worker must separately prove exclusive provider ownership
and previous-generation compute absence before attach. There is no detach/force-detach API.

After successful primary bootstrap, managed T3 is enabled on that VM. On reboot
`workspaces-workspace-mount.service` must succeed before T3 or either scratch
unit may start. It checks the persisted generation and reopens `/workspace`
using the recorded provider by-id device and ext4 UUID, then verifies the
existing owner marker. It never formats a device or initializes ownership.
Only an unfenced `Ready` generation can automatically remount; a clean stop
does not authorize reopening it. Ephemeral storage is unchanged.

The mount guard waits up to 45 seconds for the provider device. If the device
is missing or identity checks fail, see
`journalctl -b -u workspaces-workspace-mount.service`. T3 and scratch stay
stopped; `/workspace` is not used as local fallback storage. After the same
registered device is available, starting `workspaces-t3.service` retries the
mount. The runtime reports `StorageUnavailable` until the retained mount can
be verified; on its next successful observation the worker blocks gateway
admission until both the retained mount and T3 readiness are verified again.
Existing VMs get this behavior only when started on a newly qualified image.

An **ephemeral** configuration must omit `volume`, `backup`, `restore`, and export hooks:

```json
{
  "vmId": "vm-ephemeral", "generation": 1, "kind": "ephemeral",
  "uid": 2000, "gid": 2000, "workspace": "/workspace",
  "ignoredDataAcknowledged": true,
  "repositories": [{
    "path": "/workspace/repos/project", "origin": "https://example.invalid/team/project.git",
    "baseSha": "0123456789012345678901234567890123456789",
    "branch": "work/vm-ephemeral-g1", "remoteRef": "refs/heads/work/vm-ephemeral-g1",
    "defaultBranches": ["main"], "pushAuthorized": false
  }]
}
```

The worker supplies a unique work branch and exact resolved base SHA. First boot clones and
checks out that branch; no commit/push is performed. Primary bootstrap never clones over
retained files. Ephemeral storage is root-disk only; volume claims, common mounted exports,
known primary/backup paths and shared container sockets are refused. Provider allocation,
networking, credential grants and gateway authorization must enforce the remaining isolation.
No primary credentials are inherited. Private Git over pinned SSH can use an explicitly
selected per-generation key at `/run/workspaces-git/key` (owner 2000, mode 0400; directory
root-owned 0711), plus root-owned `/etc/workspaces-git-known-hosts` (0644). HTTPS credential
helpers from repositories/user configuration are disabled. SSH keys require independent
revocation policy; GitHub deploy keys are **not intrinsically expiring**.

Preferred private Git uses an explicitly selected, genuinely repository-bound GitHub App
installation token issued for at most one hour. Parent stages `/run/workspaces-git/credential.json`
(regular file, owner 2000, mode 0400, single link, directory root-owned 0711):
`{"origin":"https://github.com/org/repo.git","username":"x-access-token","password":"SECRET",
"expiresAt":1790000000}`. Do not put this material in runtime config, image, logs or receipts.
The installed root-owned `git-credential.py get` answers only the exact HTTPS host/path
with future expiry no more than one hour away; store/erase, redirects, missing path,
other repositories/hosts and expired credentials have no response or diagnostics.
Git requests may end at EOF after a complete newline-terminated field or at a blank line;
truncated fields still fail closed.
There is no prompt/fallback. Parent must use the token's **real issuer expiry**, renew/revoke
explicitly and never claim this helper turns a broad PAT/deploy key into a short-lived token.
All Git/helper/SSH execution runs as UID 2000, never root. Remote evidence is collected in
a fresh independent verifier with redirects disabled, no checkout-controlled HTTP/credential
configuration, and no replacement refs/grafts. Absent the staged file, public Git uses no helper.

Upload a distinct root-owned 0600 **one-time** `/run/workspaces-workspace/enrollment.json`:
`{"url":"https://control.example.invalid/enroll","token":"ONE_TIME_SECRET","vmId":"…",
"generation":7,"operationId":"start-operation","expiresAt":1790000000}`.
Optional `caFile` selects a private trust CA. Then invoke over **pinned management SSH**:

```sh
python3 -B /opt/workspaces-workspace/bootstrap.py bootstrap
```

It mounts/initializes the declared layout, starts services, verifies that the loopback
T3 descriptor matches the installed image's T3 version and protocol, and POSTs HTTPS JSON
`{schemaVersion,vmId,generation,kind,operationId,imageManifestSha256,bootstrapReceipt}`
with the one-time token as Bearer authorization. HTTP 200 is explicit acceptance; no redirects.
Intent is persisted **before** enrollment I/O, and token input is deleted after an attempt.
An ambiguous result is never automatically replayed: reconcile/reissue through the parent.
No tools are reinstalled. Parent must not publish Ready/gateway routes until acceptance and
its own generation/endpoint checks succeed.

## Paths and supervision

`/workspace/repos` includes all tracked, untracked and ignored primary edits;
`/workspace/t3` is T3's `--base-dir`; `/workspace/transcripts` holds explicitly file-backed
task transcripts; `/workspace/home` holds intentionally selected coding-agent state
(`CODEX_HOME=/workspace/home/.codex`); `config` and `artifacts` hold workspace
configuration/publication files. Users choose any additional application directories
under their workspace. Root home, `/run`, temporary files and caches are **not durable**.
There is no assumed database engine, emulator export format or application migration.
Application-specific lifecycle integration must be explicit, not inferred from a path.

### Storage tiers

`/workspace` is the **retained** tier: the provider volume that survives VM replacement.
Regenerable data lives on the **scratch** tier, `/var/lib/workspaces-scratch` on the VM's
local disk, which is never under `/workspace` and may be empty after any VM replacement:

| Path | Owner, mode | Holds |
| --- | --- | --- |
| `/var/lib/workspaces-scratch` | root, 0755 | scratch root |
| `cache` | coding, 0700 | package, build and tool caches (below) |
| `tmp` | coding, 0700 | `TMPDIR`; emptied at every boot |
| `docker` | root, 0711 | reserved for a container runtime's data root |
| `repos` | root, 0700 | sources of build-output bind mounts into `/workspace/repos` |

`/etc/tmpfiles.d/workspaces-scratch.conf` creates the layout at every boot
(`systemd-tmpfiles-setup.service`, before `sysinit.target` and therefore before T3), and
`storage_tiers.py prepare` re-applies it before each T3 start.

`/opt/workspaces-workspace/scratch.env` is the single list of redirected variables. The T3
service reads it with `EnvironmentFile=`, so T3, its terminals and agents inherit it;
processes started by the runtime with `systemd-run` get the same `EnvironmentFile=`; and
`/etc/profile.d/workspaces-scratch.sh` exports it in login shells of UID 2000 only (for
example `su - coding` or `runuser -l coding` from the root management SSH session).
Root and other users are unchanged. Values are fixed absolute paths, parsed line by line,
never sourced.

| Variable | Scratch path (under `/var/lib/workspaces-scratch`) |
| --- | --- |
| `XDG_CACHE_HOME` | `cache` (also Yarn 1, pip/uv fallbacks, Playwright fallback, …) |
| `TMPDIR` | `tmp` |
| `npm_config_cache` | `cache/npm` |
| `pnpm_config_store_dir` | `cache/pnpm/store` (pnpm 11; pnpm 10 follows the store link below) |
| `NUGET_PACKAGES`, `NUGET_HTTP_CACHE_PATH` | `cache/nuget/packages`, `cache/nuget/http-cache` |
| `PIP_CACHE_DIR`, `UV_CACHE_DIR` | `cache/pip`, `cache/uv` |
| `GOCACHE`, `GOMODCACHE` | `cache/go-build`, `cache/go-mod` |
| `PLAYWRIGHT_BROWSERS_PATH` | `cache/ms-playwright` |
| `PYTHONPYCACHEPREFIX` | `cache/pycache` |

Tools whose home directory mixes caches with configuration or credentials keep that home
under `/workspace/home`; only their cache subdirectories become symlinks into
`scratch/cache`, created by `storage_tiers.py` as the coding user when absent:
`~/.cache` (the cache root itself), `~/.cargo/{registry,git}`,
`~/.gradle/{caches,wrapper/dists}`, `~/.m2/repository`, `~/.local/share/pnpm/store` and
`~/.yarn/berry/cache`. An existing real directory or a user-chosen link is left alone.
Deliberately **not** set:

* `CARGO_HOME` and `GRADLE_USER_HOME`: they also hold `credentials.toml`/`config.toml`,
  `gradle.properties` and installed binaries, which must stay retained.
* `CARGO_TARGET_DIR`: one shared target directory breaks project-relative `./target`
  paths and lets projects collide; build outputs use per-repository bind mounts instead.
* `YARN_CACHE_FOLDER`: it would also override Yarn Berry's per-project (zero-install) cache.
* `PNPM_HOME`: it holds globally installed pnpm binaries.
* `/etc/environment`: pam_env would apply it to root and every other user.

Consequences: Playwright browsers, `~/.m2/repository` (including artifacts installed with
`mvn install`) and every cache must be re-downloaded or rebuilt after VM replacement.
Tools that keep credentials under `$XDG_CACHE_HOME` now keep them on scratch; notably the
Hugging Face token (`$XDG_CACHE_HOME/huggingface/token` by default). To keep it retained,
export `HF_TOKEN_PATH=/workspace/home/.config/huggingface/token` (or `HF_HOME`) before
signing in, or supply `HF_TOKEN` per session.

**Container runtimes.** The image ships no Docker, containerd or Podman. If one is added,
its data root must be under `/var/lib/workspaces-scratch/docker` (for Docker,
`"data-root"` in `/etc/docker/daemon.json`); native qualification fails with
`CONTAINER_RUNTIME_DATA_ROOT_UNQUALIFIED` until that is qualified.

**One-time cache migration.** Before T3 starts, `workspaces-t3.service` runs
`storage_tiers.py prepare` as root (`ExecStartPre=-+`, so a failure is logged and never
blocks T3). Unless the root-only marker
`/var/lib/workspaces-workspace/storage-tiers-migrated.json` (0600) records completion, a
child that has dropped to UID/GID 2000 renames known cache directories in
`/workspace/home` (`.cache`, `.npm`, `.nuget/packages`, NuGet's `http-cache`, `v3-cache`
and `plugins-cache`, the pnpm and Yarn Berry stores, `go/pkg/mod`,
`.cargo/{registry,git}`, `.gradle/{caches,wrapper/dists}`, `.m2/repository`,
`.local/share/docker`) into `/workspace/home/.workspaces-cache-trash` and deletes them.
Every path component is opened with `O_NOFOLLOW` relative to its parent; only real
directories owned by UID 2000 on the `/workspace` filesystem are moved; symlinks, other
owners and other mounts are skipped. Configuration and credential files
(`.cargo/credentials.toml`, `.gradle/gradle.properties`, `.npmrc`, `NuGet.Config`, …) are
never touched, and `~/.cache` stays in place when it holds a Hugging Face token. Deletion
is bounded to 40 seconds per T3 start; anything left is retried at the next start (the
trash folder can also be deleted from the folder-size view). The marker is on the root
disk, so a replacement VM repeats the migration, which is then a no-op. The helper logs
one bounded JSON line to the journal and always exits 0.

`runtime.py serve` owns `/run/workspaces-workspace/control.sock` (0600, root peer UID checked).
The agent is outside the freezable coding slice. State/atomic fsynced receipts live in
`/var/lib/workspaces-workspace`; parent must durably persist returned lifecycle receipts
**before deleting compute**. File presence on the disposable VM alone is not off-VM durability.

### T3 service recovery

`workspaces-t3.service` restarts on unexpected exit, signal, or OOM kill
(`Restart=on-failure`, `RestartSec=5`). The default `OOMPolicy=stop` turns a kernel or
cgroup OOM kill into a failed run that restarts on the same VM generation and retained
volume, so environment identity, projects, threads, and sign-ins persist. Interrupted
agent turns are not resumed. Exit 130, T3's graceful stop code, is a success and does not
restart. `StartLimitIntervalSec=300` and `StartLimitBurst=5` bound the retries, including
explicit starts: a fresh start followed by five SIGKILLs within the window ends in
`failed` (`Result=signal` on Ubuntu systemd `255.4-1ubuntu8.10`). To recover from
exhaustion without a lifecycle stop, run `systemctl reset-failed workspaces-t3.service`
and start it again.

`systemctl stop` cancels a pending restart. While a dependency keeps the stopped unit loaded,
`inactive/dead` can retain the previous `Result=signal` even with no job or processes.
Freezing the work slice is not a restart barrier: the restart timer can create a start job
and an `ExecStartPre` control process.
The runtime queues the stop before thawing, waits for managed T3's cgroup to drain, then
refreezes. It checks for no job, no main or control process, and an empty cgroup before clearing any
residual signal/OOM failure from the canceled restart and requiring `inactive`. An unrelated
failure or any remaining work blocks the lifecycle stop. After unmount,
`ConditionPathIsDirectory=/workspace/t3` also refuses a start. Upstream's `t3 service install`
user unit is not used; it would add a second supervisor outside the workspace lifecycle.

The native probe `t3-oom-crash-restart-stop-race-and-retry-budget` separately verifies
automatic SIGKILL and cgroup OOM recovery: same environment ID, retained project, filesystem
UUID and VM generation; authenticated HTTP and WebSocket access; and Connect management.
It also checks exit 130, the exact five-failure budget, and job and freezer state when a
stop lands during the restart delay or frozen `ExecStartPre`. The probe runs before terminal
probes. Before rollout, a disposable hosted workspace must also show that a Workspaces gateway
launch and a previously linked T3 Connect relay recover after a T3 crash.

### Build outputs on local scratch

`scratch_mounts.py` (`workspaces-scratch-mounts`) is a root helper that keeps regenerable,
git-ignored build outputs off the retained volume. It bind-mounts
`/var/lib/workspaces-scratch/repos/<repo-id>/<relative-path>` over the output directory inside
`/workspace/repos`. Paths in the checkout stay the same: no symlinks and no tool configuration.
`<repo-id>` is the checkout's directory name plus a hash of its path below `repos` and its
inode, so ids are stable and two checkouts never share one. Relative paths are validated
component by component: no `..`, no symlinks, and no other mounts. `repos` under the scratch
root is root-owned `0700` and is bind-mounted onto itself with `nosuid,nodev`. Each output
directory below it is owned by the coding user.

A directory moves to scratch only when **all** of the following hold:

* Its name is one of `node_modules`, `bin`, `obj`, `dist`, `build`, `out`, `target`, `.next`,
  `.nuxt`, `.svelte-kit`, `.turbo`, `.parcel-cache`, `.angular`, `.gradle`, `.venv`, `venv`,
  `__pycache__`, `coverage`, `.pytest_cache` or `.mypy_cache`.
* `git check-ignore` reports it ignored. Git runs as the coding user, who owns the checkout,
  with a minimal environment, no system or global config, `core.hooksPath=/dev/null` and
  `core.fsmonitor=false`. A directory that contains tracked files is never moved, even if it
  also matches an ignore rule.
* It is a real directory owned by the coding user on the `/workspace` filesystem, not
  already a mount point, and not inside `.git`.

Tracked, non-ignored and non-allowlisted directories are never touched. Discovery covers
checkouts directly below `repos` and nested projects in monorepos. It goes at most 12
levels deep and does not descend into `.git`, `.hg`, `.svn`, `.jj`, `vendor` or
allowlisted output trees.

Manifests also create the expected outputs before the first build, as empty bind mounts, but
only when Git would ignore them:

| Manifest | Outputs |
| --- | --- |
| `package.json` | `node_modules` |
| `*.csproj`, `*.fsproj`, `*.vbproj` | `bin`, `obj` |
| `Cargo.toml` | `target` (outermost Cargo project only) |
| `pyproject.toml`, `setup.py`, `requirements*.txt` (checkout root) | `.venv` |
| `build.gradle*` | `build`, `.gradle` |
| `settings.gradle*` | `.gradle` |
| `pom.xml` | `target` |
| `angular.json` | `.angular` |
| `next.config.*` | `.next` |
| `nuxt.config.*` | `.nuxt` |
| `svelte.config.*` | `.svelte-kit` |
| `turbo.json` | `.turbo` |

The helper runs at three points:

1. `workspaces-scratch-mounts.service`, a oneshot ordered before `workspaces-t3.service`.
   T3 pulls it in with `Wants=`. It also runs before any coding process starts.
2. Runtime `bootstrap`, after initial clones and before T3 starts.
3. `workspaces-scratch-watch.service`, a long-running inotify watcher (via `ctypes`, with a
   bounded polling fallback). It handles new checkouts, manifests, output directories and
   removed mount points. It also runs a full pass every 5 minutes, or every minute while work
   is deferred.

An existing non-empty output relocates only after 60 seconds without changes, and only when
no process has its cwd, root or an open file inside it (checked through `/proc`). The helper:

1. Copies the directory to a staging directory on scratch. Owners, modes, timestamps,
   symlinks (as symlinks) and hardlinks within the tree are preserved. A root-only journal
   is written under `/workspace/.workspaces-scratch-trash`.
2. Moves the volume copy into that trash directory, then re-checks the moved copy and
   mounts scratch.
3. Deletes the volume copy.

After a crash, the next run finishes or rolls back each journal. Data is never lost:
conflicting or unjournaled trash stays in place and is reported. Busy or changing
directories are retried later. If a mount point is removed after it is unmounted, the next
pass can recreate the ignored output directory and its mount.

**Mount propagation.** The helper units use no mount namespacing, so mounts are made in the
host namespace. `workspaces-t3.service` (`ProtectSystem=strict`, `PrivateTmp=yes`), task units
and folder-size jobs run in their own namespaces, which systemd creates as slaves of the host.
Mounts and unmounts below `/workspace` therefore propagate into T3, its terminals and agents,
including those made after T3 started. Propagated mounts keep the host's read-write flags.
`workspaceswork.slice` changes nothing here. T3 is ordered `After=` the oneshot only so the
first build already sees the mounts; the native probe checks the mounts from inside a T3
terminal.

**Lifecycle.** Before the first idle check, `quiesce`, `git-save` and `discard` run
`scratch-suspend`: the helper is marked suspended and both helper units stop. Otherwise its
short-lived Git calls as the coding user would count as work outside the slice. After
`stop-services` and before the read-only remount, backup and unmount, `scratch-unmount`
unmounts every scratch mount, deepest first. A lazy unmount is used only as a last resort
and is reported. `PrimaryQuiesced` reports `scratchMounts:{unmounted,lazy,lazyPaths}`, and
any remaining mount blocks quiescence. `Linux.unmount` repeats `unmount-all` defensively.
`resume`, `cancel-drain` and `bootstrap` clear the suspension and restart the units, so
mounts come back without user action. On images without the helper, all of these steps do
nothing.

**Backups and folder sizes.** Backups never enter nested mounts. Archiving, validation and
representative hashes use a one-filesystem walk that records a mount point as an empty
directory. Scratch is already unmounted before a backup is taken. `disk_usage.py` reports
scratch mounts as `excluded` with `separate_mount` and never deletes across them. Confirmed
checkout deletion first releases only verified, owned scratch mounts inside its target;
it does not traverse their scratch contents. The
resource sampler counts them in `nestedMountsExcluded`. The trash directory stays in backups
because it can hold user data mid-relocation. After a restore it belongs to the coding user,
so the helper renames it to `.workspaces-scratch-trash.untrusted-*` without reading its
journals.

**Pressure safety net.** When the retained volume is more than 85% full (override with
`--pressure-percent 50..99`), each pass evicts allowlisted, ignored output directories still
on the volume. Only checkouts with no process inside them qualify, and the least recently
modified directories go first, until usage falls 5 points below the threshold. Each
eviction is journaled before deletion. Tracked, non-ignored, non-allowlisted and open
content is never deleted. Actions go to the journal (`journalctl -u
workspaces-scratch-watch`) and to `/run/workspaces-scratch-mounts/status.json`
(`scratch_mounts.py status`). The threshold is a command-line option because `runtime.json`
rejects unknown fields.

**Caveats.**

* `rename(2)` between a mounted output and the rest of the checkout fails with `EXDEV`. Most
  tools fall back to copying.
* pnpm hardlinks from its store fall back to copies unless the store is on scratch too.
* File watchers see the delete and recreate during a relocation. Run the dev server again if
  it does not recover.
* `rm -rf node_modules` empties the mount, then reports `Device or resource busy` for the
  directory itself, which stays mounted. Ordinary coding-user `rm -rf <checkout>` can
  likewise exit nonzero with `EBUSY` and leave a partly removed checkout. Watcher cleanup
  does not change that original exit status; there is no shell alias or automatic retry.
* `python -m venv .venv` fills a pre-created empty `.venv`. Tools that treat any existing
  `.venv` as an environment may need to be told to create it there.
* Containers that bind-mount a checkout before relocation do not see the new mount.
* Extended attributes, ACLs and sparseness are not copied.
* A global gitignore is not honored. Only repository ignore rules decide.
* After a checkout is renamed, its old mounts stay until the next unmount, and unused scratch
  is collected.
* After a raw-shell checkout removal leaves orphaned mounts, a later complete watcher pass
  can unmount them (retrying while busy). Removal/reclone then requires another operation.
* Scratch is disposable: it survives a reboot on the same VM but is empty on a new VM, where
  outputs are rebuilt.
* Limits: 512 mounts, depth 12 and 100,000 scanned directories per pass.

### Repository checkpoints and local checkouts (off by default)

`repo_checkpoint.py` and storage layout 2 implement #105, which moves live checkouts to the
VM disk and checkpoints them to the retained volume. The image installs both. The worker
requests layout 2 only for primary volumes it creates while
`DevelopmentWorkspaceWorker:LocalCheckouts` is `true` (default `false`). The layout is stored on
the workspace, so flipping the option never converts an existing volume. Existing
workspaces, layouts and scratch behavior do not change. Backup changes and migration are
later PRs.

The engine copies a source tree (by default `/workspace/repos`) to a root-owned store at
`/workspace/.workspaces-checkpoints` (mode 0700):

* `objects/<aa>/<sha256>`: file contents, stored once.
* `manifests/<id>.json`: the tree, with modes, mtimes and symlink targets. Each object is
  referenced by hash and size.
* `current.json`: the published checkpoint and its binding: workspace, provider volume,
  filesystem UUID, VM, generation and writer generation. It also lists the retained history
  (the last 2 checkpoints).
* `status.json`: the last attempt and last success.
* `tmp/`, `staging/` and `lock`.

A save writes new objects and a staging manifest, then flushes and verifies them. It then
publishes atomically by replacing `current.json`. An interrupted, failed or disk-full save
leaves the previous checkpoint readable. Unreferenced objects are removed only after a
publish. One lock serializes all operations. Periodic saves do not wait for the lock.
Fenced saves wait up to 10 minutes.

* **Kept:** Git metadata, stashes, the index, linked worktrees, new and ignored files, and
  non-Git directories. Absolute paths, such as linked worktree `gitdir` files, stay valid
  because restore recreates the same fixed path.
* **Omitted:** only scratch-allowlisted output directories, such as `node_modules`, `bin` and
  `obj`. A directory is omitted only when Git, run as the coding user without hooks or global
  config, confirms it is ignored and contains no tracked files. If Git fails, it is copied.
* **Reported:** symlinks, `gitdir` files and `alternates` that point outside the fixed root
  are recorded by path and kind. FIFOs and sockets are skipped and reported.
* **Refused:** device nodes and other filesystems inside the tree fail the save.

The tree is walked twice. If anything changes between passes, a periodic save is deferred
(exit 75). A save under a writer fence fails instead. Racily modified files are rehashed.

`verify` checks the binding, an optional stop operation ID, and object sizes. With
`--deep`, it also checks hashes. `restore` builds the tree in a sibling `.restore-staging`
directory, checks every hash, and renames it into a target that must not already exist. A
partial staging directory is removed on retry. Restored entries belong to uid/gid 2000, and
setuid/setgid bits are dropped. `status` reports the store as `empty`, `ready` or `corrupt`.
A missing pointer with retained manifests is corrupt, never empty.

Every save, restore and `prepare-tree` takes a `--lineage` file kept next to the live tree on
the same filesystem. It records the volume, the checkpoint the tree was restored from, a
random tree token and the tree root's inode. A save is refused unless the lineage matches
the current checkpoint and the tree root has not been replaced. A save with no lineage is
only accepted into an empty store. So an empty tree on a replacement VM can never overwrite
a checkpoint that still has to be restored.

`prepare-tree` makes the live tree ready before anything writes to it:

* **Kept:** an existing tree whose lineage matches the current checkpoint, such as after a
  same-VM reboot. Edits newer than the checkpoint stay.
* **Restored:** a missing tree is restored from the current checkpoint.
* **Initialized:** with `--allow-empty` on an empty store, an empty tree is created and
  published at once. A later empty store is then reported as missing, not new.

Anything else fails: a missing checkpoint, a tree without matching lineage, or a store from
another volume.

Limitations: extended attributes, ACLs and hard links are not preserved. Writes through
`mmap` that do not update ctime can be missed. A tree that changes continuously can keep
deferring periodic saves.

#### Storage layout 2 (local checkouts)

With `"storageLayout": 2` in the runtime configuration (primary only):

* Live checkouts are in `/var/lib/workspaces-local/repos` on the VM disk. It is bind-mounted
  `nodev,nosuid` at `/workspace/repos`, so T3 and linked worktrees keep the same absolute
  paths. The rest of `/workspace`, including T3 state, stays on the retained volume. The
  local directory must be on a separate ext4, xfs or btrfs filesystem.
* The volume's own `repos` directory is only an empty root-owned 0555 mountpoint. If the
  mount is ever missing, nothing can clone underneath it. Bootstrap refuses old-layout data
  in it; migration is a later PR.
* The root-only `/workspace/.workspaces-layout.json` binds the volume to layout 2. An older
  layout refuses such a volume, and the scratch relocation units exit as disabled. A new
  marker is created only when the volume has no checkouts and no checkpoint store. It
  records the first checkpoint, so an empty store is never treated as a new workspace again.
* Bootstrap runs `prepare-tree` and mounts the result before Ready, so T3 never sees an
  unrestored tree. The boot-time mount service only reattaches a kept tree. A tree that needs
  a restore waits for bootstrap, and T3 does not start.
* Status, tasks and folder jobs verify the checkout mount as part of the retained mount.
  Folder size and delete jobs work on the checkout filesystem directly, with no scratch
  preparation. Every other mount below it is still refused.
* Stop (`quiesce`) saves a final checkpoint after T3 and the work services have stopped
  and the final T3 proof is taken, while the volume is still writable. Only then does it
  remount `/workspace/repos` and `/workspace` read-only and unmount. The receipt carries
  `storageLayout: 2` and the checkpoint (ID, manifest hash, operation and binding). The
  worker detaches the volume or deletes the VM only if that checkpoint belongs to this stop
  operation, this VM and generation, and this volume and filesystem. It checks the receipt
  again before deleting compute.
* If the save fails, nothing is remounted or unmounted. The stop is refused with
  `checkout_checkpoint_failed` and the worker resumes the VM, so its files stay usable. A
  retry of the same stop verifies the saved checkpoint instead of saving again, and refuses
  if a newer checkpoint replaced it. Resume and cancel forget the stop checkpoint.
* A worker management command times out after 75 seconds, but the runtime keeps working
  and replays the outcome to the same operation. If a layout 2 `quiesce` times out, the
  worker keeps the VM running, reports `workspace_checkpoint_in_progress` and retries the
  same step.
* Off-host backup and separate-volume restore carry the final checkpoint instead of the live
  tree. See [Storage layout 2 backups](#storage-layout-2-backups).

#### Periodic checkpoints and status

`workspaces-repos-checkpoint.timer` runs `runtime.py checkpoint-periodic` every minute while
T3 runs. It is wanted by `workspaces-t3.service` and stops with it. It saves only when the
interval has passed since the last attempt: 300 seconds by default, or
`DevelopmentWorkspaceWorker:CheckpointIntervalSeconds` (60 to 3,600). The worker sends
`checkpointIntervalSeconds` only when it differs from the default, so older layout 2 images
keep working.

* A save runs only while the workspace is Ready with no fence, freeze or read-only flag. It
  checks this again while holding the store lock, so a save can never publish after a stop
  has fenced the workspace; that refusal records nothing. A save deferred by the engine (busy
  lock, changing tree, or a fence seen under the lock) exits with 75, which systemd treats as
  success. A later run tries again.
* The service runs at low CPU and I/O priority outside `workspaceswork.slice`, as root, with
  a limited capability set. Git runs as the coding user, as in other saves.
* Stop fences the workspace first, then stops the service and waits for a running save to
  finish before it checks idleness and freezes T3. The final checkpoint is then saved as
  described above.
* `status` reports `checkpoint`: `Fresh`, `Stale` (no successful save for 3 intervals),
  `Failed` (the last attempt failed or the store is corrupt), `Missing` or `Unavailable`,
  with the time of the last checkpoint and the last attempt.
* The worker stores the state and time from each heartbeat. An `Unavailable` report keeps the
  last known value, and the time never moves backwards. An accepted stop receipt records
  `Fresh` at stop time. The API (`storageLayout`, `lastCheckpointAt`, `checkpointStatus`),
  `municode status`, and the dashboard Storage card show it. They also show the last verified
  off-host backup (`lastBackupAt`, `backupStatus`) for every workspace.

Measured on an aarch64 VM (local ext4, warm cache):

| Tree | First save | Unchanged save | Restore on a new VM | First save after restore |
| --- | --- | --- | --- | --- |
| 3 real repos, 4,838 files, 77 MB | 2.5 s | 0.06 s | 0.19 s | 0.21 s |
| 60,002 files, 324 MB | 41 s | 0.74 s | 2.8 s | 5.1 s |

Restore runs in bootstrap (600 s limit) and is far below it. A first full save of a large
tree on a slower network volume can exceed 75 s, which the retry above covers.

Run the engine checks with:

```bash
TMPDIR=$PWD/.local/artifacts/tmp python3 -B -m unittest discover \
  -s templates/development-workspaces/tests -p test_repo_checkpoint.py -v
```

### Confirmed checkout deletion

The supported single-operation path for a checkout with scratch binds is the existing
dashboard **Delete folder** operation, not raw `rm`. It requires a newly built and
qualified image containing this implementation; existing hosted images are not patched:

1. Finish builds/agents and close terminals, previews and other processes using the
   checkout. Deletion does not stop them for you.
2. On the running VM, select **Fetch disk usage**, open `/workspace/repos`, and select
   **Delete folder...** on the checkout itself, not an excluded scratch output.
3. Check the workspace, VM generation and exact folder in the dialog. Type its full
   `/workspace/repos/...` path and acknowledge permanent loss, then select
   **Delete folder permanently**.
4. Wait for **Folder deleted** before cloning to that path again. If deletion fails,
   is cancelled, partly completes or its result is unknown, scan again to inspect what
   remains; do not treat it as success.

The selection binds the workspace, VM generation, path and measured device/inode.
Root orchestration checks active use, inhibits target remounts, and normally unmounts
only verified helper-owned binds in that exact checkout before bounded deletion as the
coding user. It never lazily unmounts for this operation, traverses scratch contents,
stops unrelated work, or removes another checkout's tracked files. Unrelated mounts,
changed identity/path, active or unobservable use and unsupported image state refuse
deletion. Cancellation or a change after work starts can leave a partial result.

Target-specific inhibition markers are atomically replaced in a protected `/run`
directory before any detach. Incomplete staging writes are ignored; malformed legacy
markers fail closed for their hashed target without stopping unrelated reconciliation.
Valid historical markers remain usable. Markers record the original mount IDs, so
replay counts a successful detach even if the subprocess dies before acknowledging it.
The runtime persists a preparation stage: timeout or process death there is blocked,
not a lost deletion launch. Same-ID start/status replay rechecks identity, active use,
mounts and lifecycle guards, and resumes preparation. The preparation result is persisted
before launching the coding job; an ambiguous coding-job launch is never repeated.

Interrupted or partly completed work retains inhibition. A fresh scan and confirmed retry
can complete deletion when the same target and ancestors still match; only verified
success releases inhibition. Lost or changed identity stays inhibited rather than
guessing. Inhibition clears on VM reboot, after old deletion processes have ended.
This is not an atomic filesystem transaction or an undo mechanism.

**Acceptance correction for #31.** Ordinary `rm -rf` cannot be guaranteed to succeed
within one watcher pass. On a disposable checkout, record its actual exit status and
remaining mounts, then separately verify the confirmed deletion job succeeds in one
operation on an idle supported checkout, leaving no target mounts and permitting reclone.
Also verify busy use, unrelated mounts, changed identity and cancellation are refused or
reported partial, with another checkout's tracked files unchanged. Synthetic tests do not
qualify a hosted image; repeat these checks on the exact newly built image before claiming
hosted acceptance or rollout. No hosted/provider changes are part of #42.

### Stop held in Draining

While a stop is `Draining`, the workspace and CLI report whether unresolved control-plane
tasks, guest work inventory, or the T3 orchestration snapshot is holding the stop. These
holds retry automatically. Stop detached work cleanly and resolve T3 runs or pending requests
through T3; a provider process exiting does not by itself prove orchestration is idle. A T3
hold names its category (active or queued run, pending request, or pending background work)
without thread or task identifiers. Cancel stale queued runs normally; if a completed thread
still lists background work that is no longer running, use T3 **Force Stop** on that thread.
Stop must finish before permanent workspace deletion is allowed.

### JSON control protocol

Run `python3 -B /opt/workspaces-workspace/runtime.py call`, sending one newline-terminated
JSON object on stdin. All requests require `vmId`, integer `generation`, `operationId`
(`[A-Za-z0-9_-]{1,128}`), and `action`. Responses are `{"ok":true,"result":{…}}` or
`{"ok":false,"state":"Blocked|AwaitingGitSave","reason":"sanitized actionable reason"}`.
Inspect `ok`, not only SSH exit status. Never log enrollment or T3 secret output.

* `status`: phase, revision, tasks/lease deadlines, useful activity, backup freshness.
  `freezeCompleted` and `filesystemReadOnly` distinguish a kernel-frozen-but-blocked operation
  from fully stopped/read-only data. Neither status flag is itself a deletion receipt.
  Optional `includeWorkInventory:true` adds read-only `workInventory.state` of `IdleCandidate`
  or `BusyOrUnknown`, with a sanitized blocking reason. This unfrozen observation is advisory,
  never authorization for deletion.
* `git-check`: Ready ephemeral only; inspects every checkout without freezing or changing the
  checkout, returning `GitSaveCandidate` / `deletionEligible:false` or `AwaitingGitSave`.
  Keep the gateway open while users resolve/push. After the candidate passes, fence and drain
  the gateway, verify the real T3 work snapshot, then request `git-save`, which independently
  rechecks after freezing. On frozen Git failure, cancel the parent's deletion intent and
  explicitly `resume` the same operation before reopening the gateway for another edit/push.
* `task-start`: unique `taskId`, **argv array**, `leaseSeconds` 1–3600. Persists launch intent
  before `systemd-run`. A repeated/ambiguous task ID is **never replayed**, including after
  agent restart. All Workspaces builds/commands must enter through this path. Unknown launches
  are not retroactively made safe by an expired lease.
* `task-renew` / `task-complete`: known `taskId`; completion requires observed process exit.
  Lease renewal does not itself count as useful activity or terminate a task at expiry.
* `activity`: `source:user-input`, or `source:task-progress` with a live registered `taskId`.
  Keepalives, passive sockets, probes and log streams do not count.
* `quiesce` (primary), `git-save` (ephemeral), or `discard` (ephemeral): fence admissions,
  enumerate live work, freeze the coding cgroup, enumerate again, stop/flush T3 and
  explicitly registered user workloads, verify read-only workspace. Unknown live coding
  processes, active tasks, ambiguous launches or unreachable tools block; no arbitrary user
  task is killed. Primary then fsyncs/unmounts (no forced/lazy unmount).
  Bootstrap starts the coding environment only, not a database or sample application.
  Requires explicit root-orchestrator attestations `gatewayAdmissionsFenced:true` and
  `unobservableToolsAbsent:true`, plus nonnegative integer `t3SnapshotSequence` from the actual
  authenticated idle T3 HTTP snapshot; do not assert these without actual admission/in-flight
  fencing and complete tool/task integration.
* `discard` additionally requires **both** `authorizedDiscard:true` and
  `acknowledgeLostIgnoredAndRootData:true`. It is not a process-quiescence bypass.
* `receipt-validate`: `receiptId`; rechecks freeze, work, generation/operation, and fresh Git
  evidence. Call immediately before deletion. Receipts bind `schemaVersion,type,vmId,kind,
  generation,operationId,revision,issuedAt,receiptId`; their SHA256 IDs detect stale/tampered
  metadata, **not a substitute for authenticated SSH/control-plane authorization**.
* `cancel-drain`: same operation, only before actual freeze.
* `resume`: same operation plus `deletionNotCommitted:true`, only while Frozen (not unmounted).
  Parent must revoke/cancel deletion intent first. Receipts are durably invalidated **before**
  writes/services/admissions reopen. This lets users resolve Awaiting Git save; use a new
  shutdown operation after editing/pushing. Failed partial resumes remain blocked.

* `status` also returns `capabilities` (`resources-v1`, plus `disk-usage-v1` and
  `directory-delete-v1` when the adapter can run folder jobs) and a cached, read-only
  `resources` sample (CPU utilization over a delta window, memory, `/` and `/workspace`
  filesystem bytes and inodes). Each filesystem entry lists `roles` (`system`,
  `workspace`, `scratch`): filesystems are deduplicated by device, so a scratch tier on the
  root disk adds `scratch` to the `/` entry instead of being counted twice, and
  `scratch` is never added to `paths`. A scratch tier on its own device is reported as
  `unavailable["filesystems.scratch"]="separate_device"` (older control planes accept only
  `/` and `/workspace` entries). The workspace entry's `scratchBindMounts` counts
  bind mounts into `/workspace` whose source is under `/var/lib/workspaces-scratch/repos`;
  they are not included in `nestedMountsExcluded`. Sampling never walks directories,
  never sleeps on the request path and does not change `revision`; a sampling failure
  reports `resources.unavailable` instead of failing status.
* `fs-job-start`: `jobId`, `mode` `scan` or `delete`, `path` (component list under
  `/workspace`) and, for delete, `expect:{dev,ino}` from the scan. The job record,
  including VM/generation identity, is persisted before launch. For deletion under `repos`,
  root orchestration prepares owned scratch mounts in the host namespace first; failure
  does not launch recursive deletion. `systemd-run` launches `disk_usage.py` as the coding user with a
  time and entry budget; a repeated `jobId` returns the same job and is never relaunched.
  One job runs at a time and jobs are refused while stopping. The helper opens each path
  component with `O_NOFOLLOW` relative to the parent descriptor, stays on one filesystem,
  and deletes only strict descendants of `repos`, `home` or `artifacts` owned by the
  coding user whose device/inode and full ancestor chain still match. A mounted target,
  nested directory or file mount is refused, including same-device binds; the job's
  systemd writable binds on the three user-data roots are not checkout mounts.
  Scratch bind mounts (sources under
  `/var/lib/workspaces-scratch/repos`) are listed as excluded, non-selectable rows with
  `storage:"scratch"` and the `separate_mount` error, and are never walked or counted, even
  when scratch shares the workspace device.
  A root scan also reports the managed ext4 recovery directory as an excluded,
  non-selectable, non-deletable row with `exclusion:"ext4_recovery"`, unknown bytes and
  no scan error. Its contents are never walked or included in user-data totals;
  `complete` describes user data, not recovery contents or filesystem-wide allocation.
  Recognition requires a safe root-owned regular ownership marker, the opened
  workspace's kernel mount ID matching an ext4 mount of the filesystem root,
  root inode 2, and root-owned, non-group/other-writable directory inode 11 on the
  same device at root `lost+found`. Mounts at or beneath that directory prevent
  recognition. Missing/unreadable kernel evidence, a different filesystem or inode,
  and nested user directories named `lost+found` follow ordinary scan rules:
  unreadable user data still reports `partial`/`permission_denied`. No name-only
  exclusion or deletion-policy relaxation is applied. The extra `exclusion` field
  is helper evidence; older control planes retain the excluded/protected row.
* `fs-job-status` / `fs-job-cancel`: report `running`, `completed` (with the helper's
  JSON result), `lost` or `not_found`. Quiesce, Git save and discard stop running
  folder jobs before freezing.

### Targeted disk-usage checks

The synthetic scanner, deletion and filesystem-job checks run without privileges:

```sh
PYTHONDONTWRITEBYTECODE=1 python3 -B -m unittest discover \
  -s templates/development-workspaces/tests -p test_disk_usage.py -v
```

The same test file includes an explicitly opt-in guest check. **Only inside a
disposable image-builder VM** already marked as described above, stage this source
at a coding-user-readable location (for example `/opt/workspaces-image-source`) and
run as root:

```sh
WORKSPACES_DISK_USAGE_GUEST=RUN-DISK-USAGE-ON-DISPOSABLE-GUEST \
  unshare --mount --propagation private \
  python3 -B -m unittest discover \
  -s templates/development-workspaces/tests -p test_disk_usage.py \
  -k DiskUsageExt4GuestTests -v
```

The guard requires the root-owned disposable-builder marker, actual VM detection,
and a mount namespace distinct from PID 1. It formats only its newly created
64 MiB temporary image, mounts it privately, and invokes the scanner as UID/GID
2000. It checks a complete fresh root scan, recovery deletion refusal, partial
results for real unreadable user data (including nested `lost+found`), and an
excluded separate-device mount over recovery. It unmounts and removes its fixture.
Do not run it against an existing workspace or on a shared host. The #82 fix has
synthetic coverage; fresh disposable-volume guest verification remains pending
until this guarded check is actually run. It is not live-provider qualification.

Normal stop checkpoints a bounded, sanitized runtime rejection in the operation's
`providerEvidenceJson.lastRuntimeRejection` **before** attempting to reopen a failed
freeze. A refused recovery `status`/`resume`/`cancel-drain` is retained separately as
`lastRuntimeRecoveryRejection`; it does not overwrite the original failure.
Worker warning logs contain the same safe diagnostic, and a blocked operation's
error message includes the original reason code. Only allowlisted static reason
codes, freeze stages, failure categories, tools/subcommands and bounded numeric
exit/errno values cross this diagnostic boundary. Unknown text is withheld, not
truncated: no command output, exception messages, paths, credentials or repository
contents are persisted or logged. Older runtimes still yield known reason codes;
the updated runtime additionally identifies the failing freeze stage and adapter
failure category. Missing diagnostics never authorize deletion or bypass SSH,
generation, task, filesystem, Git-save or provider-ownership checks.

#### T3 shell is idle but committed state blocks stop

`workspace_drain_t3_committed_state` means the guest found nonterminal or
unrecognized committed T3 state independently of the shell. The message retains
the first rejecting table (for example `t3_work_not_idle_nodes` or
`t3_work_not_idle_turn_items`) while the worker retries. Terminal runs, attempts
and provider turns are not proof that their nodes or turn items are idle.

1. For a shell that is still busy, cancel queued work in T3 and use **Force Stop**
   on the affected thread for background work you intend to stop. Pinned T3
   [Force Stop targets the active or latest started run](https://github.com/pingdotgg/t3code/blob/24e77473ecf41bc1f7142529c7911c1c1aae1d76/apps/server/src/orchestration-v2/ThreadManagementService.ts#L1200-L1273);
   it is not a historical projection repair.
2. If the shell is idle but the guest still reports this hold, the supported
   recovery to try on this pin is a graceful restart of the **guest T3 service**,
   not a VM deletion. Coordinate operator maintenance and confirm that the guest
   has reopened after the failed freeze and that restarting won't interrupt
   genuine work. Restart `workspaces-t3.service` through its existing systemd
   supervisor, then let the normal stop retry verify both gates. T3 0.1.9
   [reconciles at shutdown and startup](https://github.com/pingdotgg/t3code/blob/24e77473ecf41bc1f7142529c7911c1c1aae1d76/apps/server/src/serverRuntimeStartup.ts#L475-L545)
   and [settles certain stale background items and linked nodes](https://github.com/pingdotgg/t3code/blob/24e77473ecf41bc1f7142529c7911c1c1aae1d76/apps/server/src/orchestration-v2/ProviderRuntimeRecoveryService.ts#L661-L716).
3. If the rejection persists, keep compute and the retained data intact and
   escalate to T3 support with the pin, sanitized reason/stage and counts by
   entity type/status. The pinned recovery scan
   [selects specific candidates](https://github.com/pingdotgg/t3code/blob/24e77473ecf41bc1f7142529c7911c1c1aae1d76/apps/server/src/orchestration-v2/ProjectionStore.ts#L4322-L4402);
   it does not sweep every historical orphan node or turn item. No supported
   repair for arbitrary residual rows was found in 0.1.9. `t3 doctor` compacts
   storage; it is not a lifecycle-state repair. Do not edit SQLite, invoke
   private settlement commands, or bypass the guest check.

T3 is the known infrastructure main process, **not a quiescence API**. By default child
terminals, providers/builds and every other coding process block unless finished. Parent must fence
gateway/session mutation admission and account for in-flight work before requesting freeze;
unobservable/in-process autonomous tools require explicit task tracking or remain a live
integration blocker. Do not enable automatic destruction for an uninstrumented tool. Freeze
remains in force until deletion or explicit authorized resume. For the known idle T3 main,
the supervisor queues graceful stop while frozen, briefly thaws to permit its flush, then
refreezes and inventories again. The unit targets **only its main process**, with no fallback
SIGKILL; unexpected children remain alive and block the receipt rather than being killed.

### Operator-qualified preview services

Only explicitly registered `workspaces-preview-[a-z0-9-]{1,24}.service` units may join
`infrastructureUnits`; every infrastructure unit must also appear in `stopUnits`.
The default image contains no preview application or preview service unit. Users must
explicitly create and register a workload; registration does not turn a bundled test
application into product infrastructure.

Preview admission exempts **only the unit's MainPID**, with all four process UIDs equal to
2000 and exact cgroup `0::/workspaceswork.slice/<unit>`. The loaded system unit must report
`Slice=workspaceswork.slice`, `KillMode=process`, and `SendSIGKILL=no`. Its fragment and every
drop-in, including their directory ancestry, must be root/operator-owned and not group/other
writable. Transient/unknown fragments and unsafe settings block, including before any preview
stop. Children (even other UIDs), additional preview-cgroup processes and leftovers after
MainPID exits block; preview ancestry never qualifies an idle terminal shell.

Preview units use the same queued main-only graceful stop, controlled thaw, mandatory
refreeze and post-stop inventory as T3. Failed stops/refreezes cannot yield a quiescence
receipt. Fresh bootstrap and authorized resume start only T3. Registration alone
does not start an application; users choose when to start their workloads.

Registration and a healthy HTTP probe are **not** proof that a general application has no
in-process work. The operator must explicitly qualify each application: the initial test
preview is static HTTP with no autonomous/future jobs, proxy admissions fenced, and the
maximum 60-second requests drained for 75 seconds before freeze. Uninstrumented schedulers,
background threads doing useful work, queues or other invisible activity remain unsupported;
do not assert `unobservableToolsAbsent` for them. Native/live service qualification remains
required; the default tests exercise fixtures only.

### Pinned T3 committed-state proof

After the kernel freeze, a read-only `mode=ro`, `query_only` SQLite transaction checks
`/workspace/t3/userdata/statev2.sqlite` (never `immutable=1`; committed WAL must be observed).
Pinned v0.1.9 `ProjectionStore.ts` defines the HTTP cursor as
`MAX(sequence)` in `orchestration_events` for `application_event_version=2` and
`aggregate_kind='thread'`; it must match `t3SnapshotSequence`. `EventSink.ts` updates only
the relevant metadata row `thread-projections`, schema version 2, against the **global V2**
event cursor, including project events. Unrelated metadata rows are not compared. Empty
thread sets with project events are valid. Verified run/attempt/node/session/provider-thread/
turn/subagent/runtime-request/turn-item/outbox status columns must contain only known inactive
states; pending, active, missing schema, unknown statuses or lagging projections block.
The same committed-idle checks run after service stop/flush (allowing its final cursor to
advance); receipts bind the original HTTP cursor plus final `t3State` cursor/count evidence.
This proves committed state, not invisible RAM work: parent admission fencing, its complete
real HTTP snapshot and the independent process guard remain mandatory.

An inspection failure after freeze leaves `Frozen`, with no deletion receipt. Parent must
cancel/revoke its deletion intent, `resume` the same operation, then reopen the gateway so
users can resolve work. Retry of an already completed read-only freeze revalidates the
recorded final state and does not replay user workload hooks or shutdown commands.

### Qualified unused terminals

Only after the gateway's source-verified terminal-input codec, authenticated activity delivery,
and per-generation tunnel have been qualified may the parent set root-owned runtime config
`observedTerminalInputs:true` (default false). Parent must first revoke routes/sessions, wait
75 seconds for its maximum HTTP/WS authorization windows, and prove a complete idle real T3
orchestration-shell snapshot; unknown/active/pending runs block. Passive observe/ping is not
input activity. A live lease/process still blocks, irrespective of expiry or shell classification.

The opt-in OS adapter permits only single-threaded UID-2000 root-owned image bash/dash processes
descended from the known `workspaces-t3.service` MainPID in that exact service cgroup. The shell
must be an interactive/automatic-TTY invocation, a foreground session/process-group leader,
have matching stdin/stderr PTY slave character devices and controlling-terminal device number,
have no child, zombie, stopped or background job, and be sleeping in kernel `read(0, buffer,
positive-count)` with a recognized TTY-read wait channel. Kernel `/proc/PID/stat` supplies the
foreground PGID/session; foreign-terminal ioctl results are not guessed. PID/start-time, process
tree, executable ownership, descriptors and syscall evidence are rechecked, including the
second **frozen** inventory. All other coding processes remain unknown work and block.

These are process-fixture tests, **not live PTY proof**. Before enabling automatic idle deletion,
qualify actual unused PTYs, foreground/background/stopped jobs and active agents on the exact
amd64 image/kernel, through gateway fencing and cgroup freeze/stop. A `pselect6`/`poll` shell,
unreadable syscall/children file, unknown wait channel or freezer-obscured read is deliberately
unsupported and blocks; low CPU, `S` state alone and a remembered pre-freeze read are insufficient.
Such a kernel/shell needs a separately reviewed pidfd-bound native syscall-stop adapter
(ptrace plus stopped-register/FD-set inspection for polling waits, or equivalent kernel
instrumentation retaining continuous read-wait evidence across freezer entry). No such native
adapter is installed or silently substituted here.

Git save inventories **every** discovered checkout and linked worktree, canonical authorized
origin, exact base SHA/branch/ref, clean tracked/untracked state, full/non-shallow history,
and unsupported LFS/submodules. Fresh exact-ref/default-branch evidence and fetched remote
objects in a separate disposable verifier prove final HEAD reachability; the frozen checkout
is never modified by fetch. It runs twice and is repeated on receipt validation. It never
commits, pushes, force-pushes, writes default branches, opens PRs or uploads workspace data.
Ignored files/root databases are **not saved by Git**. Failures mean Awaiting Git save and
continued billing, never deletion or a hidden filesystem backup.

## Primary encrypted backup / separate-volume restore

A retained volume is not a backup and this runtime makes **no provider-volume encryption
claim**. Configure primary `backup` with `transport:"management-ssh-stream"`, public age
`recipient`, independent external `keyRecoveryId`, independent `destinationIdentity`,
positive `retentionDays`, and optional `maxAgeSeconds` (default 86400).
**No recovery private key is accepted or sent to VMs/T3.**

1. `quiesce` with `backupRequired:true` (or configured `requireFreshBackup:true`) freezes,
   stops and encrypts, then blocks unmount pending off-host acknowledgement. `backup` returns
   `PrimaryBackupPrepared` with object ID, ciphertext SHA256, representative hashes,
   retention deadline and `AwaitingOffHostAcknowledgement`. No plaintext archive is staged.
2. `runtime.py export-backup --receipt-id <prepared-id>` streams **ciphertext only** to
   stdout over pinned SSH. Parent/operator stores and fsyncs it on an independently owned
   machine/store, verifies SHA256, and confirms the separately recoverable private key.
3. Root authenticated `backup-ack` with the same operation, `preparedReceiptId`,
   `ciphertextSha256`, `destinationIdentity`, `durablyStoredOffHost:true`,
   `independentRecoveryKeyConfirmed:true` returns `PrimaryBackup` / `VerifiedOffHost`,
   and removes the local encrypted staging file. Resume `quiesce` with that operation.

An optional direct pinned-SSH ciphertext uploader is also implemented: omit streaming
transport and provide `host,user,directory,knownHostsFile,sshIdentityFile,destinationIdentity`
in the backup policy. It requires a genuinely off-host target and exclusive remote create,
fsync/checksum. This needs separately authorized outbound SSH and is **not enabled by the
default provider egress rules**. Streaming avoids any backup-store login on coding VMs.

Status distinguishes not configured, never backed up, awaiting acknowledgement, failed,
fresh and stale. Retention deadline is an operator retention contract, **not proof a remote
object still exists**. Remote retention enforcement/freshness polling belong to the backup
operator; these hooks never silently prune independent copies. Default stop need not make
a full backup; opt-in fresh-backup policy blocks stop if it cannot complete.

Restore decryption occurs **on the operator machine**:

1. Authenticate `restore` to a new VM generation with a **separate**, empty, correctly owned
   ext4 volume and `authorizedRestore:true`, `independentRecoveryKeyConfirmed:true`,
   `backupReceipt:<parent-persisted verified PrimaryBackup>`. Original-volume restore,
   nonempty targets, corrupt receipts and ephemeral kind are rejected.
2. Verify ciphertext SHA256 off VM, then stream `age --decrypt --identity <operator-key> …`
   into pinned SSH running `runtime.py restore-import --operation-id <restore-operation>`.
   Only the decrypted application archive crosses this channel; the key never does.
3. Safe tar extraction rejects traversal/device/unsafe links, preserves the target ownership
   marker, restores the coding workspace ownership, verifies representative hashes, fsyncs and
   returns `PrimaryRestored`. Partial/failed restores cannot be blindly replayed over data.
   Persist the receipt, then bootstrap normally. Live separate-volume restore qualification
   is still required before claiming a backup is operationally recoverable.

Symlinks are saved as metadata, never followed: data outside the workspace is not backed up.
Restore defers all symlinks until regular-file extraction, hash verification, ownership and
layout are complete. Archive writes beneath a link, conflicting links and hardlinks to
volume metadata or unresolved links are rejected. This preserves ordinary tool/virtualenv
links without following them during privileged extraction or excluding user tool directories.
The source volume's ownership marker and empty root `lost+found` are not restored over the
target's metadata; nonempty recovery data blocks backup instead of being silently omitted.

### Storage layout 2 backups

In layout 2 the live checkouts are on the VM disk, so a backup holds the checkout tree only
as the final checkpoint of the stop that froze the workspace:

* Backup requires that stop's checkpoint and checks that it is still the current one before
  and after the archive is written. It reads the whole checkpoint back first, so a damaged
  checkpoint fails the backup instead of reaching the archive.
* The archive keeps `repos` as an empty directory and never enters it, even if the checkout
  tree is mounted on it. From the checkpoint store it takes only `current.json`, the current
  manifest and the objects that manifest uses. Unfinished saves (`tmp`, `staging`), the lock,
  the attempt status, the previous checkpoint and unused objects stay behind. The layout
  marker names the source volume and is left out too. T3 state and the rest of `/workspace`
  are archived as in layout 1.
* The receipt adds `storageLayout: 2` and the checkpoint ID and manifest hash.
* Restore requires a target with the same storage layout as the backup. Moving a layout 1
  backup to layout 2 is a migration, not a restore.
* After extraction and the representative hash check, the store is made root-only again and
  its checkpoint is checked against the receipt and read back in full. It is then republished
  under a new ID, bound to the target volume, with a new tree token, and the source volume's
  manifest is removed. A tree restored from the source volume therefore never matches it. A
  new layout marker records the new checkpoint as the volume's first one, and the receipt
  returns it as `checkpoint` with `adoptedFrom`.
* The checkpoint store and layout marker stay root-owned and `repos` stays the empty 0555
  mount point. The next bootstrap restores the checkout tree onto the VM disk from the
  checkpoint, as on any replacement VM.
Archives stay on the volume's filesystem: a nested mount (such as a scratch build-output
mount) is recorded as an empty directory and never entered.

## T3 session issuance / outstanding qualification

`t3 serve --host 127.0.0.1 --port 3773 --base-dir /workspace/t3 --no-browser`; no base-path
support. Parent uses separate per-VM/generation HTTP+WS origins/tunnels. The installed
`issue-t3-session.py --vm-id … --generation … --operation-id … --subject … --ttl 300
--scope <authorized-scope> [--scope …]` emits **only the bearer to sensitive stdout**.
Pipe directly into the control-plane secret store. It checks actual CLI help before using
`t3 auth session issue --token-only --ttl 300s --label … --subject … --scope … --base-dir …`.
Upstream uses **singular repeatable `--scope`**, not `--scopes`; no DPoP binding is requested.

Not executed here: paid image capture, real amd64 image boot, actual T3 headless issuance,
real cgroup freeze/stop behavior, privileged ext4 mount/unmount, real age encryption and
off-host durability, independent-key separate-volume restore, provider network isolation.
The development host is ARM64; the hash-verified amd64 T3 binary cannot execute here.
All these are explicit live qualification gates, not silently successful fake integrations.
