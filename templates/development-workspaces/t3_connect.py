#!/usr/bin/env python3
"""Loopback-only Connect enrollment. CLI approval output is memory-only, never terminal history."""
import argparse
from contextlib import contextmanager
import fcntl
import hashlib
import hmac
import http.server
import json
import os
from pathlib import Path
import re
import signal
import secrets
import stat
import subprocess
import threading
import time
import urllib.request

from runtime import (Blocked, Git, Linux, Runtime, T3_RELAY_CLIENT, atomic_json, installed_t3, load_config,
                     require, run, t3_descriptor_matches)

CONFIG = "/etc/workspaces-workspace/runtime.json"
STATE = "/var/lib/workspaces-workspace"
BASE = "/workspace/t3"
MANAGEMENT_UNIT = "workspaces-t3-connect.service"
MANAGEMENT_SCRIPT = "/opt/workspaces-workspace/t3_connect.py"
ACTIVATION_TIMEOUT = 240
ACTIVATION_FILE = "connect-activation.json"
IN_FLIGHT = ("pending", "restarting")
TRACE_FILE = Path(BASE) / "userdata/logs/server.trace.ndjson"
# Match the trace retention defaults in the pinned T3 CLI.
TRACE_MAX_BYTES = 10 * 1024 * 1024
TRACE_ROTATIONS = 10


def command(action):
    require(action in ("link", "status", "logout"), "Unsupported Connect operation")
    return ["runuser", "-u", "coding", "--", "env", "-i", "HOME=/workspace/home",
            "CODEX_HOME=/workspace/home/.codex", "PATH=/usr/local/bin:/usr/bin:/bin",
            "T3CODE_CLOUDFLARED_PATH=" + T3_RELAY_CLIENT,
            "t3", "connect", action, "--base-dir", BASE,
            *(["--headless"] if action == "link" else ["--json"] if action == "status" else [])]


def require_root_reserved_port():
    require(int(Path("/proc/sys/net/ipv4/ip_unprivileged_port_start").read_text()) > 774,
            "Root-reserved enrollment port required; refuse credential interception by coding processes")


def admitted(config, state_dir=STATE):
    require(config["kind"] == "primary" and config["workspace"] == "/workspace",
            "Connect is available only on the managed primary volume")
    require((Path(state_dir) / "state.json").is_file(), "Enrolled runtime required")
    runtime = Runtime(config, state_dir, git=Git())
    require(runtime.s["phase"] == "Ready", "Workspace admissions are fenced")
    return runtime


def require_trusted_listener(proc_root=Path("/proc")):
    properties = dict(line.split("=", 1) for line in run([
        "systemctl", "show", MANAGEMENT_UNIT, "-p", "ActiveState", "-p", "MainPID",
        "-p", "FragmentPath", "-p", "DropInPaths", "-p", "User", "-p", "ControlGroup"
    ]).splitlines() if "=" in line)
    unit_file = "/etc/systemd/system/" + MANAGEMENT_UNIT
    require(properties.get("ActiveState") == "active" and properties.get("User") == "root" and
            properties.get("FragmentPath") == unit_file and properties.get("DropInPaths") == "" and
            properties.get("ControlGroup") == "/system.slice/" + MANAGEMENT_UNIT,
            "Untrusted Connect management unit")
    pid = properties.get("MainPID", "")
    require(pid.isdecimal() and int(pid) > 0, "Connect management listener is missing")
    for path in (unit_file, MANAGEMENT_SCRIPT, "/opt/workspaces-workspace/runtime.py"):
        Linux.verify_unit_file(path)
    process = proc_root / pid
    status = dict(line.split(":", 1) for line in (process / "status").read_text().splitlines() if ":" in line)
    require(status.get("Uid", "").split() == ["0"] * 4 and
            (process / "cgroup").read_text().strip() == "0::/system.slice/" + MANAGEMENT_UNIT and
            (process / "cmdline").read_bytes() ==
            b"/usr/bin/python3\0-B\0" + os.fsencode(MANAGEMENT_SCRIPT) + b"\0serve\0",
            "Connect management process is not the root-owned helper")
    executable = process / "exe"
    Linux.verify_unit_file(str(executable.resolve(strict=True)))
    sockets = set()
    for descriptor in (process / "fd").iterdir():
        try:
            target = os.readlink(descriptor)
        except FileNotFoundError:
            continue
        if re.fullmatch(r"socket:\[[0-9]+\]", target):
            sockets.add(target[8:-1])
    listeners = []
    for table in ("tcp", "tcp6"):
        for line in (proc_root / "net" / table).read_text().splitlines()[1:]:
            fields = line.split()
            require(len(fields) >= 10, "Unrecognized kernel socket table")
            if fields[3] == "0A" and int(fields[1].split(":")[1], 16) == 774:
                listeners.append((table, fields[1], fields[7], fields[9]))
    require(len(listeners) == 1 and listeners[0][:3] == ("tcp", "0100007F:0306", "0") and
            listeners[0][3] in sockets, "Connect listener is not owned by the root management helper")
    require(run(["systemctl", "show", MANAGEMENT_UNIT, "-p", "MainPID", "--value"]) == pid,
            "Connect management process changed during admission")


