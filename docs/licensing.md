# Workspaces licensing

The Workspaces control-plane implementation and its tests incorporate source
from MiniCloud at `b0e4edc3398364210c9e274e0b1de9cd8352e3fe`. These files
retain the upstream license terms.

| Files | License |
| --- | --- |
| `src/Municode.Api/`, `src/Municode.Common/` except the paths below, `tests/Municode.Tests/`, `docs/setup/identity.schema.json` | [AGPL-3.0-only](../LICENSES/AGPL-3.0-only.txt) |
| `src/Municode.Common/Contracts/`, `src/Municode.Common/Validation/ValidationResult.cs` | [MPL-2.0](../LICENSES/MPL-2.0.txt) |
| `src/Municode.Worker/`, `src/Municode.Gateway/`, `templates/development-workspaces/`, `tests/Municode.Worker.Tests/`, `tests/Municode.Gateway.Tests/`, `tools/workspace-gateway-benchmark/`, `scripts/workspace-browser-acceptance.mjs` | [AGPL-3.0-only](../LICENSES/AGPL-3.0-only.txt) |

The runtime port uses the workspace source from the same MiniCloud revision.
The image's remount qualification and its synthetic tests also incorporate the
follow-up at `d5417ad89e9e43b72215921a55621f7c4cfa8742`.

Third-party dependencies retain their respective license terms.
