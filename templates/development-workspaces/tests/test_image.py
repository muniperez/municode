import hashlib
import http.server
import importlib.util
import io
import json
import os
from pathlib import Path
import shutil
import stat
import subprocess
import tarfile
import tempfile
import threading
import sys
import unittest
import zipfile
from contextlib import ExitStack, contextmanager
from unittest.mock import MagicMock, Mock, patch


HERE = Path(__file__).resolve().parents[1]
ARTIFACTS = HERE.parents[1] / ".local/artifacts/runtime"


def load(name, filename):
    spec = importlib.util.spec_from_file_location(name, HERE / "image" / filename)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


builder = load("generic_image_builder", "build.py")
native = load("generic_native_qualification", "native.py")


class NativeRendererTests(unittest.TestCase):
    ENTRY = (b'const __vite__mapDeps=(i,m=__vite__mapDeps,d=(m.f||(m.f=["assets/app.js","assets/app.css"])))'
             b'=>i.map(i=>d[i]);import("./app.js");')

    @contextmanager
    def renderer(self, script_status=200, script_type="text/javascript", script_body=ENTRY,
                 script_url="/assets/main.js", dependency=(200, "text/javascript", b"export {};")):
        requests = []
        html = ('<img id="boot-shell-logo" src="/apple-touch-icon.png">'
                f'<script type="module" src="{script_url}"></script>'
                '<link rel="stylesheet" href="/assets/main.css">').encode()
        responses = {
            "/": (200, "text/html; charset=utf-8", html),
            "/assets/main.js": (script_status, script_type, script_body),
            "/assets/main.css": (200, "text/css", b"body {}"),
            "/assets/app.js": dependency,
            "/assets/app.css": (200, "text/css", b"main {}"),
            "/apple-touch-icon.png": (200, "image/png", b"\x89PNG\r\n\x1a\n"),
        }

        class Handler(http.server.BaseHTTPRequestHandler):
            def do_GET(self):
                requests.append((self.path, dict(self.headers)))
                status, mime, body = responses.get(self.path, (404, "text/plain", b"missing"))
                self.send_response(status)
                self.send_header("Content-Type", mime)
                self.send_header("Content-Length", str(len(body)))
                self.end_headers()
                self.wfile.write(body)

            def log_message(self, *args):
                pass

        server = http.server.ThreadingHTTPServer(("127.0.0.1", 0), Handler)
        thread = threading.Thread(target=server.serve_forever)
        thread.start()
        connection = native.http.client.HTTPConnection
        try:
            with patch.object(native.http.client, "HTTPConnection", side_effect=lambda host, port, timeout:
                              connection("127.0.0.1", server.server_port, timeout=timeout)):
                yield requests
        finally:
            server.shutdown()
            server.server_close()
            thread.join()

    def qualify(self):
        ARTIFACTS.mkdir(parents=True, exist_ok=True)
        with tempfile.TemporaryDirectory(dir=ARTIFACTS) as directory:
            probe = native.Native.__new__(native.Native)
            probe.directory = Path(directory)
            probe.run_id = "synthetic-renderer"
            probe.t3_project_id = "synthetic-project"
            probe.wait = Mock()
            client = Mock()
            client.call.return_value = {
                "environmentId": "synthetic-environment", "snapshotSequence": 1,
                "realTicketWebSocketOpened": True,
            }

            def command(argv, **kwargs):
                if "--operation-id" in argv:
                    return "synthetic-token"
                if argv[:4] == ["t3", "auth", "session", "issue"]:
                    return "--scope --ttl --token-only --base-dir"
                return {"MainPID": "123", "KillMode": "process", "SendSIGKILL": "no"}[argv[-2]]

            with patch.object(native, "command", side_effect=command), \
                    patch.object(native, "T3Client", return_value=client):
                return probe.start_services()

    def test_html_and_ticketed_websocket_success_do_not_qualify_missing_renderer(self):
        for status, mime, body in ((200, "text/html", b"<html>sensitive-body</html>"),
                                   (404, "text/javascript", b"missing"),
                                   (200, "text/javascript", b"")):
            with self.subTest(status=status, mime=mime), self.renderer(status, mime, body):
                with self.assertRaisesRegex(native.ProbeError, "T3_RENDERER_ASSET") as caught:
                    self.qualify()
                self.assertNotIn("sensitive-body", str(caught.exception))
                self.assertNotIn("/assets/main.js", str(caught.exception))

    def test_qualification_checks_renderer_without_credentials_or_ui_claim(self):
        with self.renderer() as requests:
            result = self.qualify()
        self.assertIn("renderer", result)
        self.assertEqual(result["renderer"], {
            "moduleScripts": 1, "stylesheets": 1, "modulePreloads": 0, "splashLogos": 1,
            "entryDependencies": 2, "browserUiVerified": False,
        })
        self.assertEqual({path for path, _ in requests},
                         {"/", "/assets/main.js", "/assets/main.css", "/apple-touch-icon.png",
                          "/assets/app.js", "/assets/app.css"})
        for _, headers in requests:
            self.assertNotIn("Authorization", headers)
            self.assertNotIn("Cookie", headers)

    def test_missing_entry_preload_dependency_does_not_qualify(self):
        # Vite preloads every listed chunk before mounting; one HTML-fallback or missing chunk leaves the splash.
        for dependency in ((200, "text/html", b"<html>sensitive-body</html>"),
                           (404, "text/javascript", b"missing"),
                           (200, "text/javascript", b"")):
            with self.subTest(dependency=dependency[:2]), self.renderer(dependency=dependency):
                with self.assertRaisesRegex(native.ProbeError, "T3_RENDERER_DEPENDENCY") as caught:
                    self.qualify()
                self.assertNotIn("sensitive-body", str(caught.exception))
                self.assertNotIn("/assets/app.js", str(caught.exception))

    def test_external_entry_dependency_is_rejected_without_requesting_it(self):
        entry = b'const __vite__mapDeps=(i,m=__vite__mapDeps,d=(m.f||(m.f=["https://external.example/x.js"])))'
        with self.renderer(script_body=entry) as requests, \
                self.assertRaisesRegex(native.ProbeError, "T3_RENDERER_REFERENCE"):
            self.qualify()
        self.assertEqual([path for path, _ in requests], ["/", "/apple-touch-icon.png", "/assets/main.js"])

    def test_external_renderer_reference_is_rejected_without_requesting_it(self):
        with self.renderer(script_url="https://external.example/private.js") as requests, \
                self.assertRaisesRegex(native.ProbeError, "T3_RENDERER_REFERENCE"):
            self.qualify()
        self.assertEqual([path for path, _ in requests], ["/"])


