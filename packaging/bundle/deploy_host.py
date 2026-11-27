#!/usr/bin/env python3
# SPDX-License-Identifier: MPL-2.0
"""Deploy a Municode release on an installed host.

Run and rollback are invoked in a transient systemd unit by the caller:
systemd-run --unit municode-deploy-<operationId> --collect --pipe <agent> ...
Use --unseal-key-stdin to supply one newline-terminated key; never put it in
argv or a plan. --drain-timeout is in seconds.

run --plan reads the preflight request plus approvalDigest (or --approve).
rollback --plan uses operationId to locate the approved host journal.
run/rollback return the journal as "state", with "blockers" on failure (exit 1).
status --operation returns {"state": <journal>, "unit": {activeState, subState}}.
The journal exposes phase, previousRelease, targetRelease, pendingStep and
the migration, traffic and worker intent markers; openAt is the exact value
required by --accept-data-loss-since. Status does not acquire the deploy lock.
"""

import argparse
from dataclasses import dataclass
from datetime import datetime, timezone
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import stat
import subprocess
import sys
import tarfile
import tempfile
import time

sys.dont_write_bytecode = True
from install_host import (assignments, atomic_write, deploy_lock, directory, freeze_release,
                          present, regular, remove_release_stage, render, write_json)
from verify_bundle import digest, main as verify_bundle


PROTOCOL = 1
IDENTITY = ("releaseId", "tag", "sourceRevision", "bundleSha256", "trust")
SHA256 = r"[0-9a-f]{64}"
OPERATION = r"[A-Za-z0-9][A-Za-z0-9_-]{0,79}"
INSTALLATION_LABEL = "com.municode.installation-id"
OWNERSHIP_SERVICES = {"openbao", "openbao-agent"}
SERVICES = OWNERSHIP_SERVICES | {"postgres", "kratos", "api", "worker", "workspace-gateway", "caddy"}
STAGED_IDENTITY = ".deploy-release.json"
GIB = 1024 ** 3
CLIENTS = ("caddy", "worker", "workspace-gateway", "api", "kratos")
BACKEND = ("openbao-agent", "kratos", "api", "workspace-gateway")
DATABASES = ("municode", "kratos")
VOLUMES = ("openbao", "caddy-data")
JOBS = ("database-initialize", "kratos-migrate", "api-migrate")
BAO_OPTIONS = ("-address=https://openbao:8200", "-ca-cert=/run/secrets/openbao-ca.crt")
DEPLOY_ERRORS = (OSError, ValueError, TypeError, KeyError, AttributeError, EOFError, tarfile.TarError)


def require(condition, message):
    if not condition:
        raise ValueError(message)


def object_file(path):
    require(regular(path), f"Missing or invalid {path.name}.")
    value = json.loads(path.read_text())
    require(isinstance(value, dict), f"Expected a JSON object in {path.name}.")
    return value


@dataclass(frozen=True)
class Host:
    root: Path

    @property
    def releases(self):
        return self.root / "opt/municode/releases"

    @property
    def config(self):
        return self.root / "etc/municode/config"

    @property
    def state(self):
        return self.root / "var/lib/municode"

    @property
    def secrets(self):
        return self.root / "etc/municode/secrets"

    def backup(self, op):
        self.operation(op)
        parent = self.state / "backups"
        path = parent / op
        require(parent.is_dir() and parent.resolve() == parent and not path.is_symlink(),
                "Backups directory is missing or not canonical.")
        return path

    def operation(self, op):
        require(isinstance(op, str) and re.fullmatch(OPERATION, op), "Invalid deploy operation ID.")
        parent = self.state / "deploys"
        path = parent / op
        require(parent.is_dir() and parent.resolve() == parent and not path.is_symlink(),
                "Deploy operations directory is missing or not canonical.")
        return path

    def release(self, release_id):
        require(isinstance(release_id, str) and re.fullmatch(
            r"municode-v[0-9][A-Za-z0-9.-]*|local-[a-f0-9]{12}-[a-f0-9]{12}", release_id),
            "Invalid release ID.")
        path = self.releases / release_id
        require(path.is_dir() and path.resolve() == path, "Release directory is missing or not canonical.")
        return path


def read_state(host, op):
    path = host.operation(op) / "state.json"
    return object_file(path) if present(path) else None


def write_state(host, op, state):
    path = host.operation(op)
    directory(path, 0o700)
    durable_json(path / "state.json", state)
    sync_path(path.parent)


def sync_path(path):
    fd = os.open(path, os.O_RDONLY | os.O_NOFOLLOW)
    try:
        os.fsync(fd)
    finally:
        os.close(fd)


def durable_write(path, data, mode=0o600):
    with tempfile.NamedTemporaryFile(dir=path.parent, prefix=f".{path.name}-", delete=False) as file:
        temporary = Path(file.name)
        try:
            file.write(data)
            file.flush()
            os.fchmod(file.fileno(), mode)
            os.fsync(file.fileno())
            temporary.replace(path)
            sync_path(path.parent)
        finally:
            temporary.unlink(missing_ok=True)


def durable_json(path, value):
    durable_write(path, canonical(value) + b"\n")


class Runner:
    def __init__(self):
        self.log = None

    def __call__(self, step, *argv, allowed=(0,), timeout=120, data=None, source=None, output=None):
        # Do not let SSH environment variables select a different daemon or override Compose inputs.
        env = {key: os.environ[key] for key in ("PATH", "HOME") if key in os.environ}
        env["DOCKER_HOST"] = "unix:///var/run/docker.sock"
        try:
            result = subprocess.run(argv, stdin=(source if source is not None else
                                                 subprocess.DEVNULL if data is None else None),
                                    input=data, stdout=output if output is not None else subprocess.PIPE,
                                    stderr=subprocess.DEVNULL, env=env, timeout=timeout)
        except subprocess.TimeoutExpired as error:
            self.record(step, "timeout")
            raise ValueError(f"{step} timed out.") from error
        self.record(step, str(result.returncode))
        require(result.returncode in allowed, f"{step} failed (exit {result.returncode}).")
        return result.stdout.decode(errors="replace").strip() if output is None else ""

    def record(self, step, outcome):
        if self.log is not None:
            with os.fdopen(os.open(self.log, os.O_WRONLY | os.O_APPEND | os.O_CREAT | os.O_NOFOLLOW, 0o600),
                           "a") as stream:
                stream.write(f"{step}: {outcome}\n")


def compose(run, step, release, env, *args, **options):
    return run(step, "docker", "compose", "--project-name", "municode",
               "--project-directory", str(release), "--env-file", str(env),
               "-f", str(release / "compose.yml"), *args, **options)


def identity(manifest, sha, trust):
    revision = manifest["sourceRevision"]
    tag = "municode-v" + manifest["version"]
    require(isinstance(sha, str) and re.fullmatch(SHA256, sha), "Invalid bundle SHA-256.")
    require(trust in ("github-sigstore", "local-build"), "Invalid release trust.")
    return dict(releaseId=tag if trust == "github-sigstore" else f"local-{revision[:12]}-{sha[:12]}",
                tag=tag, sourceRevision=revision, bundleSha256=sha, trust=trust)


