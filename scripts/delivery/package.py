#!/usr/bin/env python3
# SPDX-License-Identifier: MPL-2.0
"""Assemble and verify a self-contained Municode Workspaces release."""

from __future__ import annotations

import argparse
import gzip
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import re
import shutil
import stat
import tarfile
import tempfile


ROOT = Path(__file__).resolve().parents[2]
BUNDLE = ROOT / "packaging" / "bundle"
TARGETS = ROOT / "packaging" / "targets.json"
REQUIRED_BACKEND_FILES = {
    "api": "Municode.Api.dll",
    "worker": "Municode.Worker.dll",
    "gateway": "Municode.Gateway.dll",
}


class PackageError(Exception):
    pass


def require(condition: bool, message: str) -> None:
    if not condition:
        raise PackageError(message)


def file_digest(path: Path) -> str:
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def reject_links(root: Path) -> None:
    require(root.exists(), f"Missing component payload: {root}")
    require(not root.is_symlink(), f"Component root must not be a symlink: {root}")
    for path in root.rglob("*"):
        require(not path.is_symlink(), f"Symlink payloads are forbidden: {path}")


def copy_tree(source: Path, destination: Path) -> None:
    reject_links(source)
    destination.parent.mkdir(parents=True, exist_ok=True)
    shutil.copytree(source, destination, symlinks=False)


def component_inventory(root: Path) -> dict[str, object]:
    files: list[dict[str, object]] = []
    for path in sorted(root.rglob("*")):
        if path.is_dir():
            continue
        info = path.stat()
        require(stat.S_ISREG(info.st_mode), f"Payload entry is not a regular file: {path}")
        relative = path.relative_to(root).as_posix()
        files.append(
            {
                "path": relative,
                "sha256": file_digest(path),
                "size": info.st_size,
                "executable": bool(info.st_mode & stat.S_IXUSR),
            }
        )
    return {
        "files": files,
        "sha256": hashlib.sha256(
            "".join(f"{item['sha256']}  {item['path']}\n" for item in files).encode()
        ).hexdigest(),
    }


def add_cli_aliases(root: Path, targets: dict[str, object]) -> None:
    for rid in targets["components"]["cli"]["rids"]:
        directory = root / rid
        if not directory.exists():
            continue
        if rid == "win-x64":
            binary = directory / "municode.exe"
            alias = directory / "workspaces.cmd"
            if binary.is_file() and not alias.exists():
                alias.write_bytes(b'@echo off\r\n"%~dp0municode.exe" %*\r\n')
        else:
            binary = directory / "municode"
            alias = directory / "workspaces"
            if binary.is_file() and not alias.exists():
                alias.write_text('#!/bin/sh\nexec "$(dirname "$0")/municode" "$@"\n')
                alias.chmod(0o755)


def validate_targets() -> dict[str, object]:
    targets = json.loads(TARGETS.read_text())
    require(targets.get("schemaVersion") == 1, "Unsupported package target schema.")
    for component in ("api", "worker", "gateway", "dashboard", "cli"):
        require(component in targets["components"], f"Missing package target: {component}")
    return targets


def validate_release() -> dict[str, object]:
    release = json.loads((ROOT / "packaging" / "release.json").read_text())
    require(release.get("schemaVersion") == 1, "Unsupported release metadata schema.")
    require(release.get("product") == "municode-workspaces", "Unexpected release product.")
    images = release.get("images")
    require(isinstance(images, dict) and images, "Release image inventory is empty.")
    for name, image in images.items():
        require(
            isinstance(image, str)
            and re.fullmatch(r"[^ \t\r\n]+@sha256:(?!0{64}$)[0-9a-f]{64}", image),
            f"Release image {name} must use an immutable digest.",
        )
    return release


def validate_identity(args: argparse.Namespace) -> None:
    require(
        re.fullmatch(
            r"(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)"
            r"(?:-[0-9A-Za-z]+(?:[.-][0-9A-Za-z]+)*)?",
            args.version,
        ),
        "Version must be an exact semantic version.",
    )
    require(
        re.fullmatch(r"[0-9a-f]{40}|[0-9a-f]{64}", args.source),
        "Source must be an exact Git object ID.",
    )
    require(
        re.fullmatch(r"[0-9A-Za-z][0-9A-Za-z._-]{0,63}", args.rid),
        "RID must be a safe package identifier.",
    )


