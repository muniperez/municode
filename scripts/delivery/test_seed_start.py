# SPDX-License-Identifier: MPL-2.0
import json
import os
from pathlib import Path
import shutil
import stat
import subprocess
import sys
import tempfile
import unittest


ROOT = Path(__file__).resolve().parents[2]
SEED_START = ROOT / "packaging" / "bundle" / "seed-start.sh"


INSTALLER = r"""#!/bin/sh
set -eu
while [ "$#" -gt 0 ]; do
    case "$1" in
        --install-root) install=$2; shift 2 ;;
        --secrets-dir) secrets=$2; shift 2 ;;
        *) shift ;;
    esac
done
mkdir -p "$install/runtime/api" "$install/runtime/worker" "$install/runtime/gateway" \
    "$install/dashboard" "$secrets/ssh" "$secrets/bootstrap" "$secrets/openbao-approle"
cat > "$install/.env" <<'EOF'
WORKSPACES_INSTALLATION_ID=replace
CONSOLE_HOSTNAME=console.invalid
CONSOLE_URL=https://console.invalid
API_HOSTNAME=api.invalid
API_URL=https://api.invalid
WORKSPACES_GATEWAY_DOMAIN=workspaces.invalid
WORKSPACES_SECRETS_DIR=/replace
WORKSPACES_DOTNET_IMAGE=synthetic
WORKSPACES_POSTGRES_IMAGE=synthetic
WORKSPACES_OPENBAO_IMAGE=synthetic
WORKSPACES_KRATOS_IMAGE=synthetic
WORKSPACES_CADDY_IMAGE=synthetic
WORKSPACES_WORKER_SSH_PUBLIC_KEY=synthetic
EOF
printf synthetic > "$install/release-manifest.json"
printf synthetic > "$install/runtime/api/Municode.Api.dll"
printf synthetic > "$install/runtime/worker/Municode.Worker.dll"
printf synthetic > "$install/runtime/gateway/Municode.Gateway.dll"
printf '<html>' > "$install/dashboard/index.html"
cat > "$install/verify_bundle.py" <<'PY'
raise SystemExit(0)
PY
for file in postgres-admin-password app-db-password kratos-db-password \
    workspace-gateway-credential kratos.env ssh/worker-key ssh/worker-key.pub \
    openbao-server.crt openbao-server.key openbao-ca.crt \
    openbao-approle/role-id openbao-approle/secret-id; do
    : > "$secrets/$file"
    chmod 600 "$secrets/$file"
done
printf secret > "$secrets/postgres-admin-password"
printf secret > "$secrets/app-db-password"
printf secret > "$secrets/kratos-db-password"
printf secret > "$secrets/workspace-gateway-credential"
printf secret > "$secrets/kratos.env"
printf secret > "$secrets/ssh/worker-key"
printf secret > "$secrets/ssh/worker-key.pub"
printf secret > "$secrets/openbao-server.crt"
printf secret > "$secrets/openbao-server.key"
printf secret > "$secrets/openbao-ca.crt"
if [ "${FAIL_INSTALL_ONCE:-0}" = 1 ] && [ ! -e "$FAKE_STATE_DIR/install-failed" ]; then
    : > "$FAKE_STATE_DIR/install-failed"
    exit 42
fi
"""