def active_installation(host, run):
    active = object_file(host.state / "active-release.json")
    require(isinstance(active.get("activatedAt"), str) and active["activatedAt"],
            "Active release record has no activation time.")
    release = host.release(active["releaseId"])
    manifest = object_file(release / "release-manifest.json")
    require(all(active[key] == value for key, value in
                identity(manifest, active["bundleSha256"], active["trust"]).items()),
            "Active release record does not match the installed manifest.")
    operator = host.config / "operator.env"
    env = release / ".env"
    require(regular(operator) and regular(env), "Missing active or operator environment.")
    current_env = assignments(env)
    require(all(current_env.get(key) == active[field] for key, field in (
        ("MUNICODE_RELEASE_TAG", "tag"), ("MUNICODE_RELEASE_SOURCE_REVISION", "sourceRevision"),
        ("MUNICODE_RELEASE_TRUST", "trust"))), "Active environment does not match the release record.")
    installation = current_env.get("MUNICODE_INSTALLATION_ID", "").strip()
    if len(installation) >= 2 and installation[0] in "\"'" and installation[-1] == installation[0]:
        installation = installation[1:-1]
    require(re.fullmatch(r"[A-Za-z0-9._-]+", installation), "Invalid active installation ID.")
    container_ids = run("list running containers", "docker", "ps", "--no-trunc", "--quiet",
                        "--filter", "label=com.docker.compose.project=municode").splitlines()
    require(container_ids and all(re.fullmatch(SHA256, item) for item in container_ids),
            "No running Municode Compose installation found.")
    containers = {}
    inspected = json.loads(run("inspect running containers", "docker", "container", "inspect", *container_ids))
    require(sorted(container["Id"] for container in inspected) == sorted(container_ids),
            "Running Municode containers changed during inspection.")
    for container in inspected:
        labels = container["Config"]["Labels"]
        service = labels.get("com.docker.compose.service")
        require(labels.get("com.docker.compose.project.working_dir") == str(release),
                "Active release record does not match a running container's Compose working directory.")
        if service in OWNERSHIP_SERVICES or INSTALLATION_LABEL in labels:
            require(labels.get(INSTALLATION_LABEL) == installation,
                    "Active installation ID does not match a running container's ownership label.")
        require(service and service not in containers,
                "Unexpected or duplicate running Municode Compose service.")
        containers[service] = container
    require(OWNERSHIP_SERVICES <= containers.keys(), "Running installation has no OpenBao ownership evidence.")
    return active, release, manifest, current_env, installation, containers


def extract(archive, stage, local):
    names = set()
    total = 0
    root = None
    with tarfile.open(fileobj=archive, mode="r|gz") as bundle:
        for member in bundle:
            parts = member.name.rstrip("/").split("/")
            require(len(parts) <= 128 and all(part not in ("", ".", "..") for part in parts)
                    and "\\" not in member.name
                    and (member.isdir() or member.isfile()) and not member.issparse(),
                    "Bundle contains an unsafe archive member.")
            if root is None:
                root = parts[0]
            relative = "/".join(parts[1:])
            require(parts[0] == root and relative not in names and len(names) < 100_000
                    and (relative or member.isdir()) and relative not in {".env", STAGED_IDENTITY},
                    "Bundle contains a duplicate, reserved or unexpected archive path.")
            names.add(relative)
            path = stage / relative
            if member.isdir():
                path.mkdir(parents=True, exist_ok=True)
                continue
            image = local and relative in {f"images/{name}.tar" for name in ("api", "worker", "gateway")}
            total += member.size
            require(0 <= member.size <= (16 * GIB if image else GIB // 8)
                    and total <= (32 * GIB if local else 2 * GIB)
                    and member.size <= shutil.disk_usage(stage).free,
                    "Bundle exceeds the extraction size or available disk space.")
            path.parent.mkdir(parents=True, exist_ok=True)
            with bundle.extractfile(member) as source, path.open("xb") as output:
                shutil.copyfileobj(source, output)
            path.chmod(0o755 if member.mode & 0o100 else 0o644)
    return root


def registry_name(reference):
    return reference.removeprefix("docker.io/").removeprefix("library/")


def allowed_images(release, manifest):
    dependencies = set(manifest["dependencies"].values())
    require(all(re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9./:_-]*@sha256:" + SHA256, ref)
                for ref in dependencies), "Release dependency image is not pinned by SHA-256.")
    local = {item["reference"] for item in manifest.get("imageArchives", {}).values()}
    refs = {value for key, value in assignments(release / ".env.example").items() if key.endswith("_IMAGE")}
    require({registry_name(ref) for ref in refs} <= {registry_name(ref) for ref in dependencies | local},
            "Release environment references an image outside the verified inventory.")
    return refs, local


def prime_images(run, release, manifest):
    refs, local = allowed_images(release, manifest)
    for image in manifest.get("imageArchives", {}).values():
        loaded = run("load local image", "docker", "image", "load", "--input",
                     str(release / image["path"]), "--quiet", timeout=1800)
        image_id = image["imageId"]
        require(loaded == f"Loaded image ID: {image_id}" and
                run("inspect loaded image", "docker", "image", "inspect", "--format", "{{.Id}}",
                    image_id) == image_id, "Loaded local image does not match the bundle's image ID.")
        run("tag local image", "docker", "image", "tag", image_id, image["reference"])
    for ref in sorted(refs - local):
        run("pull pinned image", "docker", "image", "pull", ref, timeout=1800)


def stage(host, op, archive, sha, trust, verified_tag, run):
    host.operation(op)
    require(re.fullmatch(SHA256, sha), "Invalid bundle SHA-256.")
    require(bool(verified_tag) == (trust == "github-sigstore"),
            "Signed bundles require the externally verified release tag.")
    active, *_ = active_installation(host, run)
    work = host.operation(op)
    staging = host.releases / f".staging-{op}"
    request = {"bundleSha256": sha, "trust": trust, "verifiedTag": verified_tag}
    previous = read_state(host, op)
    require(previous is None or (previous.get("request") == request
                                 and previous.get("phase") in ("staging", "staged")),
            "Operation already belongs to a different deploy request or phase.")
    if previous is None:
        require(not present(staging), "Unowned staging path exists.")
    if previous is not None and previous["phase"] == "staged":
        target = previous["targetRelease"]
        release = host.release(target["releaseId"])
        recorded = active if target["releaseId"] == active["releaseId"] else object_file(release / STAGED_IDENTITY)
        require(all(recorded[key] == target[key] for key in IDENTITY),
                "Staged release no longer matches this operation.")
        return {"targetRelease": target, "deployProtocol": PROTOCOL}
    write_state(host, op, {"phase": "staging", "request": request})
    remove_release_stage(staging)
    # Verify a private snapshot; the uploaded file stays writable by the SSH user.
    with tempfile.TemporaryFile(dir=work) as snapshot:
        with os.fdopen(os.open(archive, os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK), "rb") as source:
            info = os.fstat(source.fileno())
            limit = (32 if trust == "local-build" else 2) * GIB
            require(stat.S_ISREG(info.st_mode) and 0 < info.st_size <= limit
                    and info.st_size <= shutil.disk_usage(work).free, "Invalid uploaded bundle size or type.")
            remaining = info.st_size
            while remaining and (chunk := source.read(min(remaining, 1024 ** 2))):
                snapshot.write(chunk)
                remaining -= len(chunk)
            require(remaining == 0 and not source.read(1), "Uploaded bundle file changed during staging.")
        snapshot.seek(0)
        require(hashlib.file_digest(snapshot, "sha256").hexdigest() == sha, "Uploaded bundle SHA-256 mismatch.")
        snapshot.seek(0)
        staging.mkdir(mode=0o700)
        try:
            archive_root = extract(snapshot, staging, trust == "local-build")
            verify_bundle(staging)
            manifest = object_file(staging / "release-manifest.json")
            target = identity(manifest, sha, trust)
            require(archive_root == f"municode-{manifest['version']}", "Bundle root does not match the manifest.")
            require(verified_tag == target["tag"] if trust == "github-sigstore" else
                    manifest.get("sourceDirty") is False and "imageArchives" in manifest,
                    "Release tag or clean local image inventory does not match the requested trust.")
            require(("imageArchives" in manifest) == (trust == "local-build"), "Unexpected local image inventory.")
            destination = host.releases / target["releaseId"]
            if present(destination):
                host.release(target["releaseId"])
                recorded = (active if target["releaseId"] == active["releaseId"] else
                            object_file(destination / STAGED_IDENTITY))
                require(all(recorded[key] == value for key, value in target.items()),
                        "Release ID already exists with a different bundle identity.")
            prime_images(run, staging, manifest)
            if not present(destination):
                write_json(staging / STAGED_IDENTITY, target)
                freeze_release(staging)
                staging.rename(destination)
        finally:
            remove_release_stage(staging)
    write_state(host, op, {"phase": "staged", "request": request, "targetRelease": target})
    return {"targetRelease": target, "deployProtocol": PROTOCOL}