def validate_payloads(args: argparse.Namespace) -> dict[str, tuple[str, Path]]:
    components = {
        "api": ("runtime/api", args.api),
        "worker": ("runtime/worker", args.worker),
        "gateway": ("runtime/gateway", args.gateway),
        "dashboard": ("dashboard", args.dashboard),
        "cli": ("cli", args.cli),
    }
    for name, (_, source) in components.items():
        reject_links(source)
        require(source.is_dir(), f"{name} payload must be a directory: {source}")
    for name, binary in REQUIRED_BACKEND_FILES.items():
        require(
            (components[name][1] / binary).is_file(),
            f"{name.capitalize()} payload lacks {binary}.",
        )
    require((args.dashboard / "index.html").is_file(), "Dashboard payload lacks index.html.")
    require(
        any(path.is_file() for path in args.cli.rglob("*")),
        f"cli payload is empty: {args.cli}",
    )
    require(
        any(path.is_file() and path.stat().st_mode & stat.S_IXUSR for path in args.cli.rglob("*")),
        "CLI payload lacks an executable.",
    )
    return components


def deterministic_archive(staging: Path, archive: Path) -> None:
    with archive.open("wb") as raw:
        with gzip.GzipFile(fileobj=raw, mode="wb", mtime=0, filename="") as compressed:
            with tarfile.open(fileobj=compressed, mode="w", format=tarfile.PAX_FORMAT) as bundle:
                for path in sorted(staging.rglob("*")):
                    relative = PurePosixPath(staging.name) / path.relative_to(staging).as_posix()
                    info = bundle.gettarinfo(str(path), arcname=str(relative))
                    info.uid = info.gid = 0
                    info.uname = info.gname = "root"
                    info.mtime = 0
                    if info.isfile():
                        info.mode = 0o755 if path.stat().st_mode & stat.S_IXUSR else 0o644
                        with path.open("rb") as stream:
                            bundle.addfile(info, stream)
                    else:
                        bundle.addfile(info)


def package(args: argparse.Namespace) -> Path:
    validate_identity(args)
    targets = validate_targets()
    release = validate_release()
    components = validate_payloads(args)
    args.output.mkdir(parents=True, exist_ok=True)
    archive_name = f"municode-workspaces-{args.version}-{args.rid}.tar.gz"
    archive = args.output / archive_name
    manifest_path = args.output / "release-manifest.json"
    checksums_path = args.output / "checksums.txt"
    require(
        not archive.exists() and not manifest_path.exists() and not checksums_path.exists(),
        "Release output already exists; select an empty ignored artifacts directory.",
    )

    with tempfile.TemporaryDirectory(prefix="workspaces-package-") as temporary:
        root = Path(temporary) / f"municode-workspaces-{args.version}"
        copy_tree(BUNDLE, root)
        (root / "docs").mkdir()
        for name in ("operator.md", "user.md"):
            shutil.copy2(ROOT / "docs" / name, root / "docs" / name)

        inventories: dict[str, object] = {}
        for name, (relative, source) in components.items():
            destination = root / relative
            copy_tree(source, destination)
            if name == "cli":
                add_cli_aliases(destination, targets)
            inventories[name] = component_inventory(destination)

        manifest = {
            "schemaVersion": 1,
            "product": "municode-workspaces",
            "version": args.version,
            "rid": args.rid,
            "sourceRepository": "https://github.com/muniperez/municode",
            "sourceRevision": args.source,
            "targets": targets["components"],
            "dependencies": release["images"],
            "components": inventories,
            "bundle": archive_name,
        }
        (root / "release-manifest.json").write_text(
            json.dumps(manifest, indent=2, sort_keys=True) + "\n"
        )
        deterministic_archive(root, archive)

    (manifest_path).write_text(json.dumps(manifest, indent=2, sort_keys=True) + "\n")
    checksums_path.write_text(
        "".join(
            f"{file_digest(path)}  {path.name}\n"
            for path in sorted((manifest_path, archive), key=lambda item: item.name)
        )
    )
    return archive


def parser() -> argparse.ArgumentParser:
    result = argparse.ArgumentParser(description=__doc__)
    result.add_argument("--version", required=True)
    result.add_argument("--source", required=True)
    result.add_argument("--rid", required=True, help="Archive runtime, for example linux-x64.")
    result.add_argument("--api", type=Path, required=True)
    result.add_argument("--worker", type=Path, required=True)
    result.add_argument("--gateway", type=Path, required=True)
    result.add_argument("--dashboard", type=Path, required=True)
    result.add_argument("--cli", type=Path, required=True)
    result.add_argument("--output", type=Path, required=True)
    return result


def main() -> int:
    args = parser().parse_args()
    try:
        require(bool(args.version) and bool(args.source), "Version and source revision are required.")
        archive = package(args)
    except (OSError, PackageError, json.JSONDecodeError, tarfile.TarError) as error:
        print(f"Packaging failed: {error}", file=os.sys.stderr)
        return 1
    print(archive)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