def trusted_admission(config, state_dir=STATE):
    runtime = admitted(config, state_dir)
    require_root_reserved_port()
    require_trusted_listener()
    require_root_reserved_port()
    return runtime


def admission_proof(config, state_dir=STATE):
    with (Path(state_dir) / "control.lock").open("a") as lock:
        fcntl.flock(lock, fcntl.LOCK_EX)
        try:
            trusted_admission(config, state_dir)
            accepted = True
        except Exception:
            accepted = False
        return dict(type="ConnectManagementAdmission", admitted=accepted,
                    vmId=config["vmId"], generation=config["generation"])


def activation_status(config, state_dir=STATE):
    path = Path(state_dir) / ACTIVATION_FILE
    if not path.exists():
        return None
    value = json.loads(path.read_text())
    if any(value.get(key) != config[key] for key in ("vmId", "generation")):
        return None
    if value["state"] in IN_FLIGHT and time.time() >= value["deadline"]:
        value.update(state="failed", reason="interrupted")
        atomic_json(path, value)
    return value


def save_activation(config, state_dir, operation, state, reason=None, diagnostics=None):
    value = activation_status(config, state_dir)
    require(value is not None and value["id"] == operation, "Activation operation changed")
    value.update(state=state, reason=reason, diagnostics=diagnostics or {})
    atomic_json(Path(state_dir) / ACTIVATION_FILE, value)
    return value


def activation_diagnostics(value):
    require(isinstance(value, dict), "Invalid Connect status")
    relay = value.get("relayClient")
    relay = relay if isinstance(relay, dict) else {}
    reachability = value.get("reachability")
    reachability = reachability if isinstance(reachability, dict) else {}
    return {
        **public_status(value),
        "authenticated": value.get("authenticated") is True,
        "relayClientStatus": relay.get("status") if relay.get("status") in
            ("available", "missing", "unsupported") else "unknown",
        "relayClientSource": relay.get("source") if relay.get("source") in
            ("override", "managed", "path") else "unknown",
        "relayClientPinned": relay.get("executablePath") == T3_RELAY_CLIENT,
        "relayClientExpectedPath": T3_RELAY_CLIENT,
        "reachability": reachability.get("status") if reachability.get("status") in
            ("reachable", "unreachable", "checking") else "unknown",
        "reachabilityCause": reachability.get("cause") if reachability.get("cause") in
            ("not_linked", "connector_not_running", "connector_not_ready", "edge_unreachable",
             "origin_unreachable", "no_connector", "unexpected_response", "dns_failed",
             "network_error", "timeout") else "unknown",
    }


def trace_position():
    try:
        info = TRACE_FILE.stat()
        return info.st_ino, info.st_size
    except OSError:
        return None


