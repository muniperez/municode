# Live seed validation

This checklist is intentionally separate from synthetic CI. It is a manual
validation for a designated disposable machine and provider account. It has not
been executed as part of implementation, and it must not use production
credentials, existing customer data or a shared installation.

## Safety gates

- [ ] Select a new, funded test provider account; record its billing and cleanup
      owner. No provider credential may be registered in Workspaces.
- [ ] Use a fresh public DNS zone or isolated records under operator control.
- [ ] Use an exact published `workspaces-vX.Y.Z`, released `workspaces` CLI,
      Cosign 2.6.5 and the provider's current API token in an owner-only file.
- [ ] Confirm no pre-existing control-plane server carries the generated seed
      operation label. Confirm baseline zero coding VMs separately.

## Plan and interruption

- [ ] Run `workspaces seed plan` for each provider path selected for qualification.
      Check the provider-reported region, size, monthly/hourly amount, currency,
      exclusions, exact source revision, manifest digest and bundle digest.
- [ ] Run `workspaces seed apply` with the displayed approval digest.
- [ ] Interrupt or terminate the workstation process immediately after the
      provider reports VPS creation and before any SSH trust completes.
- [ ] Confirm exactly one operation-owned control-plane VPS exists in the
      provider console. Resume from a new process and confirm no second create
      request/server appears.
- [ ] Read the ed25519 fingerprint from the authenticated provider console,
      pass `--confirm-console-fingerprint`, and verify a changed fingerprint is
      rejected without host commands.

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
      control-plane environment, OpenBao paths or provider account records.
- [ ] Confirm exactly zero workspace/coding VMs exist. After owner sign-in,
      explicitly registering a provider and qualifying an image are separate
      actions; creation of a workspace is outside seed validation.

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