def target_environment(host, current_env, operator, release, manifest, target):
    template = assignments(release / ".env.example")
    contract = manifest["envContract"]
    values, missing = {}, []
    operator_keys = {key for key, entry in contract.items() if entry["owner"] == "operator"}
    for key, entry in contract.items():
        owner = entry["owner"]
        if owner == "release":
            values[key] = template[key]
        elif owner == "installer":
            require(bool(current_env.get(key)), f"Missing installer-owned environment key: {key}.")
            values[key] = current_env[key]
        elif operator.get(key):
            values[key] = operator[key]
        elif key not in operator and "default" in entry:
            values[key] = entry["default"]
        else:
            missing.append(key)
    values.update(MUNICODE_RELEASE_TAG=target["tag"], MUNICODE_RELEASE_SOURCE_REVISION=target["sourceRevision"],
                  MUNICODE_RELEASE_TRUST=target["trust"])
    if "MUNICODE_POLICY_FILE" not in operator and "MUNICODE_POLICY_FILE" in values:
        values["MUNICODE_POLICY_FILE"] = str(host.config / "municode.json")
    diff = {
        "added": sorted(values.keys() - current_env.keys()),
        "removed": sorted(current_env.keys() - values.keys()),
        "changed": sorted(key for key in values.keys() & current_env.keys() if values[key] != current_env[key]),
        "missingOperatorKeys": sorted(missing),
        "unknownOperatorKeys": sorted(operator.keys() - operator_keys),
    }
    return values, diff


def canonical(value):
    return json.dumps(value, sort_keys=True, separators=(",", ":"), ensure_ascii=True).encode()


def migration_status(run, release, service, network, secrets):
    environment = service["environment"]
    keys = ("Database__Provider", "Database__PasswordFile", "ConnectionStrings__Municode")
    require(tuple(environment[key] for key in keys) == (
        "Postgres", "/run/secrets/app-db-password", "Host=postgres;Port=5432;Database=municode;Username=municode"),
        "Migration probe does not match the live PostgreSQL connection contract.")
    password = secrets / "app-db-password"
    require(regular(password) and password.stat().st_size > 0, "Missing database password file.")
    # Attach only to the existing database network. Compose run may create new networks or volumes.
    result = json.loads(run(
        "inspect database migrations", "docker", "run", "--rm", "--pull=never", "--read-only", "--network", network,
        "--user", "0", "--cap-drop", "ALL", "--security-opt", "no-new-privileges",
        "--tmpfs", "/tmp:rw,noexec,nosuid,nodev,size=32m,mode=1777",
        *(arg for key in keys for arg in ("--env", f"{key}={environment[key]}")),
        "--mount", f"type=bind,src={password},dst=/run/secrets/app-db-password,readonly",
        "--mount", f"type=bind,src={release / 'runtime/api'},dst=/opt/municode,readonly",
        "--entrypoint", "/usr/bin/dotnet", service["image"],
        "/opt/municode/Municode.Api.dll", "migrations", "status", "--json"))
    require(type(result["historyTableExists"]) is bool and all(
        isinstance(result[field], list) and all(
            isinstance(item, str) and re.fullmatch(r"[0-9]{14}_[A-Za-z0-9_]+", item) for item in result[field])
        for field in ("applied", "pending", "unknownApplied")), "Invalid migration status response.")
    return result


def exec_service(run, containers, service, *args, allowed=(0,)):
    require(service in containers, f"Required service is not running: {service}.")
    return run(f"probe {service}", "docker", "exec", containers[service]["Id"], *args, allowed=allowed)


def storage_status(host, run, containers, services, secrets, images, local_images):
    postgres = exec_service(
        run, containers, "postgres", "psql", "-XAt", "-U", "postgres", "-d", "postgres",
        "-v", "ON_ERROR_STOP=1", "-c",
        "SELECT current_setting('server_version_num'), pg_database_size('municode') + pg_database_size('kratos')")
    require(re.fullmatch(r"[0-9]+\|[0-9]+", postgres), "Invalid PostgreSQL storage response.")
    version, db = map(int, postgres.split("|"))
    image_info = json.loads(run("inspect staged images", "docker", "image", "inspect", *images))
    require(len(image_info) == len(images), "Staged image inventory is incomplete.")
    for ref, info in zip(images, image_info, strict=True):
        require(ref not in local_images or info["Id"] == local_images[ref],
                "Staged local image does not match the bundle's image ID.")
    pg_image = image_info[images.index(services["postgres"]["image"])]
    majors = [value.split("=", 1)[1] for value in pg_image["Config"]["Env"] if value.startswith("PG_MAJOR=")]
    require(len(majors) == 1 and re.fullmatch(r"[0-9]+", majors[0]), "Invalid PostgreSQL image major version.")
    volumes = []
    for service, name in (("openbao", "openbao"), ("caddy", "caddy-data")):
        mounts = [entry for entry in containers[service]["Mounts"]
                  if entry.get("Type") == "volume" and entry.get("Name") == f"municode_{name}"]
        require(len(mounts) == 1, f"Missing or ambiguous {service} backup volume.")
        volumes.append(mounts[0]["Source"])
    sizes = run("estimate backup size", "du", "--summarize", "--apparent-size", "--block-size=1", "--",
                *volumes, str(secrets), str(host.config), str(host.state / "active-release.json"))
    size_lines = [line.partition("\t")[0] for line in sizes.splitlines()]
    require(len(size_lines) == len(volumes) + 3 and all(re.fullmatch(r"[0-9]+", item) for item in size_lines),
            "Incomplete backup size estimate.")
    # Uncompressed data plus headroom for archive metadata and database growth before drain.
    estimate = (db + sum(map(int, size_lines))) * 2 + 64 * 1024 ** 2
    free = shutil.disk_usage(host.state / "backups").free
    return {"currentMajor": version // 10000, "targetMajor": int(majors[0])}, {
        "backupRequiredBytes": estimate, "backupFreeBytes": free,
        "stagedImageBytes": sum(image["Size"] for image in image_info),
    }


