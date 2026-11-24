#!/usr/bin/env python3
"""Synthetic checks by default; explicit disposable-guest opt-in enables real ext4 checks."""
import io
import json
import os
from pathlib import Path
import stat
import subprocess
import sys
import tempfile
import threading
from types import SimpleNamespace
import unittest
from unittest.mock import patch

HERE = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(HERE))
import disk_usage as du
import runtime as rt

ARTIFACTS = HERE.parents[1] / ".local/artifacts/issue156/disk-usage"
ARTIFACTS.mkdir(parents=True, exist_ok=True)


def write(path, size):
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("wb") as stream:
        stream.write(os.urandom(size))


OPEN_ROOT = du.open_root


def volume_root(workspace):
    """Fixtures are one filesystem; this makes the workspace root look like a separate volume."""
    fd, info = OPEN_ROOT(workspace)
    return fd, SimpleNamespace(st_dev=info.st_dev + 1, st_ino=info.st_ino)


def identity(path):
    info = os.stat(path, follow_symlinks=False)
    return dict(dev=info.st_dev, ino=info.st_ino)


class DiskUsageTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="case-", dir=ARTIFACTS)
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.workspace = self.root / "workspace"
        self.outside = self.root / "outside"
        write(self.outside / "secret.bin", 4096)
        for name in ("repos", "home", "artifacts", "t3", "config", "transcripts"):
            (self.workspace / name).mkdir(parents=True)
        write(self.workspace / "repos/big/data.bin", 256 * 1024)
        write(self.workspace / "repos/big/.cache/blob.bin", 64 * 1024)
        write(self.workspace / "repos/small/file.txt", 10)
        write(self.workspace / "repos/loose.bin", 32 * 1024)
        (self.workspace / "repos/escape").symlink_to(self.outside, target_is_directory=True)
        (self.workspace / "repos/big/escape").symlink_to(self.outside, target_is_directory=True)
        os.link(self.workspace / "repos/big/data.bin", self.workspace / "repos/big/data-link.bin")
        write(self.workspace / ".workspaces-volume.json", 10)
        patcher = patch.object(du, "CODING_UID", os.getuid())
        patcher.start()
        self.addCleanup(patcher.stop)

    def scan(self, *path, **kwargs):
        return du.scan(str(self.workspace), list(path), **kwargs)

    def test_scan_lists_child_folders_by_size_with_files_here_and_no_link_following(self):
        result = self.scan("repos")
        self.assertTrue(result["complete"])
        names = [row["name"] for row in result["entries"]]
        self.assertEqual(["big", "small"], names)
        big = result["entries"][0]
        # The hard link counts once and the symlinked outside folder is never measured.
        self.assertLess(big["bytes"], 2 * 256 * 1024)
        self.assertGreaterEqual(big["bytes"], 256 * 1024 + 64 * 1024)
        self.assertTrue(big["deletable"])
        self.assertGreaterEqual(result["filesHereBytes"], 32 * 1024)
        self.assertEqual(result["totalBytes"], result["filesHereBytes"] + sum(r["bytes"] for r in result["entries"]))
        self.assertNotIn("escape", names)

    def test_scratch_bind_mounts_are_labelled_and_never_walked(self):
        write(self.workspace / "repos/big/target/out.bin", 512 * 1024)
        target = str(self.workspace / "repos/big/target")
        unlabelled = self.scan("repos")["entries"][0]["bytes"]
        big = self.scan("repos", scratch={target, "/elsewhere/target"})["entries"][0]
        # Same device here, so only the scratch label keeps the build output out of the retained total.
        self.assertEqual("complete", big["state"])
        self.assertEqual([], big["errors"])
        self.assertLessEqual(big["bytes"], unlabelled - 512 * 1024)
        rows = {row["name"]: row for row in self.scan("repos", "big", scratch={target})["entries"]}
        self.assertEqual(("excluded", False, False, "scratch", ["separate_mount"]),
                         (rows["target"]["state"], rows["target"]["selectable"], rows["target"]["deletable"],
                          rows["target"]["storage"], rows["target"]["errors"]))
        self.assertNotIn("storage", rows[".cache"])

    def test_scratch_mountpoints_follow_the_scratch_repos_source(self):
        mountinfo = Path(self.temp.name) / "mountinfo"
        cases = {
            # Scratch on the root disk: bind sources are paths below /var/lib/workspaces-scratch/repos.
            "shared": ("1 0 8:1 / / rw - ext4 /dev/sda1 rw\n"
                       "2 1 8:2 / /workspace rw - ext4 /dev/sdb rw\n"
                       "3 2 8:1 /var/lib/workspaces-scratch/repos/p/target /workspace/repos/p/tar\\040get rw - ext4 /dev/sda1 rw\n"
                       "4 2 8:1 /srv/other /workspace/repos/p/other rw - ext4 /dev/sda1 rw\n"
                       "5 2 8:1 /var/lib/workspaces-scratch/repos-old /workspace/repos/p/old rw - ext4 /dev/sda1 rw\n"),
            # Scratch on its own filesystem: bind roots are relative to that filesystem.
            "separate": ("1 0 8:1 / / rw - ext4 /dev/sda1 rw\n"
                         "2 1 8:2 / /workspace rw - ext4 /dev/sdb rw\n"
                         "3 1 8:3 / /var/lib/workspaces-scratch rw - ext4 /dev/sdc rw\n"
                         "4 2 8:3 /repos/p/target /workspace/repos/p/tar\\040get rw - ext4 /dev/sdc rw\n"
                         "5 2 8:3 /cache /workspace/repos/p/other rw - ext4 /dev/sdc rw\n"),
        }
        for name, text in cases.items():
            with self.subTest(name):
                mountinfo.write_text(text)
                rows = [(f[2], rt._unescape_mount(f[3]), rt._unescape_mount(f[4]))
                        for f in (line.split(" ") for line in text.splitlines())]
                self.assertEqual({"/workspace/repos/p/tar get"}, du.scratch_mountpoints(str(mountinfo)))
                self.assertEqual({"/workspace/repos/p/tar get"}, rt.scratch_bind_mounts(rows))
        self.assertEqual(set(), du.scratch_mountpoints(str(Path(self.temp.name) / "missing")))

    def test_roots_and_tool_folders_are_never_deletable(self):
        result = self.scan()
        rows = {row["name"]: row for row in result["entries"]}
        self.assertEqual({"repos", "home", "artifacts", "t3", "config", "transcripts"}, set(rows))
        self.assertFalse(any(row["deletable"] for row in rows.values()))
        write(self.workspace / "t3/userdata/state.bin", 10)
        self.assertFalse(any(row["deletable"] for row in self.scan("t3")["entries"]))
        for path in (["repos"], ["t3", "userdata"], ["config", "x"], ["lost+found", "x"], []):
            with self.assertRaises(du.Refused) as error:
                du.delete(str(self.workspace), path, dict(dev=1, ino=1))
            self.assertEqual("protected_path", error.exception.code)

    def deny_directories(self, *names):
        original = os.open

        def guarded(name, flags, *args, **kwargs):
            if name in names:
                raise PermissionError(name)
            return original(name, flags, *args, **kwargs)

        return patch.object(du.os, "open", side_effect=guarded)

    def test_proven_root_recovery_is_reported_separately_without_opening_or_counting(self):
        write(self.workspace / "lost+found/recovered.bin", 8192)
        with patch.object(du, "ext4_recovery_directory", return_value=True) as recognize, \
                self.deny_directories("lost+found"):
            result = self.scan()
        recognize.assert_called_once()
        row = next(row for row in result["entries"] if row["name"] == "lost+found")
        self.assertEqual(("excluded", "ext4_recovery", None, False, False, []),
                         (row["state"], row["exclusion"], row["bytes"], row["selectable"],
                          row["deletable"], row["errors"]))
        self.assertTrue(result["complete"])
        self.assertEqual({}, result["errors"])
        self.assertEqual(result["filesHereBytes"] + sum(r["bytes"] or 0 for r in result["entries"]),
                         result["totalBytes"])

    def test_unproven_root_recovery_and_real_unreadable_user_paths_stay_partial(self):
        for path in ("lost+found", "repos/unreadable", "repos/project/lost+found"):
            with self.subTest(path=path):
                directory = self.workspace / path
                directory.mkdir(parents=True)
                with patch.object(du, "ext4_recovery_directory", return_value=False), \
                        self.deny_directories(directory.name):
                    result = self.scan()
                self.assertFalse(result["complete"])
                self.assertEqual({"permission_denied": 1}, result["errors"])
                directory.rmdir()

    def test_root_recovery_exclusion_does_not_hide_nested_user_lost_found(self):
        (self.workspace / "lost+found").mkdir()
        (self.workspace / "repos/lost+found").mkdir()
        with patch.object(du, "ext4_recovery_directory", return_value=True) as recognize, \
                self.deny_directories("lost+found"):
            result = self.scan()
            nested = self.scan("repos")
        recognize.assert_called_once()
        self.assertFalse(result["complete"])
        self.assertEqual({"permission_denied": 1}, result["errors"])
        row = next(row for row in nested["entries"] if row["name"] == "lost+found")
        self.assertEqual("unreadable", row["state"])
        self.assertNotIn("exclusion", row)

    def test_recovery_symlink_and_scratch_mount_never_use_metadata_recognition(self):
        recovery = self.workspace / "lost+found"
        recovery.symlink_to(self.outside, target_is_directory=True)
        with patch.object(du, "ext4_recovery_directory") as recognize:
            self.assertNotIn("lost+found", [row["name"] for row in self.scan()["entries"]])
            recovery.unlink()
            recovery.mkdir()
            result = self.scan(scratch={str(recovery)})
        recognize.assert_not_called()
        row = next(row for row in result["entries"] if row["name"] == "lost+found")
        self.assertEqual(("excluded", "scratch", ["separate_mount"]),
                         (row["state"], row["storage"], row["errors"]))

    def test_ext4_recognition_requires_kernel_mount_and_reserved_inode_evidence(self):
        device = os.stat(self.workspace).st_dev
        root = SimpleNamespace(st_dev=device, st_ino=2)
        recovery = SimpleNamespace(st_dev=device, st_ino=11, st_mode=stat.S_IFDIR | 0o700,
                                   st_uid=0, st_gid=0)
        marker = SimpleNamespace(st_mode=stat.S_IFREG | 0o644, st_uid=0)
        mount = f"42 1 {os.major(device)}:{os.minor(device)} / /managed\\040workspace rw - ext4 /dev/vdb rw\n"
        cases = [
            ("verified", {}, {}, {}, mount, "mnt_id:\t42\n", True),
            ("not-filesystem-root", {"st_ino": 100}, {}, {}, mount, "mnt_id:\t42\n", False),
            ("user-inode", {}, {"st_ino": 100}, {}, mount, "mnt_id:\t42\n", False),
            ("user-owner", {}, {"st_uid": 2000}, {}, mount, "mnt_id:\t42\n", False),
            ("writable-recovery", {}, {"st_mode": stat.S_IFDIR | 0o777}, {}, mount, "mnt_id:\t42\n", False),
            ("different-device", {}, {"st_dev": device + 1}, {}, mount, "mnt_id:\t42\n", False),
            ("symlink", {}, {"st_mode": stat.S_IFLNK | 0o777}, {}, mount, "mnt_id:\t42\n", False),
            ("unsafe-marker", {}, {}, {"st_uid": 2000}, mount, "mnt_id:\t42\n", False),
            ("marker-link", {}, {}, {"st_mode": stat.S_IFLNK | 0o777}, mount, "mnt_id:\t42\n", False),
            ("not-ext4", {}, {}, {}, mount.replace("ext4", "xfs"), "mnt_id:\t42\n", False),
            ("subtree-bind", {}, {}, {}, mount.replace(" / /managed", " /subtree /managed"), "mnt_id:\t42\n", False),
            ("stale-mount", {}, {}, {}, mount, "mnt_id:\t43\n", False),
            ("missing-mount-id", {}, {}, {}, mount, "", False),
            ("malformed-mount-id", {}, {}, {}, mount, "mnt_id:\n", False),
            ("malformed", {}, {}, {}, "42 1 malformed\n", "mnt_id:\t42\n", False),
            ("same-device-bind", {}, {}, {}, mount +
             f"43 42 {os.major(device)}:{os.minor(device)} / /managed\\040workspace/lost+found rw - ext4 /dev/vdb rw\n",
             "mnt_id:\t42\n", False),
            ("nested-mount", {}, {}, {}, mount +
             "43 42 0:5 / /managed\\040workspace/lost+found/recovered rw - tmpfs tmpfs rw\n",
             "mnt_id:\t42\n", False),
        ]
        for label, root_changes, recovery_changes, marker_changes, mounts, fdinfo, expected in cases:
            with self.subTest(label=label), \
                    patch.object(du.os, "stat", return_value=SimpleNamespace(**(vars(marker) | marker_changes))), \
                    patch.object(du.os, "readlink", return_value="/managed workspace"), \
                    patch("builtins.open", side_effect=[io.StringIO(fdinfo), io.StringIO(mounts)]):
                self.assertEqual(expected, du.ext4_recovery_directory(
                    123, SimpleNamespace(**(vars(root) | root_changes)),
                    SimpleNamespace(**(vars(recovery) | recovery_changes))))
        for operation in ("stat", "readlink"):
            with self.subTest(operation=operation), patch.object(du.os, "stat", return_value=marker), \
                    patch.object(du.os, operation, side_effect=PermissionError):
                self.assertFalse(du.ext4_recovery_directory(123, root, recovery))
        with patch.object(du.os, "stat", return_value=marker), \
                patch.object(du.os, "readlink", return_value="/managed workspace"), \
                patch("builtins.open", side_effect=PermissionError):
            self.assertFalse(du.ext4_recovery_directory(123, root, recovery))

    def test_path_traversal_and_symlink_components_are_refused(self):
        for path in (["..", "outside"], ["repos/../.."], ["repos", ""], ["repos", "a\0b"]):
            with self.assertRaises(du.Refused):
                self.scan(*path)
        with self.assertRaises(du.Refused) as error:
            self.scan("repos", "escape")
        self.assertEqual("symbolic_link", error.exception.code)
        with self.assertRaises(du.Refused):
            du.delete(str(self.workspace), ["repos", "escape"], identity(self.outside))
        self.assertTrue((self.outside / "secret.bin").exists())

    def test_delete_removes_verified_folder_without_following_links(self):
        target = self.workspace / "repos/big"
        result = du.delete(str(self.workspace), ["repos", "big"], identity(target))
        self.assertEqual("succeeded", result["outcome"])
        self.assertFalse(target.exists())
        self.assertTrue((self.outside / "secret.bin").exists())
        self.assertTrue((self.workspace / "repos/small/file.txt").exists())
        self.assertGreater(result["removedBytes"], 256 * 1024)

    def test_replaced_folder_is_never_deleted(self):
        target = self.workspace / "repos/small"
        old = identity(target)
        (self.workspace / "repos/small").rename(self.workspace / "repos/moved")
        write(self.workspace / "repos/small/new.txt", 10)
        with self.assertRaises(du.Refused) as error:
            du.delete(str(self.workspace), ["repos", "small"], old)
        self.assertEqual("target_changed", error.exception.code)
        self.assertTrue((self.workspace / "repos/small/new.txt").exists())
        self.assertTrue((self.workspace / "repos/moved/file.txt").exists())

    def test_delete_rechecks_all_ancestors_and_never_walks_a_renamed_checkout(self):
        target = self.workspace / "repos/big"
        original = os.scandir
        moved = False

        def rename_parent(fd):
            nonlocal moved
            if not moved and isinstance(fd, int) and \
                    os.readlink("/proc/self/fd/" + str(fd)) == str(target):
                moved = True
                (self.workspace / "repos").rename(self.workspace / "moved-repos")
                (self.workspace / "repos").mkdir()
            return original(fd)

        with patch.object(du.os, "scandir", side_effect=rename_parent):
            result = du.delete(str(self.workspace), ["repos", "big"], identity(target))
        self.assertEqual(("failed", 0, {"target_changed": 1}),
                         (result["outcome"], result["removedEntries"], result["errors"]))
        self.assertTrue((self.workspace / "moved-repos/big/data.bin").exists())

    def test_delete_refuses_same_device_nested_file_mount_before_removing_anything(self):
        target = self.workspace / "repos/big"
        mountinfo = self.root / "mountinfo"
        mountinfo.write_text("1 0 0:1 / / rw - ext4 x rw\n2 1 0:1 /other " +
                            str(target / "data.bin") + " rw - ext4 x rw\n")
        with patch.object(du, "MOUNTINFO", str(mountinfo)):
            with self.assertRaises(du.Refused) as error:
                du.delete(str(self.workspace), ["repos", "big"], identity(target))
        self.assertEqual("separate_mount", error.exception.code)
        self.assertTrue((target / "data.bin").exists())
        self.assertTrue((target / ".cache/blob.bin").exists())

    def test_delete_allows_job_writable_root_bind_but_rejects_nested_bind(self):
        target = self.workspace / "repos/small"

        def sandbox_mount_id(fd):
            path = os.readlink("/proc/self/fd/" + str(fd))
            return 2 if path.startswith(str(self.workspace / "repos")) else 1

        with patch.object(du, "mount_id", side_effect=sandbox_mount_id):
            result = du.delete(str(self.workspace), ["repos", "small"], identity(target))
        self.assertEqual("succeeded", result["outcome"])
        target = self.workspace / "repos/big/.cache"

        def nested_mount_id(fd):
            path = os.readlink("/proc/self/fd/" + str(fd))
            return 3 if path.startswith(str(self.workspace / "repos/big")) else sandbox_mount_id(fd)

        with patch.object(du, "mount_id", side_effect=nested_mount_id):
            with self.assertRaises(du.Refused) as error:
                du.delete(str(self.workspace), ["repos", "big", ".cache"], identity(target))
        self.assertEqual("separate_mount", error.exception.code)
        self.assertTrue((target / "blob.bin").exists())

    def test_cancelled_delete_after_file_removal_keeps_partial_target(self):
        target = self.workspace / "repos/small"
        original = os.unlink

        def cancel_after_unlink(*args, **kwargs):
            original(*args, **kwargs)
            du._cancelled = True

        with patch.object(du, "_cancelled", False), \
                patch.object(du.os, "unlink", side_effect=cancel_after_unlink):
            result = du.delete(str(self.workspace), ["repos", "small"], identity(target))
        self.assertEqual(("cancelled", 1, "cancelled"),
                         (result["outcome"], result["removedEntries"], result["limitReached"]))
        self.assertTrue(target.is_dir())

    def test_folder_not_owned_by_coding_user_is_refused(self):
        with patch.object(du, "CODING_UID", os.getuid() + 1):
            self.assertFalse(any(row["deletable"] for row in self.scan("repos")["entries"]))
            with self.assertRaises(du.Refused) as error:
                du.delete(str(self.workspace), ["repos", "small"], identity(self.workspace / "repos/small"))
        self.assertEqual("not_owned_by_coding_user", error.exception.code)

    def test_local_checkout_mount_is_measured_and_deletable_on_its_own_device(self):
        with patch.object(du, "open_root", side_effect=volume_root):
            top = {row["name"]: row for row in self.scan(local_repos=True)["entries"]}
            self.assertEqual(("complete", "local", []),
                             (top["repos"]["state"], top["repos"].get("storage"), top["repos"]["errors"]))
            self.assertGreater(top["repos"]["bytes"], 256 * 1024)
            rows = {row["name"]: row for row in self.scan("repos", local_repos=True)["entries"]}
            self.assertTrue(rows["big"]["deletable"])
            with self.assertRaises(du.Refused) as caught:
                self.scan("repos")
            self.assertEqual("separate_mount", caught.exception.code)
            result = du.delete(str(self.workspace), ["repos", "small"], identity(self.workspace / "repos/small"),
                               local_repos=True)
        self.assertEqual("succeeded", result["outcome"])
        self.assertFalse((self.workspace / "repos/small").exists())

    def test_missing_local_checkout_mount_is_never_measured_as_volume_data(self):
        top = {row["name"]: row for row in self.scan(local_repos=True)["entries"]}
        self.assertEqual(("unreadable", None, ["io_error"]),
                         (top["repos"]["state"], top["repos"]["bytes"], top["repos"]["errors"]))
        for call in (lambda: self.scan("repos", local_repos=True),
                     lambda: du.delete(str(self.workspace), ["repos", "big"], identity(self.workspace / "repos/big"),
                                       local_repos=True)):
            with self.assertRaises(du.Refused) as caught:
                call()
            self.assertEqual("io_error", caught.exception.code)
        self.assertTrue((self.workspace / "repos/big").exists())

    def test_limits_report_partial_results_and_bounded_rows(self):
        result = self.scan("repos", budget=du.Budget(30, entries=3))
        self.assertFalse(result["complete"])
        self.assertEqual("entries", result["limitReached"])
        with patch.object(du, "MAX_ROWS", 1):
            result = self.scan("repos")
        self.assertEqual(["big"], [row["name"] for row in result["entries"]])
        self.assertEqual(1, result["omittedEntries"])
        self.assertGreater(result["omittedBytes"], 0)

    def test_cancelled_delete_reports_cancelled_and_keeps_folder(self):
        budget = du.Budget(60)
        budget.limit = "cancelled"
        result = du.delete(str(self.workspace), ["repos", "big"], identity(self.workspace / "repos/big"), budget)
        self.assertEqual("cancelled", result["outcome"])
        self.assertTrue((self.workspace / "repos/big").exists())

    def test_command_line_prints_one_bounded_json_document(self):
        output = io.StringIO()
        with patch.object(sys, "stdout", output):
            du.main([json.dumps(dict(mode="scan", workspace=str(self.workspace), path=["repos"]))])
            du.main([json.dumps(dict(mode="delete", workspace=str(self.workspace), path=["repos"],
                                     expect=dict(dev=1, ino=1)))])
        first, second = [json.loads(line) for line in output.getvalue().splitlines()]
        self.assertEqual("scan", first["mode"])
        self.assertEqual("protected_path", second["refused"])


