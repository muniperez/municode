#!/usr/bin/env python3
# SPDX-License-Identifier: MPL-2.0
"""Install the first Municode release; refuse to change an active release."""

import argparse
from contextlib import contextmanager
from datetime import datetime, timezone
import fcntl
import ipaddress
import json
import os
from pathlib import Path
import re
import secrets
import shlex
import shutil
import subprocess
import sys
import tempfile

from verify_bundle import main as verify_bundle


SOURCE = Path(__file__).resolve().parent
HOSTNAME = r"[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?(?:\.[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?)+"


def present(path):
    return path.exists() or path.is_symlink()


def regular(path):
    return path.is_file() and not path.is_symlink()


def atomic_write(path, data, mode=0o600):
    with tempfile.NamedTemporaryFile(dir=path.parent, prefix=f".{path.name}.", delete=False) as stream:
        temporary = Path(stream.name)
        try:
            stream.write(data)
            stream.close()
            temporary.chmod(mode)
            temporary.replace(path)
        finally:
            temporary.unlink(missing_ok=True)


def write_json(path, value):
    atomic_write(path, (json.dumps(value, indent=2) + "\n").encode())


def directory(path, mode):
    path.mkdir(mode=mode, parents=True, exist_ok=True)
    if path.is_symlink():
        raise ValueError(f"Installation directory must not be a symlink: {path}")


@contextmanager
def deploy_lock(path, *, create=False):
    flags = os.O_NOFOLLOW | os.O_NONBLOCK | (os.O_WRONLY | os.O_CREAT if create else os.O_RDONLY)
    if present(path) and not regular(path):
        raise ValueError("Deploy lock must be a regular file.")
    with os.fdopen(os.open(path, flags, 0o600), "a" if create else "r") as lock:
        try:
            fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
        except BlockingIOError as error:
            raise ValueError("Another Municode installation or deploy holds the deploy lock.") from error
        yield


def freeze_release(path):
    for entry in path.rglob("*"):
        if entry != path / ".env":
            entry.chmod(0o555 if entry.is_dir() or entry.stat().st_mode & 0o100 else 0o444)
    path.chmod(0o555)


def assignments(path):
    values = {}
    for number, line in enumerate(path.read_text().splitlines(), 1):
        if not line.strip() or line.lstrip().startswith("#"):
            continue
        key, separator, value = line.partition("=")
        if not separator or not re.fullmatch(r"[A-Z][A-Z0-9_]*", key) or key in values:
            raise ValueError(f"Invalid or duplicate assignment in {path.name} at line {number}.")
        values[key] = value
    return values


def render(values):
    return "".join(f"{key}={value}\n" for key, value in values.items()).encode()


def seed_settings(args):
    fields = (args.installation_id, args.dashboard_hostname, args.api_hostname, args.gateway_domain)
    if all(field is None for field in fields):
        return {}
    installation, dashboard, api, gateway = fields
    if (not (args.verified_release_tag or args.local_build) or not installation
            or not re.fullmatch(r"[a-zA-Z0-9._-]+", installation)
            or not all(host and re.fullmatch(HOSTNAME, host) for host in (dashboard, api, gateway))):
        raise ValueError("Seed requires a verified tag or an explicit local build, an installation ID "
                         "and all three public hostnames.")
    return {
        "MUNICODE_INSTALLATION_ID": installation,
        "CONSOLE_HOSTNAME": dashboard,
        "CONSOLE_URL": "https://" + dashboard,
        "API_HOSTNAME": api,
        "API_URL": "https://" + api,
        "MUNICODE_GATEWAY_DOMAIN": gateway,
    }


def configure_operator(path, defaults, required):
    if present(path):
        if not regular(path):
            raise ValueError(f"Operator environment must be a regular file: {path}")
        values = assignments(path)
        if values.keys() - defaults.keys():
            raise ValueError(f"Non-operator keys in {path}: {', '.join(sorted(values.keys() - defaults.keys()))}")
        if any(values.get(key) != value for key, value in required.items()):
            raise ValueError("Existing operator environment does not match the seed configuration.")
        values = defaults | values
    else:
        values = defaults | required
        atomic_write(path, render(values))
    return values