def preflight(host, plan, run, prepared=None):
    """Inspect under the caller's deploy lock; only the operation may receive temporary files."""
    op = plan["operationId"]
    work = host.operation(op)
    require(type(plan["deployProtocol"]) is int and plan["deployProtocol"] == PROTOCOL,
            "Unsupported deploy protocol.")
    require(isinstance(plan["instanceName"], str) and re.fullmatch(r"[A-Za-z0-9._-]+", plan["instanceName"])
            and isinstance(plan["hostFingerprint"], str) and
            re.fullmatch(r"SHA256:[A-Za-z0-9+/]{43}", plan["hostFingerprint"]), "Invalid pinned instance identity.")
    active, current, current_manifest, current_env, installation, containers = active_installation(host, run)
    require(plan["installationId"] == installation, "Plan installation ID does not match the running installation.")
    requested = plan["targetRelease"]
    release = host.release(requested["releaseId"])
    target = ({key: active[key] for key in IDENTITY} if release == current else
              object_file(release / STAGED_IDENTITY))
    require(all(requested[key] == target[key] for key in IDENTITY), "Plan does not match the staged release identity.")
    manifest = object_file(release / "release-manifest.json")
    require(all(type(item.get("deployProtocol")) is int and item["deployProtocol"] == PROTOCOL
                for item in (current_manifest, manifest)), "Installed and target deploy protocols must match exactly.")
    policy_path, operator_path = (host.config / name for name in ("municode.json", "operator.env"))
    require(regular(policy_path) and regular(operator_path), "Missing host policy or operator environment.")
    config_hashes = {"policySha256": digest(policy_path), "operatorEnvSha256": digest(operator_path)}
    values, diff = target_environment(host, current_env, assignments(operator_path), release, manifest, target)
    result = {"currentRelease": active, "targetRelease": target, "deployProtocol": PROTOCOL,
              "envDiff": diff, "composeConfigValid": False, "blockers": []}
    blockers = result["blockers"]
    if diff["missingOperatorKeys"]:
        blockers.append("Missing required operator environment keys.")
    if diff["unknownOperatorKeys"]:
        blockers.append("Unknown or non-operator keys in operator.env.")
    health = {}
    for service in sorted(SERVICES):
        state = containers.get(service, {}).get("State", {})
        health[service] = (state.get("Health", {}).get("Status", "running")
                           if state.get("Running") else "not-running")
    result["serviceHealth"] = health
    unhealthy = any(status not in ("healthy", "running") for status in health.values())
    if unhealthy:
        blockers.append("One or more Municode services are not healthy.")
    if diff["missingOperatorKeys"] or diff["unknownOperatorKeys"] or not {"postgres", "caddy", "api"} <= containers.keys():
        return result
    directory(work, 0o700)
    with tempfile.TemporaryDirectory(prefix="preflight-", dir=work) as temporary:
        env = Path(temporary) / ".env"
        atomic_write(env, render(dict(sorted(values.items()))))
        try:
            compose(run, "validate target Compose config", release, env, "--profile", "*", "config", "--quiet")
            config = json.loads(compose(run, "normalize target Compose config", release, env,
                                        "--profile", "*", "config", "--format", "json"))
        except ValueError as error:
            blockers.append(str(error))
            return result
        result["composeConfigValid"] = True
        services = config["services"]
        images = sorted({service["image"] for service in services.values()})
        refs, local = allowed_images(release, manifest)
        require(set(map(registry_name, images)) <= set(map(registry_name, refs)),
                "Target Compose config selects an image outside the verified inventory.")
        api_env = services["api"]["environment"]
        require(api_env["Bootstrap__InstallationId"] == installation and all(
            services[service]["labels"][INSTALLATION_LABEL] == installation for service in OWNERSHIP_SERVICES),
            "Target Compose config changes the installation ID.")
        require(all(any(mount["source"] == str(policy_path) and mount["read_only"]
                        and mount["target"] == "/etc/municode/municode.json"
                        for mount in services[name]["volumes"]) for name in ("api", "api-migrate", "worker")),
                "Target Compose config must use the installed policy file.")
        secrets = host.root / "etc/municode/secrets"
        require(values["MUNICODE_SECRETS_DIR"] == str(secrets) and secrets.resolve() == secrets,
                "Target Compose config must use the installed secrets directory.")
        result["imagesToPull"] = sorted(set(images) - local)
        local_ids = {item["reference"]: item["imageId"] for item in manifest.get("imageArchives", {}).values()}
        postgres, disk = storage_status(host, run, containers, services, secrets, images, local_ids)
        result.update(postgres=postgres, disk=disk)
        if postgres["currentMajor"] != postgres["targetMajor"]:
            blockers.append("PostgreSQL major-version changes are not supported.")
        if disk["backupFreeBytes"] < disk["backupRequiredBytes"]:
            blockers.append("Insufficient disk space for the backup.")
    network = containers["postgres"]["NetworkSettings"]["Networks"]["municode_private"]["NetworkID"]
    require(re.fullmatch(SHA256, network), "Invalid live PostgreSQL network ID.")
    current_config = json.loads(compose(run, "read active Compose config", current, current / ".env",
                                        "config", "--format", "json"))
    current_migrations = migration_status(run, current, current_config["services"]["api-migrate"], network, secrets)
    migrations = migration_status(run, release, services["api-migrate"], network, secrets)
    result["migrations"] = migrations
    applied = sorted(current_migrations["applied"] + current_migrations["unknownApplied"])
    target_ids = manifest["migrations"]["postgres"]
    if (not migrations["historyTableExists"] or not current_migrations["historyTableExists"]
            or migrations["unknownApplied"] or migrations["applied"] != applied
            or target_ids != migrations["applied"] + migrations["pending"]):
        blockers.append("Target migrations do not extend the applied PostgreSQL history.")
    maintenance = json.loads(exec_service(run, containers, "api", "/usr/bin/dotnet",
                                          "/opt/municode/Municode.Api.dll", "maintenance", "status", "--json"))
    require(type(maintenance["enabled"]) is bool and type(maintenance["nonterminalOperations"]) is int
            and maintenance["nonterminalOperations"] >= 0, "Invalid maintenance status response.")
    result["maintenance"] = maintenance
    bao = json.loads(exec_service(run, containers, "openbao", "bao", "status", "-format=json",
                                 "-address=https://openbao:8200", "-ca-cert=/run/secrets/openbao-ca.crt",
                                 allowed=(0, 2)))
    require(type(bao["sealed"]) is bool and type(bao["initialized"]) is bool, "Invalid OpenBao status response.")
    result["openBao"] = {"sealed": bao["sealed"], "initialized": bao["initialized"],
                         "requiresUnsealKeyFile": not regular(secrets / "openbao-unseal-key")
                         or (secrets / "openbao-unseal-key").stat().st_size == 0}
    if bao["sealed"] or not bao["initialized"]:
        blockers.append("The running OpenBao service must be initialized and unsealed.")
    if not unhealthy:
        try:
            exec_service(run, containers, "caddy", "wget", "-qO", "/dev/null", "http://api:8080/health/ready")
        except ValueError as error:
            blockers.append(str(error))
    require(config_hashes == host_config_hashes(host),
            "Host configuration changed during preflight; retry the plan.")
    effective = hashlib.sha256(canonical(config_hashes | {"compose": config})).hexdigest()
    result["effectiveConfigHash"] = effective
    result["digestInputs"] = {
        **{key: plan[key] for key in ("instanceName", "installationId", "hostFingerprint")},
        "currentRelease": {key: active[key] for key in (*IDENTITY, "activatedAt")},
        "targetRelease": target, "deployProtocol": PROTOCOL, "effectiveConfigHash": effective,
        "envDiff": diff, "appliedMigrations": applied, "pendingMigrations": migrations["pending"], "images": images,
    }
    if prepared is not None:
        prepared.update(values=values, configHashes=config_hashes, runningServices=set(containers))
    return result


def now():
    return datetime.now(timezone.utc).isoformat(timespec="microseconds").replace("+00:00", "Z")


def host_config_hashes(host):
    return {"policySha256": digest(host.config / "municode.json"),
            "operatorEnvSha256": digest(host.config / "operator.env")}


def error_message(error):
    if isinstance(error, (KeyError, TypeError, AttributeError, json.JSONDecodeError)):
        return "Malformed deploy input or probe response."
    if isinstance(error, OSError):
        return f"Deploy filesystem or command access failed (errno {error.errno})."
    if isinstance(error, (EOFError, tarfile.TarError)):
        return "Invalid or incomplete deploy archive."
    return str(error)


def compose_at(run, release, step, *args, **options):
    return compose(run, step, release, release / ".env", *args, **options)


def sql(run, release, step, database, query, **options):
    return compose_at(run, release, step, "exec", "-T", "postgres",
                      "psql", "-XAt", "-U", "postgres", "-d", database,
                      "-v", "ON_ERROR_STOP=1", "-c", query, **options)


def roles_hash(run, release):
    # Do not replay globals onto a live cluster. Refuse to restore over drifted roles.
    roles = sql(run, release, "inspect database roles", "postgres",
                "SELECT coalesce(json_agg(r ORDER BY rolname)::text, '[]') FROM pg_authid r")
    memberships = sql(run, release, "inspect database role memberships", "postgres",
                      "SELECT coalesce(json_agg(m ORDER BY roleid,member,grantor)::text, '[]') FROM pg_auth_members m")
    return hashlib.sha256(canonical([roles, memberships])).hexdigest()


def volume_path(run, installation, name):
    volume = f"municode_{name}"
    info, = json.loads(run("inspect backup volume", "docker", "volume", "inspect", volume))
    path = Path(info["Mountpoint"])
    require(info["Name"] == volume and info["Driver"] == "local" and not info.get("Options")
            and info["Labels"].get(INSTALLATION_LABEL) == installation
            and path.is_absolute() and path.is_dir() and path.resolve() == path,
            "Backup volume is not a canonical local Municode volume.")
    return path