DOCKER = r"""#!/usr/bin/env python3
import os
from pathlib import Path
import sys

state = Path(os.environ["FAKE_STATE_DIR"])
configured = Path(os.environ["FAKE_SECRETS_DIR"])
secrets = configured if configured.exists() else Path(str(configured).removesuffix(".seed-pending"))
args = sys.argv[1:]
with (state / "docker.log").open("a") as log:
    log.write(" ".join(args) + "\n")

def touch(name, content="1"):
    path = state / name
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(content)

def exists(name):
    return (state / name).exists()

if args == ["compose", "version"]:
    raise SystemExit(0)
if args[:2] == ["compose", "exec"] and any(
        command in " ".join(args) for command in
        ("bao status", "bao operator init", "bao operator unseal")):
    environment = {}
    index = 2
    while args[index] != "openbao":
        if args[index] == "-T":
            index += 1
        elif args[index] == "-e":
            key, separator, value = args[index + 1].partition("=")
            environment[key] = value if separator else os.environ.get(key)
            index += 2
        else:
            raise SystemExit("Unexpected OpenBao exec option")
    if (environment.get("BAO_ADDR") != "https://openbao:8200"
            or environment.get("BAO_CACERT") != "/run/secrets/openbao-ca.crt"
            or environment.get("BAO_SKIP_VERIFY") not in (None, "false")
            or "-tls-skip-verify" in " ".join(args)):
        touch("openbao-tls-rejected")
        raise SystemExit("OpenBao exec requires verified internal TLS")
    touch("openbao-tls-" + ("status" if "bao status" in " ".join(args)
                          else "init" if "operator init" in " ".join(args)
                          else "unseal"))
if len(args) >= 3 and args[:2] == ["compose", "exec"] and "bao status" in " ".join(args):
    if os.environ.get("UNKNOWN_OPENBAO_ALWAYS") == "1" or os.environ.get(
            "MALFORMED_OPENBAO_ALWAYS") == "1":
        print("temporarily unavailable")
        raise SystemExit(0)
    initialized = exists("openbao-initialized")
    print(f'{{"initialized": {str(initialized).lower()}}}')
    raise SystemExit(0)
if len(args) >= 3 and args[:2] == ["compose", "exec"] and "operator init" in " ".join(args):
    touch("openbao-initialized")
    if os.environ.get("PARTIAL_OPENBAO_ONCE") == "1" and not exists("partial-openbao-done"):
        touch("partial-openbao-done")
        print("{")
    else:
        print('{"unseal_keys_b64":["recovery-key"],"root_token":"root-token"}')
    raise SystemExit(0)
if "openbao-configure" in " ".join(args):
    if os.environ.get("FAIL_OPENBAO_CONFIGURE_ONCE") == "1" and not exists("openbao-configure-failed"):
        touch("openbao-configure-failed")
        raise SystemExit(42)
    (secrets / "openbao-approle" / "role-id").write_text("role")
    (secrets / "openbao-approle" / "secret-id").write_text("secret")
    raise SystemExit(0)
if "api-bootstrap" in " ".join(args) and "prepare" in args:
    if os.environ.get("PARTIAL_SETUP_ONCE") == "1" and not exists("partial-setup-done"):
        touch("partial-setup-done")
        (secrets / "bootstrap" / "setup-token").write_text("partial")
        raise SystemExit(42)
    if os.environ.get("EXISTING_SETUP_RECORD") == "1":
        touch("prepare-existing")
        raise SystemExit(0)
    (secrets / "bootstrap" / "setup-token").write_text("a" * 64 + "\n")
    raise SystemExit(0)
if "api-bootstrap" in " ".join(args) and "rotate-token" in args:
    touch("token-rotated")
    (secrets / "bootstrap" / "setup-token").write_text("b" * 64 + "\n")
    raise SystemExit(0)
if "api-bootstrap" in " ".join(args) and "status" in args:
    if os.environ.get("EXPIRED_SETUP_TOKEN") == "1" and not exists("token-rotated"):
        print('{"state":"required","expiresAt":"2000-01-01T00:00:00Z"}')
    elif os.environ.get("NEAR_EXPIRY_SETUP_TOKEN") == "1" and not exists("token-rotated"):
        expires = "2099-01-01T00:00:00Z"
        if os.environ.get("NEAR_EXPIRY_SETUP_TOKEN") == "1":
            from datetime import datetime, timedelta, timezone
            expires = (datetime.now(timezone.utc) + timedelta(minutes=2)).isoformat()
        print(f'{{"state":"required","expiresAt":"{expires}"}}')
    else:
        print('{"state":"required","expiresAt":"2099-01-01T00:00:00Z"}')
    raise SystemExit(0)
if len(args) >= 2 and args[:2] == ["volume", "ls"]:
    installation = os.environ.get("FAKE_VOLUME_INSTALLATION", "workspaces-seed-operation")
    requested = next((value.split("=", 2)[2] for value in args
                      if value.startswith("label=com.municode.workspaces.installation-id=")),
                     None)
    if requested is None or installation == requested:
        print("municode-workspaces_openbao")
    raise SystemExit(0)
if len(args) >= 2 and args[:2] == ["volume", "inspect"]:
    print(os.environ.get("FAKE_VOLUME_INSTALLATION", "workspaces-seed-operation"))
    raise SystemExit(0)
if len(args) >= 2 and args[:2] == ["volume", "rm"]:
    if exists("openbao-initialized"):
        (state / "openbao-initialized").unlink()
    touch("openbao-reset")
    raise SystemExit(0)
if len(args) >= 3 and args[:2] == ["compose", "ps"]:
    print("cid-" + args[-1])
    raise SystemExit(0)
if len(args) >= 3 and args[0] == "inspect" and "installation-id" in " ".join(args):
    print("workspaces-seed-operation")
    raise SystemExit(0)
if len(args) >= 2 and args[0] == "inspect":
    service = args[-1].removeprefix("cid-")
    if service == "worker" and os.environ.get("FLAP_WORKER_ONCE") == "1":
        count = int((state / "worker-checks").read_text()) if exists("worker-checks") else 0
        touch("worker-checks", str(count + 1))
        print("false" if count == 1 else "true")
    else:
        print("true")
    raise SystemExit(0)
raise SystemExit(0)
"""


class SeedStartTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.root = Path(self.temporary.name)
        self.release = self.root / "release"
        self.release.mkdir()
        installer = self.release / "install.sh"
        installer.write_text(INSTALLER)
        installer.chmod(0o755)
        self.bin = self.root / "bin"
        self.bin.mkdir()
        docker = self.bin / "docker"
        docker.write_text(DOCKER)
        docker.chmod(0o755)
        self.state = self.root / "state"
        self.state.mkdir()
        self.install = self.root / "install"
        self.secrets = self.root / "secrets"
        self.token = self.root / "local" / "setup-token"
        self.env = os.environ.copy()
        self.env.update({
            "PATH": str(self.bin) + os.pathsep + self.env["PATH"],
            "FAKE_STATE_DIR": str(self.state),
            "FAKE_SECRETS_DIR": str(self.secrets) + ".seed-pending",
            "WORKSPACES_SEED_HEALTH_SETTLE_INTERVAL": "0",
            "WORKSPACES_SEED_OPENBAO_READY_INTERVAL": "0",
        })

    def tearDown(self):
        self.temporary.cleanup()

    def command(self):
        return [
            str(SEED_START),
            "--release-tag", "workspaces-v1.2.3",
            "--release-root", str(self.release),
            "--installation-id", "workspaces-seed-operation",
            "--dashboard-hostname", "console.example.com",
            "--api-hostname", "api.example.com",
            "--gateway-domain", "workspaces.example.net",
            "--install-root", str(self.install),
            "--secrets-dir", str(self.secrets),
            "--setup-token-output", str(self.token),
        ]

    def run_seed(self, **values):
        env = self.env.copy()
        env.update(values)
        return subprocess.run(self.command(), env=env, capture_output=True, text=True)

    def test_openbao_commands_use_verified_internal_tls(self):
        completed = self.run_seed(
            BAO_ADDR="https://untrusted.invalid:8200",
            BAO_CACERT="/untrusted/ca.crt",
        )
        self.assertEqual(completed.returncode, 0, completed.stderr)
        self.assertFalse((self.state / "openbao-tls-rejected").exists())
        for command in ("status", "init", "unseal"):
            with self.subTest(command=command):
                self.assertTrue((self.state / ("openbao-tls-" + command)).exists())

    def test_synthetic_openbao_rejects_missing_tls_settings(self):
        settings = (
            "BAO_ADDR=https://openbao:8200",
            "BAO_CACERT=/run/secrets/openbao-ca.crt",
        )
        for command in (["status", "-format=json"],
                        ["operator", "init", "-format=json"],
                        ["operator", "unseal", "synthetic-key"]):
            for missing in settings:
                with self.subTest(command=command, missing=missing):
                    args = [str(self.bin / "docker"), "compose", "exec", "-T"]
                    for setting in settings:
                        if setting != missing:
                            args.extend(["-e", setting])
                    result = subprocess.run(
                        args + ["openbao", "bao"] + command,
                        env=self.env, capture_output=True, text=True,
                    )
                    self.assertNotEqual(result.returncode, 0)
                    self.assertIn("requires verified internal TLS", result.stderr)
        self.assertFalse((self.state / "openbao-initialized").exists())

    def test_owned_partial_install_is_discarded_and_retried_from_staging(self):
        failed = self.run_seed(FAIL_INSTALL_ONCE="1")
        self.assertNotEqual(failed.returncode, 0)
        self.assertFalse(self.install.exists())
        self.assertFalse(self.secrets.exists())
        self.assertTrue((self.install.parent / (self.install.name + ".seed-pending")).exists())

        completed = self.run_seed()
        self.assertEqual(completed.returncode, 0, completed.stderr)
        self.assertTrue((self.secrets / "seed-install-complete").is_file())
        self.assertEqual(
            (Path(str(self.secrets) + ".seed-owner")).read_text().strip(),
            "workspaces-seed-operation workspaces-v1.2.3 complete",
        )

    def test_initialized_openbao_with_malformed_recovery_is_preserved_and_refused(self):
        partial = self.run_seed(PARTIAL_OPENBAO_ONCE="1")
        self.assertNotEqual(partial.returncode, 0)
        self.assertIn("preserving state", partial.stderr)
        self.assertNotIn("recoverable on resume", partial.stderr)
        pending = self.secrets / "openbao-seed-init.json.pending"
        self.assertEqual(pending.read_bytes(), b"{\n")
        initialized = self.state / "openbao-initialized"
        self.assertTrue(initialized.exists())
        original_state = initialized.read_bytes()
        owner = Path(str(self.secrets) + ".seed-owner")
        original_owner = owner.read_bytes()
        initial_log = (self.state / "docker.log").read_text()

        resumed = self.run_seed()
        self.assertNotEqual(resumed.returncode, 0)
        self.assertIn("positively reports initialized", resumed.stderr)
        self.assertIn("seed recovery output is invalid; preserving data", resumed.stderr)
        self.assertEqual(pending.read_bytes(), b"{\n")
        self.assertEqual(initialized.read_bytes(), original_state)
        self.assertEqual(owner.read_bytes(), original_owner)
        self.assertFalse((self.secrets / "openbao-seed-init.json").exists())
        self.assertFalse((self.secrets / "openbao-seed-configured").exists())
        self.assertFalse((self.state / "openbao-reset").exists())
        self.assertFalse((self.state / "token-rotated").exists())
        self.assertFalse(self.token.exists())
        resumed_log = (self.state / "docker.log").read_text()[len(initial_log):]
        self.assertIn("bao status -format=json", resumed_log)
        for mutation in ("volume rm", "rm -f", "operator init",
                         "operator unseal", "openbao-configure", "api-bootstrap",
                         "database-initialize", "kratos-migrate", "api-migrate"):
            with self.subTest(mutation=mutation):
                self.assertNotIn(mutation, resumed_log)

    def test_existing_setup_record_without_token_is_rotated_before_handoff(self):
        resumed = self.run_seed(EXISTING_SETUP_RECORD="1")
        self.assertEqual(resumed.returncode, 0, resumed.stderr)
        self.assertTrue((self.state / "prepare-existing").exists())
        self.assertTrue((self.state / "token-rotated").exists())
        self.assertEqual(self.token.read_text(), "b" * 64 + "\n")
        self.assertFalse((self.state / "openbao-reset").exists())

    def test_worker_flap_requires_three_consecutive_service_settlement_checks(self):
        completed = self.run_seed(FLAP_WORKER_ONCE="1")
        self.assertEqual(completed.returncode, 0, completed.stderr)
        self.assertEqual(int((self.state / "worker-checks").read_text()), 5)
        log = (self.state / "docker.log").read_text()
        for migration in ("database-initialize", "kratos-migrate", "api-migrate"):
            with self.subTest(migration=migration):
                self.assertIn(f"--exit-code-from {migration} {migration}", log)
        for service in ("postgres", "openbao", "openbao-agent", "kratos", "api",
                        "worker", "workspace-gateway", "caddy"):
            with self.subTest(service=service):
                checks = [line for line in log.splitlines()
                          if line.startswith("inspect -f {{.State.Running}} ")
                          and line.endswith(" cid-" + service)]
                self.assertGreaterEqual(len(checks), 3)
        self.assertIn("is installed and running", completed.stdout)

    def test_partial_setup_token_output_is_removed_then_rotated(self):
        interrupted = self.run_seed(PARTIAL_SETUP_ONCE="1",
                                    EXISTING_SETUP_RECORD="1")
        self.assertNotEqual(interrupted.returncode, 0)
        self.assertEqual(
            (self.secrets / "bootstrap" / "setup-token").read_text(), "partial")
        resumed = self.run_seed(EXISTING_SETUP_RECORD="1")
        self.assertEqual(resumed.returncode, 0, resumed.stderr)
        self.assertTrue((self.state / "token-rotated").exists())
        self.assertEqual(self.token.read_text(), "b" * 64 + "\n")

    def test_openbao_reset_refuses_volume_owned_by_another_installation(self):
        interrupted = self.run_seed(PARTIAL_OPENBAO_ONCE="1")
        self.assertNotEqual(interrupted.returncode, 0)
        refused = self.run_seed(FAKE_VOLUME_INSTALLATION="another-installation")
        self.assertNotEqual(refused.returncode, 0)
        self.assertIn("ownership changed before startup", refused.stderr)
        self.assertFalse((self.state / "openbao-reset").exists())

    def test_openbao_ownership_preflight_precedes_compose_up(self):
        completed = self.run_seed()
        self.assertEqual(completed.returncode, 0, completed.stderr)
        lines = (self.state / "docker.log").read_text().splitlines()
        inspect = next(index for index, line in enumerate(lines)
                       if line.startswith("volume inspect "))
        compose_up = next(index for index, line in enumerate(lines)
                          if line.startswith("compose up -d postgres openbao"))
        self.assertLess(inspect, compose_up)

    def test_openbao_unknown_status_preserves_volume_and_recovery(self):
        refused = self.run_seed(UNKNOWN_OPENBAO_ALWAYS="1")
        self.assertNotEqual(refused.returncode, 0)
        self.assertIn("valid readiness status", refused.stderr)
        self.assertFalse((self.state / "openbao-reset").exists())

    def test_malformed_openbao_status_preserves_volume_and_recovery(self):
        refused = self.run_seed(MALFORMED_OPENBAO_ALWAYS="1")
        self.assertNotEqual(refused.returncode, 0)
        self.assertIn("valid readiness status", refused.stderr)
        self.assertFalse((self.state / "openbao-reset").exists())

    def test_positive_uninitialized_status_allows_owned_reset(self):
        interrupted = self.run_seed(FAIL_OPENBAO_CONFIGURE_ONCE="1")
        self.assertNotEqual(interrupted.returncode, 0)
        self.assertTrue((self.secrets / "openbao-seed-init.json").exists())
        self.assertFalse((self.secrets / "openbao-seed-configured").exists())

        (self.state / "openbao-initialized").unlink()
        initial_log = (self.state / "docker.log").read_text()
        resumed = self.run_seed()
        self.assertEqual(resumed.returncode, 0, resumed.stderr)
        self.assertTrue((self.state / "openbao-reset").exists())
        self.assertTrue((self.state / "openbao-initialized").exists())
        self.assertTrue((self.secrets / "openbao-seed-configured").exists())
        self.assertFalse((self.state / "openbao-tls-rejected").exists())
        resumed_log = (self.state / "docker.log").read_text()[len(initial_log):]
        self.assertLess(resumed_log.index("bao status -format=json"),
                        resumed_log.index("volume rm "))
        self.assertLess(resumed_log.index("volume rm "),
                        resumed_log.index("bao operator init "))

    def test_initialized_openbao_valid_pending_recovery_is_promoted_without_reset(self):
        interrupted = self.run_seed(FAIL_OPENBAO_CONFIGURE_ONCE="1")
        self.assertNotEqual(interrupted.returncode, 0)
        recovery = self.secrets / "openbao-seed-init.json"
        original_record = recovery.read_bytes()
        pending = recovery.with_name(recovery.name + ".pending")
        recovery.rename(pending)
        initial_log = (self.state / "docker.log").read_text()

        resumed = self.run_seed()
        self.assertEqual(resumed.returncode, 0, resumed.stderr)
        self.assertEqual(recovery.read_bytes(), original_record)
        self.assertFalse(pending.exists())
        self.assertTrue((self.secrets / "openbao-seed-configured").exists())
        self.assertFalse((self.state / "openbao-reset").exists())
        resumed_log = (self.state / "docker.log").read_text()[len(initial_log):]
        self.assertNotIn("volume rm", resumed_log)
        self.assertNotIn("operator init", resumed_log)

    def test_expired_first_owner_token_is_rotated_before_handoff(self):
        initial = self.run_seed()
        self.assertEqual(initial.returncode, 0, initial.stderr)
        completed = self.run_seed(EXPIRED_SETUP_TOKEN="1")
        self.assertEqual(completed.returncode, 0, completed.stderr)
        self.assertTrue((self.state / "token-rotated").exists())
        self.assertEqual(self.token.read_text(), "b" * 64 + "\n")

    def test_near_expiry_first_owner_token_is_rotated_before_handoff(self):
        initial = self.run_seed()
        self.assertEqual(initial.returncode, 0, initial.stderr)
        completed = self.run_seed(NEAR_EXPIRY_SETUP_TOKEN="1")
        self.assertEqual(completed.returncode, 0, completed.stderr)
        self.assertTrue((self.state / "token-rotated").exists())
        self.assertEqual(self.token.read_text(), "b" * 64 + "\n")

    def test_setup_token_output_conflict_is_preserved_without_authorized_rotation(self):
        completed = self.run_seed()
        self.assertEqual(completed.returncode, 0, completed.stderr)
        self.token.write_text("c" * 64 + "\n")
        conflicted = self.run_seed()
        self.assertNotEqual(conflicted.returncode, 0)
        self.assertIn("conflicts with the current admission token", conflicted.stderr)
        self.assertEqual(self.token.read_text(), "c" * 64 + "\n")

    def test_openbao_services_carry_the_reset_ownership_label(self):
        compose = (ROOT / "packaging" / "bundle" / "compose.yml").read_text()
        openbao = compose.split("\n  openbao:", 1)[1].split("\n  openbao-configure:", 1)[0]
        agent = compose.split("\n  openbao-agent:", 1)[1].split("\n  openbao-ready:", 1)[0] \
            if "\n  openbao-ready:" in compose else \
            compose.split("\n  openbao-agent:", 1)[1].split("\n  kratos-migrate:", 1)[0]
        self.assertIn("labels: *installation-labels", openbao)
        self.assertIn("labels: *installation-labels", agent)


if __name__ == "__main__":
    unittest.main()