class NativeScratchFixtureTests(unittest.TestCase):
    @contextmanager
    def fixture(self, directory, watcher="active"):
        home = Path(directory) / "home"
        home.mkdir(mode=0o700)
        environ = Path(directory) / "environ"
        environ.write_text("")
        marker = Path(directory) / "migration-marker"
        marker.touch()
        tiers = Mock(SCRATCH="/synthetic-scratch", ENV_FILE="/synthetic-scratch.env", MARKER=str(marker))
        tiers.load_environment.return_value = {}
        tiers.scratch_ready.return_value = False
        probe = native.Native.__new__(native.Native)
        probe.storage_tiers = Mock(return_value=tiers)
        probe.t3_unit = Mock(return_value={"MainPID": 123})
        probe.rt = Mock()
        probe.config = {}
        adapter = probe.rt.Linux.return_value
        events = []
        adapter.stop_services.side_effect = lambda: events.append("stop")
        adapter.thaw.side_effect = lambda: events.append("thaw")
        adapter.start_services.side_effect = lambda: events.append("start")
        probe.t3_recovered = Mock(side_effect=lambda pid: events.append(("recovered", pid)))
        owners = {}
        real_stat = os.stat

        def path(*parts):
            value = Path(*parts)
            if value == Path("/workspace/home"):
                return home
            if value == Path("/proc/123/environ"):
                return environ
            return value

        def file_stat(value, *args, **kwargs):
            if os.fspath(value) == tiers.SCRATCH:
                return Mock(st_dev=home.stat().st_dev + 1)
            if os.fspath(value) == "/workspace":
                return home.stat()
            return real_stat(value, *args, **kwargs)

        def command(argv, **kwargs):
            if argv[:2] == ["systemctl", "show"]:
                return tiers.ENV_FILE
            if argv[:2] == ["systemctl", "reset-failed"]:
                events.append(argv)
            if argv[:2] == ["systemctl", "is-active"]:
                events.append(argv)
                return watcher
            return ""

        with ExitStack() as stack:
            stack.enter_context(patch.object(native, "Path", side_effect=path))
            stack.enter_context(patch.object(native.os, "stat", side_effect=file_stat))
            stack.enter_context(patch.object(native.os, "chown",
                                            side_effect=lambda path, uid, gid: owners.update({path: (uid, gid)})))
            stack.enter_context(patch.object(native.shutil, "which", return_value=None))
            stack.enter_context(patch.object(native, "command", side_effect=command))
            yield probe, adapter, home, owners, events

    def test_cache_fixture_owns_private_leaf_directories_as_well_as_ancestors_and_files(self):
        with tempfile.TemporaryDirectory(dir=ARTIFACTS) as directory:
            with self.fixture(directory) as (probe, adapter, home, owners, _):
                adapter.stop_services.side_effect = RuntimeError("fixture-created")
                with self.assertRaisesRegex(RuntimeError, "fixture-created"):
                    probe.scratch_tier()
                leaves = list(home.rglob("native-cache-entry"))
                self.assertEqual(len(leaves), 7)
                for path in home.rglob("*"):
                    self.assertEqual(owners.get(path), (2000, 2000), str(path))
                    if path.is_dir():
                        self.assertEqual(path.stat().st_mode & 0o777, 0o700, str(path))

    def test_watcher_budget_reset_precedes_normal_t3_start_and_dependency_must_be_active(self):
        for watcher in ("active", "failed"):
            with self.subTest(watcher=watcher), tempfile.TemporaryDirectory(dir=ARTIFACTS) as directory, \
                    self.fixture(directory, watcher) as (probe, _, _, _, events):

                def wait(predicate):
                    native.check(predicate(), "EXPECTED_NATIVE_STATE_TIMEOUT")
                    raise RuntimeError("watcher-verified")

                probe.wait = Mock(side_effect=wait)
                error = RuntimeError if watcher == "active" else native.ProbeError
                message = "watcher-verified" if watcher == "active" else "EXPECTED_NATIVE_STATE_TIMEOUT"
                with self.assertRaisesRegex(error, message):
                    probe.scratch_tier()
                self.assertEqual(events, [
                    "stop", "thaw",
                    ["systemctl", "reset-failed", "workspaces-scratch-watch.service"],
                    "start", ("recovered", 123),
                    ["systemctl", "is-active", "workspaces-scratch-watch.service"],
                ])


class NativeProviderProbeTests(unittest.TestCase):
    COPILOT = dict(driver="acpRegistry", enabled=True, config={
        "source": "local", "commandPath": "/usr/local/bin/copilot", "commandArgs": ["--acp", "--stdio"]})

    def run_probe(self, instances, unusable=(), agents=None):
        ARTIFACTS.mkdir(parents=True, exist_ok=True)
        with tempfile.TemporaryDirectory(dir=ARTIFACTS) as directory:
            settings = Path(directory) / "settings.json"
            settings.write_text(json.dumps({"providerInstances": instances}))
            calls = []

            def command(argv, **kwargs):
                calls.append(argv)
                if "test" in argv:
                    if argv[-1] in unusable:
                        raise native.ProbeError("COMMAND_FAILED")
                    return ""
                if argv[-1] == "configure":
                    return json.dumps({"state": "AlreadyConfigured", "agents": agents or {
                        "antigravity": "AlreadyConfigured", "muse": "AlreadyConfigured"}})
                return json.dumps({"state": "Compatible", "version": "1.0.114", "authentication": "NotChecked"})
            probe = native.Native.__new__(native.Native)
            with patch.object(native, "Path", side_effect=lambda value: settings if value.endswith(
                    "settings.json") else Path(value)), patch.object(native, "command", side_effect=command):
                return probe.copilot_provider(), calls

    def seeded(self):
        return {"workspaces-copilot": self.COPILOT,
                **{instance_id: dict(driver=driver, enabled=True, config={"binaryPath": executable})
                   for instance_id, (driver, executable) in native.SEEDED_AGENT_PROVIDERS.items()}}

    def test_copilot_probe_requires_every_seeded_agent_with_an_executable_binary_as_coding(self):
        self.assertEqual(sorted(native.SEEDED_AGENT_PROVIDERS), ["workspaces-antigravity", "workspaces-muse"])
        result, calls = self.run_probe(self.seeded())
        self.assertEqual(result["agentProvidersSeeded"], ["workspaces-antigravity", "workspaces-muse"])
        checked = [argv[-1] for argv in calls if "test" in argv]
        self.assertEqual(sorted(checked), ["/opt/workspaces-antigravity/agy_acp_server.par", "/usr/local/bin/muse"])
        self.assertTrue(all(argv[:2] == ["setpriv", "--reuid=2000"] for argv in calls))
        self.assertIn("copilot-stdio-provider-setup", native.REQUIRED_PROBES)
        self.assertEqual(len(native.REQUIRED_PROBES), 24)

    def test_copilot_probe_rejects_missing_disabled_or_unusable_agent_providers(self):
        for instance_id in native.SEEDED_AGENT_PROVIDERS:
            for change in ("missing", "disabled", "binaryPath"):
                with self.subTest(instance_id=instance_id, change=change):
                    instances = self.seeded()
                    if change == "missing":
                        del instances[instance_id]
                    elif change == "disabled":
                        instances[instance_id]["enabled"] = False
                    else:
                        instances[instance_id]["config"] = {"binaryPath": "/elsewhere"}
                    with self.assertRaisesRegex(native.ProbeError, "AGENT_PROVIDER_NOT_SEEDED"):
                        self.run_probe(instances)
            with self.subTest(instance_id=instance_id, change="unusable"):
                executable = native.SEEDED_AGENT_PROVIDERS[instance_id][1]
                with self.assertRaisesRegex(native.ProbeError, "AGENT_PROVIDER_BINARY_UNUSABLE") as caught:
                    self.run_probe(self.seeded(), unusable=(executable,))
                self.assertEqual(caught.exception.evidence, {"instanceId": instance_id})
        with self.assertRaisesRegex(native.ProbeError, "AGENT_PROVIDER_NOT_SEEDED"):
            self.run_probe(self.seeded(), agents={"antigravity": "AlreadyConfigured", "muse": "BinaryUnusable"})