def capture(run, step, path, *args, **options):
    with path.open("xb") as file:
        run(step, *args, **options, output=file)
        file.flush()
        os.fsync(file.fileno())
    require(path.stat().st_size > 0, f"{step} produced no backup data.")


def pg_archive(run, release, step, *args, **options):
    return compose_at(run, release, step, "exec", "-T", "postgres", *args, timeout=3600, **options)


def read_backup(host, op, previous):
    backup = host.backup(op)
    manifest = object_file(backup / "backup-manifest.json")
    expected = {"postgres-globals.sql", "active-release.json", "config.tar", "secrets.tar",
                *(f"{name}.dump" for name in DATABASES), *(f"{name}.tar" for name in VOLUMES)}
    require(manifest["operationId"] == op and manifest["previousRelease"] == previous
            and manifest["format"] == 1 and set(manifest["files"]) == expected,
            "Backup does not match the previous release or operation.")
    for name in sorted(expected):
        path = backup / name
        require(regular(path) and digest(path) == manifest["files"][name], "Backup file failed SHA-256 verification.")
    require(object_file(backup / "active-release.json") == previous, "Backup active-release record does not match.")
    return backup, manifest


def bao_exec(run, release, step, *args, **options):
    return compose_at(run, release, step, "exec", "-T", "openbao", "bao", *args, **options)


def ensure_unpaused(run, release):
    container = compose_at(run, release, "find OpenBao container", "ps", "-a", "-q", "openbao")
    if container:
        require(re.fullmatch(r"[a-f0-9]{12,64}", container), "Invalid OpenBao container ID.")
        paused = run("inspect OpenBao pause state", "docker", "container", "inspect", "--format",
                     "{{.State.Paused}}", container)
        require(paused in ("true", "false"), "Invalid OpenBao pause state.")
        if paused == "true":
            run("unpause OpenBao", "docker", "unpause", container)


def create_backup(host, op, previous, installation, release, run):
    backup = host.backup(op)
    require(not present(backup), "Backup path already exists.")
    backup.mkdir(mode=0o700)
    created = now()
    roles = roles_hash(run, release)

    def pg_file(name, step, *args):
        with (backup / name).open("xb") as file:
            pg_archive(run, release, step, *args, output=file)
            file.flush()
            os.fsync(file.fileno())
        require((backup / name).stat().st_size > 0, f"{step} produced no backup data.")

    pg_file("postgres-globals.sql", "dump database globals", "pg_dumpall", "-U", "postgres", "--globals-only")
    for database in DATABASES:
        name = f"{database}.dump"
        pg_file(name, f"dump {database}", "pg_dump", "-U", "postgres", "-Fc", database)
        with (backup / name).open("rb") as file:
            pg_archive(run, release, f"list {database} archive", "pg_restore", "--list",
                       source=file, output=subprocess.DEVNULL)

    def tar_backup(name, path):
        require(path.is_dir() and path.resolve() == path, "Backup source is missing or not canonical.")
        if name in ("config", "secrets"):
            require(all(not entry.is_symlink() and (entry.is_dir() or regular(entry)) for entry in path.rglob("*")),
                    "Host backup directories must contain only regular files and directories.")
        capture(run, f"archive {name}", backup / f"{name}.tar", "tar", "--numeric-owner",
                "-C", str(path), "-cf", "-", ".", timeout=3600)
        run(f"list {name} archive", "tar", "-tf", str(backup / f"{name}.tar"),
            output=subprocess.DEVNULL, timeout=3600)

    openbao = volume_path(run, installation, "openbao")
    try:
        compose_at(run, release, "pause OpenBao", "pause", "openbao")
        tar_backup("openbao", openbao)
    finally:
        ensure_unpaused(run, release)
    tar_backup("caddy-data", volume_path(run, installation, "caddy-data"))
    tar_backup("config", host.config)
    tar_backup("secrets", host.secrets)
    require(object_file(host.state / "active-release.json") == previous, "Active release changed during backup.")
    durable_json(backup / "active-release.json", previous)
    durable_json(backup / "backup-manifest.json", {
        "format": 1, "operationId": op, "previousRelease": previous, "createdAt": created, "rolesSha256": roles,
        "files": {p.name: digest(p) for p in sorted(backup.iterdir())},
    })
    sync_path(backup.parent)
    return created


def replace_directory_contents(source, target):
    entries = {entry.name: entry for entry in source.iterdir()}
    for entry in entries.values():
        destination = target / entry.name
        existing_dir = destination.is_dir() and not destination.is_symlink()
        if entry.is_dir() and existing_dir:
            replace_directory_contents(entry, destination)
        else:
            if existing_dir:
                shutil.rmtree(destination)
            elif entry.is_dir() and present(destination):
                destination.unlink()
            entry.replace(destination)
    for stale in (entry for entry in target.iterdir() if entry.name not in entries):
        if stale.is_dir() and not stale.is_symlink():
            shutil.rmtree(stale)
        else:
            stale.unlink()
    if os.geteuid() == 0:
        info = source.stat()
        os.chown(target, info.st_uid, info.st_gid)
    shutil.copystat(source, target)


def restore_host_tree(run, name, archive, path, op):
    # Keep Compose's required env_file and bind mounts present across a process kill.
    staged = path.parent / f".restore-{name}-{op}"
    if present(staged):
        require(staged.is_dir() and staged.resolve() == staged, "Invalid interrupted restore staging directory.")
        shutil.rmtree(staged)
    staged.mkdir(mode=0o700)
    try:
        run(f"unpack {name}", "tar", "--numeric-owner", "--same-permissions", "-xf",
            str(archive), "-C", str(staged), timeout=3600)
        replace_directory_contents(staged, path)
    finally:
        shutil.rmtree(staged)


def restore_backup(host, op, previous, installation, release, run):
    """Restore a matched snapshot with all clients and OpenBao already stopped."""
    backup, manifest = read_backup(host, op, previous)
    require(roles_hash(run, release) == manifest["rolesSha256"], "PostgreSQL roles changed since the backup.")
    paths = {name: volume_path(run, installation, name) for name in VOLUMES}
    paths.update(config=host.config, secrets=host.secrets)
    require(all(path.is_dir() and path.resolve() == path for path in paths.values()),
            "Restore destination is missing or not canonical.")
    for database in DATABASES:
        sql(run, release, f"drop {database}", "postgres", f"DROP DATABASE IF EXISTS {database} WITH (FORCE)")
        sql(run, release, f"recreate {database}", "postgres", f"CREATE DATABASE {database} OWNER {database}")
        with (backup / f"{database}.dump").open("rb") as file:
            pg_archive(run, release, f"restore {database}", "pg_restore", "-U", "postgres", "-d", database,
                       "--exit-on-error", "--single-transaction", source=file)
    for name, path in paths.items():
        archive = backup / f"{name}.tar"
        if name in ("config", "secrets"):
            restore_host_tree(run, name, archive, path, op)
            run(f"sync restored {name}", "sync", "-f", str(path), timeout=3600)
            continue
        # Clear the destination first: files created after the snapshot must not survive.
        for item in path.iterdir():
            if item.is_dir() and not item.is_symlink():
                shutil.rmtree(item)
            else:
                item.unlink()
        run(f"restore {name}", "tar", "--numeric-owner", "--same-permissions", "-xf",
            str(archive), "-C", str(path), timeout=3600)
        run(f"sync restored {name}", "sync", "-f", str(path), timeout=3600)


