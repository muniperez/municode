# Municode

This exact source release contains the Municode API, worker, gateway, dashboard
static build and platform-specific CLI outputs. It installs independently and does
not require application hosting services or an account in another Municode
product.

The included operator guide is the source of truth for DNS, OpenBao initialization,
first-owner bootstrap, startup and rollback. Client projects supply their compiled
outputs through the package targets in `packaging/targets.json`; this release does
not include a shared client framework. The released CLI owns `municode seed`,
which verifies the exact signed release and resumably provisions one
control-plane VPS without creating coding VMs. It imports the provider
credential as a workspace provider only when an interactive operator opts in.