@unittest.skipUnless(os.environ.get("WORKSPACES_DISK_USAGE_GUEST") ==
                     "RUN-DISK-USAGE-ON-DISPOSABLE-GUEST", "requires explicit disposable guest opt-in")
class DiskUsageExt4GuestTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        marker = Path("/etc/workspaces-disposable-image-builder")
        if os.geteuid() != 0 or not marker.is_file() or marker.is_symlink():
            raise RuntimeError("Requires root in a marked disposable image-builder VM")
        info = marker.stat()
        if info.st_uid != 0 or info.st_mode & 0o022 or \
                marker.read_text().strip() != "DESTROY-DISPOSABLE-IMAGE-BUILDER":
            raise RuntimeError("Unsafe disposable image-builder marker")
        subprocess.run(["systemd-detect-virt", "--vm"], check=True, capture_output=True)
        if os.stat("/proc/self/ns/mnt").st_ino == os.stat("/proc/1/ns/mnt").st_ino:
            raise RuntimeError("Run this test in a private mount namespace")

    def setUp(self):
        temp = tempfile.TemporaryDirectory(prefix="municode-disk-usage-ext4-", dir="/var/tmp")
        self.addCleanup(temp.cleanup)
        self.root = Path(temp.name)
        self.root.chmod(0o755)
        image = self.root / "fresh.ext4"
        with image.open("xb") as stream:
            stream.truncate(64 * 1024 * 1024)
        subprocess.run(["mkfs.ext4", "-q", "-F", "-E", "nodiscard,lazy_itable_init=1,lazy_journal_init=1",
                        str(image)], check=True, capture_output=True)
        self.workspace = self.root / "workspace"
        self.workspace.mkdir()
        subprocess.run(["mount", "-t", "ext4", "-o", "loop,nodev,nosuid", str(image),
                        str(self.workspace)], check=True, capture_output=True)
        self.addCleanup(subprocess.run, ["umount", str(self.workspace)], check=True, capture_output=True)
        self.assertEqual({"lost+found"}, {entry.name for entry in self.workspace.iterdir()})
        (self.workspace / ".workspaces-volume.json").write_text(json.dumps(dict(
            organizationId="guest", workspaceId="disposable", providerAccountId="guest",
            location="guest", providerVolumeId="disposable-loop")))

    def helper(self, mode="scan", path=None, expect=None):
        def coding_user():
            os.setgroups([])
            os.setgid(du.CODING_UID)
            os.setuid(du.CODING_UID)

        payload = dict(mode=mode, workspace=str(self.workspace), path=path or [])
        if expect is not None:
            payload["expect"] = expect
        process = subprocess.run([sys.executable, "-I", "-B", str(HERE / "disk_usage.py"),
                                  json.dumps(payload)], cwd="/", preexec_fn=coding_user,
                                 check=True, capture_output=True, text=True, timeout=45)
        return json.loads(process.stdout)

    def test_fresh_volume_and_unreadable_user_data_with_real_coding_user(self):
        result = self.helper()
        self.assertTrue(result["complete"])
        self.assertEqual({}, result["errors"])
        recovery = result["entries"][0]
        self.assertEqual(("lost+found", "excluded", "ext4_recovery", None, False, False),
                         (recovery["name"], recovery["state"], recovery["exclusion"], recovery["bytes"],
                          recovery["selectable"], recovery["deletable"]))
        self.assertEqual(result["filesHereBytes"], result["totalBytes"])
        refused = self.helper("delete", ["lost+found", "recovered"], dict(dev=1, ino=1))
        self.assertEqual("protected_path", refused["refused"])
        repos = self.workspace / "repos"
        repos.mkdir()
        os.chown(repos, du.CODING_UID, du.CODING_UID)
        for name in ("private-user-data", "lost+found"):
            with self.subTest(name=name):
                private = repos / name
                private.mkdir(mode=0o700)
                write(private / "user.bin", 8192)
                result = self.helper()
                self.assertFalse(result["complete"])
                self.assertEqual({"permission_denied": 1}, result["errors"])
                nested = self.helper(path=["repos"])
                self.assertEqual(("unreadable", ["permission_denied"]),
                                 (nested["entries"][0]["state"], nested["entries"][0]["errors"]))
                self.assertNotIn("exclusion", nested["entries"][0])
                (private / "user.bin").unlink()
                private.rmdir()
        bind = self.root / "bind-source"
        bind.mkdir()
        write(bind / "private.bin", 8192)
        subprocess.run(["mount", "--bind", str(bind), str(self.workspace / "lost+found")],
                       check=True, capture_output=True)
        try:
            result = self.helper()
            row = next(row for row in result["entries"] if row["name"] == "lost+found")
            self.assertNotIn("exclusion", row)
            self.assertEqual(("excluded", None, ["separate_mount"]),
                             (row["state"], row["bytes"], row["errors"]))
        finally:
            subprocess.run(["umount", str(self.workspace / "lost+found")], check=True, capture_output=True)