def export_reconciliation(run, release, backup, manifest):
    target = backup / "post-open-operations.json"
    if present(target):
        require(regular(target), "Invalid reconciliation export path.")
        return
    since = manifest["createdAt"]
    require(re.fullmatch(r"[0-9]{4}(?:-[0-9]{2}){2}T[0-9]{2}(?::[0-9]{2}){2}\.[0-9]{6}Z", since),
            "Invalid backup creation timestamp.")
    query = f"""
        WITH operations AS (
            SELECT * FROM development_workspace_operations WHERE created_at >= '{since}'::timestamptz
        ), vms AS (
            SELECT * FROM development_workspace_vms WHERE created_at >= '{since}'::timestamptz
            OR id IN (SELECT vm_id FROM operations)
        )
        SELECT json_build_object('operations', coalesce((SELECT json_agg(operations) FROM operations), '[]'::json),
                                 'vms', coalesce((SELECT json_agg(vms) FROM vms), '[]'::json))
    """
    result = json.loads(sql(run, release, "export operations and VMs", "municode", query, timeout=3600))
    require(isinstance(result["operations"], list) and isinstance(result["vms"], list),
            "Invalid reconciliation export.")
    durable_json(target, result)


def maintain(run, release, action, timeout=120, op=None):
    labels = ("--label", f"com.municode.deploy-operation={op}") if op else ()
    result = json.loads(compose_at(run, release, f"maintenance {action}", "run", "--rm", "--no-deps", "-T",
                                   "--pull", "never", *labels, "api-migrate", "/opt/municode/Municode.Api.dll",
                                   "maintenance", action, "--json", timeout=timeout))
    require(type(result["enabled"]) is bool and type(result["nonterminalOperations"]) is int
            and result["nonterminalOperations"] >= 0, "Invalid maintenance status response.")
    return result


def await_probe(step, probe, timeout, interval=2):
    deadline = time.monotonic() + timeout
    while True:
        remaining = deadline - time.monotonic()
        require(remaining > 0, f"{step} timed out.")
        if probe(min(remaining, 15)):
            return
        time.sleep(min(interval, max(0, deadline - time.monotonic())))


def await_drain(run, release, timeout, op):
    def drained(remaining):
        result = maintain(run, release, "status", timeout=remaining, op=op)
        require(result["enabled"], "Maintenance mode is not enabled.")
        return result["nonterminalOperations"] == 0

    await_probe("drain workspace operations", drained, timeout)


def unseal_key(host, streamed=None):
    if streamed is not None:
        key = streamed
    else:
        path = host.secrets / "openbao-unseal-key"
        require(regular(path) and path.stat().st_size <= 1024, "A protected OpenBao unseal key is required.")
        key = path.read_bytes()
    key = key.strip()
    require(re.fullmatch(rb"[A-Za-z0-9+/=_-]{16,512}", key), "Invalid OpenBao unseal key.")
    return key


def start_openbao(run, release, key, *, recreate=False):
    flags = ("--force-recreate",) if recreate else ()
    compose_at(run, release, "start OpenBao", "up", "-d", "--no-deps", "--pull", "never", *flags, "openbao")

    def responding(timeout):
        try:
            return json.loads(bao_exec(run, release, "inspect OpenBao seal", "status", *BAO_OPTIONS,
                                       "-format=json", allowed=(0, 2), timeout=timeout))
        except ValueError:
            return None

    def ready(timeout):
        status = responding(timeout)
        if status is None:
            return False
        require(status["initialized"] is True and type(status["sealed"]) is bool, "Invalid OpenBao seal state.")
        if status["sealed"]:
            require(key is not None, "A protected OpenBao unseal key is required.")
            bao_exec(run, release, "unseal OpenBao", "write", *BAO_OPTIONS, "sys/unseal", "key=-",
                     data=key, output=subprocess.DEVNULL, timeout=timeout)
            return False
        return True

    await_probe("wait for unsealed OpenBao", ready, 180)


def start_services(run, release, *services, recreate=False):
    flags = ("--force-recreate",) if recreate else ()
    compose_at(run, release, "start " + ", ".join(services), "up", "-d", "--no-deps", "--pull", "never",
               "--wait", "--wait-timeout", "180", *flags, *services, timeout=210)


def ready_api(run, release):
    image = assignments(release / ".env")["MUNICODE_CADDY_IMAGE"]

    def ready(timeout):
        try:
            run("probe private API readiness", "docker", "run", "--rm", "--pull=never",
                "--network", "municode_private", "--entrypoint", "wget", image,
                "-qO", "/dev/null", "http://api:8080/health/ready", timeout=timeout)
            return True
        except ValueError:
            return False

    await_probe("wait for private API readiness", ready, 180)


class Journal:
    def __init__(self, host, op, state, run):
        self.host, self.op, self.state, self.run = host, op, state, run
        if isinstance(run, Runner):
            run.log = host.operation(op) / "log"

    def save(self, **fields):
        self.state.update(fields)
        write_state(self.host, self.op, self.state)

    def effect(self, phase, step, action, **markers):
        done = self.state.setdefault("completedSteps", {})
        if step in done:
            return
        self.save(phase=phase, pendingStep=step, **markers)
        result = action() or {}
        self.save(pendingStep=None, completedSteps=done | {step: now()}, **result)


TERMINAL_PHASES = {"committed", "rolled-back"}


def other_operations(host, op):
    for path in sorted(host.operation(op).parent.iterdir()):
        if path.name != op and re.fullmatch(OPERATION, path.name) and not path.is_symlink() and path.is_dir():
            state = read_state(host, path.name)
            if state is not None:
                yield path.name, state


def guard_operation(host, op):
    require(not any(state.get("drainStarted") and state.get("phase") not in TERMINAL_PHASES
                    for _, state in other_operations(host, op)),
            "Another deploy operation is interrupted; resume or roll it back first.")


def operation_jobs(run, op):
    containers = run("list interrupted deploy jobs", "docker", "ps", "-aq", "--no-trunc",
                     "--filter", "label=com.docker.compose.project=municode",
                     "--filter", f"label=com.municode.deploy-operation={op}").splitlines()
    require(all(re.fullmatch(SHA256, item) for item in containers), "Invalid deploy job container ID.")
    if containers:
        run("remove interrupted deploy jobs", "docker", "rm", "-f", *containers)


def guard_recovery(host, state, run):
    previous, target = state["previousRelease"], state["targetRelease"]
    active = object_file(host.state / "active-release.json")
    require(active == previous or active == state.get("committedRelease"),
            "The active release changed since this operation.")
    releases = [str(host.release(item["releaseId"])) for item in (previous, target)]
    # Stopped containers can still be stopped/started through the same Compose project.
    ids = run("list recovery containers", "docker", "ps", "-aq", "--no-trunc",
              "--filter", "label=com.docker.compose.project=municode").splitlines()
    require(ids and all(re.fullmatch(SHA256, item) for item in ids), "No Municode recovery containers found.")
    containers = json.loads(run("inspect recovery containers", "docker", "container", "inspect", *ids))
    require(sorted(c["Id"] for c in containers) == sorted(ids), "Recovery containers changed during inspection.")
    for container in containers:
        labels = container["Config"]["Labels"]
        service = labels.get("com.docker.compose.service")
        # Stopped setup jobs can predate deploys from either release directory.
        if not container["State"].get("Running") and service not in SERVICES:
            continue
        require(labels.get("com.docker.compose.project.working_dir") in releases
                and (service not in OWNERSHIP_SERVICES or labels.get(INSTALLATION_LABEL) == state["installationId"])
                and (service in SERVICES or labels.get("com.municode.deploy-operation") == state["operationId"]),
                "A Compose container is not owned by this deploy operation.")


def stop_services(run, release, *services):
    compose_at(run, release, "stop " + ", ".join(services), "stop", *services, timeout=240)


def write_environment(release, values):
    mode = stat.S_IMODE(release.stat().st_mode)
    if os.geteuid() != 0:
        release.chmod(mode | stat.S_IWUSR)
    try:
        durable_write(release / ".env", render(dict(sorted(values.items()))), 0o400)
    finally:
        if os.geteuid() != 0:
            release.chmod(mode)


