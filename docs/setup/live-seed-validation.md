# Live seed validation

This checklist is intentionally separate from synthetic CI. It is a manual
validation for a designated disposable machine and provider account. It has not
been executed as part of implementation, and it must not use production
credentials, existing customer data or a shared installation.

## Safety gates

- [ ] Select a new, funded test provider account; record its billing and cleanup
      owner. Register no provider credential unless you are validating the seed opt-in.
- [ ] Use a fresh public DNS zone or isolated records under operator control.
- [ ] Use an exact published `municode-vX.Y.Z`, released `municode` CLI,
      Cosign 2.6.5 and the provider's current API token in an owner-only file.
- [ ] Confirm no pre-existing control-plane server carries the generated seed
      operation label. Confirm baseline zero coding VMs separately.

## Plan and interruption

- [ ] Run `municode seed plan` for each provider path selected for qualification.
      Check the provider-reported region, size, monthly/hourly amount, currency,
      exclusions, exact source revision, manifest digest and bundle digest.
- [ ] Run `municode seed apply` with the displayed approval digest.
- [ ] Interrupt or terminate the workstation process immediately after the
      provider reports VPS creation and before any SSH trust completes.
- [ ] Confirm exactly one operation-owned control-plane VPS exists in the
      provider console. Resume from a new process and confirm no second create
      request/server appears.
- [ ] Confirm apply or resume pauses for independent SSH identity after public
      IPv4 assignment and returns before SSH host commands or secret transfer.
      This is expected, not an installation failure; the VPS remains billable.
      Resume without confirmation must repeat the pause.
- [ ] For Hetzner, sign in to the authenticated Hetzner Console, open the seed
      project, select the server, and match its ID, public IPv4 and operation
      ownership marker to seed. Select the console icon at the top right of the
      server page to open the VNC console on the installed Ubuntu system.
- [ ] Log in there as `root` with the initial password supplied by Hetzner. If
      created with an SSH key or the password is unavailable, use **Rescue** >
      **Root password** > **Reset root password** on the server page and read the
      new password only in the authenticated Console. Do not enable or boot
      Rescue: its SSH host key is not the installed system's key.
- [ ] If Ubuntu requires changing the initial root password before a shell,
      complete the current-password, new-password and confirmation prompts;
      log in again if prompted. Password input is not echoed. Seed disables SSH
      password login; use this password only for the provider console and do not
      re-enable SSH password authentication.
- [ ] In that server's root console shell, run the literal command below,
      preserving the underscores in the filename. Read only the `SHA256:...`
      field from the `(ED25519)` output, not the comment or whole line:

```sh
ssh-keygen -lf /etc/ssh/ssh_host_ed25519_key.pub
```

- [ ] On the operator workstation, resume with that independently obtained
      fingerprint:

```sh
municode seed resume <operation> \
  --credential-file /protected/provider.token \
  --confirm-console-fingerprint SHA256:...
```

