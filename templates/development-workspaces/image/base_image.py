#!/usr/bin/env python3
"""Verify the pinned Ubuntu cloud image and derive the pinned RAW disk for provider imports.

Runs on an operator workstation after the README's fail-closed download. It never contacts
a provider, uploads, boots or follows links. Every output is checked against the lock.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import stat
import struct
import subprocess
import sys
import tempfile

LOCK = Path(__file__).resolve().with_name("manifest.lock.json")
QCOW2_MAGIC = 0x514649FB


class Refused(Exception):
    pass


def require(condition, message):
    if not condition:
        raise Refused(message)


def pinned_os(lock_path=LOCK):
    pin = json.loads(Path(lock_path).read_text())["os"]
    require(pin.get("format") == "qcow2" and isinstance(pin.get("size"), int)
            and isinstance(pin.get("raw", {}).get("size"), int)
            and len(pin.get("sha256", "")) == 64 and len(pin["raw"].get("sha256", "")) == 64,
            "Lock lacks the pinned QCOW2/RAW base-image attestation")
    return pin


def _open_regular(path):
    descriptor = os.open(path, os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK)
    stream = os.fdopen(descriptor, "rb")
    if not stat.S_ISREG(os.fstat(stream.fileno()).st_mode):
        stream.close()
        raise Refused("A regular, non-symlinked image file is required: " + str(path))
    return stream


def _verify_bytes(path, size, sha256, label):
    try:
        with _open_regular(path) as stream:
            require(os.fstat(stream.fileno()).st_size == size, label + " size does not match the lock")
            header = stream.read(48)
            stream.seek(0)
            digest = hashlib.file_digest(stream, "sha256").hexdigest()
    except OSError as error:
        raise Refused(f"{label} is unreadable or a symlink: {path} ({error.strerror})") from None
    require(digest == sha256, label + " SHA-256 does not match the lock; never adopt another digest")
    return header


def verify_qcow2(path, pin):
    header = _verify_bytes(path, pin["size"], pin["sha256"], "Pinned QCOW2")
    require(len(header) == 48, "Truncated QCOW2 header")
    magic, version, backing_offset, _, _, virtual_size, encryption = struct.unpack(">IIQIIQI", header[:36])
    require(magic == QCOW2_MAGIC and version in (2, 3), "Not a QCOW2 image")
    require(backing_offset == 0, "QCOW2 backing files are refused")
    require(encryption == 0, "Encrypted QCOW2 images are refused")
    require(virtual_size == pin["raw"]["size"], "QCOW2 virtual size does not match the pinned RAW size")


def verify_raw(path, pin):
    _verify_bytes(path, pin["raw"]["size"], pin["raw"]["sha256"], "Pinned RAW")


def attestation(pin):
    return {"type": "PinnedBaseImageVerified", "os": f"{pin['name']} {pin['version']}",
            "cloudImageSerial": pin["cloudImageSerial"], "qcow2Sha256": pin["sha256"],
            "qcow2Size": pin["size"], "rawSha256": pin["raw"]["sha256"], "rawSize": pin["raw"]["size"]}


def convert_raw(image, output, pin, qemu_img="qemu-img"):
    """Convert only the verified QCOW2, then publish the RAW only if it matches the lock."""
    output = Path(output)
    require(not os.path.lexists(output), "Refusing to overwrite " + str(output))
    require(output.parent.is_dir(), "Output directory does not exist")
    verify_qcow2(image, pin)
    info = subprocess.run([qemu_img, "info", "--output=json", "-f", "qcow2", "--", str(image)],
                          check=True, capture_output=True, text=True, timeout=120)
    details = json.loads(info.stdout)
    require(details.get("format") == "qcow2" and "backing-filename" not in details
            and "full-backing-filename" not in details, "qemu-img reports an unexpected QCOW2 layout")
    require(details.get("virtual-size") == pin["raw"]["size"], "qemu-img virtual size does not match the lock")
    staging = Path(tempfile.mkdtemp(prefix=".raw-", dir=output.parent))
    try:
        partial = staging / "image.raw"
        subprocess.run([qemu_img, "convert", "-f", "qcow2", "-O", "raw", "--", str(image), str(partial)],
                       check=True, capture_output=True, timeout=1800)
        verify_raw(partial, pin)
        # A hard link publishes atomically and, unlike rename, never replaces a racing file.
        os.link(partial, output)
    finally:
        shutil.rmtree(staging, ignore_errors=True)
    return attestation(pin)


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="action", required=True)
    commands.add_parser("verify-qcow2", help="Check the downloaded pinned QCOW2").add_argument("image", type=Path)
    commands.add_parser("verify-raw", help="Check a RAW disk before upload").add_argument("image", type=Path)
    raw = commands.add_parser("raw", help="Derive the pinned RAW disk for Vultr imports")
    raw.add_argument("image", type=Path)
    raw.add_argument("output", type=Path)
    raw.add_argument("--qemu-img", default="qemu-img")
    args = parser.parse_args(argv)
    try:
        pin = pinned_os()
        if args.action == "verify-qcow2":
            verify_qcow2(args.image, pin)
            result = attestation(pin)
        elif args.action == "verify-raw":
            verify_raw(args.image, pin)
            result = attestation(pin)
        else:
            result = convert_raw(args.image, args.output, pin, args.qemu_img)
    except (Refused, OSError, ValueError, subprocess.SubprocessError) as error:
        parser.exit(1, f"Base image refused: {error}\n")
    print(json.dumps(result, sort_keys=True))


if __name__ == "__main__":
    main(sys.argv[1:])
