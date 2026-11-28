#!/usr/bin/env python3
"""Seed the pinned T3 provider instances before T3 starts; never handle credentials."""
import argparse
import json
import os
from pathlib import Path
import selectors
import stat
import subprocess
import sys
import time

from runtime import Blocked, atomic_json, require

VERSION = "1.0.114"
EXECUTABLE = "/usr/local/bin/copilot"
INSTANCE_ID = "municode-copilot"
COPILOT = dict(instanceId=INSTANCE_ID, driver="acpRegistry", displayName="GitHub Copilot CLI",
               executable=EXECUTABLE,
               environment=[dict(name="COPILOT_AUTO_UPDATE", value="false", sensitive=False)],
               config=dict(source="local", commandPath=EXECUTABLE, commandArgs=["--acp", "--stdio"]))
# T3 drivers that are disabled by default; seeded only when the image installed their pinned executable.
# Claude Code needs no instance: T3 enables its default claudeAgent instance and finds `claude` on PATH.
AGENTS = (
    dict(instanceId="municode-antigravity", driver="antigravity", displayName="Antigravity",
         executable="/opt/municode-antigravity/agy_acp_server.par", environment=[]),
    dict(instanceId="municode-muse", driver="muse", displayName="Muse Code",
         executable="/usr/local/bin/muse",
         environment=[dict(name="MUSE_NO_AUTO_UPDATE", value="1", sensitive=False)]),
)
SIGN_IN = ("Run copilot login --device-code as coding (HOME=/workspace/home) in an "
           "unrecorded management terminal, then retry the provider in Settings → Providers.")
FAILED_STATES = ("InstanceIdOccupied", "BinaryUnusable")


def is_copilot(instance):
    if not isinstance(instance, dict) or instance.get("driver") != "acpRegistry":
        return False
    config = instance.get("config")
    if not isinstance(config, dict):
        return False
    arguments = config.get("commandArgs", [])
    command = config.get("commandPath")
    return (config.get("source") == "local" and
            isinstance(command, str) and Path(command).name == "copilot" and
            isinstance(arguments, list) and "--acp" in arguments and
            not any(isinstance(argument, str) and argument.startswith("--port")
                    for argument in arguments))


def binary_state(executable):
    """Classify a pinned executable for the current user without ever raising."""
    try:
        info = os.stat(executable)
    except (FileNotFoundError, NotADirectoryError):
        return "MissingBinary", None
    except OSError as error:
        return "BinaryUnusable", "cannot inspect " + executable + " (" + (error.strerror or "error") + ")"
    if not stat.S_ISREG(info.st_mode):
        return "BinaryUnusable", executable + " is not a regular file"
    if not os.access(executable, os.X_OK):
        return "BinaryUnusable", executable + " is not executable by this user"
    return "Present", None


def installed_agents():
    return [agent for agent in AGENTS if binary_state(agent["executable"])[0] == "Present"]


def existing_instance(provider, instances):
    # Any instance of the driver, even a disabled one, is the user's choice; never add a second.
    for instance_id, instance in instances.items():
        if provider is COPILOT:
            if is_copilot(instance):
                return instance_id
        elif isinstance(instance, dict) and instance.get("driver") == provider["driver"]:
            return instance_id
    return None


def seed(provider, instances):
    """Decide one provider in isolation; returns (state, instanceId, detail)."""
    existing = existing_instance(provider, instances)
    if existing is not None:
        return "AlreadyConfigured", existing, None
    # Copilot is a required image component that the build verifies; agents are optional.
    state, detail = ("Present", None) if provider is COPILOT else binary_state(provider["executable"])
    if state == "MissingBinary":
        return state, None, provider["executable"]
    if state != "Present":
        return state, None, detail
    if provider["instanceId"] in instances:
        return "InstanceIdOccupied", None, "rename it in Settings → Providers"
    instances[provider["instanceId"]] = dict(
        driver=provider["driver"], displayName=provider["displayName"], enabled=True,
        environment=json.loads(json.dumps(provider["environment"])),
        config=json.loads(json.dumps(provider.get("config") or dict(binaryPath=provider["executable"]))))
    return "Configured", provider["instanceId"], None


def report_line(provider, state, instance_id, detail):
    label = provider["instanceId"] + " (" + provider["driver"] + ")"
    if state == "Configured":
        return "T3 provider " + label + ": seeded"
    if state == "AlreadyConfigured":
        return "T3 provider " + label + ": already configured as " + instance_id
    if state == "MissingBinary":
        return "T3 provider " + label + ": skipped, missing binary " + detail
    return "T3 provider " + label + ": error, " + ("instance ID is occupied; " if state ==
                                                    "InstanceIdOccupied" else "") + detail