- [ ] Confirm the CLI compares the SSH key offered by the recorded VPS public
      IPv4 to the console fingerprint before pinning it. Network-observed keys
      alone are not a trust source. Verify a different candidate or changed
      pinned fingerprint is rejected without SSH host commands or secret
      transfer; reconcile identity instead of accepting it or replacing the pin.
      See the [operator handoff](../operator.md#seed-a-fresh-control-plane) and
      [Hetzner console instructions](https://docs.hetzner.com/cloud/servers/getting-started/vnc-console/).

## DNS, release and first owner

- [ ] Create the exact dashboard/API `A` records and gateway wildcard `A` record
      printed by seed. Remove competing A/AAAA records.
- [ ] Resume after DNS propagation. Confirm Sigstore identity, checksums,
      component hashes, all five release components and digest-pinned dependencies
      are accepted from the exact release.
- [ ] Confirm HTTPS succeeds with trusted certificates for dashboard and API,
      `/health` is ready, the setup-status endpoint responds, and public gateway
      DNS is present. No coding VM is needed to perform this control-plane check.
- [ ] Confirm Docker services include API, worker, gateway, dashboard, PostgreSQL,
      Kratos, OpenBao and Caddy. Inspect only sanitized status/log output.
- [ ] Read the protected local setup token, open `/setup` over HTTPS and complete
      first-owner setup. Sign out, sign back in and list workspaces without
      creating one. The one-time token must not be reusable.
- [ ] Confirm the provider credential never appears in the release, journal,
      control-plane environment, logs or URLs. If you answered no or ran
      unattended, also confirm no OpenBao path or provider account holds it.
- [ ] On a second disposable seed, answer yes to the workspace-provider prompt.
      After first-owner setup, confirm exactly one seed provider account in the
      first organization, a `provider.created` audit from `system:seed`, the
      staged file removed from the `seed-provider` volume, and no duplicate
      after an API restart.
- [ ] Confirm the host policy's `DevelopmentWorkspaceWorker:ManagementAddresses`
      holds the VPS's public IPv4 `/32`.
- [ ] Confirm exactly zero workspace/coding VMs exist. Qualifying an image is a
      separate action, and so is registering a provider when the seed opt-in was
      declined. Workspace creation is outside seed validation.

## Recovery and cleanup

- [ ] Re-run resume after verified completion and confirm it reports an existing
      completed operation rather than provisioning another VPS.
- [ ] Export/secure the OpenBao unseal/recovery material, installation secrets,
      PostgreSQL backup and Caddy state according to the operator policy.
- [ ] Reconcile all provider resources and invoices, then explicitly decommission
      the disposable VPS and DNS through normal provider/host procedures. Seed
      does not silently roll back an installed control plane.

Evidence should record provider, region, size, exact release tag/source,
operation ID, timestamps, public health result, resource counts and cleanup,
without credentials, setup token, OpenBao material, private keys or bearer URLs.

## Live deploy validation

This deploy checklist is also manual and is not part of CI. Use only a
designated disposable control plane with isolated data and provider capacity.
The deploy CLI requires an SSH-pinned host and a release installed with deploy
support. Do not run these steps on `dev.muni.dev` or another shared instance
without the owner's explicit approval.

### Disposable seeded instance

- [ ] Start with an older signed release and confirm it is healthy. Register it
      with `municode instance add` only if seed did not register it.
- [ ] Run `municode deploy plan --instance <name> --release latest`. Review the
      exact target, preflight, migration and configuration changes, blockers,
      approval digest and estimated drain. Confirm latest resolves to the
      intended published release.
- [ ] Apply the reviewed plan with `municode deploy apply <operation> --approve
      <digest> --offhost-backup <protected-dir>`. Confirm health and
      `/health/version` report the target release; confirm the local backup
      manifest verifies and keep its secret-bearing files protected.
- [ ] In a disposable test release, inject a controlled migration failure.
      Confirm the agent restores the matched snapshot and starts the previous
      release before traffic may have opened. Check database and public health.
- [ ] Complete a successful deploy, then test manual rollback after traffic
      opened. Read the exact `openAt` from `municode deploy status <operation>`;
      use `--restore-database --accept-data-loss-since <openAt>`. If the worker
      resumed, also use `--accept-provider-reconciliation` and reconcile the
      exported provider operations. Confirm the previous release and health.
- [ ] Build an unsigned bundle from a clean source revision with
      `scripts/delivery/build-release.sh --ref <git-ref>`. Plan it with
      `municode deploy plan --instance <name> --bundle-dir <dir> --trust
      local-build`, review the local-build trust label and digest, apply it and
      confirm `/health/version` identifies the result.

### `install.sh` instance

- [ ] Prepare a disposable host using a release with deploy support and
      `install.sh`. Create a non-root SSH account with passwordless `sudo -n`.
- [ ] Independently verify its SSH host fingerprint and register it with
      `municode instance add <name> --ssh <alias> --confirm-host-fingerprint
      SHA256:...`.
- [ ] Repeat plan and apply from the older signed release to latest. Confirm
      pinned SSH works as the non-root user, the deploy succeeds, and
      `/health/version` reports the target.

### Owner-approved personal instance

- [ ] Do not reinstall `dev.muni.dev` without explicit owner approval. There is
      no backwards compatibility: reinstalling loses workspace data, so record
      and accept that impact before proceeding. Reinstall it from a release
      containing deploy support, either by reseeding or by installing on a fresh
      host and registering it with `municode instance add`.
- [ ] Publish or build a newer release, then run deploy plan and apply against
      `dev.muni.dev`. Confirm `/health/version` reports the newer release and
      record health, timestamps and any recovery actions on issue #177.

Keep logs and evidence under `.local/artifacts/`; do not record credentials,
unseal material, backup contents, private keys or bearer URLs.
