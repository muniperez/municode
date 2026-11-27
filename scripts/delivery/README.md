# Local release builds

From a clean checkout, with Git, Python 3.11+, .NET 10, Node.js 22/npm, and
Docker with Buildx:

```sh
scripts/delivery/build-release.sh --ref HEAD
```

The script builds the exact commit in a temporary detached worktree. The
selected revision must include the local-build Compose contract. The script
reuses the release package layout and the three component Dockerfiles from CI.
It builds `linux/amd64` images by default; use `--platform linux/arm64` for an
ARM64 deployment host. Docker must support building the selected platform.

The last line is the path to `.local/artifacts/release/local-<rev12>-<bundle12>/`,
where the suffix is the first 12 hex characters of the bundle archive's
SHA-256. Pass that directory to `municode deploy plan --instance <name>
--bundle-dir <dir> --trust local-build`. It contains the usual
`release-manifest.json`, `municode-0.0.0-local-linux-any.tar.gz` and
`checksums.txt`, but no Sigstore bundle. Check the output with
`sha256sum --check --strict checksums.txt` from inside the release directory.
`--allow-dirty` is for packaging diagnostics: it still builds only committed
files and records `sourceDirty: true`, which is not accepted for deployment.

The archive contains `images/api.tar`, `images/worker.tar` and
`images/gateway.tar`. For each, the manifest's `imageArchives` records its path,
archive SHA-256, Docker image ID and local Compose reference. On the host, the
deploy agent verifies the archive, loads it using `docker image load --input`,
verifies the resulting ID, and tags it with the recorded reference. Compose
uses the local images without pulling them; other dependencies retain their
normal pinned registry references.