class ImageTests(unittest.TestCase):
    def test_native_qualification_issues_clone_and_browse_scopes_but_keeps_readonly_fixture(self):
        expected = {"terminal:operate", "orchestration:operate", "terminal:read",
                    "orchestration:read", "source-control:write", "filesystem:read"}
        issued = {}

        def command(argv, **kwargs):
            if "--scope" in argv:
                issued[argv[argv.index("--operation-id") + 1]] = [
                    argv[index + 1] for index, value in enumerate(argv) if value == "--scope"]
                return "synthetic-token"
            if argv[:4] == ["t3", "auth", "session", "issue"]:
                return "--scope --ttl --token-only --base-dir"
            return {"MainPID": "123", "KillMode": "process", "SendSIGKILL": "no"}[argv[-2]]

        with tempfile.TemporaryDirectory(dir=ARTIFACTS) as directory:
            probe = native.Native.__new__(native.Native)
            probe.directory = Path(directory)
            probe.run_id = "fixture"
            probe.t3_project_id = "fixture-project"
            probe.wait = Mock()
            client = Mock()
            client.call.return_value = {"environmentId": "fixture-env", "snapshotSequence": 1}
            with patch.object(native, "command", side_effect=command), \
                    patch.object(native, "T3Client", return_value=client), \
                    patch.object(native, "t3_renderer_evidence", return_value={"browserUiVerified": False}):
                probe.start_services()
        self.assertEqual(expected, set(issued["native-token-operate"]))
        self.assertEqual(len(expected), len(issued["native-token-operate"]))
        self.assertEqual(["orchestration:read"], issued["native-token-readonly"])

    def test_t3_recovery_resets_independent_frozen_fixtures_not_budget_retries(self):
        probe = native.Native.__new__(native.Native)
        adapter = Mock()
        probe.rt = Mock()
        probe.rt.Linux.return_value = adapter
        probe.config = {}
        events = []
        adapter.start_services.side_effect = lambda: events.append("start")
        probe.t3_ready = Mock(return_value={"MainPID": 123, "NRestarts": 0})
        probe.signal_t3_main = Mock(side_effect=lambda sig: events.append(sig.name) or 123)
        probe.t3_recovered = Mock()
        probe.check_t3_freezer = Mock()
        probe.t3_unit = Mock(return_value={"NRestarts": 1})
        probe.t3_restart_delay = Mock(side_effect=[
            {}, {}, {}, *({"NRestarts": count} for count in range(5))])
        probe.t3_frozen_stop = Mock(side_effect=lambda _, stage: events.append(stage) or {})
        probe.t3_stays_stopped = Mock(return_value={"NRestarts": 5, "Result": "signal"})
        probe.wait_t3 = Mock(side_effect=[
            {"ExecMainCode": 1, "ExecMainStatus": 130},
            *({"ActiveState": "active", "NRestarts": count} for count in range(1, 5)),
            {"ActiveState": "failed", "NRestarts": 5},
        ])
        policy = dict(Restart="on-failure", RestartUSec="5s", OOMPolicy="stop",
                      StartLimitBurst="5", StartLimitIntervalUSec="5min",
                      KillMode="process", SendSIGKILL="no")

        def command(argv):
            self.assertEqual(argv[0], "systemctl")
            self.assertEqual(argv[2], native.T3_UNIT)
            if argv[1] == "show":
                return policy[argv[4]]
            self.assertIn(argv[1], ("reset-failed", "restart", "stop"))
            events.append(argv[1])
            return ""

        with tempfile.TemporaryDirectory(dir=ARTIFACTS) as directory:
            cgroup = Path(directory)
            (cgroup / "memory.max").write_text("max")
            with patch.object(native, "T3_CGROUP", cgroup), \
                    patch.object(native, "command", side_effect=command), \
                    patch.object(native.time, "monotonic", return_value=0):
                evidence = probe.t3_recovery()

        self.assertEqual(events, [
            "reset-failed", "SIGKILL", "SIGKILL", "stop",
            "reset-failed", "start", "before-freeze",
            "reset-failed", "start", "auto-restart",
            "reset-failed", "start", "start-pre",
            "start", "SIGINT",
            "start", "reset-failed", "restart",
            "SIGKILL", "SIGKILL", "SIGKILL", "SIGKILL", "SIGKILL",
            "reset-failed", "start",
        ])
        self.assertEqual(list(evidence["frozenStopStages"]),
                         ["before-freeze", "auto-restart", "start-pre"])
        self.assertEqual(evidence["crashesUntilBudgetExhausted"], 5)
        probe.t3_stays_stopped.assert_any_call(failed=True)

    def recovery_probe(self, inventories, status_changes=None):
        probe = native.Native.__new__(native.Native)
        probe.client = Mock()
        probe.directory = Path("/synthetic-native-only")
        probe.t3_project_id = "synthetic-project"
        probe.environment_id = "synthetic-environment"
        probe.snapshot = 7
        probe.run_id = "synthetic-run"
        probe.config = {"volume": {"filesystemUuid": "synthetic-filesystem"}}
        probe.expires = native.dt.datetime.now(native.dt.timezone.utc) + native.dt.timedelta(minutes=1)
        status = dict(vmId=probe.run_id, generation=1, phase="Ready")
        statuses = status_changes or [status] * len(inventories)
        probe.control = Mock(side_effect=lambda *args, **kwargs:
            statuses[min(probe.control.call_count - 1, len(statuses) - 1)] | {
                "workInventory": inventories[min(probe.control.call_count - 1, len(inventories) - 1)]})
        probe.client.call.return_value = dict(
            environmentId=probe.environment_id, realTicketWebSocketOpened=True, snapshotSequence=7)
        clock = [0]
        def advance(seconds):
            self.assertGreater(seconds, 0)
            self.assertLessEqual(seconds, 0.2)
            clock[0] += 10
        patches = (
            patch.object(probe, "t3_ready"),
            patch.object(probe, "wait"),
            patch.object(native, "T3Client", return_value=probe.client),
            patch.object(native, "command", return_value="synthetic-filesystem"),
            patch.object(native.http.client, "HTTPConnection"),
            patch.object(native.time, "monotonic", side_effect=lambda: clock[0]),
            patch.object(native.time, "sleep", side_effect=advance),
        )
        mocks = [item.start() for item in patches]
        for item in patches:
            self.addCleanup(item.stop)
        response = mocks[4].return_value.getresponse.return_value
        response.status = 200
        response.read.return_value = json.dumps({"environmentId": probe.environment_id}).encode()
        return probe, mocks, clock

    def test_native_inventory_reason_mapping_never_leaks_unknown_values(self):
        for value in (None, {}, ["sentinel"], "sentinel"):
            self.assertEqual({"reasonCode": "other_idle_blocker"},
                             native.work_inventory_evidence({"workInventory": {"reason": value}}))
        self.assertEqual(
            {"reasonCode": "unregistered_coding_process"},
            native.work_inventory_evidence({"workInventory": {
                "reason": "Unregistered live coding process; terminal input observation is not qualified"}}))

    def test_common_t3_recovery_polls_transient_busy_until_idle(self):
        inventories = [
            dict(state="BusyOrUnknown", reason="Coding process inventory changed during inspection"),
            dict(state="BusyOrUnknown", reason="Unregistered live coding process; terminal input observation is not qualified"),
            dict(state="IdleCandidate"),
        ]
        probe, mocks, _ = self.recovery_probe(inventories)
        self.assertIsNone(probe.t3_recovered(100))
        self.assertEqual(3, probe.control.call_count)
        self.assertEqual(3, mocks[3].call_count)
        self.assertEqual(3, mocks[4].call_count)
        probe.client.call.assert_called_once()
        self.assertEqual(2, mocks[6].call_count)

    def test_common_t3_recovery_times_out_busy_or_malformed_inventory(self):
        for inventory in (dict(state="BusyOrUnknown", reason="Coding process inventory changed during inspection"),
                          dict(state="BusyOrUnknown", reason="secret-sentinel"),
                          {"state": "BusyOrUnknown"}, None, "secret-sentinel",
                          {"state": ["IdleCandidate"], "reason": object()}):
            with self.subTest(inventory=inventory):
                probe, mocks, clock = self.recovery_probe([inventory])
                with self.assertRaisesRegex(native.ProbeError, "T3_RESTART_LEFT_UNKNOWN_WORK") as caught:
                    probe.t3_recovered(100)
                self.assertEqual(30, clock[0])
                self.assertEqual(3, probe.control.call_count)
                self.assertEqual(3, mocks[6].call_count)
                self.assertNotIn("secret-sentinel", json.dumps(caught.exception.evidence))
                if isinstance(inventory, dict) and inventory.get("reason") == \
                        "Coding process inventory changed during inspection":
                    self.assertEqual({"reasonCode": "inventory_changed"}, caught.exception.evidence)

    def test_common_t3_recovery_fails_immediately_on_identity_drift(self):
        for change in (dict(vmId="changed"), dict(generation=2), dict(phase="StorageUnavailable")):
            with self.subTest(change=change):
                status = dict(vmId="synthetic-run", generation=1, phase="Ready")
                probe, mocks, _ = self.recovery_probe(
                    [{"state": "BusyOrUnknown"}, {"state": "IdleCandidate"}, {"state": "IdleCandidate"}],
                    [status, status | change, status])
                with self.assertRaisesRegex(native.ProbeError, "VM_GENERATION_CHANGED_DURING_T3_RECOVERY"):
                    probe.t3_recovered(100)
                self.assertEqual(2, probe.control.call_count)
                self.assertEqual(1, mocks[6].call_count)

    def test_common_t3_recovery_rechecks_t3_and_mount_identity(self):
        probe, mocks, _ = self.recovery_probe([
            {"state": "BusyOrUnknown"}, {"state": "IdleCandidate"}])
        mocks[4].return_value.getresponse.return_value.read.side_effect = [
            json.dumps({"environmentId": probe.environment_id}).encode(),
            json.dumps({"environmentId": "changed"}).encode(),
        ]
        with self.assertRaisesRegex(native.ProbeError, "T3_ENVIRONMENT_IDENTITY_CHANGED"):
            probe.t3_recovered(100)
        self.assertEqual(2, probe.control.call_count)
        self.assertEqual(1, mocks[6].call_count)
        probe, mocks, _ = self.recovery_probe([
            {"state": "BusyOrUnknown"}, {"state": "IdleCandidate"}])
        mocks[3].side_effect = ["synthetic-filesystem", "changed"]
        with self.assertRaisesRegex(native.ProbeError, "RETAINED_VOLUME_CHANGED_DURING_T3_RECOVERY"):
            probe.t3_recovered(100)
        self.assertEqual(2, probe.control.call_count)
        self.assertEqual(1, mocks[6].call_count)

    def test_common_t3_recovery_does_not_accept_idle_after_deadline(self):
        probe, mocks, clock = self.recovery_probe([{"state": "IdleCandidate"}])
        def late_status(*args, **kwargs):
            clock[0] = 31
            return dict(vmId=probe.run_id, generation=1, phase="Ready",
                        workInventory={"state": "IdleCandidate"})
        probe.control.side_effect = late_status
        with self.assertRaisesRegex(native.ProbeError, "T3_RESTART_LEFT_UNKNOWN_WORK"):
            probe.t3_recovered(100)
        mocks[6].assert_not_called()

    def test_t3_restart_timer_is_distinct_from_a_start_job(self):
        probe = native.Native.__new__(native.Native)
        delay = dict(ActiveState="activating", SubState="auto-restart", Result="signal",
                     Job="", MainPID=0, ControlPID=0)
        with patch.object(probe, "wait_t3", return_value=delay):
            self.assertEqual(probe.t3_restart_delay("signal"), delay)
        for change in (dict(Job="42"), dict(ControlPID=123), dict(Result="oom-kill")):
            with self.subTest(change=change), patch.object(probe, "wait_t3", return_value=delay | change):
                with self.assertRaisesRegex(native.ProbeError, "T3_RESTART_DELAY_NOT_OBSERVED"):
                    probe.t3_restart_delay("signal")

    def frozen_kill_probe(self):
        temporary = tempfile.TemporaryDirectory(prefix="synthetic-t3-kill-")
        self.addCleanup(temporary.cleanup)
        root = Path(temporary.name)
        child = root / native.T3_UNIT
        child.mkdir()
        (child / "cgroup.kill").write_text("")
        (root / "cgroup.kill").write_text("parent-sentinel")
        sibling = root / "unrelated.service"
        sibling.mkdir()
        (sibling / "cgroup.kill").write_text("sibling-sentinel")
        (root / "cgroup.freeze").write_text("1\n")
        (root / "cgroup.events").write_text("populated 1\nfrozen 1\n")
        (root / "main.cgroup").write_text("0::/workspaceswork.slice/workspaces-t3.service\n")
        probe = native.Native.__new__(native.Native)
        probe.owns_runtime = True
        probe.fresh_authorization = Mock()
        probe.t3_unit = Mock(return_value=dict(ActiveState="active", SubState="running",
                                              Job="", ControlPID=0, MainPID=123))
        read_text, open_fd = Path.read_text, os.open
        reads = {native.WORK_CGROUP / name: root / name
                 for name in ("cgroup.freeze", "cgroup.events")}
        reads[Path("/proc/123/cgroup")] = root / "main.cgroup"
        def synthetic_read(path, *args, **kwargs):
            return read_text(reads.get(path, path), *args, **kwargs)
        def synthetic_open(path, flags, **kwargs):
            if isinstance(path, Path):
                self.assertEqual(path, Path("/sys/fs/cgroup/workspaceswork.slice"))
                path = root
            else:
                self.assertIn(path, ("workspaces-t3.service", "cgroup.kill"))
                self.assertIn("dir_fd", kwargs)
            return open_fd(path, flags, **kwargs)
        patches = (
            patch.object(native, "command",
                         return_value="/workspaceswork.slice/workspaces-t3.service"),
            patch.object(Path, "resolve", autospec=True, side_effect=lambda path, **kw: path),
            patch.object(Path, "is_file", autospec=True,
                         side_effect=lambda path: path == Path("/sys/fs/cgroup/cgroup.controllers")),
            patch.object(Path, "read_text", autospec=True, side_effect=synthetic_read),
            patch.object(native.os, "open", side_effect=synthetic_open),
        )
        mocks = [item.start() for item in patches]
        for item in patches:
            self.addCleanup(item.stop)
        return probe, root, mocks

    def test_frozen_t3_full_kill_writes_only_exact_child(self):
        probe, root, mocks = self.frozen_kill_probe()
        probe.kill_frozen_t3_cgroup(123)
        self.assertEqual("1", (root / native.T3_UNIT / "cgroup.kill").read_text())
        self.assertEqual("parent-sentinel", (root / "cgroup.kill").read_text())
        self.assertEqual("sibling-sentinel", (root / "unrelated.service/cgroup.kill").read_text())
        self.assertEqual("1\n", (root / "cgroup.freeze").read_text())
        probe.fresh_authorization.assert_called_once_with()
        mocks[0].assert_called_once_with(
            ["systemctl", "show", native.T3_UNIT, "-p", "ControlGroup", "--value"])
        self.assertEqual(3, mocks[4].call_count)

    def test_frozen_t3_full_kill_rejects_identity_and_freezer_drift(self):
        probe, root, mocks = self.frozen_kill_probe()
        state = probe.t3_unit.return_value.copy()
        cases = [
            ("state", state | change) for change in (
                dict(MainPID=124), dict(MainPID=0), dict(ControlPID=456),
                dict(Job="42"), dict(SubState="start-pre"), dict(ActiveState="activating"))
        ] + [
            ("group", group) for group in (
                "", "/", "/workspaceswork.slice", "/system.slice/workspaces-t3.service",
                "/workspaceswork.slice/unrelated.service",
                "/workspaceswork.slice/workspaces-t3.service/nested")
        ] + [("pid", "0::/workspaceswork.slice/unrelated.service\n"),
             ("freeze", "0\n"), ("events", "populated 1\nfrozen 0\n"),
             ("v2", False)]
        for kind, value in cases:
            with self.subTest(kind=kind, value=value):
                probe.t3_unit.return_value = value if kind == "state" else state
                mocks[0].return_value = value if kind == "group" else \
                    "/workspaceswork.slice/workspaces-t3.service"
                (root / "main.cgroup").write_text(value if kind == "pid" else
                    "0::/workspaceswork.slice/workspaces-t3.service\n")
                (root / "cgroup.freeze").write_text(value if kind == "freeze" else "1\n")
                (root / "cgroup.events").write_text(value if kind == "events" else "frozen 1\n")
                mocks[2].return_value = value if kind == "v2" else True
                mocks[2].side_effect = None
                with self.assertRaises(native.ProbeError):
                    probe.kill_frozen_t3_cgroup(123)
                self.assertEqual("", (root / native.T3_UNIT / "cgroup.kill").read_text())
                self.assertEqual("parent-sentinel", (root / "cgroup.kill").read_text())
                self.assertEqual("sibling-sentinel",
                                 (root / "unrelated.service/cgroup.kill").read_text())

    def test_frozen_t3_full_kill_forbids_wider_scope_and_symlinks(self):
        probe, root, mocks = self.frozen_kill_probe()
        for name, value in (("T3_CGROUP", Path("/sys/fs/cgroup")),
                            ("T3_CGROUP", native.WORK_CGROUP),
                            ("T3_CGROUP", native.WORK_CGROUP / "unrelated.service"),
                            ("WORK_CGROUP", Path("/sys/fs/cgroup")),
                            ("T3_UNIT", "unrelated.service")):
            with self.subTest(name=name, value=value), patch.object(native, name, value):
                with self.assertRaisesRegex(native.ProbeError, "T3_KILL_SCOPE_NOT_OWNED"):
                    probe.kill_frozen_t3_cgroup(123)
        mocks[1].side_effect = lambda path, **kw: Path("/sys/fs/cgroup")
        with self.assertRaisesRegex(native.ProbeError, "T3_KILL_SCOPE_NOT_OWNED"):
            probe.kill_frozen_t3_cgroup(123)
        mocks[4].assert_not_called()
        self.assertEqual("", (root / native.T3_UNIT / "cgroup.kill").read_text())

    def test_frozen_t3_full_kill_requires_current_authorization_and_owned_runtime(self):
        probe, root, mocks = self.frozen_kill_probe()
        probe.fresh_authorization.side_effect = native.ProbeError("AUTHORIZATION_EXPIRED")
        with self.assertRaisesRegex(native.ProbeError, "AUTHORIZATION_EXPIRED"):
            probe.kill_frozen_t3_cgroup(123)
        probe.fresh_authorization.side_effect = None
        probe.owns_runtime = False
        with self.assertRaisesRegex(native.ProbeError, "T3_PROBE_RUNTIME_NOT_OWNED"):
            probe.kill_frozen_t3_cgroup(123)
        mocks[4].assert_not_called()
        self.assertEqual("", (root / native.T3_UNIT / "cgroup.kill").read_text())

    def test_frozen_t3_full_kill_does_not_follow_child_or_kill_file_symlinks(self):
        probe, root, _ = self.frozen_kill_probe()
        child = root / native.T3_UNIT
        kill = child / "cgroup.kill"
        kill.unlink()
        kill.symlink_to(root / "cgroup.kill")
        with self.assertRaises(OSError):
            probe.kill_frozen_t3_cgroup(123)
        self.assertEqual("parent-sentinel", (root / "cgroup.kill").read_text())
        kill.unlink()
        child.rmdir()
        child.symlink_to(root, target_is_directory=True)
        with self.assertRaises(OSError):
            probe.kill_frozen_t3_cgroup(123)
        self.assertEqual("parent-sentinel", (root / "cgroup.kill").read_text())
        self.assertEqual("sibling-sentinel", (root / "unrelated.service/cgroup.kill").read_text())

    def test_t3_cancelled_restart_allows_retained_signal_result(self):
        probe = native.Native.__new__(native.Native)
        probe.rt = Mock()
        probe.rt.Linux.t3_cgroup_empty.return_value = True
        stopped = dict(ActiveState="inactive", SubState="dead", Result="signal",
                       Job="", MainPID=0, ControlPID=0)
        cleared = stopped | dict(Result="success")
        with patch.object(probe, "t3_unit", side_effect=[stopped, cleared]), \
                patch.object(probe, "check_t3_freezer") as freezer, \
                patch.object(native.time, "monotonic", side_effect=[0, 0, 6]), \
                patch.object(native.time, "sleep"):
            self.assertEqual(probe.t3_stays_stopped(frozen=True, after_crash=True), cleared)
        self.assertEqual([call.args for call in freezer.call_args_list], [(True,), (True,)])

    def test_t3_retained_signal_does_not_hide_unexpected_stop_state(self):
        probe = native.Native.__new__(native.Native)
        probe.rt = Mock()
        stopped = dict(ActiveState="inactive", SubState="dead", Result="signal",
                       Job="", MainPID=0, ControlPID=0)
        for state, empty, options in (
                (stopped, True, {}),
                (stopped | dict(Job="42"), True, {"after_crash": True}),
                (stopped, False, {"after_crash": True}),
                (stopped | dict(ActiveState="failed", SubState="failed"), True, {"after_crash": True}),
                (stopped | dict(ActiveState="failed", SubState="failed", Result="start-limit-hit"),
                 True, {"failed": True})):
            with self.subTest(state=state, empty=empty, options=options):
                probe.rt.Linux.t3_cgroup_empty.return_value = empty
                with patch.object(probe, "t3_unit", return_value=state), \
                        self.assertRaisesRegex(native.ProbeError, "T3_DID_NOT_REMAIN_STOPPED"):
                    probe.t3_stays_stopped(**options)

    def test_copilot_install_verifies_pin_and_uses_credential_free_initialize(self):
        lock = json.loads((HERE / "image/manifest.lock.json").read_text())
        with patch.object(builder, "download") as download, \
                patch.object(builder, "run") as run, \
                patch.object(builder.Path, "mkdir"), patch.object(builder.Path, "is_file", return_value=True), \
                patch.object(builder.Path, "write_text") as write, patch.object(builder.Path, "chmod"), \
                patch.object(builder, "probe_copilot", return_value={"version": "1.0.114"}) as probe:
            builder.install_copilot(lock, Path("/synthetic-build-cache"))
        download.assert_called_once_with(lock["copilot"], Path("/synthetic-build-cache/copilot.tar.gz"))
        run.assert_called_once_with(["tar", "--no-same-owner", "-xf",
                                     "/synthetic-build-cache/copilot.tar.gz", "-C", "/opt/workspaces-copilot"])
        self.assertIn('exec /opt/workspaces-copilot/copilot "$@"', write.call_args.args[0])
        self.assertIn("COPILOT_AUTO_UPDATE=false", write.call_args.args[0])
        probe.assert_called_once_with(expected_version=lock["copilot"]["version"],
                                      environment={"PATH": "/usr/local/bin:/usr/bin:/bin",
                                                   "HOME": "/synthetic-build-cache"})

    def test_native_source_control_versions_use_coding_uid_and_service_path(self):
        lock = json.loads((HERE / "image/manifest.lock.json").read_text())
        with patch.object(native, "command", side_effect=["gh version " + lock["gh"]["version"],
                                                         "glab version " + lock["glab"]["version"]]) as run:
            result = native.source_control_versions("/usr/local/bin:/usr/bin:/bin", lock)
        self.assertTrue(result["githubVersionMatched"] and result["gitlabVersionMatched"])
        for call in run.call_args_list:
            self.assertEqual(call.args[0][:7],
                             ["setpriv", "--reuid=2000", "--regid=2000", "--clear-groups", "env", "-i",
                              "HOME=/workspace/home"])
            self.assertIn("PATH=/usr/local/bin:/usr/bin:/bin", call.args[0])

    def test_native_source_control_rejects_missing_binaries_wrong_version_or_path(self):
        lock = json.loads((HERE / "image/manifest.lock.json").read_text())
        for outputs in ([native.ProbeError("COMMAND_UNAVAILABLE_OR_TIMEOUT")],
                        ["gh version " + lock["gh"]["version"], native.ProbeError("COMMAND_FAILED")],
                        ["gh version 0.0.0"]):
            with self.subTest(outputs=len(outputs)), patch.object(native, "command", side_effect=outputs):
                with self.assertRaises(native.ProbeError):
                    native.source_control_versions("/usr/local/bin:/usr/bin:/bin", lock)
        with patch.object(native, "command") as run:
            with self.assertRaisesRegex(native.ProbeError, "UNEXPECTED_T3_SERVICE_PATH"):
                native.source_control_versions("/usr/bin:/bin", lock)
            run.assert_not_called()

    @unittest.skipUnless(shutil.which("node"), "Optional Node validates the native T3 controller")
    def test_pinned_t3_source_control_discovery_fails_closed_and_redacts_auth_output(self):
        module = (HERE / "image/source-control-probe.mjs").as_uri()
        script = """
import assert from "node:assert/strict";
import { sourceControlEvidence } from MODULE;
const versions = { gh: "2.83.1", glab: "1.74.0" };
const discovery = { sourceControlProviders: [
  { kind: "github", executable: "gh", status: "available", version: { _tag: "Some", value: "gh version 2.83.1" },
    auth: { status: "unauthenticated", detail: "sensitive-diagnostic", account: "private-account" } },
  { kind: "gitlab", executable: "glab", status: "available", version: { _tag: "Some", value: "glab 1.74.0 (build)" },
    auth: { status: "unauthenticated" } }
] };
const evidence = sourceControlEvidence(discovery, versions);
assert.equal(evidence.pinnedT3SourceControlDiscoveryChecked, true);
assert.equal(JSON.stringify(evidence).includes("sensitive"), false);
assert.equal(JSON.stringify(evidence).includes("private-account"), false);
for (const index of [0, 1]) {
  for (const change of [{ status: "missing" }, { auth: { status: "unknown" } },
    { auth: { status: "authenticated" } }, { version: { _tag: "None" } },
    { version: { _tag: "Some", value: "2.83.10" } }]) {
    const invalid = structuredClone(discovery);
    Object.assign(invalid.sourceControlProviders[index], change);
    assert.throws(() => sourceControlEvidence(invalid, versions), /SOURCE_CONTROL_DISCOVERY_FAILED/);
  }
}
assert.throws(() => sourceControlEvidence({ sourceControlProviders: [] }, versions));
assert.throws(() => sourceControlEvidence({ sourceControlProviders:
  [...discovery.sourceControlProviders, discovery.sourceControlProviders[0]] }, versions));
""".replace("MODULE", json.dumps(module))
        subprocess.run(["node", "--input-type=module", "-e", script], check=True, capture_output=True, text=True)

    def test_native_command_retains_safe_exception_identity_without_raw_stderr(self):
        with self.assertRaises(native.ProbeError) as caught:
            native.command([sys.executable, "-c",
                            "raise FileNotFoundError(2, 'synthetic-sensitive-error-value')"])
        evidence = caught.exception.evidence
        self.assertEqual(evidence["exceptionClass"], "FileNotFoundError")
        self.assertEqual(evidence["errno"], 2)
        self.assertNotIn("synthetic-sensitive-error-value", json.dumps(evidence))

    def test_install_does_not_request_a_workspace_database(self):
        class PackagesObserved(Exception):
            pass

        calls = []

        def run(argv, **kwargs):
            calls.append(argv)
            if argv[:2] == ["apt-get", "-y"]:
                raise PackagesObserved()
            return ""

        with patch.object(builder.Path, "mkdir"), patch.object(builder.Path, "iterdir", return_value=[]), \
                patch.object(builder.Path, "write_text"), patch.object(builder, "run", side_effect=run):
            with self.assertRaises(PackagesObserved):
                builder.install("a" * 40, "synthetic-disposable-builder")
        install = calls[-1]
        self.assertIn("age", install)
        self.assertIn("git", install)
        self.assertFalse(any(value.startswith(("postgresql", "libpq")) for value in install))


