# SPDX-License-Identifier: MPL-2.0
from contextlib import contextmanager, redirect_stdout
import fcntl
import hashlib
import http.client
import io
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import tarfile
import tempfile
import time
import unittest
from unittest.mock import patch


ROOT = Path(__file__).resolve().parents[2]
PACKAGE = ROOT / "scripts" / "delivery" / "package.py"
MIGRATIONS = {"postgres": ["20260101000000_InitialPostgres", "20260102000000_AddPostgres"],
              "sqlite": ["20260101000000_InitialSqlite"]}

sys.path.insert(0, str(ROOT / "packaging" / "bundle"))
import deploy_host
sys.path.pop(0)


class FakeDeployDocker:
    def __init__(self, current):
        self.current = current
        self.calls = []
        self.applied = MIGRATIONS["postgres"][:1]
        self.wrong_working_dir = False
        self.wrong_installation = False
        self.wrong_loaded_id = False
        self.postgres_major = 16
        self.fail_step = None
        self.kill_step = None
        self.steps = []
        self.maintenance = False
        self.paused = False
        self.sealed = False
        self.running = {}
        self.volumes = {}
        self.container_ids = {name: f"{index:064x}" for index, name in enumerate(
            ("api", "caddy", "kratos", "openbao", "openbao-agent", "postgres", "worker", "workspace-gateway"), 1)}

    def __call__(self, step, *args, **options):
        self.calls.append(args)
        self.steps.append(step)
        if self.fail_step == step:
            self.fail_step = None
            raise ValueError(f"{step} failed (exit 42).")
        result = self.command(step, args, options)
        if self.kill_step == step:
            self.kill_step = None
            raise SystemExit(137)
        return result

    def command(self, step, args, options):
        if args[:2] == ("docker", "ps"):
            if any(arg.startswith("label=com.municode.deploy-operation=") for arg in args):
                return ""
            return "\n".join(id for name, id in self.container_ids.items()
                             if args[2] == "-aq" or self.running.get(name, (self.current, True))[1])
        if args[:3] == ("docker", "container", "inspect"):
            if args[3] == "--format":
                return str(self.paused).lower()
            containers = []
            for name, id in self.container_ids.items():
                if id not in args[3:]:
                    continue
                release, running = self.running.get(name, (self.current, True))
                labels = {
                    "com.docker.compose.service": name,
                    "com.docker.compose.project.working_dir": "/wrong" if self.wrong_working_dir else str(release),
                }
                if name in ("openbao", "openbao-agent"):
                    labels["com.municode.installation-id"] = (
                        "wrong" if self.wrong_installation else "replace-with-stable-installation-id"
                    )
                volume = "openbao" if name == "openbao" else "caddy-data" if name == "caddy" else None
                mounts = [{"Type": "volume", "Name": f"municode_{volume}", "Source": f"/fake/{volume}"}] if volume else []
                networks = {"municode_private": {"NetworkID": "f" * 64}}
                containers.append({"Id": id, "Config": {"Labels": labels}, "Mounts": mounts,
                                   "State": {"Running": running}, "NetworkSettings": {"Networks": networks}})
            return json.dumps(containers)
        if args[:3] == ("docker", "volume", "inspect"):
            name = args[-1].removeprefix("municode_")
            return json.dumps([{"Name": args[-1], "Driver": "local", "Mountpoint": str(self.volumes[name]),
                                "Labels": {deploy_host.INSTALLATION_LABEL: "replace-with-stable-installation-id"}}])
        if args[:2] == ("docker", "unpause"):
            self.paused = False
            return ""
        if args[0] == "systemctl":
            return "ActiveState=active\nSubState=running"
        if args[:3] == ("docker", "image", "pull") or args[:3] == ("docker", "image", "tag"):
            return ""
        if args[:4] == ("docker", "image", "load", "--input"):
            image = Path(args[4])
            if self.wrong_loaded_id:
                return "Loaded image ID: sha256:" + "0" * 64
            if tarfile.is_tarfile(image):
                with tarfile.open(image) as archive:
                    entry = json.load(archive.extractfile("manifest.json"))[0]
                    config = archive.extractfile(entry["Config"]).read()
                return "Loaded image ID: sha256:" + hashlib.sha256(config).hexdigest()
            return "Loaded image ID: " + image.read_text()
        if args[:5] == ("docker", "image", "inspect", "--format", "{{.Id}}"):
            return args[-1]
        if args[:3] == ("docker", "image", "inspect"):
            return json.dumps([{"Config": {"Env": [f"PG_MAJOR={self.postgres_major}"]}, "Size": 100}
                               for ref in args[3:]])
        if args[:2] == ("docker", "compose"):
            assert args[2:4] == ("--project-name", "municode"), args
            env = dict(line.split("=", 1) for line in Path(args[7]).read_text().splitlines() if "=" in line)
            if args[10:] == ("--profile", "*", "config", "--quiet"):
                return ""
            if args[10:] in (("config", "--format", "json"), ("--profile", "*", "config", "--format", "json")):
                mounts = [{"source": env["MUNICODE_POLICY_FILE"], "target": "/etc/municode/municode.json",
                           "read_only": True}]
                services = {name: {"image": env["MUNICODE_DOTNET_IMAGE"], "volumes": mounts}
                            for name in ("api", "api-migrate", "worker")}
                services["api"]["environment"] = {"Bootstrap__InstallationId": env["MUNICODE_INSTALLATION_ID"],
                                                   "MUNICODE_RELEASE_TAG": env["MUNICODE_RELEASE_TAG"]}
                services["api-migrate"]["environment"] = {
                    "Database__Provider": "Postgres", "Database__PasswordFile": "/run/secrets/app-db-password",
                    "ConnectionStrings__Municode": "Host=postgres;Port=5432;Database=municode;Username=municode",
                }
                services["postgres"] = {"image": env["MUNICODE_POSTGRES_IMAGE"]}
                for name in ("openbao", "openbao-agent"):
                    services[name] = {"image": env["MUNICODE_OPENBAO_IMAGE"],
                                      "labels": {"com.municode.installation-id": env["MUNICODE_INSTALLATION_ID"]}}
                return json.dumps({"services": services})
            release = Path(args[5])
            action = args[10]
            if action == "run":
                self.last_job = args
                if "maintenance" in args:
                    command = args[-2]
                    if command != "status":
                        self.maintenance = command == "enter"
                    return json.dumps({"enabled": self.maintenance, "nonterminalOperations": 0})
                assert args[-1] in deploy_host.JOBS and args[11:14] == ("--rm", "--no-deps", "-T"), args
                return ""
            if action == "stop":
                for name in args[11:]:
                    self.running[name] = (self.running.get(name, (self.current, True))[0], False)
                return ""
            if action == "up":
                for name in (name for name in args[11:] if name in self.container_ids):
                    self.running[name] = (release, True)
                    if name == "openbao" and "--force-recreate" in args:
                        self.sealed = True
                return ""
            if action == "pause":
                self.paused = True
                return ""
            if action == "ps":
                return self.container_ids["openbao"]
            if action == "exec":
                command = args[13]
                if command.startswith("pg_dump"):
                    options["output"].write(b"synthetic-dump\n")
                    return ""
                if command == "pg_restore":
                    assert options["source"].read() == b"synthetic-dump\n"
                    return ""
                if command == "psql":
                    if step == "export operations and VMs":
                        return '{"operations":[{"id":"op-1"}],"vms":[{"id":"vm-1"}]}'
                    return "[]" if step.startswith("inspect database role") else ""
                if command == "bao":
                    if args[14] == "write":
                        assert options["data"] == b"synthetic-unseal-key"
                        self.sealed = False
                        return ""
                    return json.dumps({"initialized": True, "sealed": self.sealed})
        if args[:2] == ("docker", "run"):
            if step == "probe private API readiness":
                return ""
            assert args[2:7] == ("--rm", "--pull=never", "--read-only", "--network", "f" * 64), args
            assert args[-4:] == ("/opt/municode/Municode.Api.dll", "migrations", "status", "--json"), args
            source = next(arg for arg in args if arg.endswith(",dst=/opt/municode,readonly"))
            release = Path(source.split(",")[1].removeprefix("src=")).parents[1]
            ids = json.loads((release / "release-manifest.json").read_text())["migrations"]["postgres"]
            return json.dumps({"historyTableExists": True, "applied": [id for id in ids if id in self.applied],
                               "pending": [id for id in ids if id not in self.applied],
                               "unknownApplied": sorted(set(self.applied) - set(ids))})
        if args[:2] == ("docker", "exec"):
            if args[2:4] == (self.container_ids["postgres"], "psql"):
                return "160007|4096"
            if args[2:4] == (self.container_ids["openbao"], "bao"):
                return '{"initialized":true,"sealed":false}'
            if args[2] == self.container_ids["api"] and args[-3:] == ("maintenance", "status", "--json"):
                return '{"enabled":false,"nonterminalOperations":2}'
            if args[2:4] == (self.container_ids["caddy"], "wget"):
                return ""
        if args[0] == "du":
            return "\n".join(f"100\t{path}" for path in args[5:])
        if args[0] in ("tar", "sync"):
            result = subprocess.run(args, stdout=options.get("output", subprocess.PIPE),
                                    stderr=subprocess.PIPE, check=True)
            return result.stdout.decode().strip() if result.stdout else ""
        raise AssertionError((step, args, options))