class FakeProc:
    def __init__(self, root):
        self.root = Path(root)
        (self.root / "sys/kernel/random").mkdir(parents=True)
        (self.root / "self").mkdir()
        self.boot("11111111-1111-1111-1111-111111111111")
        self.cpu(100, 900)
        (self.root / "meminfo").write_text("MemTotal:       8000000 kB\nMemFree: 1 kB\nMemAvailable:   2000000 kB\n")
        (self.root / "self/mountinfo").write_text(
            "1 0 8:1 / / rw - ext4 /dev/sda1 rw\n"
            "2 1 8:2 / /workspace rw - ext4 /dev/sdb rw\n"
            "3 2 0:5 / /workspace/repos/with\\040space rw - tmpfs tmpfs rw\n"
            "4 2 8:1 /var/lib/workspaces-scratch/repos/p/target /workspace/repos/p/target rw - ext4 /dev/sda1 rw\n")

    def boot(self, value):
        (self.root / "sys/kernel/random/boot_id").write_text(value + "\n")

    def cpu(self, busy, idle, cpus=2):
        (self.root / "stat").write_text(
            f"cpu  {busy} 0 0 {idle} 0 0 0 0 50 0\n" +
            "".join(f"cpu{index} 0 0 0 0 0 0 0 0 0 0\n" for index in range(cpus)) + "intr 0\n")


class ResourceSamplerTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="proc-", dir=ARTIFACTS)
        self.addCleanup(self.temp.cleanup)
        self.proc = FakeProc(self.temp.name)
        self.now = 1000.0
        devices = {"/": 1, "/workspace": 2, rt.SCRATCH_ROOT: 1}
        self.vfs = lambda path: SimpleNamespace(f_frsize=4096, f_bsize=4096, f_blocks=1000, f_bfree=600,
                                                f_bavail=500, f_files=100, f_favail=40)
        self.devices = devices
        self.sampler = rt.ResourceSampler(proc=self.temp.name, monotonic=lambda: self.now, wall=lambda: 5000.0,
                                          statvfs=lambda path: self.vfs(path),
                                          stat_path=self.stat_path)
        self.config = dict(vmId="vm-a", generation=3, workspace="/workspace")

    def stat_path(self, path):
        if path not in self.devices:
            raise FileNotFoundError(path)
        return SimpleNamespace(st_dev=self.devices[path])

    def test_cpu_needs_two_samples_and_cache_limits_sampling(self):
        first = self.sampler.sample(self.config)
        self.assertEqual(2, first["cpu"]["logicalCount"])
        self.assertIsNone(first["cpu"]["utilizationPercent"])
        self.assertEqual("warming_up", first["unavailable"]["cpu.utilizationPercent"])
        self.assertEqual(dict(totalBytes=8000000 * 1024, availableBytes=2000000 * 1024), first["memory"])
        self.proc.cpu(400, 1000)
        self.now += 10
        self.assertIs(first, self.sampler.sample(self.config))
        self.now += 25
        second = self.sampler.sample(self.config)
        self.assertEqual(75.0, second["cpu"]["utilizationPercent"])
        self.assertEqual(35.0, second["cpu"]["windowSeconds"])
        self.assertNotIn("cpu.utilizationPercent", second["unavailable"])

    def test_reboot_counter_regression_and_cpu_count_change_reset_cpu(self):
        self.sampler.sample(self.config)
        for change in (lambda: self.proc.boot("22222222-2222-2222-2222-222222222222"),
                       lambda: self.proc.cpu(10, 10), lambda: self.proc.cpu(5000, 9000, cpus=4)):
            change()
            self.now += 31
            result = self.sampler.sample(self.config)
            self.assertIsNone(result["cpu"]["utilizationPercent"])
            self.assertEqual("counters_reset", result["unavailable"]["cpu.utilizationPercent"])

    def test_filesystems_report_reserved_space_inodes_and_nested_mounts(self):
        result = self.sampler.sample(self.config)
        self.assertEqual([["/"], ["/workspace"]], [item["paths"] for item in result["filesystems"]])
        self.assertEqual([["system", "scratch"], ["workspace"]], [item["roles"] for item in result["filesystems"]])
        self.assertNotIn("filesystems.scratch", result["unavailable"])
        workspace = result["filesystems"][1]
        self.assertEqual((0, 1), (result["filesystems"][0]["scratchBindMounts"], workspace["scratchBindMounts"]))
        self.assertEqual((4096000, 2457600, 2048000), (workspace["totalBytes"], workspace["freeBytes"],
                                                        workspace["availableBytes"]))
        self.assertEqual((100, 40, 1), (workspace["inodesTotal"], workspace["inodesAvailable"],
                                        workspace["nestedMountsExcluded"]))
        self.devices["/workspace"] = 1
        self.now += 31
        self.assertEqual([["/", "/workspace"]], [item["paths"] for item in self.sampler.sample(self.config)["filesystems"]])

    def test_scratch_is_never_counted_twice_and_is_reported_when_separate_or_missing(self):
        statvfs = []
        self.vfs = lambda path: statvfs.append(path) or SimpleNamespace(
            f_frsize=4096, f_bsize=4096, f_blocks=1000, f_bfree=600, f_bavail=500, f_files=100, f_favail=40)
        for device, roles, reason in ((2, [["system"], ["workspace", "scratch"]], None),
                                      (3, [["system"], ["workspace"]], "separate_device"),
                                      (None, [["system"], ["workspace"]], "unreadable")):
            with self.subTest(device=device):
                if device is None:
                    del self.devices[rt.SCRATCH_ROOT]
                else:
                    self.devices[rt.SCRATCH_ROOT] = device
                statvfs.clear()
                self.now += 31
                result = self.sampler.sample(self.config)
                self.assertEqual(roles, [item["roles"] for item in result["filesystems"]])
                self.assertEqual([["/"], ["/workspace"]], [item["paths"] for item in result["filesystems"]])
                self.assertEqual(["/", "/workspace"], statvfs)
                self.assertEqual(reason, result["unavailable"].get("filesystems.scratch"))
                self.assertNotIn("filesystems", result["unavailable"])

    def test_missing_values_are_null_with_reason_and_hung_probe_is_bounded(self):
        (Path(self.temp.name) / "meminfo").unlink()
        release = threading.Event()
        self.vfs = lambda path: release.wait(5) and None
        self.sampler.probe_seconds = 0.05
        result = self.sampler.sample(self.config)
        self.assertIsNone(result["memory"])
        self.assertEqual("unreadable", result["unavailable"]["memory"])
        self.assertEqual("probe_timeout", result["unavailable"]["filesystems"])
        self.now += 31
        self.assertEqual("probe_pending", self.sampler.sample(self.config)["unavailable"]["filesystems"])
        release.set()
        self.sampler.probe.join(5)

    def test_retained_repos_and_home_usage_is_counted_by_one_workspace_probe(self):
        allocated = dict(repos=7, home=11)
        calls = []

        def statvfs(path):
            calls.append(path)
            used = sum(allocated.values()) if path == "/workspace" else 3
            return SimpleNamespace(f_frsize=4096, f_blocks=100, f_bfree=100 - used,
                                   f_bavail=100 - used, f_files=100, f_favail=40)

        self.vfs = statvfs
        for folder in (None, "repos", "home"):
            if folder:
                allocated[folder] += 5
            result = self.sampler.sample(self.config)
            workspace = next(item for item in result["filesystems"] if "/workspace" in item["paths"])
            self.assertEqual(sum(allocated.values()) * 4096,
                             workspace["totalBytes"] - workspace["freeBytes"])
            self.assertEqual(["/", "/workspace"], calls)
            calls.clear()
            self.now += 31