class NativeRestoreTests(unittest.TestCase):
    def setUp(self):
        ARTIFACTS.mkdir(parents=True, exist_ok=True)
        self.temp = tempfile.TemporaryDirectory(prefix="native-restore-", dir=ARTIFACTS)
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)

    def codex_fixture(self, targets=None, *, running=True):
        home = self.root / "home"
        (home / ".codex").mkdir(parents=True)
        (self.root / "artifacts").mkdir()
        (self.root / "config").mkdir()
        run_id = "native-" + "a" * 32
        original = home / ".codex/tmp/arg0-linux/codex-observed"
        outside = self.root / "outside"
        outside.write_bytes(b"never copy or chown target contents")
        targets = targets or {name: str(outside) for name in
                              ("apply_patch", "applypatch", "codex-linux-sandbox")}

        def initialize(workspace, coding_home, *, observe):
            self.assertEqual((workspace, coding_home), (self.root, home))
            original.mkdir(parents=True)
            for name, target in targets.items():
                (original / name).symlink_to(target)
            observe(Mock(poll=Mock(return_value=None if running else 0)))
            shutil.rmtree(original)

        lstat = Path.lstat

        def coding_lstat(path, *args, **kwargs):
            info = lstat(path, *args, **kwargs)
            if stat.S_ISLNK(info.st_mode):
                values = list(info)
                values[4:6] = [2000, 2000]
                return os.stat_result(values)
            return info

        return home, run_id, original, outside, initialize, coding_lstat

    def test_codex_fixture_retains_documented_live_links_after_guard_cleanup_without_traversal(self):
        home, run_id, original, outside, initialize, coding_lstat = self.codex_fixture()
        with patch.object(native, "initialize_codex_without_credentials", side_effect=initialize), \
                patch.object(native.Path, "lstat", autospec=True, side_effect=coding_lstat), \
                patch.object(native.os, "chown") as chown:
            evidence = native.retain_codex_arg0_links(self.root, home, run_id)
        self.assertFalse(original.exists())
        self.assertEqual(outside.read_bytes(), b"never copy or chown target contents")
        self.assertEqual(json.loads((self.root / "config/native-codex-arg0-links.json").read_text()),
                         evidence)
        self.assertEqual(len(evidence["links"]), 3)
        for link in evidence["links"]:
            retained = self.root / link["relativePath"]
            self.assertTrue(retained.is_symlink())
            self.assertEqual(os.readlink(retained), str(outside))
            self.assertEqual(link["observedRelativePath"],
                             str((original / retained.name).relative_to(self.root)))
            self.assertEqual((link["uid"], link["gid"], link["mode"]), (2000, 2000, 0o777))
            chown.assert_any_call(retained, 2000, 2000, follow_symlinks=False)
        self.assertFalse(any(call.args[0] == outside for call in chown.call_args_list))

    def test_codex_fixture_rejects_missing_relative_or_exited_observations(self):
        cases = [
            ({"apply_patch": "/outside"}, True, "ACTUAL_CODEX_ABSOLUTE_SYMLINK_FIXTURE_MISSING"),
            ({"apply_patch": "../outside"}, True, "ACTUAL_CODEX_SYMLINK_METADATA_INVALID"),
            (None, False, "CODEX_NATIVE_EXITED_DURING_OBSERVATION"),
        ]
        for targets, running, code in cases:
            with self.subTest(code=code), tempfile.TemporaryDirectory(dir=ARTIFACTS) as directory:
                self.root = Path(directory)
                home, run_id, _, _, initialize, coding_lstat = self.codex_fixture(
                    targets, running=running)
                with patch.object(native, "initialize_codex_without_credentials", side_effect=initialize), \
                        patch.object(native.Path, "lstat", autospec=True, side_effect=coding_lstat), \
                        patch.object(native.os, "chown"), self.assertRaisesRegex(native.ProbeError, code):
                    native.retain_codex_arg0_links(self.root, home, run_id)
                self.assertEqual(list((self.root / "artifacts").iterdir()), [])

    def test_codex_fixture_refuses_credentials_before_launch(self):
        home, run_id, _, _, _, _ = self.codex_fixture()
        (home / ".codex/auth.json").write_text('{"synthetic":true}')
        with patch.object(native, "initialize_codex_without_credentials") as initialize, \
                self.assertRaisesRegex(native.ProbeError, "CODEX_FIXTURE_CREDENTIALS_PRESENT"):
            native.retain_codex_arg0_links(self.root, home, run_id)
        initialize.assert_not_called()

    def test_codex_stdio_observation_precedes_eof_and_uses_only_coding_environment(self):
        binary = MagicMock(spec=Path)
        binary.__str__.return_value = "/synthetic-pinned/vendor/bin/codex"
        binary.is_file.return_value = True
        binary.is_symlink.return_value = False
        binary.stat.return_value = Mock(st_uid=0, st_mode=0o755)
        process = Mock()
        process.stdin = io.StringIO()
        process.stdout = io.StringIO('{"id":1,"result":{}}\n')
        process.poll.side_effect = [None, None, 0]
        process.returncode = 0

        def observe(child):
            self.assertIs(child, process)
            self.assertFalse(child.stdin.closed)
            self.assertEqual(json.loads(child.stdin.getvalue())["method"], "initialize")

        with patch.object(native.Path, "glob", return_value=[binary]), \
                patch.object(native.subprocess, "Popen", return_value=process) as launch, \
                patch.object(native.select, "select", return_value=([process.stdout], [], [])):
            native.initialize_codex_without_credentials(self.root, self.root / "home", observe=observe)
        argv = launch.call_args.args[0]
        self.assertEqual(argv, ["setpriv", "--reuid=2000", "--regid=2000", "--clear-groups",
                               str(binary), "app-server", "--listen", "stdio://"])
        self.assertEqual(launch.call_args.kwargs["env"], {
            "PATH": "/usr/local/bin:/usr/bin:/bin:/usr/sbin:/sbin",
            "HOME": str(self.root / "home"), "CODEX_HOME": str(self.root / "home/.codex")})
        self.assertEqual(launch.call_args.kwargs["cwd"], self.root)
        self.assertTrue(process.stdin.closed)
        process.wait.assert_any_call(timeout=15)
        process.terminate.assert_not_called()

    def test_codex_observation_failure_stops_only_its_child_and_escalates_timeout(self):
        binary = MagicMock(spec=Path)
        binary.is_file.return_value = True
        binary.is_symlink.return_value = False
        binary.stat.return_value = Mock(st_uid=0, st_mode=0o755)
        process = Mock()
        process.stdin = io.StringIO()
        process.stdout = io.StringIO('{"id":1,"result":{}}\n')
        process.poll.return_value = None
        process.wait.side_effect = [subprocess.TimeoutExpired("codex", 5), 0]
        with patch.object(native.Path, "glob", return_value=[binary]), \
                patch.object(native.subprocess, "Popen", return_value=process), \
                patch.object(native.select, "select", return_value=([process.stdout], [], [])), \
                self.assertRaisesRegex(native.ProbeError, "OBSERVATION_FAILED"):
            native.initialize_codex_without_credentials(
                self.root, self.root / "home",
                observe=Mock(side_effect=native.ProbeError("OBSERVATION_FAILED")))
        process.terminate.assert_called_once()
        process.kill.assert_called_once()
        self.assertTrue(process.stdin.closed and process.stdout.closed)

    def archive(self, files, link=False):
        output = io.BytesIO()
        with tarfile.open(fileobj=output, mode="w") as archive:
            for name, value in files.items():
                member = tarfile.TarInfo(name)
                if link:
                    member.type = tarfile.SYMTYPE
                    member.linkname = "../outside"
                    archive.addfile(member)
                else:
                    member.size = len(value)
                    archive.addfile(member, io.BytesIO(value))
        output.seek(0)
        return output

    def test_restores_only_selected_nested_user_files_with_verified_hashes(self):
        files = {"config/settings.json": b'{"user":true}', "transcripts/session.txt": b"user data"}
        expected = {name: hashlib.sha256(value).hexdigest() for name, value in files.items()}
        result = native.restore_selected_files(self.archive(files), expected, self.root)
        self.assertTrue(result["allSha256Matched"])
        for name, value in files.items():
            self.assertEqual((self.root / name).read_bytes(), value)

    def test_rejects_archive_links_instead_of_following_them(self):
        expected = {"config/settings.json": hashlib.sha256(b"user data").hexdigest()}
        with self.assertRaisesRegex(native.ProbeError, "UNSAFE_RESTORE_MEMBER"):
            native.restore_selected_files(self.archive({"config/settings.json": b""}, link=True),
                                          expected, self.root)
        self.assertFalse((self.root / "config").exists())

    def test_rejects_wrong_hash_and_missing_selected_file(self):
        expected = {"config/settings.json": hashlib.sha256(b"expected data").hexdigest()}
        with self.assertRaisesRegex(native.ProbeError, "RESTORED_USER_FILE_HASH_MISMATCH"):
            native.restore_selected_files(self.archive({"config/settings.json": b"changed"}), expected, self.root)
        with self.assertRaisesRegex(native.ProbeError, "USER_BACKUP_MEMBER_MISSING"):
            native.restore_selected_files(self.archive({}), expected, self.root)


