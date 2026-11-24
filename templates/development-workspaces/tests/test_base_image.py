import hashlib
import importlib.util
import json
import os
from pathlib import Path
import shutil
import struct
import sys
import tempfile
import unittest


HERE = Path(__file__).resolve().parents[1]


def load(name, filename):
    spec = importlib.util.spec_from_file_location(name, HERE / "image" / filename)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


base_image = load("pinned_base_image", "base_image.py")
builder = load("base_image_builder", "build.py")

RAW = b"\xebsynthetic-disk" + bytes(4096 - 15)
FAKE_QEMU = """#!{python}
import json, os, sys
args = sys.argv[1:]
with open(os.environ["FAKE_QEMU_LOG"], "a") as log:
    log.write(args[0] + "\\n")
if args[0] == "info":
    print(os.environ["FAKE_QEMU_INFO"])
else:
    with open(args[-1], "wb") as output:
        output.write(bytes.fromhex(os.environ["FAKE_QEMU_RAW"]))
"""


def qcow2(virtual_size=len(RAW), backing_offset=0, encryption=0, version=3):
    header = struct.pack(">IIQIIQI", base_image.QCOW2_MAGIC, version, backing_offset, 0, 16,
                         virtual_size, encryption)
    return header.ljust(4096, b"\0")


def pin_for(image_bytes, raw=RAW):
    return {"name": "Ubuntu", "version": "24.04", "cloudImageSerial": "20251001",
            "format": "qcow2", "size": len(image_bytes), "sha256": hashlib.sha256(image_bytes).hexdigest(),
            "raw": {"size": len(raw), "sha256": hashlib.sha256(raw).hexdigest()}}


class PinnedBaseImageTests(unittest.TestCase):
    def setUp(self):
        self.directory = Path(tempfile.mkdtemp())
        self.addCleanup(shutil.rmtree, self.directory)
        self.image = self.directory / "ubuntu.img"
        self.image.write_bytes(qcow2())
        self.pin = pin_for(qcow2())
        self.qemu = self.directory / "qemu-img"
        self.qemu.write_text(FAKE_QEMU.format(python=sys.executable))
        self.qemu.chmod(0o700)
        self.log = self.directory / "qemu.log"
        self.environment(raw=RAW, info={"format": "qcow2", "virtual-size": len(RAW)})

    def environment(self, raw, info):
        for name, value in (("FAKE_QEMU_LOG", str(self.log)), ("FAKE_QEMU_RAW", raw.hex()),
                            ("FAKE_QEMU_INFO", json.dumps(info))):
            previous = os.environ.get(name)
            os.environ[name] = value
            self.addCleanup(lambda name=name, previous=previous:
                            os.environ.pop(name) if previous is None else os.environ.update({name: previous}))

    def convert(self, output=None):
        return base_image.convert_raw(self.image, output or self.directory / "ubuntu.raw", self.pin, str(self.qemu))

    def calls(self):
        return self.log.read_text().split() if self.log.exists() else []

    def test_real_lock_pins_qcow2_and_derived_raw_for_the_same_serial(self):
        pin = base_image.pinned_os()
        self.assertEqual(pin["cloudImageSerial"], "20251001")
        self.assertEqual(pin["raw"]["size"] % 512, 0)

    def test_verified_qcow2_is_converted_and_raw_published_only_after_matching_the_lock(self):
        output = self.directory / "ubuntu.raw"
        result = self.convert(output)
        self.assertEqual(output.read_bytes(), RAW)
        self.assertEqual(result["rawSha256"], self.pin["raw"]["sha256"])
        self.assertEqual(self.calls(), ["info", "convert"])
        self.assertEqual(sorted(path.name for path in self.directory.iterdir()),
                         ["qemu-img", "qemu.log", "ubuntu.img", "ubuntu.raw"])
        base_image.verify_raw(output, self.pin)

    def test_qcow2_digest_size_or_header_drift_blocks_before_qemu_runs(self):
        cases = {
            "SHA-256": lambda: self.image.write_bytes(qcow2()[:-1] + b"\1"),
            "size": lambda: self.image.write_bytes(qcow2() + b"\0"),
            "backing": lambda: self.reset(qcow2(backing_offset=512)),
            "Encrypted": lambda: self.reset(qcow2(encryption=1)),
            "virtual size": lambda: self.reset(qcow2(virtual_size=len(RAW) * 2)),
            "Not a QCOW2": lambda: self.reset(b"\0" * 4096),
        }
        for message, mutate in cases.items():
            with self.subTest(message):
                self.image.write_bytes(qcow2())
                self.pin = pin_for(qcow2())
                mutate()
                with self.assertRaisesRegex(base_image.Refused, message):
                    self.convert()
                self.assertEqual(self.calls(), [])
                self.assertFalse((self.directory / "ubuntu.raw").exists())

    def reset(self, image_bytes):
        self.image.write_bytes(image_bytes)
        self.pin = pin_for(image_bytes)

    def test_symlinked_image_and_existing_output_are_refused(self):
        link = self.directory / "link.img"
        link.symlink_to(self.image)
        self.image = link
        with self.assertRaisesRegex(base_image.Refused, "symlink"):
            self.convert()
        self.image = self.directory / "ubuntu.img"
        existing = self.directory / "existing.raw"
        existing.write_bytes(b"keep")
        with self.assertRaisesRegex(base_image.Refused, "overwrite"):
            self.convert(existing)
        self.assertEqual(existing.read_bytes(), b"keep")
        self.assertEqual(self.calls(), [])

    def test_qemu_reported_backing_file_blocks_conversion(self):
        self.environment(raw=RAW, info={"format": "qcow2", "virtual-size": len(RAW),
                                        "backing-filename": "/etc/shadow"})
        with self.assertRaisesRegex(base_image.Refused, "unexpected QCOW2 layout"):
            self.convert()
        self.assertEqual(self.calls(), ["info"])

    def test_raw_that_differs_from_the_pin_is_never_published_and_staging_is_removed(self):
        self.environment(raw=RAW[:-1] + b"\1", info={"format": "qcow2", "virtual-size": len(RAW)})
        with self.assertRaisesRegex(base_image.Refused, "Pinned RAW SHA-256"):
            self.convert()
        self.assertEqual(sorted(path.name for path in self.directory.iterdir()),
                         ["qemu-img", "qemu.log", "ubuntu.img"])

    def test_cli_reports_refusal_without_traceback(self):
        with self.assertRaises(SystemExit) as raised:
            base_image.main(["verify-raw", str(self.directory / "missing.raw")])
        self.assertEqual(raised.exception.code, 1)


class BuilderSerialGuardTests(unittest.TestCase):
    def test_missing_build_info_blocks_with_pinned_image_guidance(self):
        with tempfile.TemporaryDirectory() as directory:
            with self.assertRaisesRegex(builder.Blocked, "not booted from the pinned Ubuntu cloud image"):
                builder.cloud_image_serial(Path(directory) / "build.info")

    def test_serial_is_read_from_cloud_build_info(self):
        with tempfile.TemporaryDirectory() as directory:
            info = Path(directory) / "build.info"
            info.write_text("build_name: server\nserial: 20251001\n")
            self.assertEqual(builder.cloud_image_serial(info), "20251001")
            info.write_text("build_name: server\n")
            self.assertIsNone(builder.cloud_image_serial(info))


if __name__ == "__main__":
    unittest.main()
