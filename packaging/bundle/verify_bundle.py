#!/usr/bin/env python3
# SPDX-License-Identifier: MPL-2.0
"""Verify component digests inside an extracted Municode release."""

import hashlib
import json
from pathlib import Path
import re
import stat
import sys


def fail(message):
    raise ValueError(message)


def digest(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def verify_env_contract(root, contract):
    template = {}
    for line in (root / ".env.example").read_text().splitlines():
        if not line.strip() or line.lstrip().startswith("#"):
            continue
        key, separator, value = line.partition("=")
        if not separator or not re.fullmatch(r"[A-Z][A-Z0-9_]*", key) or key in template:
            fail(f"Invalid or duplicate environment assignment: {key}")
        template[key] = value
    if not isinstance(contract, dict) or contract.keys() != template.keys():
        fail("Environment contract keys do not match .env.example.")
    for key, entry in contract.items():
        if (not isinstance(entry, dict) or entry.keys() - {"owner", "default"}
                or entry.get("owner") not in ("release", "installer", "operator")
                or ("default" in entry and (entry["owner"] != "operator" or entry["default"] != template[key]))):
            fail(f"Invalid environment contract entry: {key}")
    return template


def verify_deploy_contract(root, manifest):
    if type(manifest.get("deployProtocol")) is not int or manifest["deployProtocol"] != 1:
        fail("Unsupported deploy protocol.")
    if not re.fullmatch(r"(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)"
                        r"(?:-[0-9A-Za-z]+(?:[.-][0-9A-Za-z]+)*)?", manifest["version"]):
        fail("Invalid release version.")
    if not re.fullmatch(r"[0-9a-f]{40}|[0-9a-f]{64}", manifest["sourceRevision"]):
        fail("Invalid release source revision.")
    migrations = manifest["migrations"]
    if not isinstance(migrations, dict) or migrations.keys() != {"postgres", "sqlite"}:
        fail("Expected Postgres and SQLite migration inventories.")
    for provider, ids in migrations.items():
        if (not isinstance(ids, list) or not ids
                or any(not isinstance(id, str) or not re.fullmatch(r"[0-9]{14}_[A-Za-z0-9_]+", id) for id in ids)
                or ids != sorted(set(ids))):
            fail(f"Invalid ordered migration inventory: {provider}")
    contract = json.loads((root / "env-contract.json").read_text())
    template = verify_env_contract(root, contract)
    if manifest.get("envContract") != contract:
        fail("Manifest environment contract differs from env-contract.json.")
    if {"imageArchives", "imagePlatform", "sourceDirty"} & manifest.keys():
        verify_local_images(root, manifest, template, contract)


def verify_local_images(root, manifest, template, contract):
    if type(manifest.get("sourceDirty")) is not bool:
        fail("Missing local source checkout status.")
    if manifest.get("imagePlatform") not in ("linux/amd64", "linux/arm64"):
        fail("Invalid local image platform.")
    images = manifest.get("imageArchives")
    if not isinstance(images, dict) or images.keys() != {"api", "worker", "gateway"}:
        fail("Incomplete local image archive inventory.")
    image_directory = root / "images"
    if {entry.name for entry in image_directory.iterdir()} != {f"{name}.tar" for name in images}:
        fail("Local image archive directory differs from its manifest.")
    env = {"MUNICODE_RUNTIME_PULL_POLICY": "never"}
    for name, image in images.items():
        relative = f"images/{name}.tar"
        image_id = image["imageId"]
        if image["path"] != relative or not re.fullmatch(r"sha256:[0-9a-f]{64}", image_id):
            fail(f"Invalid local image metadata: {name}")
        reference = f"municode-{name}:local-{image_id.removeprefix('sha256:')}"
        if image["reference"] != reference:
            fail(f"Invalid local image reference: {name}")
        path = root / relative
        if not stat.S_ISREG(path.stat().st_mode) or digest(path) != image["sha256"]:
            fail(f"Local image archive digest mismatch: {name}")
        env[f"MUNICODE_{name.upper()}_IMAGE"] = reference
    for key, value in env.items():
        if template.get(key) != value or contract.get(key) != {"owner": "release"}:
            fail(f"Invalid local image environment setting: {key}")


def main(root=None):
    if root is None:
        root = Path(__file__).resolve().parent
    for entry in root.rglob("*"):
        if entry.is_symlink():
            fail(f"Symlinks are forbidden in a release: {entry}")
    manifest = json.loads((root / "release-manifest.json").read_text())
    if manifest.get("schemaVersion") != 1 or manifest.get("product") != "municode":
        fail("Unexpected release manifest.")
    verify_deploy_contract(root, manifest)
    agent = root / "deploy_host.py"
    if not agent.is_file() or manifest.get("hostAgent") != {"path": agent.name, "sha256": digest(agent)}:
        fail("Host deploy agent inventory mismatch.")
    roots = {
        "api": root / "runtime" / "api",
        "worker": root / "runtime" / "worker",
        "gateway": root / "runtime" / "gateway",
        "dashboard": root / "dashboard",
        "cli": root / "cli",
    }
    for component, component_root in roots.items():
        expected = manifest["components"][component]
        actual = []
        for entry in sorted(component_root.rglob("*")):
            if entry.is_file():
                actual.append(
                    {"path": entry.relative_to(component_root).as_posix(),
                     "sha256": digest(entry),
                     "size": entry.stat().st_size,
                     "executable": bool(entry.stat().st_mode & stat.S_IXUSR)}
                )
                if not stat.S_ISREG(entry.stat().st_mode):
                    fail(f"Non-regular release file: {entry}")
        if actual != expected["files"]:
            fail(f"Component inventory mismatch: {component}")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (OSError, TypeError, ValueError, KeyError, json.JSONDecodeError) as error:
        print(f"Release verification failed: {error}", file=sys.stderr)
        raise SystemExit(1)