def trace_record_is_current(line, started_ns):
    try:
        record = json.loads(line)
    except (ValueError, RecursionError):
        return False
    timestamp = record.get("startTimeUnixNano") if isinstance(record, dict) else None
    return (isinstance(timestamp, str) and len(timestamp) <= 20 and timestamp.isascii()
            and timestamp.isdecimal() and int(timestamp) >= started_ns)


def relay_start_diagnostics(started_at, position):
    # Never copy log lines, exception messages, process arguments or environment values.
    events = {
        "Failed to reconcile T3 Connect desired link on startup": "link_reconcile_failed",
        "T3 Connect relay is still unavailable": "relay_retrying",
        "T3 Connect desired link reconciled on startup": "link_reconciled",
        "Failed to start relay client": "relay_spawn_failed",
        "Relay client exited; restarting": "relay_exited",
        "Relay client process started; waiting for tunnel connection": "relay_started",
        "Failed to read the desired T3 Connect link": "desired_state_read_failed",
        "Failed to start the confirmed T3 Connect tunnel": "stored_tunnel_start_failed",
        "Unlink an unused environment in T3 Connect": "account_link_limit",
        "Check this machine's date and time, update T3 Code": "link_proof_rejected",
        "If the stored credential was revoked": "account_authorization_rejected",
    }
    errors = ("CloudLinkAuthorizationMissingError", "CloudLinkEndpointUnavailableError",
              "CloudLinkInternalError", "CloudLinkOriginInvalidError", "RelayRequestError",
              "SecretStoreReadError", "SecretStorePersistError")
    markers = (*events, *errors, "T3 Connect relay returned HTTP")
    started_ns = int(started_at * 1_000_000_000)
    result = {"trace": "unavailable"}
    found_events, found_errors, exit_codes, http_statuses = set(), set(), set(), set()
    for index in range(TRACE_ROTATIONS + 1):
        path = TRACE_FILE.with_name(f"{TRACE_FILE.name}.{index}") if index else TRACE_FILE
        try:
            with os.fdopen(os.open(path, os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK), "rb") as stream:
                info = os.fstat(stream.fileno())
                require(stat.S_ISREG(info.st_mode), "Regular trace file required")
                if info.st_mtime < started_at:
                    if result["trace"] == "unavailable":
                        result["trace"] = "no_current_trace"
                    continue
                offset = position[1] if position and position[0] == info.st_ino and position[1] <= info.st_size else 0
                offset = max(offset, info.st_size - TRACE_MAX_BYTES)
                stream.seek(offset)
                text = stream.read(info.st_size - offset).decode("utf-8", errors="replace")
        except (OSError, Blocked):
            continue
        result["trace"] = "read"
        for line in text.splitlines():
            if not any(marker in line for marker in markers) or not trace_record_is_current(line, started_ns):
                continue
            found_events.update(code for phrase, code in events.items() if phrase in line)
            found_errors.update(code for code in errors if code in line)
            if "Relay client exited; restarting" in line:
                exit_codes.update(int(code) for code in
                                  re.findall(r'["\\]*exitCode["\\]*\s*:\s*(-?[0-9]{1,3})\b', line))
            http_statuses.update(int(code) for code in
                                 re.findall(r"T3 Connect relay returned HTTP ([45][0-9]{2})\b", line))
    if result["trace"] != "unavailable":
        result.update(events=sorted(found_events), errors=sorted(found_errors),
                      relayExitCodes=sorted(exit_codes), relayHttpStatuses=sorted(http_statuses))
    try:
        properties = run(["systemctl", "show", "workspaces-t3.service",
                          "-p", "ExecMainStatus", "-p", "ExecMainCode"], timeout=5)
        for line in properties.splitlines():
            key, _, value = line.partition("=")
            if key in ("ExecMainStatus", "ExecMainCode") and re.fullmatch(r"[0-9]{1,3}", value):
                result[key] = int(value)
    except Blocked:
        result["serviceStatus"] = "unavailable"
    result["relayProcessRunning"] = False
    try:
        for process in Path("/proc").iterdir():
            if not process.name.isdecimal():
                continue
            try:
                if os.readlink(process / "exe") == T3_RELAY_CLIENT and \
                        (process / "cgroup").read_text().strip() == \
                        "0::/workspaceswork.slice/workspaces-t3.service":
                    result["relayProcessRunning"] = True
                    break
            except OSError:
                continue
    except OSError:
        result["processInventory"] = "unavailable"
    return result