class InstalledManifestTests(unittest.TestCase):
    def setUp(self):
        ARTIFACTS.mkdir(parents=True, exist_ok=True)
        self.temp = tempfile.TemporaryDirectory(prefix="immutable-image-", dir=ARTIFACTS)
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name).resolve()
        self.runtime = self.root / "opt/workspaces-workspace"
        self.runtime.mkdir(parents=True)
        self.manifest = self.runtime / "image-manifest.json"
        self.lock = json.loads((HERE / "image/manifest.lock.json").read_text())
        self.lock["provenance"]["sourceRevision"] = "a" * 40
        self.owners = {}
        original_lstat = Path.lstat

        def root_owned(path, *args, **kwargs):
            info = list(original_lstat(path, *args, **kwargs))
            info[4] = self.owners.get(path, 0)
            return os.stat_result(info)

        ownership = patch.object(builder.Path, "lstat", autospec=True, side_effect=root_owned)
        ownership.start()
        self.addCleanup(ownership.stop)
        # Only ownership is synthetic; copying, hashing, modes and JSON publication use real files.
        with patch.object(builder.os, "chown"):
            for name in builder.RUNTIME_FILES:
                shutil.copy2(HERE / name, self.runtime / name)
                (self.runtime / name).chmod(0o755)
            builder.install_storage_tiers(HERE / "storage", "/opt/workspaces-workspace", self.root)
            units = self.root / "etc/systemd/system"
            units.mkdir(parents=True)
            builder.install_systemd_units(HERE / "systemd", units)
        tools = self.root / "opt/workspaces-tools"
        tools.mkdir()
        for name in ("package.json", "package-lock.json"):
            shutil.copy2(HERE / "image/tools" / name, tools / name)
            (tools / name).chmod(0o644)
        for target in (*builder.PINNED_EXECUTABLE_FILES,
                       "/opt/workspaces-tools/" + self.lock["claude"]["executable"],
                       "/opt/workspaces-t3/" + self.lock["t3"]["resourceMonitor"]["path"],
                       "/opt/workspaces-t3/release/t3"):
            path = self.root / target.lstrip("/")
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(("synthetic installed bytes: " + target).encode())
            path.chmod(0o755)
        (self.root / "usr/local/bin/t3").symlink_to("../../../opt/workspaces-t3/release/t3")

    def write_manifest(self):
        builder.write_image_manifest(self.lock, HERE, self.root)
        return json.loads(self.manifest.read_text())

    def test_manifest_records_actual_installed_bytes_and_canonical_t3_target(self):
        unrelated = self.runtime / "unselected-user-data"
        unrelated.write_bytes(b"not an immutable input")
        manifest = self.write_manifest()
        files = manifest["provenance"]["installedImmutableInputs"]["files"]
        self.assertTrue(files)
        self.assertEqual(manifest["provenance"]["sourceRevision"], "a" * 40)
        for name, digest in files.items():
            path = Path(name)
            self.assertTrue(path.is_absolute() and path.resolve() == path and path.is_file())
            self.assertFalse(path.is_symlink() or path.stat().st_mode & 0o022)
            self.assertEqual(digest, hashlib.sha256(path.read_bytes()).hexdigest())
        for name in ("runtime.py", "storage_tiers.py", "scratch_mounts.py", "repo_checkpoint.py", "t3-connect.js",
                     "scratch.env"):
            self.assertIn(str(self.runtime / name), files)
        for name in native.UNIT_HASHES:
            self.assertIn(str(self.root / "etc/systemd/system" / name), files)
        for name in ("etc/tmpfiles.d/workspaces-scratch.conf",
                     "etc/profile.d/workspaces-scratch.sh",
                     "opt/workspaces-tools/package-lock.json", "opt/workspaces-node/bin/node",
                     "opt/workspaces-t3/release/t3"):
            self.assertIn(str(self.root / name), files)
        self.assertNotIn(str(self.root / "usr/local/bin/t3"), files)
        self.assertNotIn(str(unrelated), files)
        self.assertNotIn(str(self.manifest), files)
        executable = self.root / "usr/local/bin/gh"
        executable.write_bytes(b"changed synthetic executable bytes")
        updated = self.write_manifest()["provenance"]["installedImmutableInputs"]["files"]
        self.assertNotEqual(files[str(executable)], updated[str(executable)])
        self.assertEqual(updated[str(executable)], hashlib.sha256(executable.read_bytes()).hexdigest())

    def test_source_copy_mismatches_block_without_publishing_manifest(self):
        for name in ("opt/workspaces-workspace/runtime.py",
                     "opt/workspaces-workspace/scratch_mounts.py",
                     "opt/workspaces-workspace/t3-connect.js",
                     "opt/workspaces-workspace/scratch.env",
                     "etc/tmpfiles.d/workspaces-scratch.conf",
                     "etc/profile.d/workspaces-scratch.sh",
                     "etc/systemd/system/workspaces-t3.service",
                     "opt/workspaces-tools/package-lock.json"):
            with self.subTest(name=name):
                path = self.root / name
                original = path.read_bytes()
                path.write_bytes(original + b"\nchanged installed bytes\n")
                with self.assertRaisesRegex(builder.Blocked, "differs from source"):
                    self.write_manifest()
                self.assertFalse(self.manifest.exists())
                path.write_bytes(original)

    def test_unsafe_or_missing_inputs_do_not_replace_an_existing_manifest(self):
        self.manifest.write_bytes(b"existing manifest sentinel")
        path = self.root / "usr/local/bin/gh"
        for mode, uid in ((0o775, 0), (0o757, 0), (0o755, 2000)):
            with self.subTest(mode=mode, uid=uid):
                path.chmod(mode)
                self.owners[path] = uid
                with self.assertRaisesRegex(builder.Blocked, "Unsafe installed immutable input"):
                    self.write_manifest()
                self.assertEqual(self.manifest.read_bytes(), b"existing manifest sentinel")
        self.owners[path] = 0
        path.unlink()
        with self.assertRaises(FileNotFoundError):
            self.write_manifest()
        path.symlink_to(self.root / "usr/local/bin/glab")
        with self.assertRaisesRegex(builder.Blocked, "Unsafe installed immutable input"):
            self.write_manifest()
        self.assertEqual(self.manifest.read_bytes(), b"existing manifest sentinel")

    def test_parent_aliases_and_t3_targets_outside_its_installation_are_rejected(self):
        units = self.root / "etc/systemd/system"
        units.rename(units.with_name("real-system"))
        units.symlink_to("real-system", target_is_directory=True)
        with self.assertRaisesRegex(builder.Blocked, "Unsafe installed immutable input"):
            self.write_manifest()
        units.unlink()
        units.with_name("real-system").rename(units)
        t3 = self.root / "usr/local/bin/t3"
        t3.unlink()
        t3.symlink_to("gh")
        with self.assertRaisesRegex(builder.Blocked, "Unexpected installed T3 executable"):
            self.write_manifest()
        self.assertFalse(self.manifest.exists())