def deploy_command(root, fake, *args):
    output = io.StringIO()
    with redirect_stdout(output):
        code = deploy_host.main(["--host-root", str(root), *args], run=fake)
    return code, json.loads(output.getvalue())


class PackageTests(unittest.TestCase):
    def setUp(self):
        artifacts = ROOT / ".local/artifacts"
        artifacts.mkdir(parents=True, exist_ok=True)
        self.temporary = tempfile.TemporaryDirectory(dir=artifacts)
        self.root = Path(self.temporary.name)
        self.payloads = {}
        for name, binary in (
            ("api", "Municode.Api.dll"),
            ("worker", "Municode.Worker.dll"),
            ("gateway", "Municode.Gateway.dll"),
            ("dashboard", "index.html"),
            ("cli", "municode"),
        ):
            payload = self.root / name
            payload.mkdir()
            target = payload / binary
            target.write_text(f"synthetic {name}\n")
            if name == "cli":
                target.unlink()
                platform = payload / "linux-x64"
                platform.mkdir()
                target = platform / "municode"
                target.write_text(f"synthetic {name}\n")
                target.chmod(0o755)
            self.payloads[name] = payload
        self.output = self.root / "output"
        dotnet = self.root / "dotnet"
        dotnet.write_text(
            "#!/bin/sh\n"
            '[ "$#" -eq 4 ] && [ "$1" = "$PWD/Municode.Api.dll" ] && '
            '[ "$2 $3 $4" = "migrations list --json" ] || exit 2\n'
            f"printf '%s\\n' '{json.dumps(MIGRATIONS)}'\n"
        )
        dotnet.chmod(0o755)

    def tearDown(self):
        self.temporary.cleanup()

    def command(self):
        return [
            sys.executable,
            "-B",
            str(PACKAGE),
            "--version",
            "0.0.0-test",
            "--source",
            "a" * 40,
            "--rid",
            "linux-x64",
            "--api",
            str(self.payloads["api"]),
            "--worker",
            str(self.payloads["worker"]),
            "--gateway",
            str(self.payloads["gateway"]),
            "--dashboard",
            str(self.payloads["dashboard"]),
            "--cli",
            str(self.payloads["cli"]),
            "--output",
            str(self.output),
        ]

    def package(self, command=None, *, check=True):
        env = dict(os.environ, PATH=str(self.root) + os.pathsep + os.environ["PATH"])
        return subprocess.run(command or self.command(), check=check, capture_output=True, text=True, env=env)

    def test_package_contains_all_product_payloads_and_installer(self):
        self.package()
        archive = next(self.output.glob("*.tar.gz"))
        with tarfile.open(archive, "r:gz") as bundle:
            names = {member.name for member in bundle.getmembers()}
            self.assertTrue(any(name.endswith("/runtime/api/Municode.Api.dll") for name in names))
            self.assertTrue(any(name.endswith("/runtime/worker/Municode.Worker.dll") for name in names))
            self.assertTrue(any(name.endswith("/runtime/gateway/Municode.Gateway.dll") for name in names))
            self.assertTrue(any(name.endswith("/dashboard/index.html") for name in names))
            self.assertTrue(any(name.endswith("/cli/linux-x64/municode") for name in names))
            self.assertFalse(any(name.endswith(("/workspaces", "/workspaces.cmd")) for name in names
                                 if "/cli/" in name))
            self.assertTrue(any(name.endswith("/install.sh") for name in names))
            agent = bundle.extractfile("municode-0.0.0-test/deploy_host.py").read()
            manifest = json.loads((self.output / "release-manifest.json").read_text())
            self.assertEqual(manifest["hostAgent"], {
                "path": "deploy_host.py", "sha256": hashlib.sha256(agent).hexdigest(),
            })
            seed_installer = next(member for member in bundle.getmembers()
                                  if member.name.endswith("/seed-start.sh"))
            self.assertTrue(seed_installer.mode & 0o100)
            self.assertTrue(all(not member.issym() and not member.islnk() for member in bundle.getmembers()))

    def deploy_fixture(self):
        self.package()
        archive, = self.output.glob("*.tar.gz")
        sha = hashlib.sha256(archive.read_bytes()).hexdigest()
        unpacked = self.root / "unpacked"
        with tarfile.open(archive) as bundle:
            bundle.extractall(unpacked, filter="data")
        host = self.root / "host"
        subprocess.run([unpacked / "municode-0.0.0-test/install.sh", "--host-root", host,
                        "--bundle-sha256", sha, "--verified-release-tag", "municode-v0.0.0-test"],
                       check=True, capture_output=True, text=True)
        active = host / "opt/municode/releases/municode-v0.0.0-test"
        command = self.command()
        command[command.index("--version") + 1] = "0.0.0-next"
        command[command.index("--output") + 1] = str(self.root / "next")
        self.package(command)
        target, = (self.root / "next").glob("*.tar.gz")
        return host, FakeDeployDocker(active), target

    def stage_command(self, host, fake, archive, op="test-op", sha=None):
        return deploy_command(host, fake, "stage", "--operation", op, "--bundle", str(archive),
                              "--bundle-sha256", sha or hashlib.sha256(archive.read_bytes()).hexdigest(),
                              "--trust", "github-sigstore", "--verified-release-tag", "municode-v0.0.0-next")

    def preflight_command(self, host, fake, staged):
        op = host / "var/lib/municode/deploys/test-op"
        plan = op / "plan.json"
        plan.write_text(json.dumps(staged | {
            "operationId": op.name, "instanceName": "demo", "hostFingerprint": "SHA256:" + "x" * 43,
            "installationId": "replace-with-stable-installation-id",
        }))
        return deploy_command(host, fake, "preflight", "--json", "--plan", str(plan))

    @contextmanager
    def apply_fixture(self, subcase=None):
        if subcase:
            dotnet = self.root / "dotnet"
            self.root = self.root / subcase
            self.root.mkdir()
            (self.root / "dotnet").symlink_to(dotnet)
            self.output = self.root / "output"
        host, fake, archive = self.deploy_fixture()
        for name in deploy_host.VOLUMES:
            path = host / name
            path.mkdir()
            (path / "data").write_text(f"synthetic {name}")
            fake.volumes[name] = path
        (host / "etc/municode/secrets/openbao-unseal-key").write_text("synthetic-unseal-key\n")
        code, staged = self.stage_command(host, fake, archive)
        self.assertEqual(code, 0, staged)
        code, result = self.preflight_command(host, fake, staged)
        self.assertEqual(code, 0, result)
        plan = host / "var/lib/municode/deploys/test-op/plan.json"
        request = json.loads(plan.read_text())
        request["approvalDigest"] = hashlib.sha256(deploy_host.canonical(result["digestInputs"])).hexdigest()
        plan.write_text(json.dumps(request))
        fake.steps.clear()
        with patch.object(deploy_host.time, "sleep"):
            yield host, fake, str(plan)

    def test_deploy_stages_verified_archive_without_switching_current(self):
        host, fake, archive = self.deploy_fixture()
        code, staged = self.stage_command(host, fake, archive)
        self.assertEqual(code, 0, staged)
        release = fake.current.with_name(staged["targetRelease"]["releaseId"])
        self.assertEqual(release.stat().st_mode & 0o777, 0o555)
        self.assertEqual((release / "deploy_host.py").stat().st_mode & 0o777, 0o444)
        self.assertEqual((host / "opt/municode/current").resolve(), fake.current)
        self.assertFalse((release / ".env").exists())
        self.assertTrue(any(command[:3] == ("docker", "image", "pull") for command in fake.calls))
        fake.calls.clear()
        self.assertEqual(self.stage_command(host, fake, archive), (code, staged))
        self.assertFalse(any(command[:2] == ("docker", "image") for command in fake.calls))
        code, result = self.stage_command(host, fake, archive, op="bad-sha", sha="0" * 64)
        self.assertEqual((code, result["blockers"]), (1, ["Uploaded bundle SHA-256 mismatch."]))

        linked = self.root / "link.tar.gz"
        with tarfile.open(linked, "w:gz") as bundle:
            member = tarfile.TarInfo("municode-0.0.0-next/unsafe")
            member.type = tarfile.SYMTYPE
            member.linkname = "/etc/passwd"
            bundle.addfile(member)
        code, result = self.stage_command(host, fake, linked, op="bad-link")
        self.assertEqual((code, result["blockers"]), (1, ["Bundle contains an unsafe archive member."]))
        self.assertFalse((release.parent / ".staging-bad-link").exists())

    def test_deploy_requires_active_record_and_running_ownership(self):
        host, fake, archive = self.deploy_fixture()
        code, staged = self.stage_command(host, fake, archive)
        self.assertEqual(code, 0, staged)
        active = host / "var/lib/municode/active-release.json"
        record = active.read_bytes()
        active.unlink()
        code, result = self.preflight_command(host, fake, staged)
        self.assertEqual((code, result["blockers"]), (1, ["Missing or invalid active-release.json."]))
        active.write_bytes(record)
        fake.wrong_working_dir = True
        code, result = self.preflight_command(host, fake, staged)
        self.assertEqual(code, 1)
        self.assertIn("working directory", result["blockers"][0])
        fake.wrong_working_dir = False
        fake.wrong_installation = True
        code, result = self.preflight_command(host, fake, staged)
        self.assertEqual(code, 1)
        self.assertIn("ownership label", result["blockers"][0])
        with (host / "var/lib/municode/deploy.lock").open("r") as lock:
            fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
            code, result = self.preflight_command(host, fake, staged)
        self.assertEqual(code, 1)
        self.assertIn("holds the deploy lock", result["blockers"][0])

    def test_preflight_refuses_unknown_operator_keys_and_downgrades(self):
        host, fake, archive = self.deploy_fixture()
        code, staged = self.stage_command(host, fake, archive)
        self.assertEqual(code, 0, staged)
        operator = host / "etc/municode/config/operator.env"
        values = operator.read_text()
        operator.write_text(values + "UNRECOGNIZED=synthetic-secret\n")
        code, result = self.preflight_command(host, fake, staged)
        self.assertEqual(code, 1)
        self.assertEqual(result["envDiff"]["unknownOperatorKeys"], ["UNRECOGNIZED"])
        self.assertNotIn("synthetic-secret", json.dumps(result))
        operator.write_text(values)
        fake.applied.append("20260201000000_NewerThanTarget")
        fake.postgres_major = 17
        code, result = self.preflight_command(host, fake, staged)
        self.assertEqual(code, 1)
        self.assertEqual(result["migrations"]["unknownApplied"], ["20260201000000_NewerThanTarget"])
        self.assertIn("Target migrations do not extend the applied PostgreSQL history.", result["blockers"])
        self.assertIn("PostgreSQL major-version changes are not supported.", result["blockers"])

    def test_preflight_hash_tracks_policy_without_writing_host_files(self):
        host, fake, archive = self.deploy_fixture()
        code, staged = self.stage_command(host, fake, archive)
        self.assertEqual(code, 0, staged)
        code, initial = self.preflight_command(host, fake, staged)
        self.assertEqual(code, 0, initial)
        self.assertEqual(initial["maintenance"], {"enabled": False, "nonterminalOperations": 2})
        self.assertTrue(initial["openBao"]["requiresUnsealKeyFile"])
        self.assertEqual(initial["digestInputs"]["pendingMigrations"], MIGRATIONS["postgres"][1:])
        self.assertEqual(initial["digestInputs"]["effectiveConfigHash"], initial["effectiveConfigHash"])
        policy = host / "etc/municode/config/municode.json"
        policy.write_text('{"operator":true}\n')
        op = host / "var/lib/municode/deploys/test-op"

        def snapshot():
            return {path: (path.lstat().st_mode, path.lstat().st_mtime_ns) for path in host.rglob("*")
                    if path != op and not path.is_relative_to(op)}

        before = snapshot()
        code, changed = self.preflight_command(host, fake, staged)
        self.assertEqual(code, 0, changed)
        self.assertNotEqual(initial["effectiveConfigHash"], changed["effectiveConfigHash"])
        self.assertEqual(before, snapshot())
        self.assertEqual(sorted(path.name for path in op.iterdir()), ["plan.json", "state.json"])
        code, repeated = self.preflight_command(host, fake, staged)
        self.assertEqual(code, 0, repeated)
        self.assertEqual(repeated["digestInputs"], changed["digestInputs"])

    def test_apply_recomputes_approval_before_maintenance(self):
        with self.apply_fixture() as (host, fake, plan):
            (host / "etc/municode/config/municode.json").write_text('{"operator":true}\n')
            code, result = deploy_command(host, fake, "run", "--plan", plan)
            self.assertEqual(code, 1, result)
            self.assertNotEqual(result["approvalDigest"], json.loads(Path(plan).read_text())["approvalDigest"])
            self.assertEqual(result["state"]["phase"], "staged")
            self.assertNotIn("maintenance enter", fake.steps)

    def test_apply_failure_paths_and_commit_retry(self):
        cases = [
            ("prepare", "pull pinned image", "prepare", False),
            ("drain", "maintenance enter", "rolled-back", False),
            ("backup", "archive openbao", "rolled-back", False),
            ("migrate", "run kratos-migrate", "rolled-back", True),
            ("start", "start openbao-agent, kratos, api, workspace-gateway", "rolled-back", True),
            ("open", "start caddy", "open", False),
            ("resume", "start worker", "resume", False),
        ]
        root = self.root
        for phase, failure, expected, restored in cases:
            with self.subTest(phase=phase), self.apply_fixture(phase) as (host, fake, plan):
                fake.fail_step = failure
                code, result = deploy_command(host, fake, "run", "--plan", plan)
                self.assertEqual(code, 1, result)
                state = result["state"]
                self.assertEqual(state["lastError"]["phase"], phase)
                self.assertEqual(state["phase"], expected)
                self.assertEqual("restore municode" in fake.steps, restored)
                self.assertFalse(fake.paused)
                self.assertEqual((host / "opt/municode/current").resolve(), fake.current)
                if expected == "rolled-back":
                    self.assertFalse(fake.maintenance)
                    self.assertTrue(all(release == fake.current and running for release, running in fake.running.values()))
            self.root = root

        with self.apply_fixture("commit") as (host, fake, plan):
            with patch.object(deploy_host, "prune", side_effect=ValueError("injected prune failure")):
                code, result = deploy_command(host, fake, "run", "--plan", plan)
            self.assertEqual(code, 1, result)
            self.assertEqual(result["state"]["phase"], "commit")
            self.assertTrue(result["state"]["workerResumed"])
            self.assertEqual(fake.steps.count("run api-migrate"), 1)
            code, result = deploy_command(host, fake, "run", "--plan", plan)
            self.assertEqual((code, result["state"]["phase"]), (0, "committed"), result)
            self.assertEqual(fake.steps.count("run api-migrate"), 1)
            active = json.loads((host / "var/lib/municode/active-release.json").read_text())
            self.assertEqual(active["previous"]["releaseId"], fake.current.name)
            self.assertEqual((host / "opt/municode/current").resolve().name, active["releaseId"])

    def test_interrupted_pause_unpauses_and_interrupted_migration_restores(self):
        root = self.root
        for crash in ("pause", "migrate"):
            with self.subTest(crash=crash), self.apply_fixture(crash) as (host, fake, plan):
                if crash == "pause":
                    # A process kill bypasses finally, leaving the Docker container paused.
                    with patch.object(deploy_host, "ensure_unpaused", side_effect=SystemExit(137)):
                        with self.assertRaises(SystemExit):
                            deploy_command(host, fake, "run", "--plan", plan)
                    self.assertTrue(fake.paused)
                else:
                    fake.kill_step = "run database-initialize"
                    with self.assertRaises(SystemExit):
                        deploy_command(host, fake, "run", "--plan", plan)
                with deploy_host.deploy_lock(host / "var/lib/municode/deploy.lock"):
                    code, status = deploy_command(host, fake, "status", "--operation", "test-op")
                self.assertEqual(code, 0, status)
                self.assertEqual(status["unit"], {"activeState": "active", "subState": "running"})
                self.assertEqual(status["state"]["pendingStep"],
                                 "matched-snapshot" if crash == "pause" else "database-initialize")
                self.assertEqual(bool(status["state"].get("migrationsStarted")), crash == "migrate")
                code, result = deploy_command(host, fake, "run", "--plan", plan)
                self.assertEqual((code, result["state"]["phase"]), (1, "rolled-back"), result)
                self.assertFalse(fake.paused)
                self.assertEqual("restore kratos" in fake.steps, crash == "migrate")
            self.root = root

    def test_interrupted_worker_requires_confirmations_and_matched_snapshot(self):
        with self.apply_fixture() as (host, fake, plan):
            fake.kill_step = "start worker"
            with self.assertRaises(SystemExit):
                deploy_command(host, fake, "run", "--plan", plan)
            _, status = deploy_command(host, fake, "status", "--operation", "test-op")
            state = status["state"]
            self.assertEqual(state["pendingStep"], "resume-worker")
            self.assertTrue(state["trafficMayHaveOpened"] and state["workerResumed"])
            rollback = ("rollback", "--plan", plan)
            self.assertIn("openAt", deploy_command(host, fake, *rollback)[1]["blockers"][0])
            rollback += ("--restore-database", "--accept-data-loss-since", state["openAt"])
            self.assertIn("provider-reconciliation", deploy_command(host, fake, *rollback)[1]["blockers"][0])
            rollback += ("--accept-provider-reconciliation",)
            backup = host / "var/lib/municode/backups/test-op"
            dump = backup / "municode.dump"
            original = dump.read_bytes()
            dump.write_bytes(b"corrupt")
            code, result = deploy_command(host, fake, *rollback)
            self.assertEqual(code, 1, result)
            self.assertIn("SHA-256", result["blockers"][0])
            self.assertNotIn("drop municode", fake.steps)
            dump.write_bytes(original)
            policy = host / "etc/municode/config/municode.json"
            old_policy = policy.read_bytes()
            policy.write_text("changed")
            (fake.volumes["openbao"] / "new").write_text("post-open")
            fake.kill_step = "restore municode"
            with self.assertRaises(SystemExit):
                deploy_command(host, fake, *rollback)
            code, result = deploy_command(host, fake, *rollback)
            self.assertEqual((code, result["state"]["phase"]), (0, "rolled-back"), result)
            self.assertEqual(fake.steps.count("export operations and VMs"), 1)
            self.assertLess(fake.steps.index("export operations and VMs"), fake.steps.index("drop municode"))
            self.assertEqual(json.loads((backup / "post-open-operations.json").read_text()),
                             {"operations": [{"id": "op-1"}], "vms": [{"id": "vm-1"}]})
            self.assertEqual(policy.read_bytes(), old_policy)
            self.assertFalse((fake.volumes["openbao"] / "new").exists())

    def test_manifest_records_component_digests_and_no_floating_dependencies(self):
        self.package()
        manifest = json.loads((self.output / "release-manifest.json").read_text())
        self.assertEqual(manifest["deployProtocol"], 1)
        self.assertEqual(manifest["migrations"], MIGRATIONS)
        contract = json.loads((ROOT / "packaging" / "bundle" / "env-contract.json").read_text())
        self.assertEqual(manifest["envContract"], contract)
        env_keys = {
            line.partition("=")[0]
            for line in (ROOT / "packaging" / "bundle" / ".env.example").read_text().splitlines()
            if line.strip() and not line.lstrip().startswith("#")
        }
        self.assertEqual(env_keys, set(contract))
        self.assertEqual(
            set(manifest["components"]), {"api", "worker", "gateway", "dashboard", "cli"}
        )
        for component in manifest["components"].values():
            self.assertRegex(component["sha256"], r"^[0-9a-f]{64}$")
            self.assertTrue(component["files"])
        release = json.loads((ROOT / "packaging" / "release.json").read_text())
        for value in release["images"].values():
            self.assertRegex(value, r"@sha256:[0-9a-f]{64}$")
        self.assertNotIn("0" * 64, (ROOT / "packaging" / "release.json").read_text())
        env = (ROOT / "packaging" / "bundle" / ".env.example").read_text()
        for value in release["images"].values():
            self.assertIn(value, env)
        self.assertNotIn("latest", env)

    def test_bundle_declares_only_independent_workspaces_services(self):
        compose = (ROOT / "packaging" / "bundle" / "compose.yml").read_text().lower()
        caddy = (ROOT / "packaging" / "bundle" / "Caddyfile").read_text().lower()
        self.assertNotIn("minicloud", compose)
        self.assertNotIn("minicloud", caddy)
        for service in ("api:", "worker:", "workspace-gateway:", "caddy:", "postgres:",
                        "openbao:", "kratos:", "dashboard"):
            self.assertIn(service, compose + caddy)

    def test_api_receives_release_identity_environment(self):
        compose = (ROOT / "packaging" / "bundle" / "compose.yml").read_text()
        api = compose.split("\n  api:\n", 1)[1].split("\n  worker:\n", 1)[0]
        for key in ("MUNICODE_RELEASE_TAG", "MUNICODE_RELEASE_SOURCE_REVISION", "MUNICODE_RELEASE_TRUST"):
            with self.subTest(key=key):
                self.assertIn(f"      {key}: ${{{key}:-unknown}}\n", api)

    def test_packaged_caddy_pins_gateway_routes_to_dedicated_proxy_network(self):
        self.package()
        archive = next(self.output.glob("*.tar.gz"))
        with tarfile.open(archive, "r:gz") as bundle:
            prefix = "municode-0.0.0-test/"
            caddy = bundle.extractfile(prefix + "Caddyfile").read().decode()
            compose = bundle.extractfile(prefix + "compose.yml").read().decode()

        self.assertEqual(
            re.findall(r"(?m)^\s*ask\s+(\S+)\s*$", caddy),
            ["http://172.31.20.3:8080/internal/certificate"],
        )
        workspace_route = caddy.split("https:// {", 1)[1]
        self.assertEqual(
            re.findall(r"(?m)^\s*reverse_proxy\s+(\S+)", workspace_route),
            ["172.31.20.3:8080"],
        )
        self.assertNotIn("workspace-gateway:8080", caddy)
        self.assertIn("on_demand_tls {", caddy)
        self.assertIn("\ttls {\n\t\ton_demand\n\t}", workspace_route)

        gateway = compose.split("\n  workspace-gateway:\n", 1)[1].split("\n  caddy:\n", 1)[0]
        proxy = compose.split("\n  caddy:\n", 1)[1].split("\nsecrets:\n", 1)[0]
        for service, address in ((gateway, "172.31.20.3"), (proxy, "172.31.20.2")):
            with self.subTest(address=address):
                self.assertIn(
                    "    networks:\n"
                    "      private: {}\n"
                    "      workspace-gateway-proxy:\n"
                    f"        ipv4_address: {address}\n",
                    service,
                )
        self.assertIn(
            "      DevelopmentWorkspaceGateway__TrustedProxyAddress: 172.31.20.2\n",
            gateway,
        )
        networks = compose.split("\nnetworks:\n", 1)[1]
        self.assertIn(
            "  workspace-gateway-proxy:\n"
            "    labels: *installation-labels\n"
            "    driver: bridge\n"
            "    ipam:\n"
            "      config:\n"
            "        - subnet: 172.31.20.0/24\n",
            networks,
        )

    def test_compose_renders_passkeys_and_external_policy(self):
        bundle = ROOT / "packaging" / "bundle"
        self.assertIn(
            "    passkey:\n"
            "      enabled: true\n"
            "      config:\n"
            "        rp:\n"
            "          display_name: Municode\n",
            (bundle / "kratos.yml").read_text(),
        )
        for schema in (bundle / "identity.schema.json",
                       ROOT / "docs" / "setup" / "identity.schema.json"):
            with self.subTest(schema=schema.name):
                email = json.loads(schema.read_text())["properties"]["traits"]["properties"]["email"]
                self.assertEqual(
                    email["ory.sh/kratos"]["credentials"]["passkey"], {"display_name": True}
                )

        if shutil.which("docker") is None or subprocess.run(
                ["docker", "compose", "version"], capture_output=True).returncode != 0:
            self.skipTest("docker compose is unavailable")
        secrets = self.root / "secrets"
        for name in ("postgres-admin-password", "app-db-password", "kratos-db-password",
                     "workspace-gateway-credential", "ssh/worker-key", "openbao-server.crt",
                     "openbao-server.key", "openbao-ca.crt", "openbao-approle/role-id",
                     "openbao-approle/secret-id", "kratos.env"):
            (secrets / name).parent.mkdir(parents=True, exist_ok=True)
            (secrets / name).write_text("synthetic\n")
        (secrets / "bootstrap").mkdir()

        def render(**overrides):
            environment = {
                key: value for key, value in os.environ.items()
                if not key.startswith(("CONSOLE_", "MUNICODE_"))
            }
            environment.update(MUNICODE_SECRETS_DIR=str(secrets),
                               CONSOLE_HOSTNAME="dashboard.customer.test",
                               CONSOLE_URL="https://dashboard.customer.test", **overrides)
            rendered = subprocess.run(
                ["docker", "compose", "-f", str(bundle / "compose.yml"),
                 "--env-file", str(bundle / ".env.example"), "config", "--format", "json"],
                env=environment, check=True, capture_output=True, text=True,
            ).stdout
            services = json.loads(rendered)["services"]
            return services

        services = render()
        kratos, api = services["kratos"]["environment"], services["api"]["environment"]
        self.assertEqual(kratos["SELFSERVICE_METHODS_PASSKEY_ENABLED"], "true")
        self.assertEqual(kratos["SELFSERVICE_METHODS_PASSKEY_CONFIG_RP_ID"], "dashboard.customer.test")
        self.assertEqual(kratos["SELFSERVICE_METHODS_PASSKEY_CONFIG_RP_ORIGINS"],
                         "https://dashboard.customer.test")
        self.assertEqual(api["Kratos__DashboardOrigin"], "https://dashboard.customer.test")
        self.assertEqual(api["Kratos__PasskeyEnabled"], "true")

        policy = str(self.root / "config" / "municode.json")
        services = render(MUNICODE_PASSKEYS_ENABLED="false", MUNICODE_POLICY_FILE=policy)
        kratos, api = services["kratos"]["environment"], services["api"]["environment"]
        self.assertEqual(kratos["SELFSERVICE_METHODS_PASSKEY_ENABLED"], "false")
        self.assertEqual(api["Kratos__PasskeyEnabled"], "false")
        for name in ("api", "api-migrate", "worker"):
            mount = next(v for v in services[name]["volumes"] if v["target"] == "/etc/municode/municode.json")
            self.assertEqual(mount["source"], policy)
            self.assertTrue(mount["read_only"])
            self.assertFalse(mount["bind"].get("create_host_path", False))

    def test_archive_checksum_is_present(self):
        self.package()
        lines = (self.output / "checksums.txt").read_text().splitlines()
        self.assertEqual(len(lines), 2)
        self.assertEqual({line.split("  ", 1)[1] for line in lines},
                         {"release-manifest.json", next(self.output.glob("*.tar.gz")).name})

    @unittest.skipUnless(os.environ.get("MUNICODE_TEST_CADDY") == "1",
                         "Set MUNICODE_TEST_CADDY=1 to run the isolated Caddy runtime test")
    def test_packaged_dashboard_revalidates_across_two_releases(self):
        image = json.loads((ROOT / "packaging" / "release.json").read_text())["images"]["caddy"]
        served = self.root / "served"
        served.mkdir()
        shells = []
        for number in (1, 2):
            dashboard = self.payloads["dashboard"]
            assets = dashboard / "assets"
            assets.mkdir(exist_ok=True)
            for asset in assets.iterdir():
                asset.unlink()
            shell = (
                '<!doctype html><html><script type="module" '
                f'src="/assets/index-release{number}.js"></script></html>\n'
            ).encode()
            shells.append(shell)
            (dashboard / "index.html").write_bytes(shell)
            (assets / f"index-release{number}.js").write_text(f"export default {number};\n")
            (assets / f"index-release{number}.css").write_text(f"body {{ opacity: .{number}; }}\n")
            command = self.command()
            command[command.index("--version") + 1] = f"0.0.0-test{number}"
            output = self.root / f"output{number}"
            command[command.index("--output") + 1] = str(output)
            self.package(command)
            with tarfile.open(output / f"municode-0.0.0-test{number}-linux-x64.tar.gz") as bundle:
                bundle.extractall(self.root / f"release{number}", filter="data")
            release = self.root / f"release{number}" / f"municode-0.0.0-test{number}"
            subprocess.run([sys.executable, "-B", str(release / "verify_bundle.py")],
                           check=True, capture_output=True, text=True)
            self.assertEqual((release / "dashboard" / "index.html").read_bytes(), shell)
        first = self.root / "release1" / "municode-0.0.0-test1"
        second = self.root / "release2" / "municode-0.0.0-test2"
        shutil.copytree(first / "dashboard", served, dirs_exist_ok=True)
        container = subprocess.run(
            ["docker", "run", "--detach", "--pull=never",
             "--read-only", "--tmpfs", "/data", "--tmpfs", "/config",
             "--publish", "127.0.0.1::80",
             "--mount", f"type=bind,src={first / 'Caddyfile'},dst=/etc/caddy/Caddyfile,readonly",
             "--mount", f"type=bind,src={served},dst=/srv/municode-dashboard,readonly",
             "--env", "CONSOLE_HOSTNAME=http://localhost",
             "--env", "API_HOSTNAME=http://api.localhost",
             "--env", "API_URL=https://api.localhost",
             "--env", "MUNICODE_SOURCE_URL=https://github.com/muniperez/municode",
             "--env", "MUNICODE_GATEWAY_DOMAIN=workspaces.localhost", image],
            check=True, capture_output=True, text=True,
        ).stdout.strip()
        self.addCleanup(subprocess.run, ["docker", "rm", "--force", "--volumes", container],
                        check=True, capture_output=True, text=True)
        address = subprocess.run(["docker", "port", container, "80/tcp"],
                                 check=True, capture_output=True, text=True).stdout.strip()
        client = http.client.HTTPConnection("127.0.0.1", int(address.rsplit(":", 1)[1]), timeout=5)
        self.addCleanup(client.close)

        def get(path, headers=None):
            client.request("GET", path, headers={"Host": "localhost",
                                                "Cookie": "ory_kratos_session=existing-client",
                                                **(headers or {})})
            response = client.getresponse()
            return response.status, response.headers, response.read()

        for attempt in range(50):
            try:
                status, _, _ = get("/")
                self.assertEqual(status, 200)
                break
            except (OSError, http.client.HTTPException):
                client.close()
                if attempt == 49:
                    logs = subprocess.run(["docker", "logs", container],
                                          capture_output=True, text=True)
                    self.fail(f"Caddy did not become ready:\n{logs.stdout}\n{logs.stderr}")
                time.sleep(0.1)

        paths = ("/", "/index.html", "/workspaces/existing-client",
                 "/settings/github/callback?code=private", "/runtime-config.json/not-config")
        status, headers, body = get("/assets/index-release1.js")
        self.assertEqual(status, 200)
        self.assertEqual(headers.get_content_type(), "application/javascript")
        self.assertEqual(body, b"export default 1;\n")
        for path in paths:
            status, headers, body = get(path)
            self.assertEqual(status, 200, path)
            self.assertEqual(headers.get_content_type(), "text/html", path)
            self.assertEqual(body, shells[0], path)
            self.assertEqual(headers["Cache-Control"],
                             "no-store" if path.startswith("/settings/github/") else "no-cache", path)
            self.assertIsNone(headers["ETag"], path)
            self.assertIsNone(headers["Last-Modified"], path)
            if path.startswith("/settings/github/"):
                self.assertEqual(headers["Referrer-Policy"], "no-referrer")

        # Both packaged shells have identical sizes and epoch mtimes: old validators must not yield 304.
        self.assertEqual(len(shells[0]), len(shells[1]))
        self.assertEqual((first / "dashboard/index.html").stat().st_mtime, 0)
        self.assertEqual((second / "dashboard/index.html").stat().st_mtime, 0)
        shutil.rmtree(served / "assets")
        shutil.copytree(second / "dashboard", served, dirs_exist_ok=True)
        for path in paths:
            status, headers, body = get(path, {"If-None-Match": "*",
                                             "If-Modified-Since": "Wed, 31 Dec 2099 23:59:59 GMT"})
            self.assertEqual(status, 200, path)
            self.assertEqual(headers.get_content_type(), "text/html", path)
            self.assertEqual(body, shells[1], path)
            self.assertEqual(headers["Cache-Control"],
                             "no-store" if path.startswith("/settings/github/") else "no-cache", path)
            if path.startswith("/settings/github/"):
                self.assertEqual(headers["Referrer-Policy"], "no-referrer")
        for extension, content_type in (("js", "application/javascript"), ("css", "text/css")):
            status, headers, body = get(f"/assets/index-release2.{extension}")
            self.assertEqual(status, 200)
            self.assertEqual(headers.get_content_type(), content_type)
            self.assertEqual(body, (second / "dashboard/assets" / f"index-release2.{extension}").read_bytes())
            self.assertIsNotNone(headers["ETag"])
            self.assertEqual(get(f"/assets/index-release2.{extension}",
                                 {"If-None-Match": headers["ETag"]})[0], 304)
        for path in ("/assets/index-release1.js", "/assets/missing.css", "/assets/nested/missing.js"):
            status, headers, body = get(path)
            self.assertEqual(status, 404, path)
            self.assertNotEqual(headers.get_content_type(), "text/html", path)
            self.assertNotEqual(body, shells[1], path)
        status, headers, body = get("/runtime-config.json")
        self.assertEqual(status, 200)
        self.assertEqual(headers.get_content_type(), "application/json")
        self.assertEqual(headers["Cache-Control"], "no-store")
        self.assertEqual(json.loads(body), {
            "apiBaseUrl": "https://api.localhost",
            "sourceUrl": "https://github.com/muniperez/municode",
            "auth": {"origin": "https://api.localhost",
                     "capabilitiesUrl": "https://api.localhost/api/account/capabilities"},
        })

    def test_missing_dashboard_output_fails_without_partial_archive(self):
        (self.payloads["dashboard"] / "index.html").unlink()
        result = self.package(check=False)
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("Dashboard payload lacks index.html", result.stderr)
        self.assertEqual(list(self.output.glob("*.tar.gz")), [])

    def test_unsafe_package_version_fails_before_creating_output(self):
        command = self.command()
        command[command.index("--version") + 1] = "../../release"
        result = self.package(command, check=False)
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("exact semantic version", result.stderr)
        self.assertFalse(self.output.exists())

    def test_installer_verifies_and_stages_a_fresh_release(self):
        self.package()
        archive = next(self.output.glob("*.tar.gz"))
        extracted = self.root / "extracted"
        extracted.mkdir()
        with tarfile.open(archive, "r:gz") as bundle:
            bundle.extractall(extracted, filter="data")
        release_root = next(extracted.iterdir())
        host = self.root / "host"
        digest = hashlib.sha256(archive.read_bytes()).hexdigest()
        release_id = f"local-{'a' * 12}-{digest[:12]}"
        install_root = host / "opt/municode/releases" / release_id
        current = host / "opt/municode/current"
        state = host / "var/lib/municode"
        secrets = host / "etc/municode/secrets"
        policy = host / "etc/municode/config/municode.json"
        operator = policy.with_name("operator.env")
        operator.parent.mkdir(parents=True)
        operator.write_text("MUNICODE_INSTALLATION_ID=synthetic-installation\n"
                            "CONSOLE_HOSTNAME=console.example.com\n")
        command = [str(release_root / "install.sh"), "--host-root", str(host),
                   "--bundle-sha256", digest]
        subprocess.run(
            command,
            check=True,
            capture_output=True,
            text=True,
        )
        self.assertTrue(current.is_symlink())
        self.assertEqual(current.readlink(), Path("releases") / release_id)
        self.assertTrue(os.access(host / "usr/local/sbin/municode-compose", os.X_OK))
        self.assertTrue((state / "deploy.lock").is_file())
        self.assertTrue((state / "deploys").is_dir())
        self.assertTrue((state / "backups").is_dir())
        active = state / "active-release.json"
        record = json.loads(active.read_text())
        self.assertEqual(record, {
            "releaseId": release_id, "tag": "municode-v0.0.0-test", "sourceRevision": "a" * 40,
            "bundleSha256": digest, "trust": "local-build",
            "activatedAt": record["activatedAt"], "previous": None,
        })
        self.assertTrue(all(not path.stat().st_mode & 0o222
                            for path in (install_root, *install_root.rglob("*"))))
        self.assertTrue((install_root / "runtime" / "api" / "Municode.Api.dll").is_file())
        self.assertTrue((secrets / "workspace-gateway-credential").is_file())
        self.assertTrue((secrets / "ssh" / "worker-key").is_file())
        self.assertTrue((secrets / "openbao-approle" / "role-id").is_file())
        self.assertEqual(secrets.stat().st_mode & 0o777, 0o700)
        self.assertEqual((install_root / ".env").stat().st_mode & 0o777, 0o400)
        env = (install_root / ".env").read_text()
        self.assertNotIn("synthetic-contract", env)
        self.assertEqual(env.count("MUNICODE_SECRETS_DIR="), 1)
        self.assertIn(f"MUNICODE_POLICY_FILE={policy}", env.splitlines())
        self.assertIn("MUNICODE_INSTALLATION_ID=synthetic-installation", env.splitlines())
        self.assertIn("CONSOLE_HOSTNAME=console.example.com", env.splitlines())
        self.assertIn("MUNICODE_RELEASE_TAG=municode-v0.0.0-test", env.splitlines())
        self.assertIn(f"MUNICODE_RELEASE_SOURCE_REVISION={'a' * 40}", env.splitlines())
        self.assertIn("MUNICODE_RELEASE_TRUST=local-build", env.splitlines())
        self.assertEqual(policy.read_bytes(), (release_root / "municode.json").read_bytes())

        kratos_env = secrets / "kratos.env"
        cipher_values = [
            line.partition(b"=")[2]
            for line in kratos_env.read_bytes().splitlines()
            if line.startswith(b"SECRETS_CIPHER=")
        ]
        self.assertEqual(len(cipher_values), 1)
        self.assertEqual(len(cipher_values[0]), 32)
        self.assertTrue(all(byte in b"ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_"
                            for byte in cipher_values[0]))
        self.assertEqual(kratos_env.stat().st_mode & 0o7777, 0o600)

        policy.write_text('{"operator":true}\n')
        operator.write_text("CONSOLE_HOSTNAME=edited.example.com\n")
        protected = (active, kratos_env, install_root / ".env", policy, operator)
        original = {path: (path.read_bytes(), path.stat().st_mtime_ns) for path in protected}
        subprocess.run(command, check=True, capture_output=True, text=True)
        rejected = subprocess.run(command[:-1] + ["f" * 64], capture_output=True, text=True)
        self.assertNotEqual(rejected.returncode, 0)
        self.assertIn("cannot upgrade or repair", rejected.stderr)
        self.assertEqual({path: (path.read_bytes(), path.stat().st_mtime_ns) for path in protected}, original)
        self.assertEqual(policy.read_text(), '{"operator":true}\n')

        with (release_root / ".env.example").open("a") as template:
            template.write("UNKNOWN_KEY=not-in-contract\n")
        rejected = subprocess.run(
            [sys.executable, "-B", str(release_root / "verify_bundle.py")],
            capture_output=True, text=True,
        )
        self.assertNotEqual(rejected.returncode, 0)
        self.assertIn("Environment contract keys do not match", rejected.stderr)