class FilesystemLinux:
    def __init__(self):
        self.running = set()
        self.stopped = []
        self.launches = []
        self.hold = False
        self.events = []

    def ephemeral(self):
        pass

    def layout(self):
        pass

    def filesystem_jobs_supported(self):
        return True

    def filesystem_launch(self, unit, payload, output):
        self.launches.append(payload)
        if self.hold:
            self.running.add(unit)
            return
        with patch.object(sys, "stdout", io.StringIO()) as stream:
            du.main([json.dumps(payload)])
        Path(output).write_text(stream.getvalue())

    def filesystem_running(self, unit):
        return unit in self.running

    def filesystem_stop(self, unit):
        self.stopped.append(unit)
        self.running.discard(unit)

    def idle(self, tasks):
        self.events.append("idle")
        raise rt.Blocked("Stop here")

    def task_running(self, task):
        return False


class RuntimeFilesystemJobTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="runtime-", dir=ARTIFACTS)
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.workspace = self.root / "workspace"
        write(self.workspace / "repos/project/file.bin", 8192)
        write(self.workspace / "home/cache/file.bin", 8192)
        self.config = dict(vmId="vm-a", generation=3, kind="ephemeral", uid=2000, gid=2000,
                           workspace=str(self.workspace), ignoredDataAcknowledged=True, repositories=[])
        self.adapter = FilesystemLinux()
        self.runtime = rt.Runtime(self.config, self.root / "state", self.adapter,
                                  rt.Git(allow_local=True, verification_dir=self.root / "verify"), clock=lambda: 1000)
        self.runtime.s["phase"] = "Ready"
        patcher = patch.object(du, "CODING_UID", os.getuid())
        patcher.start()
        self.addCleanup(patcher.stop)

    def request(self, action, operation="fsjob-operation", **kwargs):
        return self.runtime.handle(dict(vmId="vm-a", generation=3, operationId=operation, action=action,
                                        gatewayAdmissionsFenced=True, unobservableToolsAbsent=True,
                                        t3SnapshotSequence=0, **kwargs))

    def test_status_advertises_capabilities_and_resources_without_changing_revision(self):
        before = self.runtime.s["revision"]
        result = self.request("status")
        self.assertEqual(["resources-v1", "disk-usage-v1", "directory-delete-v1"], result["capabilities"])
        self.assertEqual(1, result["resources"]["schemaVersion"])
        self.assertEqual("vm-a", result["resources"]["vmId"])
        self.assertEqual(before, self.runtime.s["revision"])

    def test_status_survives_sampler_failure_and_old_adapter_has_no_folder_jobs(self):
        broken = SimpleNamespace(sample=lambda config: 1 / 0)
        runtime = rt.Runtime(self.config, self.root / "state", object(), clock=lambda: 1000, resources=broken,
                             git=rt.Git(allow_local=True, verification_dir=self.root / "verify"))
        result = runtime.handle(dict(vmId="vm-a", generation=3, operationId="op-status", action="status"))
        self.assertEqual(["resources-v1"], result["capabilities"])
        self.assertEqual("sampling_failed", result["resources"]["unavailable"]["sample"])
        with self.assertRaises(rt.Blocked):
            runtime.handle(dict(vmId="vm-a", generation=3, operationId="op-start", action="fs-job-start",
                                jobId="job-00000001", mode="scan", path=[]))

    def test_scan_and_delete_jobs_are_idempotent_and_confined(self):
        scan = self.request("fs-job-start", jobId="job-scan-1", mode="scan", path=["repos"])
        self.assertEqual("completed", scan["state"])
        row = scan["result"]["entries"][0]
        self.assertEqual("project", row["name"])
        self.assertEqual(scan, self.request("fs-job-start", jobId="job-scan-1", mode="scan", path=["repos"]))
        self.assertEqual(1, len(self.adapter.launches))
        with self.assertRaises(rt.Blocked):
            self.request("fs-job-start", jobId="job-scan-1", mode="scan", path=["home"])
        for path in (["repos"], ["t3", "x"], ["..", "x"], ["repos", "a/b"]):
            with self.assertRaises(rt.Blocked):
                self.request("fs-job-start", jobId="job-delete-x", mode="delete", path=path,
                             expect=dict(dev=row["dev"], ino=row["ino"]))
        invalidations = []
        self.runtime.resources = SimpleNamespace(invalidate=lambda: invalidations.append(True))
        deleted = self.request("fs-job-start", jobId="job-delete-1", mode="delete", path=["repos", "project"],
                               expect=dict(dev=row["dev"], ino=row["ino"]))
        self.assertEqual("succeeded", deleted["result"]["outcome"])
        self.assertEqual([True], invalidations)
        self.assertFalse((self.workspace / "repos/project").exists())
        self.assertEqual("not_found", self.request("fs-job-status", jobId="job-unknown-1")["state"])

    def test_one_job_at_a_time_cancel_and_freeze_stops_running_jobs(self):
        self.adapter.hold = True
        running = self.request("fs-job-start", jobId="job-scan-2", mode="scan", path=[])
        self.assertEqual("running", running["state"])
        with self.assertRaises(rt.Blocked):
            self.request("fs-job-start", jobId="job-scan-3", mode="scan", path=[])
        self.assertEqual("lost", self.request("fs-job-cancel", jobId="job-scan-2")["state"])
        self.assertEqual(1, len(self.adapter.stopped))
        self.request("fs-job-start", jobId="job-scan-4", mode="scan", path=[])
        with self.assertRaises(rt.Blocked):
            self.request("git-save", operation="stop-operation")
        self.assertEqual(2, len(self.adapter.stopped))
        self.assertEqual(["idle"], self.adapter.events)

    def test_jobs_are_refused_while_stopping(self):
        self.runtime.s["fenceOperation"] = "stop-operation"
        with self.assertRaises(rt.Blocked):
            self.request("fs-job-start", jobId="job-scan-5", mode="scan", path=[])

    def test_linux_prepares_host_mounts_before_creating_coding_job_namespace(self):
        linux = rt.Linux(self.config)
        payload = dict(mode="delete", workspace=str(self.workspace), path=["repos", "project"],
                       expect=identity(self.workspace / "repos/project"), jobId="job-delete-native",
                       vmId="vm-a", generation=3)
        events = []
        preparation = dict(ready=True, inhibited=True, chain=[payload["expect"]], unmountedMounts=1)

        def prepare(command, request):
            events.append((command, request))
            return preparation

        def launch(command):
            events.append(("launch", command))

        with patch.object(linux, "filesystem_scratch", side_effect=prepare), \
                patch.object(rt, "run", side_effect=launch):
            result = linux.filesystem_launch("fixture-unit", payload, self.root / "output")
        self.assertEqual(preparation, result)
        self.assertEqual(["prepare-delete", "launch"], [event[0] for event in events])
        command = events[1][1]
        self.assertIn("--uid=2000", command)
        self.assertIn("--property=KillMode=control-group", command)
        self.assertIn("--property=ProtectSystem=strict", command)
        child = json.loads(command[-1])
        self.assertEqual((3, "vm-a", preparation["chain"]),
                         (child["generation"], child["vmId"], child["chain"]))
        denied = dict(schemaVersion=1, refused="checkout_in_use")
        with patch.object(linux, "filesystem_scratch", return_value=denied), \
                patch.object(rt, "run") as no_launch:
            linux.filesystem_launch("fixture-unit", payload, self.root / "denied")
        no_launch.assert_not_called()
        self.assertEqual(denied, json.loads((self.root / "denied").read_text()))

    def test_local_checkout_jobs_skip_scratch_preparation(self):
        config = dict(self.config, kind="primary", storageLayout=2, ignoredDataAcknowledged=False,
                      volume=dict(providerVolumeId="vol-1"))
        self.adapter.verify_workspace_mount = lambda: None
        self.adapter.filesystem_prepare = lambda payload: self.fail("Layout 2 has no scratch preparation")
        self.adapter.filesystem_finish = lambda record: self.fail("Layout 2 has no scratch release")
        runtime = rt.Runtime(config, self.root / "local-state", self.adapter, git=object(), clock=lambda: 1000)
        runtime.s["phase"] = "Ready"
        target = identity(self.workspace / "repos/project")
        with patch.object(du, "open_root", side_effect=volume_root):
            deleted = runtime.handle(dict(vmId="vm-a", generation=3, operationId="op", action="fs-job-start",
                                          jobId="job-local-1", mode="delete", path=["repos", "project"],
                                          expect=target))
        self.assertTrue(self.adapter.launches[-1]["localRepos"])
        self.assertEqual("succeeded", deleted["result"]["outcome"])
        linux = rt.Linux(config)
        payload = dict(mode="delete", workspace=str(self.workspace), path=["repos", "x"], expect=target,
                       jobId="job-local-2", vmId="vm-a", generation=3, localRepos=True)
        with patch.object(linux, "filesystem_scratch", side_effect=AssertionError), \
                patch.object(rt, "run") as launch:
            self.assertIsNone(linux.filesystem_launch("unit", payload, self.root / "out"))
            launch.assert_called_once()
            with self.assertRaisesRegex(rt.Blocked, "storage layout"):
                linux.filesystem_launch("unit", dict(payload, localRepos=False), self.root / "out")

    def test_runtime_retains_inhibition_on_cancel_partial_and_lost_jobs(self):
        self.adapter.hold = True
        self.adapter.filesystem_finish = lambda record: self.fail("Unsafe inhibition release")
        original = self.adapter.filesystem_launch

        def launch(*args):
            original(*args)
            return dict(ready=True, inhibited=True, unmountedMounts=1)

        self.adapter.filesystem_launch = launch
        job = "job-delete-cancel"
        self.request("fs-job-start", jobId=job, mode="delete", path=["repos", "project"],
                     expect=identity(self.workspace / "repos/project"))
        self.assertEqual("lost", self.request("fs-job-cancel", jobId=job)["state"])
        output = self.root / "state/fsjobs" / (job + ".out")
        output.write_text(json.dumps(dict(mode="delete", outcome="partially_deleted",
                                         removedEntries=1, errors={"permission_denied": 1})))
        result = self.request("fs-job-status", jobId=job)["result"]
        self.assertTrue(result["remountSuppressed"])
        self.assertEqual(1, result["unmountedMounts"])
        output.write_text(json.dumps(dict(schemaVersion=1, refused="target_changed")))
        result = self.request("fs-job-status", jobId=job)["result"]
        self.assertEqual(("failed", 0, {"target_changed": 1}),
                         (result["outcome"], result["removedEntries"], result["errors"]))
        self.assertNotIn("refused", result)
        self.assertTrue(result["remountSuppressed"])

    def test_scratch_subprocess_timeouts_and_death_are_blocked_not_lost(self):
        linux = rt.Linux(self.config)
        payload = dict(mode="delete", workspace=str(self.workspace), path=["repos", "project"],
                       expect=identity(self.workspace / "repos/project"), jobId="job-delete-native",
                       vmId="vm-a", generation=3)
        for result in (subprocess.TimeoutExpired("scratch helper", 30),
                       subprocess.CompletedProcess([], -9, "", "")):
            with self.subTest(result=type(result).__name__):
                with patch.object(rt.subprocess, "run", side_effect=result if isinstance(result, Exception)
                                  else None, return_value=result):
                    with self.assertRaises(rt.Blocked):
                        linux.filesystem_prepare(payload)


if __name__ == "__main__":
    unittest.main()