class SourceControlInstallTests(unittest.TestCase):
    def setUp(self):
        ARTIFACTS.mkdir(parents=True, exist_ok=True)
        self.temp = tempfile.TemporaryDirectory(prefix="source-control-image-", dir=ARTIFACTS)
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.bin = self.root / "bin"
        self.bin.mkdir()
        self.lock = json.loads((HERE / "image/manifest.lock.json").read_text())

    def download(self, item, target, *, kind="file"):
        with tarfile.open(target, "w:gz") as archive:
            member = tarfile.TarInfo(item["executable"] if kind != "missing" else "unexpected")
            if kind == "link":
                member.type = tarfile.SYMTYPE
                member.linkname = "/outside"
                archive.addfile(member)
            else:
                member.size = 6
                archive.addfile(member, io.BytesIO(b"binary"))
            # Unselected members must never be extracted.
            outside = tarfile.TarInfo("../../outside")
            outside.size = 6
            archive.addfile(outside, io.BytesIO(b"unsafe"))

    def install(self, download=None, versions=None):
        with patch.object(builder, "Path", return_value=self.bin), \
                patch.object(builder, "download", side_effect=download or self.download), \
                patch.object(builder.os, "chown"), \
                patch.object(builder, "run", side_effect=versions or [
                    "gh version " + self.lock["gh"]["version"],
                    "glab " + self.lock["glab"]["version"]]):
            builder.install_source_control_clis(self.lock, self.root)

    def test_installs_both_pinned_binaries_without_extracting_other_members(self):
        self.install()
        self.assertEqual((self.bin / "gh").read_bytes(), b"binary")
        self.assertEqual((self.bin / "glab").read_bytes(), b"binary")
        self.assertEqual(set(path.name for path in self.bin.iterdir()), {"gh", "glab"})
        self.assertFalse((self.root.parent / "outside").exists())

    def test_rejects_missing_or_linked_executable(self):
        for kind in ("missing", "link"):
            with self.subTest(kind=kind), self.assertRaisesRegex(builder.Blocked, "archive layout"):
                self.install(download=lambda item, target: self.download(item, target, kind=kind))
        self.assertEqual(list(self.bin.iterdir()), [])

    def test_rejects_wrong_installed_version(self):
        with self.assertRaisesRegex(builder.Blocked, "Wrong source-control CLI version"):
            self.install(versions=["gh version 2.83.10"])

    def test_checksum_mismatch_blocks_before_any_executable_is_installed(self):
        with patch.object(builder.urllib.request, "urlopen", return_value=io.BytesIO(b"not-the-release")):
            with self.assertRaisesRegex(builder.Blocked, "checksum mismatch"):
                builder.download(self.lock["gh"], self.root / "download")
        self.assertEqual(list(self.bin.iterdir()), [])