class LocalBuildTests(unittest.TestCase):
    def setUp(self):
        artifacts = ROOT / ".local" / "artifacts"
        artifacts.mkdir(parents=True, exist_ok=True)
        temporary = tempfile.TemporaryDirectory(prefix="local-release-test-", dir=artifacts)
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name) / "checkout"
        self.root.mkdir()
        self.git("init", "--quiet")
        for directory in ("packaging", "scripts/delivery"):
            shutil.copytree(ROOT / directory, self.root / directory, ignore=shutil.ignore_patterns("test_*.py"))
        (self.root / "docs").mkdir()
        for name in ("operator.md", "user.md"):
            (self.root / "docs" / name).write_text("Selected source revision\n")
        (self.root / ".gitignore").write_text(".local/\n")
        self.git("add", ".")
        self.commit("First tree")
        self.revision = self.git("rev-parse", "HEAD").strip()
        self.git("tag", "requested")
        (self.root / "docs" / "operator.md").write_text("Wrong source revision\n")
        self.git("add", ".")
        self.commit("Second tree")

        fake_bin = self.root.parent / "bin"
        fake_bin.mkdir()
        tool = fake_bin / "fake-tool"
        tool.write_text(
            "#!/usr/bin/env python3\n"
            "from pathlib import Path\n"
            "import io, json, sys, tarfile\n"
            "tool, *args = sys.argv\n"
            "tool = Path(tool).name\n"
            "if tool == 'dotnet' and args[0] == 'publish':\n"
            "    out = Path(args[args.index('-o') + 1])\n"
            "    out.mkdir(parents=True)\n"
            "    name = Path(args[1]).stem\n"
            "    filename = ('municode.exe' if out.name == 'win-x64' else 'municode') if name.endswith('.Cli') else name + '.dll'\n"
            "    binary = out / filename\n"
            "    binary.write_text('synthetic binary')\n"
            "    binary.chmod(0o755)\n"
            "elif tool == 'dotnet':\n"
            "    assert args[1:] == ['migrations', 'list', '--json']\n"
            f"    print({json.dumps(MIGRATIONS)!r})\n"
            "elif tool == 'npm':\n"
            "    if args[:2] == ['run', 'build']:\n"
            "        out = Path('.local/artifacts/dashboard-dist')\n"
            "        out.mkdir(parents=True)\n"
            "        (out / 'index.html').write_text(Path('docs/operator.md').read_text())\n"
            "    else:\n"
            "        assert args == ['ci', '--prefix', 'modules/dashboard']\n"
            "elif tool == 'docker':\n"
            "    if args[:2] == ['buildx', 'build']:\n"
            "        name = Path(args[args.index('--file') + 1]).parent.name\n"
            "        assert name in ('Municode.Api', 'Municode.Worker', 'Municode.Gateway')\n"
            "        assert args[2:5] == ['--platform', 'linux/arm64', '--load'] and args[-1] == '.'\n"
            "        assert not Path('untracked').exists()\n"
            "    elif args[:2] == ['image', 'inspect']:\n"
            "        name = args[-1].split(':')[0]\n"
            "        print('sha256:' + {'municode-api': 'a', 'municode-worker': 'b', 'municode-gateway': 'c'}[name] * 64)\n"
            "    elif args[:2] == ['image', 'save']:\n"
            "        assert args[2] == '--output' and args[-1].startswith('sha256:')\n"
            "        with tarfile.open(args[3], 'w') as archive:\n"
            "            for name, data in [('manifest.json', json.dumps([{'Config': 'config.json'}]).encode()),\n"
            "                               ('config.json', args[-1].encode())]:\n"
            "                member = tarfile.TarInfo(name)\n"
            "                member.size = len(data)\n"
            "                archive.addfile(member, io.BytesIO(data))\n"
            "    else:\n"
            "        assert args[:2] == ['image', 'rm']\n"
        )
        tool.chmod(0o755)
        for name in ("docker", "dotnet", "npm"):
            (fake_bin / name).symlink_to(tool)
        self.env = dict(os.environ, PATH=str(fake_bin) + os.pathsep + os.environ["PATH"])

    def git(self, *args):
        return subprocess.check_output(["git", "-C", str(self.root), *args], text=True)

    def commit(self, message):
        self.git("-c", "user.name=Test", "-c", "user.email=test@example.test",
                 "-c", "commit.gpgsign=false", "commit", "--quiet", "-m", message)

    def build(self, *args, check=True):
        return subprocess.run([self.root / "scripts" / "delivery" / "build-release.sh", *args],
                              cwd=self.root, env=self.env, check=check, capture_output=True, text=True)

    def test_requires_ref_and_rejects_unsupported_platform(self):
        for args, expected in (([], "required: --ref"),
                               (["--ref", "HEAD", "--platform", "windows/amd64"], "invalid choice")):
            with self.subTest(args=args):
                result = self.build(*args, check=False)
                self.assertEqual(result.returncode, 2)
                self.assertIn(expected, result.stderr)

    def test_dirty_tree_requires_override_and_records_it_without_building_dirty_files(self):
        (self.root / "untracked").touch()
        result = self.build("--ref", "requested", check=False)
        self.assertEqual(result.returncode, 1)
        self.assertIn("Checkout is dirty", result.stderr)
        self.assertFalse((self.root / ".local" / "artifacts" / "release").exists())
        (self.root / "docs" / "operator.md").write_text("Uncommitted content\n")
        output = Path(self.build("--ref", "HEAD", "--platform", "linux/arm64", "--allow-dirty")
                      .stdout.splitlines()[-1])
        manifest = json.loads((output / "release-manifest.json").read_text())
        self.assertIs(manifest["sourceDirty"], True)
        with tarfile.open(output / manifest["bundle"]) as archive:
            entry = archive.extractfile("municode-0.0.0-local/dashboard/index.html")
            self.assertEqual(entry.read(), b"Wrong source revision\n")

    def test_build_uses_exact_ref_and_verifies_image_inventory(self):
        output = Path(self.build("--ref", "requested", "--platform", "linux/arm64").stdout.splitlines()[-1])
        manifest_path = output / "release-manifest.json"
        manifest = json.loads(manifest_path.read_text())
        archive = output / manifest["bundle"]
        bundle_digest = hashlib.sha256(archive.read_bytes()).hexdigest()
        self.assertEqual(output.name, f"local-{self.revision[:12]}-{bundle_digest[:12]}")
        self.assertEqual(list(output.parent.iterdir()), [output])
        self.assertEqual(self.git("status", "--porcelain"), "")
        self.assertEqual(self.git("worktree", "list", "--porcelain").count("worktree "), 1)
        self.assertEqual(manifest["sourceRevision"], self.revision)
        self.assertIs(manifest["sourceDirty"], False)
        self.assertEqual(manifest["imagePlatform"], "linux/arm64")
        self.assertEqual(manifest["deployProtocol"], 1)
        self.assertEqual(manifest["migrations"], MIGRATIONS)
        self.assertEqual((output / "checksums.txt").read_text().splitlines(), [
            f"{bundle_digest}  {archive.name}",
            f"{hashlib.sha256(manifest_path.read_bytes()).hexdigest()}  {manifest_path.name}",
        ])
        self.assertFalse((output / "checksums.txt.sigstore.json").exists())
        extracted = self.root.parent / "unpacked"
        with tarfile.open(archive) as bundle:
            bundle.extractall(extracted, filter="data")
        release = extracted / "municode-0.0.0-local"
        self.assertEqual((release / "dashboard" / "index.html").read_text(), "Selected source revision\n")
        self.assertEqual((release / manifest_path.name).read_bytes(), manifest_path.read_bytes())
        env = (release / ".env.example").read_text()
        self.assertIn("MUNICODE_RUNTIME_PULL_POLICY=never\n", env)
        self.assertEqual(set(manifest["imageArchives"]), {"api", "worker", "gateway"})
        for name, letter in (("api", "a"), ("worker", "b"), ("gateway", "c")):
            config = hashlib.sha256(("sha256:" + letter * 64).encode()).hexdigest()
            image_id = f"sha256:{config}"
            reference = f"municode-{name}:local-{config}"
            path = f"images/{name}.tar"
            self.assertEqual(manifest["imageArchives"][name], {
                "path": path, "sha256": hashlib.sha256((release / path).read_bytes()).hexdigest(),
                "imageId": image_id, "reference": reference,
            })
            self.assertIn(f"MUNICODE_{name.upper()}_IMAGE={reference}\n", env)
        command = [sys.executable, "-B", str(release / "verify_bundle.py")]
        subprocess.run(command, check=True)

        host = self.root.parent / "host"
        subprocess.run([release / "install.sh", "--host-root", host, "--bundle-sha256", bundle_digest],
                       check=True, capture_output=True, text=True)
        installed = host / "opt/municode/releases" / output.name
        self.assertEqual((host / "opt/municode/current").readlink(), Path("releases") / output.name)
        rendered = (installed / ".env").read_text().splitlines()
        self.assertIn("MUNICODE_RUNTIME_PULL_POLICY=never", rendered)
        for name in ("api", "worker", "gateway"):
            reference = manifest["imageArchives"][name]["reference"]
            self.assertIn(f"MUNICODE_{name.upper()}_IMAGE={reference}", rendered)
        self.assertEqual(json.loads((host / "var/lib/municode/active-release.json").read_text())["releaseId"],
                         output.name)

        fake = FakeDeployDocker(installed)
        stage = ("--bundle", str(archive), "--bundle-sha256", bundle_digest, "--trust", "local-build")
        code, result = deploy_command(host, fake, "stage", "--operation", "test-local", *stage)
        self.assertEqual(code, 0, result)
        self.assertEqual(result["targetRelease"]["releaseId"], output.name)
        self.assertEqual(
            {call[3:] for call in fake.calls if call[:3] == ("docker", "image", "tag")},
            {(item["imageId"], item["reference"]) for item in manifest["imageArchives"].values()},
        )
        fake.calls.clear()
        fake.wrong_loaded_id = True
        code, result = deploy_command(host, fake, "stage", "--operation", "invalid-local", *stage)
        self.assertEqual((code, result["blockers"]), (1, ["Loaded local image does not match the bundle's image ID."]))
        self.assertFalse(any(call[:3] == ("docker", "image", "tag") for call in fake.calls))

        (release / "images" / "api.tar").write_text("corrupted")
        result = subprocess.run(command, capture_output=True, text=True)
        self.assertEqual(result.returncode, 1)
        self.assertIn("Local image archive digest mismatch: api", result.stderr)


if __name__ == "__main__":
    unittest.main()