def create_secrets(root):
    root.mkdir()
    for name in ("ssh", "bootstrap", "openbao-approle"):
        (root / name).mkdir()
    for name in ("role-id", "secret-id"):
        (root / "openbao-approle" / name).touch()
    for name in ("postgres-admin-password", "app-db-password", "kratos-db-password",
                 "workspace-gateway-credential"):
        (root / name).write_text(secrets.token_hex(32) + "\n")
    kratos_password = (root / "kratos-db-password").read_text().strip()
    (root / "kratos.env").write_text(
        f"DSN=postgres://kratos:{kratos_password}@postgres:5432/kratos?sslmode=disable\n"
        f"SECRETS_COOKIE={secrets.token_hex(32)}\n"
        f"SECRETS_CIPHER={secrets.token_urlsafe(24)}\n"
    )
    subprocess.run(["ssh-keygen", "-q", "-t", "ed25519", "-N", "", "-f", root / "ssh/worker-key"], check=True)
    (root / "ssh/worker-key.pub").chmod(0o644)
    subprocess.run(
        ["openssl", "req", "-x509", "-newkey", "rsa:3072", "-sha256", "-nodes", "-days", "3650",
         "-subj", "/CN=openbao", "-addext", "subjectAltName=DNS:openbao",
         "-keyout", root / "openbao-server.key", "-out", root / "openbao-server.crt"],
        stdout=subprocess.DEVNULL, stderr=subprocess.PIPE, text=True, check=True,
    )
    shutil.copyfile(root / "openbao-server.crt", root / "openbao-ca.crt")
    for name in ("openbao-server.crt", "openbao-ca.crt"):
        (root / name).chmod(0o644)


def compose_wrapper(current, releases):
    return f"""#!/bin/sh
set -eu
if ! release="$(readlink -e -- {shlex.quote(str(current))})" ||
   [ "$(dirname -- "$release")" != {shlex.quote(str(releases))} ] || [ ! -f "$release/.env" ]; then
    echo "The active Municode release is missing or invalid." >&2
    exit 1
fi
while IFS='=' read -r key value; do
    case "$key" in ''|[!A-Z]*|*[!A-Z0-9_]*) continue ;; esac
    unset "$key"
done < "$release/.env"
exec docker compose --project-name municode --project-directory "$release" \\
    --env-file "$release/.env" -f "$release/compose.yml" "$@"
""".encode()


def remove_release_stage(stage):
    if stage.is_dir() and not stage.is_symlink():
        for path in (stage, *stage.rglob("*")):
            if path.is_dir() and not path.is_symlink():
                path.chmod(0o700)
        shutil.rmtree(stage)
    elif present(stage):
        raise ValueError(f"Invalid release staging directory: {stage}")


def management_address(value):
    try:
        network = ipaddress.ip_network(value, strict=True)
    except ValueError as error:
        raise argparse.ArgumentTypeError("--management-address must be one exact /32 or /128 CIDR.") from error
    if (network.prefixlen != network.max_prefixlen or network.network_address.is_loopback
            or network.network_address.is_unspecified):
        raise argparse.ArgumentTypeError("--management-address must be one host /32 or /128 CIDR.")
    return str(network)


WORKSPACE_DEFAULT_KEYS = {"Currency", "VolumeMonthlyRatePerGb", "MaxPricingAgeDays", "Profiles", "Images"}


def workspace_defaults(path):
    """Read seed's default Ubuntu workspace policy: one stock image and the profiles that use its default plan."""
    value = json.loads(path.read_text())
    if (not isinstance(value, dict) or set(value) != WORKSPACE_DEFAULT_KEYS
            or not all(isinstance(value[k], dict) and value[k] for k in ("Profiles", "Images"))
            or not all(isinstance(v, dict) for k in ("Profiles", "Images") for v in value[k].values())
            or any(image.get("Source") != "ubuntu" for image in value["Images"].values())):
        raise ValueError("--workspace-defaults must hold seed's stock Ubuntu image, profiles and pricing.")
    return value


def initial_policy(source, args):
    """Seed the bundled policy with the control-plane management address and seed's default workspace path;
    operator policies are copied as-is."""
    content = source.read_bytes()
    if args.policy_file or not (args.management_address or args.workspace_defaults):
        return content
    value = json.loads(content)
    if args.management_address:
        worker = value.setdefault("DevelopmentWorkspaceWorker", {})
        if not worker.get("ManagementAddresses"):
            worker["ManagementAddresses"] = [args.management_address]
    if args.workspace_defaults:
        defaults = workspace_defaults(args.workspace_defaults)
        # Stamped with the host clock that the API compares against, so the image is eligible immediately.
        now = datetime.now(timezone.utc).isoformat(timespec="seconds").replace("+00:00", "Z")
        for image in defaults["Images"].values():
            image["QualifiedAt"] = now
        workspaces = value.setdefault("DevelopmentWorkspaces", {})
        workspaces.update(defaults, PricingUpdatedAt=now)
    return (json.dumps(value, indent=2) + "\n").encode()