def activate(config, state_dir=STATE):
    # This independent oneshot survives the management page's own PartOf restart.
    with (Path(state_dir) / "control.lock").open("a") as lock:
        fcntl.flock(lock, fcntl.LOCK_EX)
        operation = activation_status(config, state_dir)
        try:
            admitted(config, state_dir)
        except Blocked:
            if operation and operation["state"] in IN_FLIGHT:
                save_activation(config, state_dir, operation["id"], "failed", "workspace_fenced")
                return
            raise
        require(operation is not None, "Queued activation required")
        if operation["state"] != "pending":
            return
        save_activation(config, state_dir, operation["id"], "restarting")
    reason = "activation_failed"
    diagnostics = {}
    position = trace_position()
    try:
        reason = "t3_inactive"
        require(run(["systemctl", "is-active", "workspaces-t3.service"], timeout=5) == "active",
                "Activation never starts stopped compute or an inactive T3 service")
        reason = "status_unavailable"
        status = json.loads(run(command("status"), timeout=40, cwd="/workspace"))
        diagnostics.update(activation_diagnostics(status))
        reason = "approval_required"
        require(status.get("desired") is True and status.get("authenticated") is True,
                "Finish T3 account approval before activation")
        reason = "relay_not_pinned"
        require(diagnostics["relayClientPinned"] and diagnostics["relayClientStatus"] == "available",
                "Pinned relay client must resolve before restarting")
        reason = "interrupted"
        require(time.time() < operation["deadline"] - 100, "Activation start was delayed; retry explicitly")
        reason = "restart_failed"
        run(["systemctl", "restart", "workspaces-t3.service"], timeout=90)
        until = min(time.time() + 90, operation["deadline"] - 30)
        linked = False
        while time.time() < until:
            reason = "status_unavailable"
            try:
                status = json.loads(run(command("status"), timeout=20, cwd="/workspace"))
                diagnostics.update(activation_diagnostics(status))
                reason = "link_not_provisioned" if not diagnostics["linked"] else "relay_unavailable"
                if diagnostics["revoked"]:
                    reason = "link_revoked"
                    break
                if diagnostics["linked"] and diagnostics["reachable"]:
                    linked = True
                    break
            except (Blocked, ValueError, TypeError):
                diagnostics["statusQuery"] = "unavailable"
            time.sleep(3)
        diagnostics.update(relay_start_diagnostics(operation["startedAt"], position))
        state = "linked" if linked else "failed"
    except (Blocked, OSError, subprocess.SubprocessError, ValueError, TypeError) as error:
        cause = error.__cause__ if isinstance(error, Blocked) else error
        if isinstance(cause, subprocess.CalledProcessError):
            diagnostics["commandExitCode"] = cause.returncode
        if isinstance(cause, subprocess.TimeoutExpired):
            diagnostics["commandTimedOut"] = True
        diagnostics.update(relay_start_diagnostics(operation["startedAt"], position))
        state = "failed"
    with (Path(state_dir) / "control.lock").open("a") as lock:
        fcntl.flock(lock, fcntl.LOCK_EX)
        save_activation(config, state_dir, operation["id"], state,
                        None if state == "linked" else reason, diagnostics)