def activate(journal, record):
    host = journal.host
    active = host.state / "active-release.json"
    current = host.releases.parent / "current"
    target = host.release(record["releaseId"])
    allowed = {host.release(journal.state[key]["releaseId"]) for key in ("previousRelease", "targetRelease")}
    require(current.is_symlink() and current.resolve() in allowed, "Unexpected active release link.")

    def write_active():
        durable_json(active, record)

    def flip_current():
        link = current.parent / f".current-{journal.op}-{phase}"
        if present(link):
            require(link.is_symlink() and link.readlink() == target, "Unexpected temporary release link.")
        else:
            link.symlink_to(target)
        link.replace(current)
        sync_path(current.parent)

    phase = journal.state["phase"]
    journal.effect(phase, phase + "-active-release", write_active)
    journal.effect(phase, phase + "-current-link", flip_current)


def prune(journal):
    host, current = journal.host, journal.state
    history = [(journal.op, current), *other_operations(host, journal.op)]
    keep = {current[key]["releaseId"] for key in ("previousRelease", "targetRelease")}
    for _, state in history:
        if state.get("phase") not in TERMINAL_PHASES and state is not current and "targetRelease" in state:
            keep.add(state["targetRelease"]["releaseId"])
    for _, state in history:
        if state.get("phase") not in TERMINAL_PHASES:
            continue
        for key in ("targetRelease", "previousRelease"):
            record = state[key]
            release_id = record["releaseId"]
            if release_id in keep:
                continue
            path = host.releases / release_id
            retired = host.releases / f".prune-{journal.op}-{release_id}"
            if present(path):
                release = host.release(release_id)
                proof = release / STAGED_IDENTITY
                expected = {field: record[field] for field in IDENTITY}
                if present(proof):
                    owned = object_file(proof) == expected
                else:
                    manifest = object_file(release / "release-manifest.json")
                    owned = (key == "previousRelease" and
                             identity(manifest, record["bundleSha256"], record["trust"]) == expected)
                require(owned and not present(retired), "Retired release does not match the deployment journal.")
                release.rename(retired)
                sync_path(host.releases)
            remove_release_stage(retired)
    backups = [(state["backupCreatedAt"], op) for op, state in history
               if "backupCreatedAt" in state and (state is current or state.get("phase") in TERMINAL_PHASES)
               and regular(host.backup(op) / "backup-manifest.json")]
    for _, op in sorted(backups, reverse=True)[3:]:
        backup = host.backup(op)
        if object_file(backup / "backup-manifest.json").get("operationId") == op:
            shutil.rmtree(backup)


def rollback_release(journal, key, *, manual=False, restore=False):
    host, op, state, run = journal.host, journal.op, journal.state, journal.run
    previous = state["previousRelease"]
    release = host.release(previous["releaseId"])
    target = host.release(state["targetRelease"]["releaseId"])
    installation = state["installationId"]
    if "rollbackMode" not in state:
        journal.save(phase="rollback", rollbackMode="snapshot" if restore else "services",
                     manualRollback=manual)
    restore = state["rollbackMode"] == "snapshot"
    if restore and "restore-snapshot" not in state["completedSteps"]:
        backup, manifest = read_backup(host, op, previous)
    guard_recovery(host, state, run)
    journal.effect("rollback", "rollback-cleanup-jobs", lambda: operation_jobs(run, op))
    # Recovery may itself have been interrupted while OpenBao was paused.
    journal.effect("rollback", "rollback-unpause", lambda: ensure_unpaused(run, release))
    if restore:
        journal.effect("rollback", "rollback-stop-clients",
                       lambda: stop_services(run, release, *CLIENTS, "openbao-agent", "openbao"))
        if "restore-snapshot" not in state["completedSteps"]:
            if state.get("workerResumed"):
                journal.effect("rollback", "export-reconciliation",
                               lambda: export_reconciliation(run, target, backup, manifest))
            journal.effect("rollback", "rollback-postgres", lambda: start_services(run, release, "postgres"))
            journal.effect("rollback", "restore-snapshot",
                           lambda: restore_backup(host, op, previous, installation, release, run))
    if restore or state.get("clientsMayHaveStopped"):
        journal.effect("rollback", "rollback-openbao", lambda: start_openbao(run, release, key, recreate=restore))
        journal.effect("rollback", "rollback-backend", lambda: start_services(run, release, *BACKEND, recreate=restore))
        journal.effect("rollback", "rollback-readiness", lambda: ready_api(run, release))
        journal.effect("rollback", "rollback-open", lambda: start_services(run, release, "caddy"))
        journal.effect("rollback", "rollback-worker", lambda: start_services(run, release, "worker", recreate=restore))
    journal.effect("rollback", "rollback-exit-maintenance",
                   lambda: require(not maintain(run, release, "exit", op=op)["enabled"],
                                   "Maintenance mode did not exit."))
    if restore:
        activate(journal, previous)
    journal.save(phase="rolled-back", pendingStep=None)


def prepare_apply(journal, plan, approval, streamed):
    host, state, run = journal.host, journal.state, journal.run
    prepared = {}
    inspection = preflight(host, plan, run, prepared)
    if inspection["blockers"]:
        return {"blockers": inspection["blockers"]}
    actual = hashlib.sha256(canonical(inspection["digestInputs"])).hexdigest()
    if actual != approval:
        return {"blockers": ["Approved deploy digest does not match live preflight."], "approvalDigest": actual}
    require(inspection["targetRelease"] == state["targetRelease"],
            "The staged release changed since approval.")
    require(prepared["runningServices"] == SERVICES, "Unexpected running service in the Municode Compose project.")
    previous = inspection["currentRelease"]
    require(previous["releaseId"] != state["targetRelease"]["releaseId"], "The target release is already active.")
    release = host.release(state["targetRelease"]["releaseId"])
    unseal_key(host, streamed)
    require(not present(host.backup(journal.op)), "Backup path already exists.")
    journal.save(phase="prepare", approvalDigest=approval, previousRelease=previous,
                 installationId=plan["installationId"], configHashes=prepared["configHashes"])
    manifest = object_file(release / "release-manifest.json")
    refs, local = allowed_images(release, manifest)
    for ref in sorted(refs - local):
        def pull(ref=ref):
            run("pull pinned image", "docker", "image", "pull", ref, timeout=1800)
        journal.effect("prepare", "pull-" + ref, pull)
    # Rewrite on a prepare retry: recomputation above may reflect updated operator configuration.
    journal.save(phase="prepare", pendingStep="render-env")
    write_environment(release, prepared["values"])
    journal.save(pendingStep=None)
    require(host_config_hashes(host) == state["configHashes"], "Host configuration changed before drain.")
    return {}


