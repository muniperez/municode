# Configure the Workspaces API

Start with [the example](workspaces.example.json). Set
`DevelopmentWorkspaces__ConfigurationFile` to its absolute path to load a local
copy. Environment variables take precedence. Use an HTTPS endpoint for browser
sign-in and exactly matching origins in `Console:BaseUrl`,
`Kratos:DashboardOrigin`, and `Cors:AllowedOrigins` for a single-origin setup.

## Database and first operator

- For PostgreSQL, set `Database__Provider=Postgres` and
  `ConnectionStrings__Workspaces` to a dedicated empty database.
  `Database__PasswordFile` can supply the password from a protected file.
  Run the same `migrate` command before starting the API. Each engine has its
  own initial migration and model snapshot. This setup is for fresh installations.
- Configure a separate Kratos instance with its own storage, cookie names, and
  secrets. Use the supplied [identity schema](identity.schema.json) as the default
  `user` schema and enable password sign-in. To offer passkeys, also enable
  Kratos's `passkey` method with `rp.id` set to the dashboard host name and
  `rp.origins` set to exactly `Kratos:DashboardOrigin`, and set
  `Kratos:PasskeyEnabled=true` on the API. Configure the public base URL as
  the private endpoint reachable by the API, keep the admin URL private, and
  allow the Workspaces console as a browser return origin. The Kratos
  `session.lifespan` must cover the longest configured Workspaces idle window
  if remembered sessions are enabled. Workspaces issues
  `__Host-workspaces-session` and validates the attached Kratos session.
- Set `Bootstrap__InstallationId` to a stable installation identifier. Prepare
  a one-time setup token after migration. Substitute the installation and
  operation identifiers for your instance.

```sh
umask 077
mkdir -p .local/artifacts/setup
printf '%s\n' '{"installationId":"workspace-example","operationId":"first-operator"}' |
  dotnet run --project src/Workspaces.Api -c Release --no-build -- \
    bootstrap prepare --stdin --output-file "$PWD/.local/artifacts/setup/setup-token"
```

Complete first-operator setup over HTTPS using `POST /api/account/setup` with the
configured origin, or use `bootstrap complete --input-file <protected-file>`.
The protected JSON for the command contains the same `installationId` and
`operationId`, plus `setupToken`, `email`, `password`, `displayName`,
`organizationName`, and `organizationSlug`. The command creates the first owner
and binds the owner's Kratos identity to Workspaces. Remove the protected input
and setup token after completion.

## Providers, images, and Git

Configure `OpenBao:Address`, a dedicated KV v2 `MountPath` (default
`workspaces`), a deployment-specific `EnvironmentPrefix`, and `TokenFile`.
Grant the service access to its provider and integration credential paths
under `env/<environment>/orgs/`. Use distinct credentials for each installation.

An organization owner or admin can register a provider through `/api/providers`
and a plan through `/api/plans`. In `DevelopmentWorkspaces`, register a
runtime-qualified provider image with its account, allowed regions, image ID,
manifest SHA-256, and T3 protocol metadata. Match the image to an allowed plan,
SKU, region, and profile. Supply dated storage and compute estimates before
enabling admissions. Read `GET /api/development-workspaces/selections?organizationId=…`
to verify what the organization can select. Rotation of provider credentials
requires completed workspace operations and retired compute. Replacement
credentials must still verify any retained volumes.

Set `GitHub:ApiUrl` to the canonical public HTTPS API origin for GitHub App callbacks.
For ephemeral Git workflows, add an explicit tenant/repository/base-ref push
policy in `DevelopmentWorkspaces:Repositories`. A GitHub App connection created
under `/api/github-apps` must belong to the same organization. The issuer checks
the installation and selected repository before minting a short-lived,
repository-scoped token. An operator can also select a short-lived exact
OpenBao credential reference under that organization's
`integrations/workspace-repository-*/credentials/` path.

## Integration contracts

Workspace client routes remain under `/api/development-workspaces`. Browser
mutations use `X-Workspaces-CSRF`. Scoped API keys use `workspaces:read` and
`workspaces:write`. Provider, plan, and GitHub administration have their own
scopes. CLI device login exchanges an approved session once and persists only
the generated API key's hash.

Runtime projects in this repository can reference `Workspaces.Common` for
`WorkspaceDbContext`, workspace services, DTOs, gateway options and
`IDevelopmentWorkspaceProvider`. Use `WorkspaceOwnership.Manager` in the
`managed-by` label. API enrollment and gateway authorization use their
existing generation-bound HTTP contracts. Gateway service endpoints accept the
`X-Workspaces-Gateway-Credential` header. The API queues lifecycle operations.

See [runtime setup](runtime.md) for worker, gateway and prepared-image configuration.
The worker claims and executes them. SSH keys and gateway service credentials
belong to the Workspaces runtime installation.