def credential(config, state_dir=STATE):
    with (Path(state_dir) / "control.lock").open("a") as lock:
        fcntl.flock(lock, fcntl.LOCK_EX)
        runtime = trusted_admission(config, state_dir)
        path = Path(state_dir) / "connect-credential.json"
        if path.exists():
            saved = json.loads(path.read_text())
            require(saved["identity"] == runtime.identity(), "Stale Connect credential")
            return saved["token"]
        token = secrets.token_urlsafe(48)
        atomic_json(path, dict(identity=runtime.identity(), token=token))
        return token


def public_status(value):
    require(isinstance(value, dict), "Invalid Connect status")
    reachability = value.get("reachability")
    return {
        "linked": value.get("linked") is True,
        "desired": value.get("desired") is True,
        "revoked": value.get("revoked") is True,
        "reachable": isinstance(reachability, dict) and reachability.get("status") == "reachable",
    }


class Enrollment:
    def __init__(self, config, state_dir=STATE):
        self.config, self.state_dir = config, state_dir
        self.lock = threading.RLock()
        self.process = None
        self.prompt = None
        self.result = "idle"
        self.remote_removal_confirmed = None

    @contextmanager
    def guard(self):
        with (Path(self.state_dir) / "control.lock").open("a") as lock:
            fcntl.flock(lock, fcntl.LOCK_EX)
            admitted(self.config, self.state_dir)
            yield

    def clear_pending(self):
        with (Path(self.state_dir) / "control.lock").open("a") as lock:
            fcntl.flock(lock, fcntl.LOCK_EX)
            (Path(self.state_dir) / "connect-operation.json").unlink(missing_ok=True)

    def status(self):
        with self.lock, self.guard():
            identity = {key: self.config[key] for key in ("vmId", "generation")}
            activation = activation_status(self.config, self.state_dir)
            if activation and activation["state"] in IN_FLIGHT:
                return dict(operation="idle", activation=activation, **identity)
            if self.process is not None:
                return dict(operation=self.result, approval=self.prompt, **identity)
            try:
                result = subprocess.run(command("status"), capture_output=True, text=True,
                                        timeout=40, cwd="/workspace")
                require(result.returncode == 0, "Connect status unavailable")
                value = public_status(json.loads(result.stdout))
                with urllib.request.urlopen("http://127.0.0.1:3773/.well-known/t3/environment", timeout=3) as response:
                    raw = response.read(65537)
                require(len(raw) <= 65536, "Oversized environment descriptor")
                descriptor = json.loads(raw)
                require(t3_descriptor_matches(descriptor, installed_t3()), "Unqualified environment descriptor")
                environment_id = descriptor.get("environmentId")
                require(isinstance(environment_id, str) and
                        re.fullmatch(r"[a-zA-Z0-9._:-]{1,128}", environment_id),
                        "Unqualified environment descriptor")
            except (Blocked, OSError, subprocess.SubprocessError, ValueError):
                if activation:
                    return dict(operation=self.result, activation=activation,
                                statusAvailable=False, **identity)
                raise
            if activation and activation["state"] == "failed" and value["linked"] and value["reachable"]:
                activation = save_activation(self.config, self.state_dir, activation["id"], "linked",
                                             diagnostics=activation["diagnostics"])
            return dict(operation=self.result, environmentId=environment_id, activation=activation, **identity,
                        remoteRemovalConfirmed=self.remote_removal_confirmed,
                        **value)

    def require_no_activation(self):
        activation = activation_status(self.config, self.state_dir)
        require(not activation or activation["state"] not in IN_FLIGHT, "Activation is already pending")

    def request_activation(self):
        with self.lock, self.guard():
            require(self.process is None, "Finish account approval first")
            activation = activation_status(self.config, self.state_dir)
            if activation and activation["state"] in IN_FLIGHT:
                return dict(operation="idle", activation=activation)
            now = time.time()
            activation = dict(id=secrets.token_hex(16), state="pending", startedAt=now,
                              deadline=now + ACTIVATION_TIMEOUT, reason=None, diagnostics={},
                              **{key: self.config[key] for key in ("vmId", "generation")})
            atomic_json(Path(self.state_dir) / ACTIVATION_FILE, activation)
            try:
                # Restart the helper so a retry cannot be lost while its previous invocation exits.
                run(["systemctl", "restart", "--no-block", "workspaces-t3-connect-activate.service"], timeout=10)
            except Blocked:
                activation = save_activation(self.config, self.state_dir, activation["id"],
                                             "failed", "queue_failed")
            return dict(operation="idle", activation=activation)

    def start(self):
        with self.lock, self.guard():
            self.require_no_activation()
            require(self.process is None, "A Connect operation is already pending")
            relay = Path(T3_RELAY_CLIENT)
            require(relay.is_file() and not relay.is_symlink() and relay.stat().st_uid == 0 and
                    not relay.stat().st_mode & 0o022, "Pinned root-owned relay client required")
            with relay.open("rb") as binary:
                require(hashlib.file_digest(binary, "sha256").hexdigest() == installed_t3()["relayClient"]["sha256"],
                        "Pinned relay client checksum mismatch")
            self.remote_removal_confirmed = None
            self.prompt, self.result = None, "awaiting_approval"
            marker = Path(self.state_dir) / "connect-operation.json"
            atomic_json(marker, {key: self.config[key] for key in ("vmId", "generation")})
            try:
                self.process = subprocess.Popen(command("link"), stdin=subprocess.DEVNULL,
                                                stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                                                cwd="/workspace", start_new_session=True)
            except Exception:
                marker.unlink(missing_ok=True)
                raise
            threading.Thread(target=self.collect, args=(self.process,), daemon=True).start()
            (Path(self.state_dir) / ACTIVATION_FILE).unlink(missing_ok=True)
            timer = threading.Timer(900, self.expire, args=(self.process,))
            timer.daemon = True
            timer.start()

    def collect(self, process):
        url = None
        size = 0
        result = "approval_failed"
        timed_out = False
        try:
            while line := process.stdout.readline(4097):
                size += len(line)
                require(size <= 32768 and len(line) <= 4096, "Unexpected CLI output")
                text = line.decode("utf-8", errors="replace").strip()
                if text.endswith("Timed out waiting for T3 Connect authorization."):
                    timed_out = True
                if re.fullmatch(r"https://[a-zA-Z0-9.-]+/[^\s<>\"']*", text):
                    url = text
                match = re.fullmatch(r"Code: ([A-Za-z0-9-]{4,32})", text)
                if match and url:
                    with self.lock:
                        if self.process is process and self.result == "awaiting_approval":
                            self.prompt = dict(url=url, code=match[1])
            if process.wait() == 0:
                result = "approval_saved_activate_required"
            elif timed_out:
                result = "approval_expired"
        except Exception:
            self.terminate(process)
        finally:
            process.stdout.close()
            with self.lock:
                if self.process is process:
                    if self.result == "awaiting_approval":
                        self.result = result
                    self.process, self.prompt = None, None
                    self.clear_pending()

    @staticmethod
    def terminate(process):
        if process.poll() is None:
            os.killpg(process.pid, signal.SIGTERM)
            try:
                process.wait(timeout=5)
            except subprocess.TimeoutExpired:
                os.killpg(process.pid, signal.SIGKILL)
                process.wait(timeout=5)

    def expire(self, process):
        with self.lock:
            if self.process is process:
                self.terminate(process)
                self.prompt = None
                self.result = "approval_expired"

    def unlink(self):
        with self.lock, self.guard():
            self.require_no_activation()
            if self.process is not None:
                process, self.process = self.process, None
                self.terminate(process)
            self.prompt = None
            result = subprocess.run(command("logout"), capture_output=True, text=True,
                                    timeout=45, cwd="/workspace")
            (Path(self.state_dir) / "connect-operation.json").unlink(missing_ok=True)
            self.result = "unlinked" if result.returncode == 0 else "unlink_not_confirmed"
            # Upstream reports remote DELETE failures as warnings, sometimes with exit code zero.
            remote_confirmed = result.returncode == 0 and "Revoked the relay-side environment record." in result.stdout
            self.remote_removal_confirmed = remote_confirmed
            (Path(self.state_dir) / ACTIVATION_FILE).unlink(missing_ok=True)
            return dict(operation=self.result, remoteRemovalConfirmed=remote_confirmed)