class AgentInstallTests(unittest.TestCase):
    def setUp(self):
        ARTIFACTS.mkdir(parents=True, exist_ok=True)
        self.temp = tempfile.TemporaryDirectory(prefix="agent-image-", dir=ARTIFACTS)
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.lock = json.loads((HERE / "image/manifest.lock.json").read_text())
        chown = patch.object(builder.os, "chown")
        chown.start()
        self.addCleanup(chown.stop)

    def test_agent_pins_are_consistent_across_lock_npm_lock_and_api_agent_list(self):
        package = json.loads((HERE / "image/tools/package.json").read_text())
        npm_lock = json.loads((HERE / "image/tools/package-lock.json").read_text())
        claude = npm_lock["packages"]["node_modules/@anthropic-ai/claude-code-linux-x64"]
        self.assertEqual(package["dependencies"]["@anthropic-ai/claude-code-linux-x64"], self.lock["claude"]["version"])
        self.assertEqual((claude["version"], claude["integrity"]),
                         (self.lock["claude"]["version"], self.lock["claude"]["integrity"]))
        self.assertEqual({agent["id"]: agent["version"] for agent in self.lock["agents"]},
                         {name: self.lock[name]["version"]
                          for name in ("codex", "copilot", "claude", "antigravity", "muse")})
        for name in ("antigravity", "muse"):
            self.assertRegex(self.lock[name]["sha256"], "^[0-9a-f]{64}$")
            self.assertTrue(self.lock[name]["url"].startswith("https://"))

    def test_claude_wrapper_disables_self_update_and_requires_the_pinned_version(self):
        binary = self.root / self.lock["claude"]["executable"]
        binary.parent.mkdir(parents=True)
        binary.write_bytes(b"binary")
        wrapper = self.root / "claude"
        with patch.object(builder, "run", return_value="2.1.328 (Claude Code)") as run:
            builder.install_claude(self.lock, self.root, wrapper)
        self.assertEqual(run.call_args.args[0], [str(wrapper), "--version"])
        self.assertIn("DISABLE_AUTOUPDATER=1", wrapper.read_text())
        self.assertIn("exec " + str(binary) + ' "$@"', wrapper.read_text())
        with patch.object(builder, "run", return_value="2.1.3289 (Claude Code)"), \
                self.assertRaisesRegex(builder.Blocked, "Wrong Claude Code version"):
            builder.install_claude(self.lock, self.root, wrapper)
        binary.unlink()
        with self.assertRaisesRegex(builder.Blocked, "package layout"):
            builder.install_claude(self.lock, self.root, wrapper)

    def antigravity(self, members, size_matches=True):
        lock = json.loads(json.dumps(self.lock))
        lock["antigravity"]["members"] = {"agy_acp_server.par": 3, "localharness_external": 4}

        def download(item, target):
            with zipfile.ZipFile(target, "w") as archive:
                for name, data in members.items():
                    archive.writestr(name, data)
            item["size"] = target.stat().st_size if size_matches else 1
        destination = self.root / "antigravity"
        with patch.object(builder, "download", side_effect=download):
            builder.install_antigravity(lock, self.root, destination)
        return destination

    def test_antigravity_copies_only_pinned_members(self):
        destination = self.antigravity({"agy_acp_server.par": b"par", "localharness_external": b"ext!",
                                        "../outside": b"unsafe"})
        self.assertEqual(sorted(path.name for path in destination.iterdir()),
                         ["agy_acp_server.par", "localharness_external"])
        self.assertEqual((destination / "agy_acp_server.par").read_bytes(), b"par")
        self.assertFalse((self.root.parent / "outside").exists())
        self.assertFalse((self.root / "antigravity.zip").exists())

    def test_antigravity_rejects_missing_resized_members_or_archive_size(self):
        for members, size_matches, message in (
                ({"agy_acp_server.par": b"par"}, True, "archive layout"),
                ({"agy_acp_server.par": b"parx", "localharness_external": b"ext!"}, True, "archive layout"),
                ({"agy_acp_server.par": b"par", "localharness_external": b"ext!"}, False, "size mismatch")):
            with self.subTest(message=message, members=sorted(members)):
                shutil.rmtree(self.root / "antigravity", ignore_errors=True)
                with self.assertRaisesRegex(builder.Blocked, message):
                    self.antigravity(members, size_matches)

    def test_muse_is_checksum_and_size_verified_and_wrapped_without_self_update(self):
        lock = json.loads(json.dumps(self.lock))
        lock["muse"].update(sha256=hashlib.sha256(b"muse").hexdigest(), size=4)
        destination, wrapper = self.root / "muse", self.root / "muse-wrapper"
        with patch.object(builder.urllib.request, "urlopen", return_value=io.BytesIO(b"muse")):
            builder.install_muse(lock, self.root, destination, wrapper)
        self.assertEqual((destination / "muse").read_bytes(), b"muse")
        self.assertIn("MUSE_NO_AUTO_UPDATE=1", wrapper.read_text())
        lock["muse"]["size"] = 5
        shutil.rmtree(destination)
        with patch.object(builder.urllib.request, "urlopen", return_value=io.BytesIO(b"muse")), \
                self.assertRaisesRegex(builder.Blocked, "size mismatch"):
            builder.install_muse(lock, self.root, destination, wrapper)

    def test_agent_install_directories_are_root_owned_and_traversable_under_restrictive_umask(self):
        lock = json.loads(json.dumps(self.lock))
        lock["muse"].update(sha256=hashlib.sha256(b"muse").hexdigest(), size=4)
        muse, wrapper = self.root / "muse", self.root / "muse-wrapper"
        previous = os.umask(0o077)
        try:
            antigravity = self.antigravity({"agy_acp_server.par": b"par", "localharness_external": b"ext!"})
            with patch.object(builder.urllib.request, "urlopen", return_value=io.BytesIO(b"muse")):
                builder.install_muse(lock, self.root, muse, wrapper)
        finally:
            os.umask(previous)
        for path in (antigravity, antigravity / "agy_acp_server.par", antigravity / "localharness_external",
                     muse, muse / "muse", wrapper):
            with self.subTest(path=path.name):
                self.assertEqual(path.stat().st_mode & 0o777, 0o755)
                builder.os.chown.assert_any_call(path, 0, 0)