def validate_secrets(root):
    for name in ("postgres-admin-password", "app-db-password", "kratos-db-password",
                 "workspace-gateway-credential", "kratos.env", "ssh/worker-key", "ssh/worker-key.pub",
                 "openbao-server.crt", "openbao-server.key", "openbao-ca.crt",
                 "openbao-approle/role-id", "openbao-approle/secret-id"):
        path = root / name
        if not regular(path) or (not name.startswith("openbao-approle/") and path.stat().st_size == 0):
            raise ValueError(f"Missing or incomplete installation secret: {path}")


def install(args, identity, manifest, required):
    root = args.host_root
    base = root / "opt/municode"
    releases = base / "releases"
    current = base / "current"
    release = releases / identity["releaseId"]
    stage = releases / ".install-pending"
    config = root / "etc/municode/config"
    operator = config / "operator.env"
    policy = config / "municode.json"
    secret_dir = root / "etc/municode/secrets"
    secret_stage = secret_dir.with_name(".install-secrets-pending")
    state = root / "var/lib/municode"
    active = state / "active-release.json"
    journal = state / "install-pending.json"
    wrapper = root / "usr/local/sbin/municode-compose"
    expected_link = Path("releases") / release.name
    template = assignments(SOURCE / ".env.example")
    contract = manifest["envContract"]
    wrapper_content = compose_wrapper(current, releases)
    defaults = {k: entry.get("default", template[k]) for k, entry in contract.items()
                if entry["owner"] == "operator"}
    release_values = {k: template[k] for k, entry in contract.items() if entry["owner"] == "release"}
    release_values.update(MUNICODE_RELEASE_TAG=identity["tag"],
                          MUNICODE_RELEASE_SOURCE_REVISION=identity["sourceRevision"],
                          MUNICODE_RELEASE_TRUST=identity["trust"])
    installer_keys = {k for k, entry in contract.items() if entry["owner"] == "installer"}
    defaults["MUNICODE_POLICY_FILE"] = str(policy)
    if required:
        required = required | {"MUNICODE_POLICY_FILE": str(policy)}
    for path, mode in ((releases, 0o755), (config, 0o700), (state, 0o700), (wrapper.parent, 0o755)):
        directory(path, mode)

    with deploy_lock(state / "deploy.lock", create=True):
        if present(active):
            if not regular(active):
                raise ValueError("Active release record must be a regular file.")
            record = json.loads(active.read_text())
            if (not isinstance(record, dict) or any(record.get(k) != v for k, v in identity.items())
                    or not record.get("activatedAt") or not current.is_symlink()
                    or current.resolve() != release or not regular(release / ".env")
                    or not regular(wrapper) or not regular(operator) or not regular(policy)
                    or secret_dir.is_symlink()):
                raise ValueError("Active installation differs or is incomplete; install.sh cannot upgrade or repair it.")
            if required:
                configured, snapshot = assignments(operator), assignments(release / ".env")
                if any(configured.get(k) != v or snapshot.get(k) != v for k, v in required.items()):
                    raise ValueError("Active operator environment does not match the seed configuration.")
            verify_bundle(release)
            validate_secrets(secret_dir)
            if regular(journal):
                journal.unlink()
            return release

        for path in (state / "deploys", state / "backups"):
            directory(path, 0o700)
        installation = {"identity": identity, "seed": required}
        if present(journal):
            if not regular(journal):
                raise ValueError("Invalid pending installation record.")
            pending = json.loads(journal.read_text())
            phase = pending.get("phase") if isinstance(pending, dict) else None
            if phase not in ("staging", "promotion") or pending != installation | {"phase": phase}:
                raise ValueError("Pending installation belongs to a different release or seed configuration.")
        else:
            if any(map(present, (current, wrapper, release, secret_dir, stage, secret_stage))):
                raise ValueError("Unowned installation paths exist; refusing to adopt or delete them.")
            phase = "staging"
            write_json(journal, installation | {"phase": phase})

        if phase == "staging":
            if any(map(present, (current, wrapper, release, secret_dir))):
                raise ValueError("Pending installation conflicts with unexpected final paths.")
            remove_release_stage(stage)
            if present(secret_stage):
                shutil.rmtree(secret_stage)
            if not present(policy):
                policy_source = args.policy_file or SOURCE / "municode.json"
                if not regular(policy_source):
                    raise ValueError("Input policy must be a regular file.")
                atomic_write(policy, initial_policy(policy_source, args), 0o644)
            elif not regular(policy):
                raise ValueError("Existing policy must be a regular file.")
            elif args.policy_file and args.policy_file.read_bytes() != policy.read_bytes():
                raise ValueError("Existing policy differs from the requested input; refusing to overwrite it.")
            operator_values = configure_operator(operator, defaults, required)
            if operator_values["MUNICODE_POLICY_FILE"] != str(policy):
                raise ValueError(f"MUNICODE_POLICY_FILE must point to {policy}.")
            create_secrets(secret_stage)
            shutil.copytree(SOURCE, stage)
            installer_values = {
                "MUNICODE_SECRETS_DIR": str(secret_dir),
                "MUNICODE_WORKER_SSH_PUBLIC_KEY": (secret_stage / "ssh/worker-key.pub").read_text().strip(),
            }
            if installer_values.keys() != installer_keys:
                raise ValueError("Release contract does not match the installer-owned keys.")
            values = release_values | installer_values | operator_values
            stage.chmod(0o700)
            atomic_write(stage / ".env", render({key: values[key] for key in template}), 0o400)
            freeze_release(stage)
            write_json(journal, installation | {"phase": "promotion"})

        promotions = []
        for staged, final, proof, validate in ((secret_stage, secret_dir, "ssh/worker-key", validate_secrets),
                                               (stage, release, ".env", verify_bundle)):
            candidate = final if present(final) else staged
            if (candidate.is_symlink() or not regular(candidate / proof)
                    or (candidate == final and present(staged))):
                raise ValueError(f"Pending installation cannot promote {final}; preserving state.")
            if phase == "promotion":
                validate(candidate)
            if candidate == staged:
                promotions.append((staged, final))
        for staged, final in promotions:
            staged.rename(final)
        if present(wrapper):
            if not regular(wrapper) or wrapper.read_bytes() != wrapper_content:
                raise ValueError(f"Unexpected Compose wrapper: {wrapper}")
        else:
            atomic_write(wrapper, wrapper_content, 0o755)
        if present(current):
            if not current.is_symlink() or current.readlink() != expected_link:
                raise ValueError(f"Unexpected active release link: {current}")
        else:
            current.symlink_to(expected_link)
        activated = datetime.now(timezone.utc).isoformat(timespec="seconds").replace("+00:00", "Z")
        write_json(active, identity | {"activatedAt": activated, "previous": None})
        journal.unlink()
        return release


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--bundle-sha256", required=True, help="SHA-256 of the verified .tar.gz bundle")
    parser.add_argument("--host-root", type=Path, default=Path("/"))
    parser.add_argument("--policy-file", type=Path, help="Input policy to copy into /etc/municode/config")
    parser.add_argument("--verified-release-tag", help="Tag whose Sigstore provenance was already verified")
    parser.add_argument("--local-build", action="store_true",
                        help="Install an explicitly trusted, clean unsigned local build with bundled images")
    parser.add_argument("--management-address", type=management_address,
                        help="Control-plane public /32 or /128 added to a newly created bundled policy")
    parser.add_argument("--workspace-defaults", type=Path,
                        help="Seed's default Ubuntu workspace image, profile and pricing for a newly created bundled policy")
    for option in ("installation-id", "dashboard-hostname", "api-hostname", "gateway-domain"):
        parser.add_argument("--" + option, help=argparse.SUPPRESS)
    args = parser.parse_args()
    if not args.host_root.is_absolute() or any(c in str(args.host_root) for c in "\r\n#$"):
        parser.error("--host-root must be an absolute Compose-safe path.")
    args.host_root = args.host_root.resolve()
    if not re.fullmatch(r"[0-9a-f]{64}", args.bundle_sha256):
        parser.error("--bundle-sha256 must be 64 lowercase hexadecimal characters.")
    verify_bundle()
    manifest = json.loads((SOURCE / "release-manifest.json").read_text())
    revision = manifest["sourceRevision"]
    if not re.fullmatch(r"[0-9a-f]{40}", revision):
        raise ValueError("Expected a 40-character lowercase source revision.")
    tag = "municode-v" + manifest["version"]
    if args.verified_release_tag is not None and args.verified_release_tag != tag:
        raise ValueError("Verified release tag does not match the manifest.")
    if args.local_build and (args.verified_release_tag is not None or manifest.get("sourceDirty") is not False
                             or "imageArchives" not in manifest):
        raise ValueError("A local-build install requires a clean local bundle with image archives and no signed tag.")
    signed = args.verified_release_tag is not None
    identity = {
        "releaseId": tag if signed else f"local-{revision[:12]}-{args.bundle_sha256[:12]}",
        "tag": tag,
        "sourceRevision": revision,
        "bundleSha256": args.bundle_sha256,
        "trust": "github-sigstore" if signed else "local-build",
    }
    release = install(args, identity, manifest, seed_settings(args))
    print(f"Installed release at {release}.")


if __name__ == "__main__":
    try:
        main()
    except (OSError, ValueError, TypeError, KeyError, subprocess.CalledProcessError) as error:
        detail = error.stderr.strip() if isinstance(error, subprocess.CalledProcessError) and error.stderr else error
        sys.exit(f"Municode installation failed: {detail}")