def configure(settings_path, agents=None, log=None):
    """Called only by ExecStartPre, under the same UID/HOME as the T3 service.

    Each provider is decided independently, so one unusable binary or occupied ID never stops
    the others from being seeded. Settings that cannot be read still block every provider.
    """
    providers = (COPILOT, *(AGENTS if agents is None else agents))
    settings_path = Path(settings_path)
    require(not settings_path.is_symlink(), "Copilot setup refuses symlinked T3 settings")
    try:
        settings = json.loads(settings_path.read_text()) if settings_path.exists() else {}
    except (OSError, ValueError):
        raise Blocked("Cannot read T3 settings; repair userdata/settings.json before restarting T3") from None
    require(isinstance(settings, dict), "T3 settings must be an object")
    instances = settings.get("providerInstances", {})
    require(isinstance(instances, dict), "T3 providerInstances must be an object")
    result = dict(providers={})
    changed = False
    for provider in providers:
        state, instance_id, detail = seed(provider, instances)
        changed = changed or state == "Configured"
        result["providers"][provider["instanceId"]] = dict(
            driver=provider["driver"], state=state,
            **({"instanceId": instance_id} if instance_id else {}),
            **({"detail": detail} if detail else {}))
        if log is not None:
            log(report_line(provider, state, instance_id, detail))
    if changed:
        settings["providerInstances"] = instances
        settings_path.parent.mkdir(mode=0o700, parents=True, exist_ok=True)
        atomic_json(settings_path, settings)
    copilot = result["providers"].get(INSTANCE_ID)
    if copilot is not None:
        result.update(state=copilot["state"], **({"instanceId": copilot["instanceId"]}
                                                  if "instanceId" in copilot else {}))
    agent_states = {entry["driver"]: entry["state"] for instance_id, entry in result["providers"].items()
                    if instance_id != INSTANCE_ID}
    if agent_states:
        result["agents"] = agent_states
    return result


def failed(result):
    return any(entry["state"] in FAILED_STATES for entry in result["providers"].values())


def probe(executable=EXECUTABLE, expected_version=VERSION, timeout=30, environment=None):
    """Verify real stdio negotiation without opening sessions or exporting login material."""
    process = None
    try:
        process = subprocess.Popen(
            [executable, "--acp", "--stdio"], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
            stderr=subprocess.DEVNULL,
            env=dict(os.environ if environment is None else environment, COPILOT_AUTO_UPDATE="false"),
            start_new_session=True)
        request = dict(jsonrpc="2.0", id=1, method="initialize",
                       params=dict(protocolVersion=1, clientCapabilities={}))
        process.stdin.write((json.dumps(request) + "\n").encode())
        process.stdin.flush()
        deadline = time.monotonic() + timeout
        data = b""
        with selectors.DefaultSelector() as selector:
            selector.register(process.stdout, selectors.EVENT_READ)
            while time.monotonic() < deadline:
                if not selector.select(max(0, deadline - time.monotonic())):
                    break
                chunk = os.read(process.stdout.fileno(), 65536)
                require(chunk and len(data) + len(chunk) <= 1048576,
                        "Copilot ACP returned invalid output; rebuild the qualified image")
                data += chunk
                while b"\n" in data:
                    line, data = data.split(b"\n", 1)
                    message = json.loads(line)
                    if message.get("id") != 1:
                        continue
                    result = message.get("result") or {}
                    agent = result.get("agentInfo") or {}
                    require(result.get("protocolVersion") == 1 and
                            agent.get("name") == "Copilot" and agent.get("version") == expected_version,
                            "Incompatible Copilot ACP; rebuild with the pinned CLI " + expected_version)
                    return dict(state="Compatible", version=expected_version,
                                authentication="NotChecked", signIn=SIGN_IN)
        raise Blocked("Copilot ACP timed out; rebuild the qualified image and retry")
    except (OSError, ValueError, TypeError, AttributeError):
        raise Blocked("Copilot ACP unavailable or incompatible; rebuild the qualified image") from None
    finally:
        if process is not None:
            try:
                process.stdin.close()
            except OSError:
                pass
            process.terminate()
            try:
                process.wait(timeout=5)
            except subprocess.TimeoutExpired:
                process.kill()
                process.wait(timeout=5)
            process.stdout.close()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("action", choices=("configure", "probe"))
    parser.add_argument("--base-dir", default="/workspace/t3")
    args = parser.parse_args()
    os.umask(0o077)
    try:
        require(os.geteuid() == 2000, "Run Copilot provider setup as the coding user (UID 2000)")
        if args.action == "configure":
            # stderr reaches the service journal; stdout stays the machine-readable result.
            result = configure(Path(args.base_dir) / "userdata/settings.json",
                               log=lambda line: print(line, file=sys.stderr, flush=True))
        else:
            result = probe()
        print(json.dumps(result, sort_keys=True))
        if args.action == "configure" and failed(result):
            sys.exit(1)
    except Blocked as error:
        print(json.dumps(dict(state="SetupRequired", reason=str(error), signIn=SIGN_IN)), file=sys.stderr)
        sys.exit(1)


if __name__ == "__main__":
    main()
