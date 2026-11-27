#!/usr/bin/env python3
# SPDX-License-Identifier: MPL-2.0
"""Stage and inspect a Municode release on an installed host."""

import argparse
from dataclasses import dataclass
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
    write_json(path / "state.json", state)


class Runner:
    def __call__(self, step, *argv, allowed=(0,), timeout=120):
        # Do not let SSH environment variables select a different daemon or override Compose inputs.
        env = {key: os.environ[key] for key in ("PATH", "HOME") if key in os.environ}
        env["DOCKER_HOST"] = "unix:///var/run/docker.sock"
        try:
            result = subprocess.run(argv, stdin=subprocess.DEVNULL, capture_output=True, text=True, errors="replace",
                                    env=env, timeout=timeout)
        except subprocess.TimeoutExpired as error:
            raise ValueError(f"{step} timed out.") from error
        require(result.returncode in allowed, f"{step} failed (exit {result.returncode}).")
        return result.stdout.strip()


def compose(run, step, release, env, *args):
    return run(step, "docker", "compose", "--project-name", "municode",
               "--project-directory", str(release), "--env-file", str(env),
               "-f", str(release / "compose.yml"), *args)


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


def preflight(host, plan, run):
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
    require(config_hashes == {"policySha256": digest(policy_path), "operatorEnvSha256": digest(operator_path)},
            "Host configuration changed during preflight; retry the plan.")
    effective = hashlib.sha256(canonical(config_hashes | {"compose": config})).hexdigest()
    result["effectiveConfigHash"] = effective
    result["digestInputs"] = {
        **{key: plan[key] for key in ("instanceName", "installationId", "hostFingerprint")},
        "currentRelease": {key: active[key] for key in (*IDENTITY, "activatedAt")},
        "targetRelease": target, "deployProtocol": PROTOCOL, "effectiveConfigHash": effective,
        "envDiff": diff, "appliedMigrations": applied, "pendingMigrations": migrations["pending"], "images": images,
    }
    return result


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
    for command in (staging, inspection):
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
        with deploy_lock(host.state / "deploy.lock"):
            run = run or Runner()
            if args.command == "stage":
                result = stage(host, args.operation, args.bundle, args.bundle_sha256,
                               args.trust, args.verified_release_tag, run)
            else:
                result = preflight(host, object_file(args.plan), run)
    except (OSError, ValueError, TypeError, KeyError, AttributeError, EOFError, tarfile.TarError) as error:
        if isinstance(error, (KeyError, TypeError, AttributeError, json.JSONDecodeError)):
            message = "Malformed deploy input or probe response."
        elif isinstance(error, OSError):
            message = f"Deploy filesystem or command access failed (errno {error.errno})."
        elif isinstance(error, (EOFError, tarfile.TarError)):
            message = "Invalid or incomplete bundle archive."
        else:
            message = str(error)
        result = {"blockers": [message]}
    print(json.dumps(result, sort_keys=True))
    return 1 if result.get("blockers") else 0


if __name__ == "__main__":
    sys.exit(main())