def apply_release(journal, plan, approval, streamed, timeout):
    state, run, host, op = journal.state, journal.run, journal.host, journal.op
    require(state.get("approvalDigest", approval) == approval, "Approval does not match the host journal.")
    if state["phase"] in TERMINAL_PHASES:
        return ({"state": state, "blockers": ["This deploy rolled back; create a new plan to retry."]}
                if state["phase"] == "rolled-back" else {"state": state})
    key = None
    try:
        if state.get("rollbackMode") or (state.get("drainStarted") and not state.get("trafficMayHaveOpened")):
            restore = bool(state.get("migrationsStarted"))
            if restore or streamed is not None or present(host.secrets / "openbao-unseal-key"):
                key = unseal_key(host, streamed)
            rollback_release(journal, key, restore=restore)
            return {"state": state, "blockers": ["Interrupted deploy rolled back; create a new plan to retry."]}
        if state.get("trafficMayHaveOpened"):
            guard_recovery(host, state, run)
            if state.get("pendingStep") == "exit-maintenance":
                operation_jobs(run, op)
        else:
            result = prepare_apply(journal, plan, approval, streamed)
            if result:
                return {"state": state, **result}

            active = host.release(state["previousRelease"]["releaseId"])
            release = host.release(state["targetRelease"]["releaseId"])
            key = unseal_key(host, streamed)
            journal.effect("drain", "enter-maintenance",
                           lambda: require(maintain(run, active, "enter", op=op)["enabled"],
                                           "Maintenance mode did not enter."), drainStarted=True)
            journal.effect("drain", "drain-operations", lambda: await_drain(run, active, timeout, op))
            journal.effect("drain", "stop-clients", lambda: stop_services(run, active, *CLIENTS, "openbao-agent"),
                           clientsMayHaveStopped=True)
            journal.effect("backup", "matched-snapshot", lambda: {
                "backupCreatedAt": create_backup(host, op, state["previousRelease"], state["installationId"], active, run)})
            require(host_config_hashes(host) == state["configHashes"], "Host configuration changed during drain or backup.")
            journal.effect("migrate", "target-postgres", lambda: start_services(run, release, "postgres"),
                           migrationsStarted=True)
            for name in JOBS:
                def migrate(name=name):
                    compose_at(run, release, f"run {name}", "run", "--rm", "--no-deps", "-T", "--pull", "never",
                               "--label", f"com.municode.deploy-operation={op}", name, timeout=1800)
                journal.effect("migrate", name, migrate)
            journal.effect("start", "target-openbao", lambda: start_openbao(run, release, key, recreate=True))
            journal.effect("start", "target-backend", lambda: start_services(run, release, *BACKEND))
            journal.effect("start", "target-readiness", lambda: ready_api(run, release))

        release = host.release(state["targetRelease"]["releaseId"])
        journal.effect("open", "open-traffic", lambda: start_services(run, release, "caddy"),
                       trafficMayHaveOpened=True, openAt=state.get("openAt") or now())
        journal.effect("resume", "resume-worker", lambda: start_services(run, release, "worker"), workerResumed=True)
        journal.effect("resume", "exit-maintenance",
                       lambda: require(not maintain(run, release, "exit", op=op)["enabled"],
                                       "Maintenance mode did not exit."))
        if "committedRelease" not in state:
            journal.save(phase="commit", committedRelease=state["targetRelease"] | {
                "activatedAt": now(), "previous": {key: state["previousRelease"][key] for key in (*IDENTITY, "activatedAt")},
            })
        activate(journal, state["committedRelease"])
        journal.effect("commit", "prune", lambda: prune(journal))
        journal.save(phase="committed", pendingStep=None)
        return {"state": state}
    except DEPLOY_ERRORS as error:
        journal.save(lastError={"phase": state["phase"], "step": state.get("pendingStep"),
                                "message": error_message(error)})
        blockers = [error_message(error)]
        if (state.get("drainStarted") and not state.get("trafficMayHaveOpened") and not state.get("rollbackMode")
                and (key is not None or not state.get("migrationsStarted"))):
            try:
                rollback_release(journal, key, restore=bool(state.get("migrationsStarted")))
            except DEPLOY_ERRORS as recovery:
                blockers.append("Rollback incomplete: " + error_message(recovery))
                journal.save(rollbackError=error_message(recovery))
        return {"state": state, "blockers": blockers}


def manual_rollback(journal, args, streamed):
    state, host, op = journal.state, journal.host, journal.op
    try:
        if state["phase"] == "rolled-back":
            return {"state": state}
        require(state.get("drainStarted"), "This operation has not changed the running release.")
        if state.get("trafficMayHaveOpened"):
            require(args.restore_database and args.accept_data_loss_since == state["openAt"],
                    "Rollback requires --restore-database and --accept-data-loss-since matching openAt.")
        if state.get("workerResumed"):
            require(args.accept_provider_reconciliation, "Rollback requires --accept-provider-reconciliation.")
        guard_recovery(host, state, journal.run)
        restore = bool(state.get("migrationsStarted"))
        if restore and "restore-snapshot" not in state["completedSteps"]:
            read_backup(host, op, state["previousRelease"])
        key = (unseal_key(host, streamed) if restore or streamed is not None
               or present(host.secrets / "openbao-unseal-key") else None)
        rollback_release(journal, key, manual=True, restore=restore)
        return {"state": state}
    except DEPLOY_ERRORS as error:
        return {"state": state, "blockers": [error_message(error)]}


def deploy_status(host, op, run):
    state = read_state(host, op)
    require(state is not None, "Deploy operation not found.")
    output = run("inspect deploy unit", "systemctl", "show", f"municode-deploy-{op}.service",
                 "--property=ActiveState,SubState", allowed=(0, 1, 4))
    fields = dict(line.split("=", 1) for line in output.splitlines() if "=" in line)
    return {"state": state, "unit": {"activeState": fields.get("ActiveState", "unknown"),
                                      "subState": fields.get("SubState", "unknown")}}


def main(argv=None, run=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--host-root", type=Path, default=Path("/"))
    commands = parser.add_subparsers(dest="command", required=True)
    staging = commands.add_parser("stage")
    staging.add_argument("--operation", required=True)
    staging.add_argument("--bundle", type=Path, required=True)
    staging.add_argument("--bundle-sha256", required=True)
    staging.add_argument("--trust", choices=("github-sigstore", "local-build"), required=True)
    staging.add_argument("--verified-release-tag")
    inspection = commands.add_parser("preflight")
    inspection.add_argument("--plan", type=Path, required=True)
    apply = commands.add_parser("run")
    apply.add_argument("--approve")
    apply.add_argument("--drain-timeout", type=int, default=900)
    rollback = commands.add_parser("rollback")
    rollback.add_argument("--restore-database", action="store_true")
    rollback.add_argument("--accept-data-loss-since")
    rollback.add_argument("--accept-provider-reconciliation", action="store_true")
    for action in (apply, rollback):
        action.add_argument("--plan", type=Path, required=True)
        action.add_argument("--unseal-key-stdin", action="store_true")
    status = commands.add_parser("status")
    status.add_argument("--operation", required=True)
    for command in (staging, inspection, apply, rollback, status):
        command.add_argument("--json", action="store_true", help="Output JSON (the default).")
    args = parser.parse_args(argv)
    result = {}
    try:
        require(args.host_root.is_absolute() and not any(c in str(args.host_root) for c in "\r\n#$,"),
                "--host-root must be an absolute Compose-safe path.")
        host = Host(args.host_root.resolve())
        require(host.state.is_dir() and host.state.resolve() == host.state
                and host.releases.is_dir() and host.releases.resolve() == host.releases
                and host.config.is_dir() and host.config.resolve() == host.config,
                "Municode deployable layout is missing or not canonical.")
        run = run or Runner()
        if args.command == "status":
            result = deploy_status(host, args.operation, run)
        else:
            streamed = sys.stdin.buffer.readline(1025) if getattr(args, "unseal_key_stdin", False) else None
            if streamed is not None:
                require(streamed.endswith(b"\n") and len(streamed) <= 1024, "Invalid streamed OpenBao unseal key.")
            with deploy_lock(host.state / "deploy.lock"):
                if args.command == "stage":
                    result = stage(host, args.operation, args.bundle, args.bundle_sha256,
                                   args.trust, args.verified_release_tag, run)
                elif args.command == "preflight":
                    result = preflight(host, object_file(args.plan), run)
                else:
                    plan = object_file(args.plan)
                    op = plan["operationId"]
                    state = read_state(host, op)
                    require(state is not None and state["phase"] != "staging", "Deploy operation has not been staged.")
                    guard_operation(host, op)
                    state["operationId"] = op
                    journal = Journal(host, op, state, run)
                    if args.command == "run":
                        approval = args.approve or plan.get("approvalDigest")
                        require(isinstance(approval, str) and re.fullmatch(SHA256, approval),
                                "A SHA-256 deploy approval digest is required.")
                        require(args.drain_timeout > 0, "Drain timeout must be positive.")
                        result = apply_release(journal, plan, approval, streamed, args.drain_timeout)
                    else:
                        result = manual_rollback(journal, args, streamed)
    except DEPLOY_ERRORS as error:
        result = {"blockers": [error_message(error)]}
    print(json.dumps(result, sort_keys=True))
    return 1 if result.get("blockers") else 0


if __name__ == "__main__":
    sys.exit(main())
