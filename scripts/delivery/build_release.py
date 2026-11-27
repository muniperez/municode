# SPDX-License-Identifier: MPL-2.0
"""Build an unsigned Municode release from an exact local Git revision."""

import argparse
from pathlib import Path
import re
import subprocess
import sys
import tempfile

from package import (
    LocalBuild, PackageError, REQUIRED_BACKEND_FILES, ROOT, file_digest, package, require, validate_targets,
)


VERSION = "0.0.0-local"
PLATFORMS = ("linux/amd64", "linux/arm64")


def parser():
    result = argparse.ArgumentParser(description=__doc__)
    result.add_argument("--ref", required=True, help="Git ref or commit to build.")
    result.add_argument("--allow-dirty", action="store_true",
                        help="Record a dirty checkout in the manifest; only committed files are built.")
    result.add_argument("--platform", choices=PLATFORMS, default=PLATFORMS[0],
                        help="Docker platform of the deployment host (default: %(default)s).")
    return result


def run(root, *command, capture=False):
    return subprocess.run(command, cwd=root, check=True, text=True,
                          stdout=subprocess.PIPE if capture else None).stdout


def resolve_source(root, ref, allow_dirty):
    dirty = bool(run(root, "git", "status", "--porcelain", "--untracked-files=all",
                     "--ignore-submodules=none", capture=True))
    require(not dirty or allow_dirty, "Checkout is dirty; commit changes or pass --allow-dirty.")
    revision = run(root, "git", "rev-parse", "--verify", "--end-of-options", ref + "^{commit}", capture=True).strip()
    require(re.fullmatch(r"[0-9a-f]{40}|[0-9a-f]{64}", revision), "Expected an exact Git commit ID.")
    return revision, dirty


def release_id(revision, bundle_digest):
    return f"local-{revision[:12]}-{bundle_digest[:12]}"


def publish_payloads(source, payload, targets, revision):
    for name in REQUIRED_BACKEND_FILES:
        run(source, "dotnet", "publish", targets[name]["project"], "-c", "Release", "-o", str(payload / name))

    dashboard = targets["dashboard"]
    prefix = str(Path(dashboard["project"]).parent)
    run(source, "npm", "ci", "--prefix", prefix)
    run(source, "npm", "run", "build", "--prefix", prefix)

    cli = targets["cli"]
    for rid in cli["rids"]:
        destination = payload / "cli" / rid
        run(source, "dotnet", "publish", cli["project"], "-c", "Release",
            "--runtime", rid, "--self-contained", "true",
            "-p:PublishSingleFile=true", "-p:IncludeNativeLibrariesForSelfExtract=true",
            "-p:PublishTrimmed=false", "-p:DebugType=None", "-p:DebugSymbols=false",
            f"-p:Version={VERSION}", f"-p:SourceRevisionId={revision}",
            "-p:IncludeSourceRevisionInInformationalVersion=false",
            f"-p:InformationalVersion={VERSION}+sha.{revision}", "-o", str(destination))
        executable = cli["artifact"] + (".exe" if rid.startswith("win-") else "")
        require((destination / executable).is_file(), f"CLI publish did not produce {rid}/{executable}.")
    return source / dashboard["artifactDirectory"]


def save_images(source, images, targets, platform, build_id):
    images.mkdir()
    ids = {}
    for name in REQUIRED_BACKEND_FILES:
        tag = f"municode-{name}:build-{build_id}"
        dockerfile = Path(targets[name]["project"]).with_name("Dockerfile")
        # Use the same Dockerfile and buildx context as the tagged image workflow.
        run(source, "docker", "buildx", "build", "--platform", platform, "--load",
            "--file", str(dockerfile), "--tag", tag, ".")
        try:
            image_id = run(source, "docker", "image", "inspect", "--format", "{{.Id}}", tag, capture=True).strip()
            require(re.fullmatch(r"sha256:[0-9a-f]{64}", image_id), f"Docker returned an invalid {name} image ID.")
            run(source, "docker", "image", "save", "--output", str(images / f"{name}.tar"), image_id)
            ids[name] = image_id
        finally:
            run(source, "docker", "image", "rm", tag)
    return ids


def build(root, args):
    revision, dirty = resolve_source(root, args.ref, args.allow_dirty)
    output = root / ".local" / "artifacts" / "release"
    require(output.resolve() == output, "Release output path must not traverse symlinks.")
    output.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="build-", dir=output) as temporary:
        staging = Path(temporary)
        source = staging / "source"
        run(root, "git", "worktree", "add", "--detach", str(source), revision)
        try:
            targets = validate_targets(source)["components"]
            payload = staging / "payload"
            images = staging / "images"
            image_ids = save_images(source, images, targets, args.platform, staging.name)
            dashboard = publish_payloads(source, payload, targets, revision)
            archive = package(argparse.Namespace(
                version=VERSION, source=revision, rid="linux-any", output=staging / "release",
                api=payload / "api", worker=payload / "worker", gateway=payload / "gateway",
                dashboard=dashboard, cli=payload / "cli",
            ), source_root=source, local=LocalBuild(dirty, args.platform, images, image_ids))
        finally:
            run(root, "git", "worktree", "remove", "--force", str(source))
        destination = output / release_id(revision, file_digest(archive))
        require(not destination.exists(), f"Release output already exists: {destination}")
        archive.parent.rename(destination)
        return destination


def main():
    args = parser().parse_args()
    try:
        print(build(ROOT, args))
    except (OSError, ValueError, KeyError, PackageError, subprocess.CalledProcessError) as error:
        print(f"Local release build failed: {error}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