class Handler(http.server.BaseHTTPRequestHandler):
    def setup(self):
        super().setup()
        self.connection.settimeout(10)

    def log_message(self, *_):
        pass

    def reply(self, status, value, content_type="application/json"):
        data = value if isinstance(value, bytes) else json.dumps(value).encode()
        self.send_response(status)
        self.send_header("Content-Type", content_type)
        self.send_header("Content-Length", str(len(data)))
        self.send_header("Cache-Control", "no-store")
        self.send_header("Referrer-Policy", "no-referrer")
        self.send_header("X-Content-Type-Options", "nosniff")
        self.send_header("Content-Security-Policy",
                         "default-src 'self'; script-src 'self'; style-src 'self'; "
                         "connect-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'")
        self.end_headers()
        self.wfile.write(data)

    def do_GET(self):
        try:
            if self.path == "/":
                self.reply(200, (Path(__file__).parent / "t3-connect.html").read_bytes(), "text/html; charset=utf-8")
            elif self.path == "/connect.js":
                self.reply(200, (Path(__file__).parent / "t3-connect.js").read_bytes(), "text/javascript; charset=utf-8")
            elif self.path == "/connect.css":
                self.reply(200, (Path(__file__).parent / "t3-connect.css").read_bytes(), "text/css; charset=utf-8")
            elif self.path == "/status":
                if self.authorized():
                    self.reply(200, self.server.enrollment.status())
            else:
                self.reply(404, {})
        except Exception:
            self.reply(409, dict(error="Connect unavailable; use a running, unfenced primary VM."))

    def do_POST(self):
        # Cross-origin forms cannot send this header, and this server never grants CORS access.
        if not self.authorized():
            return
        if self.headers.get("X-Workspaces-Connect") != "1" or self.headers.get("Content-Length", "0") != "0":
            self.reply(403, {})
            return
        try:
            manager = self.server.enrollment
            if self.path == "/link":
                manager.start()
                result = dict(operation="awaiting_approval")
            elif self.path == "/unlink":
                result = manager.unlink()
            elif self.path == "/activate":
                result = manager.request_activation()
            else:
                self.reply(404, {})
                return
            self.reply(200, result)
        except Exception:
            self.reply(409, dict(error="Connect operation blocked or failed. No credentials were logged."))

    def authorized(self):
        supplied = self.headers.get("Authorization", "")
        if len(supplied) > 256 or not hmac.compare_digest(
                supplied.encode(), ("Bearer " + self.server.credential).encode()):
            self.reply(403, {})
            return False
        return True


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("action", choices=("serve", "activate", "credential", "admission"))
    parser.add_argument("--vm-id")
    parser.add_argument("--generation", type=int)
    args = parser.parse_args()
    require(os.geteuid() == 0, "Root-owned management service required")
    config = load_config(CONFIG)
    if args.action == "serve" and config["kind"] != "primary":
        return
    if args.action in ("credential", "admission"):
        require(config["vmId"] == args.vm_id and config["generation"] == args.generation,
                "Stale VM/generation")
        if args.action == "credential":
            require(not os.isatty(1), "Secret capture required")
            print(credential(config))
        else:
            print(json.dumps(admission_proof(config)))
    elif args.action == "activate":
        activate(config)
    else:
        admitted(config)
        require_root_reserved_port()
        server = http.server.ThreadingHTTPServer(("127.0.0.1", 774), Handler)
        server.enrollment = Enrollment(config)
        server.credential = credential(config)
        # Binding succeeded, and systemd has killed the previous service's entire control group.
        server.enrollment.clear_pending()
        server.serve_forever()


if __name__ == "__main__":
    try:
        main()
    except Exception:
        raise SystemExit("Connect management unavailable; no credentials logged")
