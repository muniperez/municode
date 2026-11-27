#!/usr/bin/env python3
# SPDX-License-Identifier: MPL-2.0
"""Verify component digests inside an extracted Municode release."""

import hashlib
import json
from pathlib import Path
import stat
import sys


def fail(message):
    raise ValueError(message)


def digest(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def main():
    root = Path(__file__).resolve().parent
    for entry in root.rglob("*"):
        if entry.is_symlink():
            fail(f"Symlinks are forbidden in a release: {entry}")
    manifest = json.loads((root / "release-manifest.json").read_text())
    if manifest.get("schemaVersion") != 1 or manifest.get("product") != "municode":
        fail("Unexpected release manifest.")
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
