#!/usr/bin/env python3
# SPDX-License-Identifier: MPL-2.0
"""Assemble and verify a self-contained Municode release."""

from __future__ import annotations

import argparse
from dataclasses import dataclass
import gzip
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import re
import shutil
import stat
import subprocess
import sys
import tarfile
import tempfile


ROOT = Path(__file__).resolve().parents[2]
REQUIRED_BACKEND_FILES = {
    "api": "Municode.Api.dll",
    "worker": "Municode.Worker.dll",
    "gateway": "Municode.Gateway.dll",
}


class PackageError(Exception):
    pass


@dataclass(frozen=True)
class LocalBuild:
    source_dirty: bool
    image_platform: str
    image_directory: Path
    image_ids: dict[str, str]


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


def validate_targets(source_root: Path = ROOT) -> dict[str, object]:
    targets = json.loads((source_root / "packaging" / "targets.json").read_text())
    require(targets.get("schemaVersion") == 1, "Unsupported package target schema.")
    for component in ("api", "worker", "gateway", "dashboard", "cli"):
        require(component in targets["components"], f"Missing package target: {component}")
    return targets


def validate_release(source_root: Path = ROOT) -> dict[str, object]:
    release = json.loads((source_root / "packaging" / "release.json").read_text())
    require(release.get("schemaVersion") == 1, "Unsupported release metadata schema.")
    require(release.get("product") == "municode", "Unexpected release product.")
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


def add_local_images(root: Path, contract: dict, local: LocalBuild) -> dict:
    images = {}
    env = {"MUNICODE_RUNTIME_PULL_POLICY": "never"}
    require(local.image_ids.keys() == REQUIRED_BACKEND_FILES.keys(), "Incomplete local image inventory.")
    compose = (root / "compose.yml").read_text()
    (root / "images").mkdir()
    for name, image_id in local.image_ids.items():
        require(re.fullmatch(r"sha256:[0-9a-f]{64}", image_id), f"Invalid {name} image ID.")
        relative = f"images/{name}.tar"
        image = root / relative
        shutil.move(local.image_directory / f"{name}.tar", image)
        reference = f"municode-{name}:local-{image_id.removeprefix('sha256:')}"
        images[name] = {"path": relative, "sha256": file_digest(image),
                        "imageId": image_id, "reference": reference}
        env[f"MUNICODE_{name.upper()}_IMAGE"] = reference
    with (root / ".env.example").open("a") as template:
        template.write("\n")
        for key, value in env.items():
            require(f"${{{key}:-" in compose, f"Source revision does not support local builds: {key}")
            contract[key] = {"owner": "release"}
            template.write(f"{key}={value}\n")
    (root / "env-contract.json").write_text(json.dumps(contract, indent=2, sort_keys=True) + "\n")
    return {"sourceDirty": local.source_dirty, "imagePlatform": local.image_platform,
            "imageArchives": images}


def package(args: argparse.Namespace, *, source_root: Path = ROOT, local: LocalBuild | None = None) -> Path:
    validate_identity(args)
    targets = validate_targets(source_root)
    release = validate_release(source_root)
    components = validate_payloads(args)
    args.output.mkdir(parents=True, exist_ok=True)
    archive_name = f"municode-{args.version}-{args.rid}.tar.gz"
    archive = args.output / archive_name
    manifest_path = args.output / "release-manifest.json"
    checksums_path = args.output / "checksums.txt"
    require(
        not archive.exists() and not manifest_path.exists() and not checksums_path.exists(),
        "Release output already exists; select an empty ignored artifacts directory.",
    )

    with tempfile.TemporaryDirectory(prefix="municode-package-", dir=args.output) as temporary:
        root = Path(temporary) / f"municode-{args.version}"
        copy_tree(source_root / "packaging" / "bundle", root)
        (root / "docs").mkdir()
        for name in ("operator.md", "user.md"):
            shutil.copy2(source_root / "docs" / name, root / "docs" / name)

        inventories: dict[str, object] = {}
        for name, (relative, source) in components.items():
            destination = root / relative
            copy_tree(source, destination)
            inventories[name] = component_inventory(destination)

        api = root / components["api"][0]
        migrations = subprocess.run(
            ["dotnet", str((api / REQUIRED_BACKEND_FILES["api"]).resolve()), "migrations", "list", "--json"],
            cwd=api, check=True, stdout=subprocess.PIPE, text=True,
        )
        contract = json.loads((root / "env-contract.json").read_text())
        manifest = {
            "schemaVersion": 1,
            "deployProtocol": 1,
            "migrations": json.loads(migrations.stdout),
            "envContract": contract,
            "product": "municode",
            "version": args.version,
            "rid": args.rid,
            "sourceRepository": "https://github.com/muniperez/municode",
            "sourceRevision": args.source,
            "targets": targets["components"],
            "dependencies": release["images"],
            "components": inventories,
            "bundle": archive_name,
        }
        if local is not None:
            manifest.update(add_local_images(root, contract, local))
        (root / "release-manifest.json").write_text(
            json.dumps(manifest, indent=2, sort_keys=True) + "\n"
        )
        subprocess.run([sys.executable, "-B", str(root / "verify_bundle.py")], check=True)
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
    except (OSError, PackageError, json.JSONDecodeError, subprocess.CalledProcessError, tarfile.TarError) as error:
        print(f"Packaging failed: {error}", file=os.sys.stderr)
        return 1
    print(archive)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
