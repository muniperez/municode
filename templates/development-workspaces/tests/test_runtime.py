#!/usr/bin/env python3
"""No cloud, mounts, Docker, credentials or installed T3 required."""
import copy
import io
import json
import os
from pathlib import Path
import shutil
import stat
import subprocess
import sys
import tempfile
import time
from types import SimpleNamespace
import unittest
from unittest.mock import patch

HERE = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(HERE))
import repo_checkpoint as rc
import runtime as rt

ARTIFACTS = HERE.parents[1] / ".local/artifacts/runtime"
ARTIFACTS.mkdir(parents=True, exist_ok=True)


def git(path, *args):
    env = dict(os.environ, GIT_CONFIG_NOSYSTEM="1", GIT_CONFIG_GLOBAL="/dev/null",
               GIT_AUTHOR_NAME="Fixture", GIT_AUTHOR_EMAIL="fixture@example.invalid",
               GIT_COMMITTER_NAME="Fixture", GIT_COMMITTER_EMAIL="fixture@example.invalid",
               GIT_AUTHOR_DATE="2025-01-01T00:00:00+00:00", GIT_COMMITTER_DATE="2025-01-01T00:00:00+00:00")
    return subprocess.run(["git", "-C", str(path), *args], env=env, check=True,
                          capture_output=True, text=True, timeout=30).stdout.strip()


class FakeLinux:
    def __init__(self):
        self.events = []
        self.live = False
        self.unknown = False
        self.frozen = False
        self.ro = False
        self.mounted = False
        self.uuid = "fixture-fs"
        self.owner = {"workspaceId": "workspace"}
        self.launches = 0
        self.ambiguous = False
        self.on_freeze = None
        self.checkpoint_error = None
        self.checkpoints = 0
        self.checkpoint_state = dict(state="empty")

    def mount(self, claim):
        self.events.append("mount")
        rt.require(claim["filesystemUuid"] == self.uuid, "Filesystem UUID mismatch")
        rt.require(claim["owner"] == self.owner, "Unexpected volume owner claim")
        self.mounted = True

    def ephemeral(self):
        self.events.append("ephemeral")
        rt.require(not self.mounted, "Ephemeral cannot mount primary")

    def verify_workspace_mount(self):
        rt.require(self.mounted, "Retained volume is not mounted")

    def layout(self):
        self.events.append("layout")

    def restore_ownership(self):
        self.events.append("restore-ownership")

    def launch(self, task_id, argv):
        self.launches += 1
        self.events.append("launch")
        if self.ambiguous:
            raise rt.Blocked("Ambiguous launch")
        self.live = True
        return "fixture-" + task_id + ".service"

    def task_running(self, task):
        rt.require("unit" in task, "Ambiguous task launch requires reconciliation")
        return self.live

    def idle(self, tasks):
        self.events.append("idle")
        rt.require(not self.unknown, "Unregistered live process")
        for task in tasks.values():
            rt.require(not self.task_running(task), "Registered task still running")

    def verify_t3_snapshot(self, sequence):
        self.events.append("t3-proof")
        return dict(type="T3CommittedIdle", threadSequence=0, globalV2Sequence=0, applicationSequence=0)

    def freeze(self):
        self.events.append("freeze")
        self.frozen = True
        if self.on_freeze:
            self.on_freeze()

    def thaw(self):
        self.frozen = False

    def writable(self):
        self.ro = False

    def start_services(self):
        self.events.append("start-services")

    def stop_services(self):
        self.events.append("stop-services")

    def readonly(self):
        self.events.append("readonly")
        self.ro = True

    def verify_fence(self, unmounted=False):
        rt.require(self.frozen and self.ro and (not unmounted or not self.mounted),
                   "Freeze no longer valid")

    def unmount(self):
        self.events.append("unmount")
        self.mounted = False

    def scratch_suspend(self):
        self.events.append("scratch-suspend")

    def scratch_detach(self):
        self.events.append("scratch-unmount")
        return dict(unmounted=2, lazy=0, lazyPaths=[])

    def scratch_resume(self):
        self.events.append("scratch-resume")

    def migrate_layout(self, requested):
        self.events.append("migrate-layout" if requested else "migration-check")

    def finish_layout_migration(self):
        return None

    def attach_repos(self, restore=True):
        self.events.append("attach-repos")

    def checkpoint_repos(self, operation):
        self.events.append("repos-checkpoint")
        rt.require(self.frozen and not self.ro and self.mounted, "Checkpoint needs a fenced writable volume")
        if self.checkpoint_error:
            raise rt.Blocked("Final checkout checkpoint failed: " + self.checkpoint_error)
        self.checkpoints += 1
        return dict(checkpointId="c-%020d-%016x" % (self.checkpoints, self.checkpoints),
                    manifestSha256="a" * 64, operationId=operation, binding={}, files=1, bytes=1, durationMs=1)

    def verify_checkpoint(self, checkpoint, operation):
        self.events.append("repos-checkpoint-verify")
        rt.require(checkpoint["operationId"] == operation and self.mounted, "Final checkout checkpoint is not verifiable")

    def stop_periodic_checkpoints(self):
        self.events.append("periodic-checkpoint-stop")

    def checkpoint_status(self):
        if isinstance(self.checkpoint_state, Exception):
            raise self.checkpoint_state
        return self.checkpoint_state


class ManagedT3StopTests(unittest.TestCase):
    unit = "workspaces-t3.service"
    failed = dict(ActiveState="failed", SubState="failed", Result="signal", Job="", MainPID="0", ControlPID="0")
    stopped = dict(failed, ActiveState="inactive", SubState="dead", Result="success")

    def setUp(self):
        self.adapter = rt.Linux(dict(workspace="/workspace"))
        self.events = []

    def stop(self, *states, populated=(False, False, False)):
        def systemctl(argv):
            self.events.append(argv[1:])
            if argv[1] == "show":
                return "\n".join(f"{key}={value}" for key, value in next(state).items())
            return ""

        state = iter(states)
        with patch.object(rt, "run", side_effect=systemctl), \
                patch.object(self.adapter, "thaw", side_effect=lambda: self.events.append(["thaw"])), \
                patch.object(self.adapter, "freeze", side_effect=lambda: self.events.append(["freeze"])), \
                patch.object(Path, "read_text", side_effect=[
                    f"populated {int(value)}\n" for value in populated]):
            self.adapter.stop_services()

    def test_normalizes_crash_result_only_after_stop_and_refreeze(self):
        self.stop(self.failed, self.stopped)
        self.assertEqual(self.events[:4], [["stop", "--no-block", self.unit], ["thaw"],
                                          ["stop", self.unit], ["freeze"]])
        self.assertEqual([command[0] for command in self.events[4:]], ["show", "reset-failed", "show"])

    def test_preserves_unrelated_failure_and_never_resets_pending_or_populated_unit(self):
        for change, populated, reason in [
                (dict(Result="timeout"), False, "did not stop cleanly"),
                (dict(Job="42"), False, "still has a job or processes"),
                (dict(ControlPID="124"), False, "still has a job or processes"),
                ({}, True, "still has a job or processes")]:
            with self.subTest(change=change, populated=populated):
                self.events.clear()
                with self.assertRaisesRegex(rt.Blocked, reason):
                    self.stop(self.failed | change, populated=(False, populated))
                self.assertNotIn(["reset-failed", self.unit], self.events)

    def test_rechecks_for_jobs_after_reset(self):
        with self.assertRaisesRegex(rt.Blocked, "still has a job or processes"):
            self.stop(self.failed, self.stopped | dict(Job="42"))

    def test_waits_for_cgroup_to_drain_before_refreezing(self):
        with patch.object(rt.time, "sleep", side_effect=lambda _: self.events.append(["wait"])):
            self.stop(self.stopped, populated=[True, False, False])
        self.assertEqual(self.events[2:5], [["stop", self.unit], ["wait"], ["freeze"]])


class ManagedStopThawTests(unittest.TestCase):
    unit = "workspaces-t3.service"
    slice = "workspaceswork.slice"

    def setUp(self):
        self.adapter = rt.Linux(dict(workspace="/workspace"))
        self.commands = []
        self.observations = 0
        self.clock = 0
        self.thaw_error = subprocess.CalledProcessError(
            1, ["systemctl", "thaw", self.slice], stderr="private systemctl reason")

    def stop(self, proofs=(("frozen 0", "running"),), thaw_error=True,
             evidence_error=None, stop_error=None, stopped=True, query_delay=0):
        def systemctl(argv, **kwargs):
            self.commands.append(argv[1:])
            if argv[1] == "thaw" and thaw_error:
                raise self.thaw_error
            if argv[1] == "stop" and stop_error and ("--no-block" in argv) == (stop_error == "queue"):
                raise subprocess.CalledProcessError(1, argv)
            if "FreezerState" in argv:
                self.assertGreater(kwargs["timeout"], 0)
                self.assertLessEqual(kwargs["timeout"], 1)
                if evidence_error == "systemctl":
                    raise subprocess.CalledProcessError(1, argv)
                if evidence_error == "timeout":
                    raise subprocess.TimeoutExpired(argv, kwargs["timeout"])
                state = proofs[min(self.observations, len(proofs) - 1)][1]
                self.observations += 1
                self.clock += query_delay
                return SimpleNamespace(stdout=state)
            if argv[1] == "show":
                state = ManagedT3StopTests.stopped | ({} if stopped else dict(Job="42"))
                return SimpleNamespace(stdout="\n".join(f"{key}={value}" for key, value in state.items()))
            return SimpleNamespace(stdout="")

        def events(path, *args, **kwargs):
            self.assertEqual(str(path), "/sys/fs/cgroup/workspaceswork.slice/cgroup.events")
            if evidence_error == "missing":
                raise FileNotFoundError(str(path))
            if evidence_error == "permission":
                raise PermissionError(str(path))
            return proofs[min(self.observations, len(proofs) - 1)][0] + "\n"

        def sleep(seconds):
            self.clock += seconds

        with patch.object(rt.subprocess, "run", side_effect=systemctl), \
                patch.object(Path, "read_text", autospec=True, side_effect=events) as read, \
                patch.object(rt.time, "monotonic", side_effect=lambda: self.clock), \
                patch.object(rt.time, "sleep", side_effect=sleep), \
                patch.object(self.adapter, "t3_cgroup_empty", return_value=True), \
                patch.object(self.adapter, "freeze", side_effect=lambda: self.commands.append(["freeze"])):
            self.adapter.stop_services()
            return read.call_count

    def assert_original_thaw_refusal(self, **kwargs):
        with self.assertRaises(rt.Blocked) as caught:
            self.stop(**kwargs)
        self.assertIs(caught.exception.__cause__, self.thaw_error)
        self.assertNotIn(["stop", self.unit], self.commands)
        self.assertEqual(self.commands[-1], ["freeze"])
        return caught.exception

    def test_normal_thaw_needs_no_reconciliation(self):
        self.assertEqual(self.stop(thaw_error=False), 0)
        self.assertEqual(self.commands[:4], [["stop", "--no-block", self.unit],
                                          ["thaw", self.slice], ["stop", self.unit], ["freeze"]])
        self.assertEqual(self.observations, 0)

    def test_pending_stop_already_thawed_by_systemd_continues_and_refreezes(self):
        self.assertEqual(self.stop(), 1)
        self.assertEqual(self.commands[:5], [["stop", "--no-block", self.unit],
                                          ["thaw", self.slice],
                                          ["show", self.slice, "-p", "FreezerState", "--value"],
                                          ["stop", self.unit], ["freeze"]])

    def test_transient_freezer_states_wait_for_both_proofs(self):
        self.assertEqual(self.stop(proofs=(("frozen 1", "thawing"),
                                         ("frozen 0", "thawing"), ("frozen 0", "running"))), 3)
        self.assertEqual(self.clock, 0.1)
        self.assertIn(["stop", self.unit], self.commands)

    def test_unproven_or_disagreeing_states_preserve_original_failure_within_bound(self):
        for proof in (("frozen 1", "running"), ("frozen 0", "frozen"),
                      ("populated 0", "running"), ("frozen 0", "")):
            with self.subTest(proof=proof):
                self.setUp()
                self.assert_original_thaw_refusal(proofs=(proof,))
                self.assertLessEqual(self.clock, 5)
                self.assertLessEqual(self.observations, 101)

    def test_unavailable_evidence_preserves_original_failure(self):
        for failure in ("missing", "permission", "systemctl", "timeout"):
            with self.subTest(failure=failure):
                self.setUp()
                error = self.assert_original_thaw_refusal(evidence_error=failure)
                self.assertEqual(rt.failure_response(error, "stop-services")["diagnostic"],
                                 dict(stage="stop-services", failureType="tool_exit", exitCode=1,
                                      tool="systemctl", toolAction="thaw"))

    def test_late_proof_does_not_extend_deadline(self):
        self.assert_original_thaw_refusal(query_delay=5)
        self.assertEqual(self.observations, 1)

    def test_thaw_timeout_and_launch_failure_are_not_reconciled(self):
        for error in (subprocess.TimeoutExpired(["systemctl", "thaw", self.slice], 120),
                      FileNotFoundError("systemctl"),
                      subprocess.CalledProcessError(-15, ["systemctl", "thaw", self.slice]),
                      subprocess.CalledProcessError(1, ["systemctl", "thaw", "unmanaged.slice"])):
            with self.subTest(error=type(error).__name__):
                self.setUp()
                self.thaw_error = error
                self.assert_original_thaw_refusal()
                self.assertEqual(self.observations, 0)

    def test_queue_failure_never_thaws_and_synchronous_stop_failure_still_refreezes(self):
        for failure in ("queue", "sync"):
            with self.subTest(failure=failure):
                self.setUp()
                with self.assertRaises(rt.Blocked) as caught:
                    self.stop(stop_error=failure)
                self.assertEqual(caught.exception.__cause__.cmd[1], "stop")
                self.assertEqual(self.commands[-1], ["freeze"])
                if failure == "queue":
                    self.assertNotIn(["thaw", self.slice], self.commands)
                    self.assertEqual(self.observations, 0)

    def test_reconciliation_does_not_bypass_stopped_unit_verification(self):
        with self.assertRaisesRegex(rt.Blocked, "still has a job or processes"):
            self.stop(stopped=False)
        self.assertIn(["stop", self.unit], self.commands)
        self.assertIn(["freeze"], self.commands)

    def test_unmanaged_thaw_still_propagates_explicit_failure(self):
        with patch.object(rt.subprocess, "run", side_effect=self.thaw_error), \
                patch.object(Path, "read_text") as read:
            with self.assertRaises(rt.Blocked) as caught:
                self.adapter.thaw()
        self.assertIs(caught.exception.__cause__, self.thaw_error)
        read.assert_not_called()


class LayoutTests(unittest.TestCase):
    def test_layout_prepares_only_tooling_and_preserves_user_application_data(self):
        for existing_home in (False, True):
            with self.subTest(existing_home=existing_home), \
                    tempfile.TemporaryDirectory(prefix="layout-", dir=ARTIFACTS) as directory:
                workspace = Path(directory)
                application = workspace / "user-data"
                application.mkdir(mode=0o700)
                marker = application / "state.bin"
                marker.write_bytes(b"arbitrary user application state\0")
                if existing_home:
                    (workspace / "home").mkdir(mode=0o700)
                adapter = rt.Linux(dict(workspace=str(workspace), uid=os.getuid(), gid=os.getgid()))
                previous = os.umask(0o077)
                try:
                    with patch.object(rt.os, "chown", wraps=os.chown) as chown, \
                            patch.object(rt, "run") as process:
                        adapter.layout()
                    self.assertEqual(workspace.stat().st_mode & 0o777, 0o755)
                    managed = {"repos", "t3", "transcripts", "artifacts", "config", "home"}
                    self.assertEqual(managed | {"user-data"}, {p.name for p in workspace.iterdir()})
                    for relative in managed:
                        path = workspace / relative
                        self.assertEqual(path.stat().st_mode & 0o777, 0o700)
                        self.assertTrue(os.access(path, os.X_OK))
                        chown.assert_any_call(path, os.getuid(), os.getgid())
                    codex = workspace / "home/.codex"
                    self.assertTrue(codex.is_dir())
                    self.assertEqual(codex.stat().st_mode & 0o777, 0o700)
                    chown.assert_any_call(codex, os.getuid(), os.getgid())
                    self.assertEqual(b"arbitrary user application state\0", marker.read_bytes())
                    process.assert_not_called()
                finally:
                    os.umask(previous)

    def test_managed_home_symlink_is_rejected_without_touching_user_data(self):
        for relative in ("home", "home/.codex"):
            with self.subTest(relative=relative), \
                    tempfile.TemporaryDirectory(prefix="layout-", dir=ARTIFACTS) as directory:
                root = Path(directory)
                workspace = root / "workspace"
                workspace.mkdir()
                outside = root / "outside"
                outside.mkdir()
                link = workspace / relative
                link.parent.mkdir(parents=True, exist_ok=True)
                link.symlink_to(outside, target_is_directory=True)
                adapter = rt.Linux(dict(workspace=str(workspace), uid=os.getuid(), gid=os.getgid()))
                with patch.object(rt.os, "chown"), patch.object(rt, "run") as run:
                    with self.assertRaisesRegex(rt.Blocked, "Workspace layout symlink"):
                        adapter.layout()
                self.assertEqual([], list(outside.iterdir()))
                run.assert_not_called()

    def test_restore_assigns_generic_user_data_to_coding_identity_only(self):
        with tempfile.TemporaryDirectory(prefix="ownership-", dir=ARTIFACTS) as directory:
            workspace = Path(directory)
            data = workspace / "custom-application" / "state"
            data.mkdir(parents=True)
            marker = data / "durable.bin"
            marker.write_bytes(b"engine-independent data")
            nested = data / "lost+found"
            nested.mkdir()
            nested_owner = nested / ".workspaces-volume.json"
            nested_owner.write_text("user-owned nested file")
            owner = workspace / ".workspaces-volume.json"
            owner.write_text("{}")
            adapter = rt.Linux(dict(workspace=str(workspace), uid=2000, gid=2000))
            with patch.object(rt.os, "chown") as chown:
                adapter.restore_ownership()
            self.assertEqual({workspace / "custom-application", data, marker, nested, nested_owner},
                             {call.args[0] for call in chown.call_args_list})
            for call in chown.call_args_list:
                self.assertEqual(call.args[1:], (2000, 2000))
                self.assertFalse(call.kwargs["follow_symlinks"])

    def test_workspace_symlink_is_rejected_without_changing_target_permissions(self):
        with tempfile.TemporaryDirectory(prefix="layout-", dir=ARTIFACTS) as directory:
            root = Path(directory)
            outside = root / "outside"
            outside.mkdir(mode=0o700)
            workspace = root / "workspace"
            workspace.symlink_to(outside, target_is_directory=True)
            adapter = rt.Linux(dict(workspace=str(workspace), uid=os.getuid(), gid=os.getgid()))
            with self.assertRaisesRegex(rt.Blocked, "Workspace layout symlink"):
                adapter.layout()
            self.assertEqual(outside.stat().st_mode & 0o777, 0o700)
            self.assertEqual(list(outside.iterdir()), [])


class RuntimeTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="case-", dir=ARTIFACTS)
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.workspace = self.root / "workspace"
        self.workspace.mkdir()
        (self.workspace / "repos").mkdir()
        self.remote = self.root / "remote.git"
        self.remote.mkdir()
        git(self.remote, "init", "--bare", "--initial-branch=main")
        self.repo = self.workspace / "repos/project"
        git(self.workspace, "clone", str(self.remote), str(self.repo))
        (self.repo / "file.txt").write_text("initial\n")
        (self.repo / ".gitignore").write_text("ignored/\n")
        git(self.repo, "add", ".")
        git(self.repo, "commit", "-m", "base")
        self.base = git(self.repo, "rev-parse", "HEAD")
        git(self.repo, "push", "origin", "main")
        git(self.repo, "checkout", "-b", "work/vm-a")
        git(self.repo, "push", "origin", "work/vm-a")
        # A genuinely independent second clone models another VM / external remote edits.
        self.second = self.root / "other-clone"
        git(self.root, "clone", str(self.remote), str(self.second))
        self.registration = dict(path=str(self.repo), origin=str(self.remote), baseSha=self.base,
                                 branch="work/vm-a", remoteRef="refs/heads/work/vm-a",
                                 pushAuthorized=True, defaultBranches=["main"])
        self.config = dict(vmId="vm-a", generation=3, kind="ephemeral", uid=2000, gid=2000,
                           workspace=str(self.workspace), ignoredDataAcknowledged=True,
                           repositories=[self.registration])
        self.adapter = FakeLinux()
        self.g = rt.Git(allow_local=True, verification_dir=self.root / "verify")
        self.runtime = rt.Runtime(self.config, self.root / "state", self.adapter, self.g, clock=lambda: 1000)
        self.request("bootstrap")

    def request(self, action, operation="operation-a", sequence=0, **kwargs):
        return self.runtime.handle(dict(vmId="vm-a", generation=3, operationId=operation,
                                        action=action, gatewayAdmissionsFenced=True,
                                        unobservableToolsAbsent=True, t3SnapshotSequence=sequence, **kwargs))

    def assert_blocked(self, action, match=None, **kwargs):
        with self.assertRaises(rt.Blocked) as error:
            self.request(action, **kwargs)
        if match:
            self.assertIn(match, str(error.exception))
        return error.exception

    def test_clean_pushed_saved_and_revalidated_without_local_git_writes(self):
        before = (self.repo / ".git/FETCH_HEAD").read_bytes() if (self.repo / ".git/FETCH_HEAD").exists() else None
        receipt = self.request("git-save")
        self.assertEqual("GitSaved", receipt["type"])
        self.assertEqual(self.base, receipt["repositories"][0]["remoteSha"])
        self.assertFalse(receipt["ignoredDataPreserved"])
        self.assertEqual("ReceiptValid", self.request("receipt-validate", receiptId=receipt["receiptId"])["type"])
        self.assertEqual(receipt, self.request("git-save"))
        after = (self.repo / ".git/FETCH_HEAD").read_bytes() if (self.repo / ".git/FETCH_HEAD").exists() else None
        self.assertEqual(before, after)
        durable = self.root / "state/receipts" / (receipt["receiptId"] + ".json")
        self.assertEqual(receipt, json.loads(durable.read_text()))
        # Remote-only recovery in another real clone.
        git(self.second, "checkout", "work/vm-a")
        self.assertEqual(self.base, git(self.second, "rev-parse", "HEAD"))

    def test_dirty_tracked_data_blocks_and_keeps_vm_frozen(self):
        (self.repo / "file.txt").write_text("uncommitted")
        error = self.assert_blocked("git-save", "tracked modifications")
        self.assertEqual("AwaitingGitSave", error.state)
        self.assertTrue(self.adapter.frozen)
        self.assertNotIn("unmount", self.adapter.events)
        self.assert_blocked("task-start", "fenced", taskId="new", argv=["true"])

    def test_frozen_git_save_retry_uses_application_cursor_after_project_only_use(self):
        (self.repo / "file.txt").write_text("uncommitted")

        def proof(sequence):
            rt.require(sequence is None or sequence == 5, "Stale application snapshot")
            return dict(type="T3CommittedIdle", threadSequence=0, globalV2Sequence=5,
                        applicationSequence=5)

        with patch.object(self.adapter, "verify_t3_snapshot", side_effect=proof):
            self.assert_blocked("git-save", "tracked modifications", sequence=5)
            self.assertTrue(self.runtime.s["freezeCompleted"])
            self.assert_blocked("git-save", "tracked modifications", sequence=5)
        self.assertNotIn("unmount", self.adapter.events)

    def test_git_preflight_is_advisory_leaves_admissions_open_and_does_not_mutate_checkout(self):
        state = copy.deepcopy(self.runtime.s)
        fetch_head = self.repo / ".git/FETCH_HEAD"
        before = fetch_head.read_bytes() if fetch_head.exists() else None
        result = self.request("git-check")
        self.assertEqual("GitSaveCandidate", result["type"])
        self.assertFalse(result["deletionEligible"])
        self.assertEqual(state, self.runtime.s)
        self.assertFalse(self.adapter.frozen)
        self.assertEqual(before, fetch_head.read_bytes() if fetch_head.exists() else None)
        (self.repo / "file.txt").write_text("user still editing")
        error = self.assert_blocked("git-check", "tracked")
        self.assertEqual("AwaitingGitSave", error.state)
        self.assertEqual("Ready", self.runtime.s["phase"])
        self.assertFalse(self.adapter.ro)
        self.request("activity", source="user-input")
        # A previous advisory check cannot stand in for the final frozen Git proof.
        self.assert_blocked("git-save", "tracked")

    def test_status_work_inventory_is_read_only_advisory_and_unknown_work_is_not_idle(self):
        before = copy.deepcopy(self.runtime.s)
        self.assertNotIn("workInventory", self.request("status"))
        result = self.request("status", includeWorkInventory=True)
        self.assertEqual("IdleCandidate", result["workInventory"]["state"])
        self.assertFalse(result["workInventory"]["authoritative"])
        self.adapter.unknown = True
        result = self.request("status", includeWorkInventory=True)
        self.assertEqual("BusyOrUnknown", result["workInventory"]["state"])
        self.assertEqual(before, self.runtime.s)
        self.assertFalse(self.adapter.frozen)

    def test_failed_postfreeze_t3_proof_can_resume_without_requiring_readonly_mount(self):
        with patch.object(self.adapter, "verify_t3_snapshot",
                          side_effect=rt.Blocked("T3 thread changed")):
            self.assert_blocked("git-save", "T3 thread")
        self.assertEqual("Frozen", self.runtime.s["phase"])
        self.assertFalse(self.adapter.ro)
        self.assertNotIn("stop-services", self.adapter.events)
        self.assertFalse(any(receipt["type"] in ("GitSaved", "DiscardAuthorized", "PrimaryQuiesced")
                             for receipt in self.runtime.s["receipts"].values()))
        with patch.object(self.adapter, "writable") as remount:
            self.request("resume", deletionNotCommitted=True)
            remount.assert_not_called()
        self.assertFalse(self.adapter.frozen)
        self.assertEqual("Ready", self.runtime.s["phase"])

    def test_refusal_reports_failing_freeze_stage_without_exception_or_command_output(self):
        failures = [
            ("verify_t3_snapshot", PermissionError(13, "private bearer", "/private/credential"),
             "t3-snapshot", "permission_denied", None),
            ("readonly", subprocess.CalledProcessError(
                32, ["mount", "/private/credential"], output="private bearer", stderr="private key"),
             "remount-readonly", "tool_exit", 32),
        ]
        for method, error, stage, failure_type, exit_code in failures:
            with self.subTest(method=method):
                with patch.object(self.adapter, method, side_effect=error):
                    response = self.runtime.respond(dict(
                        vmId="vm-a", generation=3, operationId="operation-a", action="git-save",
                        gatewayAdmissionsFenced=True, unobservableToolsAbsent=True, t3SnapshotSequence=0))
                self.assertFalse(response["ok"])
                self.assertEqual(stage, response["diagnostic"]["stage"])
                self.assertEqual(failure_type, response["diagnostic"]["failureType"])
                self.assertEqual(exit_code, response["diagnostic"].get("exitCode"))
                self.assertNotIn("private", json.dumps(response))
                self.assertEqual("Frozen", self.runtime.s["phase"])
                self.assertFalse(any(r["type"] == "GitSaved" for r in self.runtime.s["receipts"].values()))
                self.request("resume", deletionNotCommitted=True)

    def test_blocked_tool_failure_retains_only_allowlisted_tool_and_subcommand(self):
        with patch.object(rt.subprocess, "run", side_effect=subprocess.CalledProcessError(
                1, ["systemctl", "stop", "private.service"], stderr="private bearer")):
            with self.assertRaises(rt.Blocked) as caught:
                rt.run(["systemctl", "stop", "private.service"])
        response = rt.failure_response(caught.exception, "stop-services")
        self.assertEqual(dict(stage="stop-services", failureType="tool_exit",
                              tool="systemctl", toolAction="stop", exitCode=1), response["diagnostic"])
        self.assertNotIn("private", json.dumps(response))

    def test_untracked_data_blocks(self):
        (self.repo / "untracked.txt").write_text("unsaved")
        self.assert_blocked("git-save", "untracked")

    def test_ignored_data_policy_explicit_and_never_uploaded(self):
        (self.repo / "ignored").mkdir()
        (self.repo / "ignored/secret").write_text("synthetic-not-a-credential")
        receipt = self.request("git-save")
        self.assertFalse(receipt["ignoredDataPreserved"])
        self.assertFalse((self.second / "ignored").exists())
        self.assertNotIn("backup", self.adapter.events)
        self.assert_blocked("backup", "ephemeral")

    def test_unpushed_commit_blocks(self):
        (self.repo / "file.txt").write_text("new\n")
        git(self.repo, "add", ".")
        git(self.repo, "commit", "-m", "not-pushed")
        self.assertEqual("AwaitingGitSave", self.assert_blocked("git-save").state)

    def test_user_poisoned_old_verifier_cannot_forge_remote_reachability(self):
        (self.repo / "file.txt").write_text("unpushed\n")
        git(self.repo, "commit", "-am", "unpushed")
        head = git(self.repo, "rev-parse", "HEAD")
        old = self.root / "verify" / rt.digest(str(self.repo))
        old.mkdir(parents=True)
        git(old, "init", "--bare")
        git(old, "--git-dir=" + str(old), "fetch", str(self.repo), "HEAD")
        (old / "info/grafts").write_text(self.base + " " + head + "\n")
        # Demonstrate the poisoned object graph would have made the old reused verifier accept it.
        git(old, "--git-dir=" + str(old), "merge-base", "--is-ancestor", head, self.base)
        self.assertEqual("AwaitingGitSave", self.assert_blocked("git-save").state)

    def test_edit_after_push_blocks(self):
        (self.repo / "file.txt").write_text("pushed\n")
        git(self.repo, "commit", "-am", "pushed")
        git(self.repo, "push", "origin", "work/vm-a")
        (self.repo / "file.txt").write_text("after-push\n")
        self.assert_blocked("git-save", "tracked")

    def test_edit_during_freeze_detected(self):
        self.adapter.on_freeze = lambda: (self.repo / "file.txt").write_text("raced")
        self.assert_blocked("git-save", "tracked")

    def test_edit_after_receipt_invalidates(self):
        receipt = self.request("git-save")
        (self.repo / "file.txt").write_text("after receipt")
        self.assert_blocked("receipt-validate", receiptId=receipt["receiptId"])

    def test_explicit_resume_revokes_old_receipt_before_enabling_writes(self):
        receipt = self.request("git-save")
        self.assert_blocked("resume", "not-delete-committed")
        self.request("resume", deletionNotCommitted=True)
        self.assertFalse(self.adapter.ro)
        self.assertFalse(self.adapter.frozen)
        self.assertEqual("Ready", self.runtime.s["phase"])
        self.assert_blocked("receipt-validate", "Stale receipt", receiptId=receipt["receiptId"])
        (self.repo / "file.txt").write_text("user may now resolve work")
        self.assert_blocked("git-save", "tracked", operation="operation-b")

    def test_mutations_require_registered_task_and_fail_if_freeze_is_lost(self):
        self.assert_blocked("activity", "registered live", source="task-progress", taskId="missing")
        self.request("task-start", taskId="task-a", argv=["true"])
        self.request("activity", source="task-progress", taskId="task-a")
        self.assert_blocked("task-complete", "still running", taskId="task-a")
        self.adapter.live = False
        self.request("task-complete", taskId="task-a")
        original_freeze = self.adapter.freeze
        def lost_freeze():
            original_freeze()
            self.adapter.frozen = False
        with patch.object(self.adapter, "freeze", side_effect=lost_freeze):
            self.assert_blocked("git-save", "Freeze")

    def test_second_check_detects_mutation_during_remote_verification(self):
        original = self.g.inspect
        calls = 0
        def mutate(*args):
            nonlocal calls
            result = original(*args)
            calls += 1
            if calls == 1:
                (self.repo / "untracked-race").write_text("changed")
            return result
        with patch.object(self.g, "inspect", side_effect=mutate):
            self.assert_blocked("git-save", "untracked")

    def test_remote_ref_mismatch(self):
        self.registration["remoteRef"] = "refs/heads/sibling"
        self.assert_blocked("git-save", "authorized")

    def test_remote_unavailable(self):
        shutil.move(self.remote, self.root / "remote-gone")
        self.assertEqual("AwaitingGitSave", self.assert_blocked("git-save").state)

    def test_remote_rewritten_after_receipt_blocks(self):
        receipt = self.request("git-save")
        git(self.second, "checkout", "--orphan", "unrelated")
        git(self.second, "rm", "-rf", ".")
        (self.second / "unrelated").write_text("other")
        git(self.second, "add", ".")
        git(self.second, "commit", "-m", "unrelated")
        # Test fixture deliberately simulates an external force update; runtime never pushes.
        git(self.second, "push", "--force", "origin", "HEAD:refs/heads/work/vm-a")
        self.assert_blocked("receipt-validate", receiptId=receipt["receiptId"])

    def test_missing_authority_origin_branch_base(self):
        for key, value in (("pushAuthorized", False), ("origin", str(self.root / "other.git")),
                           ("branch", "main"), ("baseSha", "main")):
            with self.subTest(key=key):
                old = self.registration[key]
                self.registration[key] = value
                self.assert_blocked("git-save")
                self.registration[key] = old

    def test_lfs_and_submodules_fail_closed(self):
        cases = [
            (".gitattributes", "*.bin filter=lfs diff=lfs merge=lfs -text\n"),
            (".gitmodules", "[submodule \"x\"]\n path=x\n url=https://example.invalid/x\n"),
            ("pointer.bin", "version https://git-lfs.github.com/spec/v1\noid sha256:0000\nsize 4\n"),
        ]
        for name, content in cases:
            with self.subTest(name=name):
                path = self.repo / name
                path.write_text(content)
                git(self.repo, "add", name)
                git(self.repo, "commit", "-m", "unsupported")
                git(self.repo, "push", "origin", "work/vm-a")
                self.assert_blocked("git-save")
                git(self.repo, "rm", name)
                git(self.repo, "commit", "-m", "remove unsupported")
                git(self.repo, "push", "origin", "work/vm-a")

    def test_unregistered_checkout_and_linked_worktree(self):
        nested = self.workspace / "repos/unknown"
        git(self.workspace, "clone", str(self.remote), str(nested))
        self.assert_blocked("git-save", "inventory")
        shutil.rmtree(nested)
        outside = self.root / "external-worktree"
        git(self.repo, "worktree", "add", "-b", "extra", str(outside), "HEAD")
        self.assert_blocked("git-save", "Unregistered Git worktree")

    def codex_probe(self, relative="home/.codex/.tmp/git-A0bC9z"):
        probe = self.workspace / relative
        probe.mkdir(parents=True)
        (probe / "objects").mkdir()
        (probe / "refs").mkdir()
        (probe / "HEAD").write_bytes(b"ref: refs/heads/main\n")
        (probe / "HEAD").chmod(0o600)
        return probe

    def test_exact_empty_codex_probes_allow_git_save_without_mutation(self):
        probes = [self.codex_probe("home/.codex/.tmp/" + name)
                  for name in ("git-A0bC9z", "git-ZvtwGU", "git-U4CEZ5")]
        def snapshot():
            return {str(path): (path.lstat().st_mode, path.lstat().st_mtime_ns,
                               path.read_bytes() if path.is_file() else None)
                    for probe in probes for path in (probe, *probe.rglob("*"))}
        before = snapshot()

        self.assertEqual("GitSaveCandidate", self.request("git-check")["type"])
        receipt = self.request("git-save")
        self.assertEqual("GitSaved", receipt["type"])
        self.assertEqual(self.base, receipt["repositories"][0]["remoteSha"])
        self.assertEqual("ReceiptValid", self.request("receipt-validate", receiptId=receipt["receiptId"])["type"])
        self.assertEqual(before, snapshot())

    def test_codex_probe_with_git_data_or_unknown_layout_remains_blocked(self):
        for case in ("commit", "loose-object", "ref", "packed-refs", "alternates", "worktrees",
                     "reflog", "config", "unknown-file", "empty-object-subdir", "detached-head", "other-unborn-head"):
            with self.subTest(case=case):
                probe = self.codex_probe()
                if case == "commit":
                    git(probe, "--bare", "fetch", str(self.repo), "HEAD:refs/heads/saved")
                elif case == "loose-object":
                    git(probe, "--bare", "hash-object", "-w", str(self.repo / "file.txt"))
                elif case == "ref":
                    (probe / "refs/saved").write_text(self.base + "\n")
                elif case == "packed-refs":
                    (probe / "packed-refs").write_text(self.base + " refs/heads/saved\n")
                elif case == "alternates":
                    (probe / "objects/info").mkdir()
                    (probe / "objects/info/alternates").write_text(str(self.repo / ".git/objects") + "\n")
                elif case == "worktrees":
                    (probe / "worktrees").mkdir()
                    (probe / "worktrees/gitdir").write_text(str(self.repo / ".git") + "\n")
                elif case == "reflog":
                    (probe / "logs").mkdir()
                    (probe / "logs/HEAD").write_text(self.base + " retained recovery data\n")
                elif case == "config":
                    (probe / "config").write_text('[remote "origin"]\n url=https://example.invalid/recovery.git\n')
                elif case == "unknown-file":
                    (probe / "recoverable-data").write_text("retain this data\n")
                elif case == "empty-object-subdir":
                    (probe / "objects/pack").mkdir()
                elif case == "detached-head":
                    (probe / "HEAD").write_text(self.base + "\n")
                else:
                    (probe / "HEAD").write_text("ref: refs/heads/recovery\n")
                before = {str(path.relative_to(probe)): path.read_bytes()
                          for path in probe.rglob("*") if path.is_file()}
                try:
                    with self.assertRaisesRegex(rt.Blocked, "Unregistered bare repository"):
                        self.g.inspect([self.registration], self.workspace)
                    self.assertEqual(before, {str(path.relative_to(probe)): path.read_bytes()
                                              for path in probe.rglob("*") if path.is_file()})
                finally:
                    shutil.rmtree(probe)

    def test_bare_inventory_outside_exact_codex_probe_scope_remains_blocked(self):
        for relative in ("repos/git-A0bC9z", "home/.codex/git-A0bC9z",
                         "home/.codex/.tmp/nested/git-A0bC9z", "home/.codex/.tmp/git-A0bC9z-extra",
                         "home/.codex/.tmp/git-ABC_de"):
            with self.subTest(relative=relative):
                probe = self.codex_probe(relative)
                try:
                    with self.assertRaisesRegex(rt.Blocked, "Unregistered bare repository"):
                        self.g.inspect([self.registration], self.workspace)
                finally:
                    shutil.rmtree(probe)
        nested = self.workspace / "home/.codex/user-checkout"
        git(self.workspace, "clone", str(self.remote), str(nested))
        with self.assertRaisesRegex(rt.Blocked, "inventory"):
            self.g.inspect([self.registration], self.workspace)

    def test_codex_probe_symlinks_and_linked_metadata_remain_blocked(self):
        for case in ("HEAD", "objects", "refs", "probe", ".tmp", ".codex", "home", "hardlinked-head"):
            with self.subTest(case=case):
                probe = self.codex_probe()
                outside = self.root / ("external-probe-" + case.replace(".", ""))
                if case in ("HEAD", "hardlinked-head"):
                    outside.write_bytes((probe / "HEAD").read_bytes())
                    (probe / "HEAD").unlink()
                    if case == "HEAD":
                        (probe / "HEAD").symlink_to(outside)
                    else:
                        os.link(outside, probe / "HEAD")
                else:
                    target = probe / case if case in ("objects", "refs") else (
                        probe if case == "probe" else self.workspace / (
                            "home/.codex/.tmp" if case == ".tmp" else "home/.codex" if case == ".codex" else "home"))
                    target.rename(outside)
                    target.symlink_to(outside, target_is_directory=True)
                try:
                    with self.assertRaises(rt.Blocked):
                        self.g.inspect([self.registration], self.workspace)
                    self.assertTrue(outside.exists())
                finally:
                    home = self.workspace / "home"
                    home.unlink() if home.is_symlink() else shutil.rmtree(home)

    def test_expired_live_task_is_not_killed(self):
        self.request("task-start", taskId="task-a", argv=["sleep", "100"], leaseSeconds=1)
        self.runtime.clock = lambda: 9999
        self.assert_blocked("git-save", "still running")
        self.assertTrue(self.adapter.live)
        self.assertNotIn("freeze", self.adapter.events)
        self.request("cancel-drain")
        self.assertEqual("Ready", self.runtime.s["phase"])
        self.adapter.live = False
        self.request("task-complete", taskId="task-a")
        self.assertEqual("GitSaved", self.request("git-save")["type"])

    def test_unknown_and_unreachable_work_fail_closed(self):
        self.adapter.unknown = True
        self.assert_blocked("git-save", "Unregistered")
        self.assertNotIn("stop-services", self.adapter.events)
        with self.assertRaisesRegex(rt.Blocked, "Fence gateway"):
            self.runtime.handle(dict(action="git-save", vmId="vm-a", generation=3,
                                     operationId="operation-a"))

    def test_ambiguous_launch_never_replayed_after_restart(self):
        self.adapter.ambiguous = True
        self.assert_blocked("task-start", "Ambiguous", taskId="task-a", argv=["false"])
        self.runtime = rt.Runtime(self.config, self.root / "state", self.adapter, self.g)
        self.assert_blocked("task-start", "never replay", taskId="task-a", argv=["false"])
        self.assertEqual(1, self.adapter.launches)
        self.assert_blocked("git-save", "Ambiguous")

    def test_stale_generation_receipt_and_operation(self):
        receipt = self.request("git-save")
        with self.assertRaisesRegex(rt.Blocked, "Stale VM"):
            self.runtime.handle(dict(vmId="vm-a", generation=2, operationId="operation-a", action="status"))
        self.assert_blocked("receipt-validate", "Stale receipt", receiptId="forged")
        self.assert_blocked("receipt-validate", "Stale receipt", operation="operation-b",
                            receiptId=receipt["receiptId"])
        self.assert_blocked("discard", "stale operation", operation="operation-b",
                            acknowledgeLostIgnoredAndRootData=True, authorizedDiscard=True)
        self.adapter.frozen = False
        self.assert_blocked("receipt-validate", "Freeze", receiptId=receipt["receiptId"])

    def test_explicit_discard_requires_both_acknowledgements(self):
        self.assert_blocked("discard", "acknowledgement")
        receipt = self.request("discard", authorizedDiscard=True,
                               acknowledgeLostIgnoredAndRootData=True)
        self.assertEqual("DiscardAuthorized", receipt["type"])
        self.assertEqual(("vm-a", 3, "operation-a"),
                         (receipt["vmId"], receipt["generation"], receipt["operationId"]))

    def test_keepalive_and_passive_sockets_do_not_reset_activity(self):
        for source in ("keepalive", "websocket", "preview", "health", "logs"):
            self.assert_blocked("activity", "not useful", source=source)
        self.assertIsNone(self.runtime.s["activityAt"])
        self.request("activity", source="user-input")
        self.assertEqual(1000, self.runtime.s["activityAt"])
        self.request("task-start", taskId="task-a", argv=["true"])
        self.runtime.clock = lambda: 2000
        self.request("task-renew", taskId="task-a")
        self.assertEqual(1000, self.runtime.s["activityAt"])

    def primary(self):
        config = dict(self.config, kind="primary",
                      volume=dict(device="/dev/disk/by-id/fake", providerDeviceId="fake",
                                  providerVolumeId="vol-primary", filesystemUuid="fixture-fs",
                                  owner={"workspaceId": "workspace"}))
        return rt.Runtime(config, self.root / "primary-state", self.adapter, self.g)

    def test_primary_mount_identity_wrong_uuid_owner_and_no_format_resume(self):
        self.runtime = self.primary()
        self.runtime.c["volume"]["filesystemUuid"] = "wrong"
        self.assert_blocked("bootstrap", "UUID")
        self.runtime.c["volume"]["filesystemUuid"] = "fixture-fs"
        self.runtime.c["volume"]["owner"] = {"workspaceId": "another"}
        self.assert_blocked("bootstrap", "owner")
        self.runtime.c["volume"]["owner"] = {"workspaceId": "workspace"}
        self.request("bootstrap")
        self.request("bootstrap")
        self.assertTrue(self.adapter.mounted)
        self.assertNotIn("format", self.adapter.events)
        receipt = self.request("quiesce")
        self.assertEqual("PrimaryQuiesced", receipt["type"])
        self.assertTrue(receipt["unmounted"])
        self.assertLess(self.adapter.events.index("stop-services"), self.adapter.events.index("unmount"))
        self.assertEqual(receipt, self.request("quiesce"))

    def test_ready_primary_with_missing_mount_blocks_status_and_new_task_until_recovery(self):
        self.runtime = self.primary()
        self.request("bootstrap")
        self.adapter.mounted = False  # A fresh kernel has not yet mounted the retained device.
        runtime = rt.Runtime(self.runtime.c, self.runtime.root, self.adapter, self.g)
        self.runtime = runtime
        status = self.request("status", includeWorkInventory=True)
        self.assertEqual("StorageUnavailable", status["phase"])
        self.assertNotIn("workInventory", status)
        with self.assertRaisesRegex(rt.Blocked, "Cannot verify the retained workspace mount"):
            self.request("task-start", taskId="new", argv=["true"])
        self.assertEqual({}, runtime.s["tasks"])
        self.assertEqual(0, self.adapter.launches)

        # Mount recovery is independent of one-shot enrollment and never rewrites phase or tasks.
        self.adapter.mounted = True
        self.assertEqual("Ready", self.request("status")["phase"])

    def test_scratch_mounts_stop_before_idle_evidence_and_detach_before_readonly_and_unmount(self):
        self.runtime = self.primary()
        self.request("bootstrap")
        events = self.adapter.events
        self.assertLess(events.index("layout"), events.index("scratch-resume"))
        del events[:]
        receipt = self.request("quiesce")
        self.assertEqual(dict(unmounted=2, lazy=0, lazyPaths=[]), receipt["scratchMounts"])
        self.assertEqual(["scratch-suspend", "idle", "freeze"], events[:3])
        self.assertLess(events.index("stop-services"), events.index("scratch-unmount"))
        self.assertLess(events.index("scratch-unmount"), events.index("readonly"))
        self.assertLess(events.index("readonly"), events.index("unmount"))
        self.assertNotIn("scratch-resume", events)

    def local_primary(self):
        runtime = self.primary()
        runtime.c["storageLayout"] = 2
        return rt.Runtime(runtime.c, self.root / "local-state", self.adapter, self.g)

    def test_local_checkouts_stop_saves_final_checkpoint_after_writers_stop_and_binds_it(self):
        self.runtime = self.local_primary()
        self.request("bootstrap")
        events = self.adapter.events
        del events[:]
        receipt = self.request("quiesce")
        self.assertEqual(2, receipt["storageLayout"])
        self.assertEqual("operation-a", receipt["checkpoint"]["operationId"])
        # Periodic saves end before any idle evidence: their Git children run outside the work slice.
        self.assertEqual(["periodic-checkpoint-stop", "idle", "freeze"], events[:3])
        self.assertEqual(receipt["checkpoint"], self.runtime.s["reposCheckpoint"])
        self.assertEqual(1, events.count("repos-checkpoint"))
        self.assertLess(events.index("stop-services"), events.index("repos-checkpoint"))
        self.assertLess(max(i for i, event in enumerate(events[:events.index("repos-checkpoint")])
                            if event == "t3-proof"), events.index("repos-checkpoint"))
        self.assertLess(events.index("repos-checkpoint"), events.index("readonly"))
        self.assertLess(events.index("readonly"), events.index("unmount"))
        self.assertNotIn("scratch-suspend", events)
        self.assertEqual("ReceiptValid", self.request("receipt-validate", receiptId=receipt["receiptId"])["type"])
        self.assertEqual(receipt, self.request("quiesce"))
        self.assertEqual(1, self.adapter.checkpoints)
        # A receipt without the checkpoint never validates.
        self.runtime.s["receipts"]["operation-a"] = dict(receipt, checkpoint=None)
        self.assert_blocked("receipt-validate", "final checkout checkpoint", receiptId=receipt["receiptId"])

    def test_local_checkpoint_failure_keeps_the_vm_and_its_files_and_resume_forgets_it(self):
        self.runtime = self.local_primary()
        self.request("bootstrap")
        self.adapter.checkpoint_error = "Checkpoint destination is full"
        self.assert_blocked("quiesce", "destination is full")
        self.assertEqual("repos-checkpoint", self.runtime.diagnostic_stage)
        for event in ("readonly", "unmount"):
            self.assertNotIn(event, self.adapter.events)
        self.assertTrue(self.adapter.mounted)
        self.assertEqual("Frozen", self.runtime.s["phase"])
        self.assertNotIn("reposCheckpoint", self.runtime.s)
        self.request("resume", deletionNotCommitted=True)
        self.assertEqual("Ready", self.runtime.s["phase"])
        self.adapter.checkpoint_error = None
        self.assertEqual("operation-b", self.request("quiesce", operation="operation-b")["checkpoint"]["operationId"])

    def test_local_status_reports_checkpoint_freshness_and_never_fails(self):
        now = 10_000
        config = dict(self.primary().c, storageLayout=2, checkpointIntervalSeconds=120)
        self.runtime = rt.Runtime(config, self.root / "local-status", self.adapter, self.g, clock=lambda: now)
        self.request("bootstrap")
        ns = lambda seconds: seconds * 10**9
        self.assertEqual(dict(state="Missing", intervalSeconds=120), self.request("status")["checkpoint"])
        ready = dict(state="ready", checkpointId="c-1", completedAt=ns(now - 100),
                     lastAttempt=dict(at=ns(now - 100), mode="periodic", outcome="succeeded", checkpointId="c-1"))
        cases = (
            (ready, dict(state="Fresh", checkpointId="c-1", completedAt=now - 100,
                         lastAttempt=dict(at=now - 100, outcome="succeeded"))),
            # Stale after three intervals without a newer checkpoint.
            (dict(ready, completedAt=ns(now - 361), lastAttempt=None),
             dict(state="Stale", checkpointId="c-1", completedAt=now - 361)),
            # A deferred save (the tree kept changing) keeps the last checkpoint's freshness.
            (dict(ready, lastAttempt=dict(at=ns(now - 10), outcome="deferred", reason="Checkout changed")),
             dict(state="Fresh", checkpointId="c-1", completedAt=now - 100,
                  lastAttempt=dict(at=now - 10, outcome="deferred", reason="Checkout changed"))),
            (dict(ready, lastAttempt=dict(at=ns(now - 10), outcome="failed", reason="Checkpoint destination is full")),
             dict(state="Failed", checkpointId="c-1", completedAt=now - 100, reason="Checkpoint destination is full",
                  lastAttempt=dict(at=now - 10, outcome="failed", reason="Checkpoint destination is full"))),
            (dict(state="empty", lastAttempt=dict(at=ns(now - 10), outcome="failed", reason="Checkpoint save failed")),
             dict(state="Failed", reason="Checkpoint save failed",
                  lastAttempt=dict(at=now - 10, outcome="failed", reason="Checkpoint save failed"))),
            (dict(state="corrupt", reason="Checkpoint pointer is missing"),
             dict(state="Failed", reason="Checkpoint pointer is missing")),
        )
        for value, expected in cases:
            with self.subTest(value=value):
                self.adapter.checkpoint_state = value
                self.assertEqual(dict(expected, intervalSeconds=120), self.request("status")["checkpoint"])
        self.adapter.checkpoint_state = OSError("store unreadable")
        status = self.request("status")
        self.assertEqual(dict(state="Unavailable"), status["checkpoint"])
        self.assertEqual("Ready", status["phase"])
        self.runtime = self.primary()
        self.request("bootstrap")
        self.assertNotIn("checkpoint", self.request("status"))

    def test_local_checkpoint_retry_after_readonly_verifies_instead_of_saving_again(self):
        self.runtime = self.local_primary()
        self.request("bootstrap")
        with patch.object(self.adapter, "verify_fence", side_effect=rt.Blocked("Workspace no longer read-only")):
            self.assert_blocked("quiesce", "read-only")
        self.assertTrue(self.adapter.ro)
        saved = self.runtime.s["reposCheckpoint"]
        del self.adapter.events[:]
        receipt = self.request("quiesce")
        self.assertEqual(saved, receipt["checkpoint"])
        self.assertEqual(1, self.adapter.checkpoints)
        self.assertIn("repos-checkpoint-verify", self.adapter.events)
        self.assertNotIn("repos-checkpoint", self.adapter.events)
        # Resuming drops the binding: writers may change the tree before the next stop.
        self.runtime.s.update(phase="Frozen")
        self.runtime.s["receipts"] = {}
        self.adapter.mounted = True
        self.request("resume", deletionNotCommitted=True)
        self.assertNotIn("reposCheckpoint", self.runtime.s)

    def test_scratch_detach_failure_keeps_volume_mounted_and_resume_reestablishes(self):
        self.runtime = self.primary()
        self.request("bootstrap")
        with patch.object(self.adapter, "scratch_detach",
                          side_effect=rt.Blocked("Scratch build-output mounts remain under the workspace")):
            self.assert_blocked("quiesce", "Scratch")
        self.assertNotIn("readonly", self.adapter.events)
        self.assertNotIn("unmount", self.adapter.events)
        self.assertEqual("scratch-unmount", self.runtime.diagnostic_stage)
        del self.adapter.events[:]
        self.request("resume", deletionNotCommitted=True)
        self.assertLess(self.adapter.events.index("scratch-resume"), self.adapter.events.index("start-services"))

    def test_ephemeral_rejects_all_storage_claims(self):
        for field in ("volume", "backup", "restore"):
            with self.subTest(field=field), self.assertRaisesRegex(rt.Blocked, "forbidden"):
                rt.Runtime(dict(self.config, **{field: {"anything": True}}),
                           self.root / ("bad-" + field), self.adapter, self.g)
        with self.assertRaisesRegex(rt.Blocked, "Acknowledge"):
            rt.Runtime(dict(self.config, ignoredDataAcknowledged=False), self.root / "unack",
                       self.adapter, self.g)
        self.assertNotIn("mount", self.adapter.events)
        self.assert_blocked("restore", "separate")
        self.assert_blocked("quiesce", "Git save")
        with self.assertRaisesRegex(rt.Blocked, "implicit credentials"):
            rt.Runtime(dict(self.config, providerToken="synthetic-not-a-token"),
                       self.root / "unknown-config", self.adapter, self.g)

    def test_primary_backup_fails_without_encryption_or_offhost_destination(self):
        self.runtime = self.primary()
        self.request("bootstrap")
        self.assert_blocked("quiesce", "recipient", backupRequired=True)
        self.assertNotIn("unmount", self.adapter.events)
        self.runtime.c["backup"] = dict(recipient="age1synthetic", keyRecoveryId="external-key",
                                       retentionDays=7, destinationIdentity="vm-a")
        self.assert_blocked("backup", "Independent off-host")
        self.assertNotIn("unmount", self.adapter.events)

    def test_restore_cannot_overwrite_source_volume_or_nonempty_target(self):
        self.runtime = self.primary()
        self.runtime.s["phase"] = "New"
        source = dict(type="PrimaryBackup", kind="primary", sourceVolumeId="vol-primary")
        source["receiptId"] = rt.digest(source)
        self.assert_blocked("restore", "separate", authorizedRestore=True, backupReceipt=source)

    def test_restore_never_changes_the_storage_layout(self):
        self.runtime = self.primary()
        self.runtime.s["phase"] = "New"
        source = dict(type="PrimaryBackup", kind="primary", sourceVolumeId="vol-source",
                      sourceFilesystemUuid="source-fs", volumeOwner={"workspaceId": "workspace"},
                      freshness="VerifiedOffHost", representativeHashes={"t3/state": "0" * 64}, storageLayout=2,
                      checkpoint=dict(checkpointId="c-1", manifestSha256="0" * 64))
        source["receiptId"] = rt.digest(source)
        self.assert_blocked("restore", "storage layout differs", authorizedRestore=True,
                            independentRecoveryKeyConfirmed=True, backupReceipt=source)
        self.runtime.c["storageLayout"] = 2
        self.runtime.local = True
        old = {key: value for key, value in source.items() if key not in ("receiptId", "storageLayout", "checkpoint")}
        old["receiptId"] = rt.digest(old)
        self.assert_blocked("restore", "storage layout differs", authorizedRestore=True,
                            independentRecoveryKeyConfirmed=True, backupReceipt=old)
        self.assertNotIn("mount", self.adapter.events)

    def make_streaming_backup(self, acknowledge=True):
        self.runtime = self.primary()
        self.runtime.c["backup"] = dict(transport="management-ssh-stream",
                                       recipient="age1test-fixture-not-a-real-key",
                                       keyRecoveryId="operator-independent-key",
                                       destinationIdentity="operator-offhost-storage", retentionDays=7)
        self.request("bootstrap")
        def fake_encrypt(source, output, recipient, scope=None):
            # Use the real archive writer, but do not claim cryptographic validation.
            with output.open("wb") as stream:
                rt.write_backup_archive(source, stream, scope)
        with patch.object(rt, "encrypt_archive", side_effect=fake_encrypt):
            self.assert_blocked("quiesce", "off-host acknowledgement", backupRequired=True)
        prepared = self.request("backup")
        stream = io.BytesIO()
        self.runtime.export_backup(prepared["receiptId"], stream)
        self.assertEqual(prepared["ciphertextSha256"], rt.hashlib.sha256(stream.getvalue()).hexdigest())
        self.assertEqual("AwaitingOffHostAcknowledgement", prepared["freshness"])
        self.assertNotIn("unmount", self.adapter.events)
        self.assertFalse((self.root / "primary-state/backup.tar").exists())
        self.assert_blocked("backup-ack", "confirmation", preparedReceiptId=prepared["receiptId"],
                            ciphertextSha256=prepared["ciphertextSha256"])
        if not acknowledge:
            return prepared, stream.getvalue()
        receipt = self.request("backup-ack", preparedReceiptId=prepared["receiptId"],
                               ciphertextSha256=prepared["ciphertextSha256"],
                               durablyStoredOffHost=True, independentRecoveryKeyConfirmed=True,
                               destinationIdentity="operator-offhost-storage")
        self.assertEqual("VerifiedOffHost", receipt["freshness"])
        self.assertFalse((self.runtime.root / receipt["objectId"]).exists())
        return receipt, stream.getvalue()

    def test_resume_discards_prepared_backup_before_reopening_writes(self):
        prepared, _ = self.make_streaming_backup(acknowledge=False)
        ciphertext = self.runtime.root / prepared["objectId"]
        self.assertTrue(ciphertext.exists())
        self.request("resume", deletionNotCommitted=True)
        self.assertNotIn("preparedBackup", self.runtime.s)
        self.assertFalse(ciphertext.exists())
        self.assert_blocked("backup-ack", "frozen", preparedReceiptId=prepared["receiptId"],
                            ciphertextSha256=prepared["ciphertextSha256"],
                            durablyStoredOffHost=True, independentRecoveryKeyConfirmed=True,
                            destinationIdentity="operator-offhost-storage")

    def test_resume_cannot_reuse_verified_backup_after_new_writes(self):
        receipt, _ = self.make_streaming_backup()
        self.request("resume", deletionNotCommitted=True)
        (self.repo / "file.txt").write_text("new writes after the acknowledged backup\n")
        def fake_encrypt(source, output, recipient, scope=None):
            with output.open("wb") as stream:
                rt.write_backup_archive(source, stream, scope)
        with patch.object(rt, "encrypt_archive", side_effect=fake_encrypt):
            self.assert_blocked("quiesce", "off-host acknowledgement", backupRequired=True)
        prepared = self.request("backup")
        self.assertNotEqual(receipt["objectId"], prepared["objectId"])
        self.assertNotEqual(receipt["representativeHashes"], prepared["representativeHashes"])

    def test_primary_streaming_backup_ack_and_restore_separate_owned_volume(self):
        (self.workspace / "lost+found").mkdir()
        (self.workspace / ".workspaces-volume.json").write_text("source volume ownership")
        state = self.workspace / "user-data/custom-service/state.bin"
        state.parent.mkdir(parents=True)
        state.write_bytes(b"generic application data\0\xff")
        nested = self.workspace / "user-data/lost+found"
        nested.mkdir()
        (nested / ".workspaces-volume.json").write_text("user-owned nested data")
        receipt, archive = self.make_streaming_backup()
        with rt.tarfile.open(fileobj=io.BytesIO(archive)) as content:
            self.assertNotIn("data/lost+found", content.getnames())
            self.assertNotIn("data/.workspaces-volume.json", content.getnames())
        stopped = self.request("quiesce", backupRequired=True)
        self.assertEqual(receipt, stopped["backup"])
        target = self.root / "restore-workspace"
        target.mkdir()
        (target / "lost+found").mkdir()
        owner = target / ".workspaces-volume.json"
        owner.write_text("separate target ownership")
        config = copy.deepcopy(self.runtime.c)
        config["workspace"] = str(target)
        config["volume"]["providerVolumeId"] = "vol-separate-target"
        config["volume"]["filesystemUuid"] = "target-fs"
        self.adapter.uuid = "target-fs"
        self.runtime = rt.Runtime(config, self.root / "restore-state", self.adapter, self.g)
        self.request("restore", authorizedRestore=True, independentRecoveryKeyConfirmed=True,
                     backupReceipt=receipt)
        restored = self.runtime.restore_import("operation-a", io.BytesIO(archive))
        self.assertEqual("PrimaryRestored", restored["type"])
        self.assertTrue(restored["representativeHashesVerified"])
        self.assertEqual("initial\n", (target / "repos/project/file.txt").read_text())
        self.assertEqual(b"generic application data\0\xff",
                         (target / "user-data/custom-service/state.bin").read_bytes())
        self.assertEqual("separate target ownership", owner.read_text())
        self.assertEqual([], list((target / "lost+found").iterdir()))
        self.assertEqual("user-owned nested data",
                         (target / "user-data/lost+found/.workspaces-volume.json").read_text())

    def test_backup_rejects_recovered_or_aliased_filesystem_metadata(self):
        lost = self.workspace / "lost+found"
        lost.mkdir()
        (lost / "recovered-file").write_bytes(b"must not silently omit recovered data")
        with self.assertRaisesRegex(rt.Blocked, "recovered data"):
            rt.write_backup_archive(self.workspace, io.BytesIO())
        (lost / "recovered-file").unlink()
        lost.rmdir()
        for directory in (False, True):
            with self.subTest(directory=directory):
                outside = self.root / ("outside-directory" if directory else "outside-file")
                outside.mkdir() if directory else outside.write_bytes(b"do not follow")
                lost.symlink_to(outside, target_is_directory=directory)
                with self.assertRaisesRegex(rt.Blocked, "metadata"):
                    rt.write_backup_archive(self.workspace, io.BytesIO())
                lost.unlink()

    def test_backup_never_enters_nested_mounts_such_as_scratch_build_outputs(self):
        modules = self.repo / "node_modules"
        (modules / "pkg").mkdir(parents=True)
        (modules / "pkg/index.js").write_text("scratch content")
        (self.repo / "dist").mkdir()
        (self.repo / "dist/app.js").write_text("volume content")
        real_lstat = Path.lstat

        def lstat(path):
            info = real_lstat(path)
            if path == modules:  # Simulates a bind mount from the local scratch disk.
                values = list(info)
                values[stat.ST_DEV] = info.st_dev + 1
                return os.stat_result(values)
            return info
        with patch.object(Path, "lstat", autospec=True, side_effect=lstat):
            rt.validate_backup_tree(self.workspace)
            hashes = rt.representative_hashes(self.workspace)
            stream = io.BytesIO()
            rt.write_backup_archive(self.workspace, stream)
        names = rt.tarfile.open(fileobj=io.BytesIO(stream.getvalue())).getnames()
        self.assertIn("data/repos/project/node_modules", names)
        self.assertIn("data/repos/project/dist/app.js", names)
        self.assertFalse(any(name.startswith("data/repos/project/node_modules/") for name in names))
        self.assertFalse(any("node_modules" in name for name in hashes))
        self.assertIn("repos/project/dist/app.js", hashes)

    def test_backup_restore_preserves_unfollowed_tool_and_virtualenv_symlinks(self):
        os.link(self.repo / "file.txt", self.repo / "hardlinked.txt")
        outside = self.root / "outside-image-files"
        outside.mkdir()
        executable = outside / "tool"
        executable.write_bytes(b"external bytes must not enter the archive")
        links = {}
        for name in ("codex-linux-sandbox", "apply_patch", "applypatch"):
            links["home/.codex/tmp/arg0-linux/codex-arg0fixture/" + name] = str(executable)
        links[".venv/bin/python"] = os.path.relpath(executable, self.workspace / ".venv/bin")
        links["external-directory"] = str(outside)
        links["project-link"] = "repos/project"
        for relative, destination in links.items():
            path = self.workspace / relative
            path.parent.mkdir(parents=True, exist_ok=True)
            path.symlink_to(destination)
        rt.validate_backup_tree(self.workspace)
        receipt, archive = self.make_streaming_backup()
        with rt.tarfile.open(fileobj=io.BytesIO(archive)) as content:
            self.assertTrue(all(content.getmember("data/" + path).issym() for path in links))
            self.assertNotIn("data/external-directory/tool", content.getnames())
        target = self.root / "restore-links"
        target.mkdir()
        config = copy.deepcopy(self.runtime.c)
        config["workspace"] = str(target)
        config["volume"].update(providerVolumeId="separate-links", filesystemUuid="target-fs")
        self.adapter.uuid = "target-fs"
        self.runtime = rt.Runtime(config, self.root / "restore-links-state", self.adapter, self.g)
        self.request("restore", authorizedRestore=True, independentRecoveryKeyConfirmed=True,
                     backupReceipt=receipt)
        with patch.object(rt.os, "chown") as chown:
            self.runtime.restore_import("operation-a", io.BytesIO(archive))
        for relative, destination in links.items():
            self.assertEqual(destination, os.readlink(target / relative))
            chown.assert_any_call(target / relative, 2000, 2000, follow_symlinks=False)
        self.assertEqual(b"external bytes must not enter the archive", executable.read_bytes())
        self.assertEqual("initial\n", (target / "project-link/file.txt").read_text())
        self.assertEqual((target / "repos/project/file.txt").stat().st_ino,
                         (target / "repos/project/hardlinked.txt").stat().st_ino)

    def test_restore_never_writes_through_deferred_links_or_links_volume_metadata(self):
        receipt, archive = self.make_streaming_backup()
        config = copy.deepcopy(self.runtime.c)
        outside = self.root / "outside-restore"
        outside.mkdir()
        for case in ("link-first", "file-first", "nested-links", "hardlink-to-link",
                     "traversal", "hardlink-to-owner"):
            with self.subTest(case=case):
                injected = io.BytesIO()
                with rt.tarfile.open(fileobj=injected, mode="w") as output:
                    with rt.tarfile.open(fileobj=io.BytesIO(archive)) as original:
                        for member in original:
                            output.addfile(member, original.extractfile(member) if member.isfile() else None)
                    link = rt.tarfile.TarInfo("data/trap")
                    link.type = rt.tarfile.SYMTYPE
                    link.linkname = str(outside)
                    file = rt.tarfile.TarInfo("data/trap/escaped")
                    file.size = 6
                    if case == "file-first":
                        output.addfile(file, io.BytesIO(b"unsafe"))
                    if case == "traversal":
                        link.name = "data/../escaped-link"
                    if case != "hardlink-to-owner":
                        output.addfile(link)
                    if case == "link-first":
                        output.addfile(file, io.BytesIO(b"unsafe"))
                    if case == "nested-links":
                        child = rt.tarfile.TarInfo("data/trap/child")
                        child.type = rt.tarfile.SYMTYPE
                        child.linkname = str(outside / "escaped")
                        output.addfile(child)
                    if case in ("hardlink-to-link", "hardlink-to-owner"):
                        hardlink = rt.tarfile.TarInfo("data/alias")
                        hardlink.type = rt.tarfile.LNKTYPE
                        hardlink.linkname = ("data/trap" if case == "hardlink-to-link"
                                             else "data/repos/../.workspaces-volume.json")
                        output.addfile(hardlink)
                target = self.root / ("restore-" + case)
                target.mkdir()
                owner = target / ".workspaces-volume.json"
                owner.write_text("unchanged target owner")
                current = copy.deepcopy(config)
                current["workspace"] = str(target)
                current["volume"].update(providerVolumeId="separate-" + case, filesystemUuid="target-fs")
                self.adapter.uuid = "target-fs"
                self.runtime = rt.Runtime(current, self.root / ("state-" + case), self.adapter, self.g)
                self.request("restore", authorizedRestore=True, independentRecoveryKeyConfirmed=True,
                             backupReceipt=receipt)
                with self.assertRaises((rt.Blocked, rt.tarfile.TarError)):
                    self.runtime.restore_import("operation-a", io.BytesIO(injected.getvalue()))
                self.assertEqual("RestoreFailed", self.runtime.s["phase"])
                self.assertEqual([], list(outside.iterdir()))
                self.assertFalse((self.root / "escaped-link").exists())
                self.assertFalse((self.root / "escaped-link").is_symlink())
                self.assertEqual("unchanged target owner", owner.read_text())

    def test_restore_rejects_nonempty_target_missing_ack_hash_tamper_and_replay(self):
        receipt, archive = self.make_streaming_backup()
        config = copy.deepcopy(self.runtime.c)
        config["volume"]["providerVolumeId"] = "different-volume"
        config["volume"]["filesystemUuid"] = "target-fs"
        self.adapter.uuid = "target-fs"
        self.runtime = rt.Runtime(config, self.root / "restore-state", self.adapter, self.g)
        self.assert_blocked("restore", "off VM", authorizedRestore=True, backupReceipt=receipt)
        self.assert_blocked("restore", "not empty", authorizedRestore=True, backupReceipt=receipt,
                            independentRecoveryKeyConfirmed=True)
        target = self.root / "empty-target"
        target.mkdir()
        config["workspace"] = str(target)
        self.runtime = rt.Runtime(config, self.root / "empty-restore-state", self.adapter, self.g)
        damaged = dict(receipt, representativeHashes={"repos/project/file.txt": "bad"})
        damaged["receiptId"] = rt.digest({k: v for k, v in damaged.items() if k != "receiptId"})
        self.request("restore", authorizedRestore=True, backupReceipt=damaged,
                     independentRecoveryKeyConfirmed=True)
        with self.assertRaisesRegex(rt.Blocked, "hash mismatch"):
            self.runtime.restore_import("operation-a", io.BytesIO(archive))
        self.assertEqual("RestoreFailed", self.runtime.s["phase"])
        with self.assertRaisesRegex(rt.Blocked, "No authenticated"):
            self.runtime.restore_import("operation-a", io.BytesIO(archive))

    def test_backup_encryption_failure_never_unmounts_or_claims_freshness(self):
        self.runtime = self.primary()
        self.runtime.c["backup"] = dict(transport="management-ssh-stream", recipient="bad",
                                       keyRecoveryId="external-key", retentionDays=7,
                                       destinationIdentity="operator")
        self.request("bootstrap")
        with patch.object(rt, "encrypt_archive", side_effect=rt.Blocked("Age encryption failed")):
            self.assert_blocked("quiesce", "Age", backupRequired=True)
        self.assertEqual("Failed", self.runtime.s["backupFreshness"])
        self.assertNotIn("lastBackup", self.runtime.s)
        self.assertNotIn("unmount", self.adapter.events)


class LocalCheckoutTests(unittest.TestCase):
    """Storage layout 2 with the real checkpoint engine; mounts are synthetic."""
    def setUp(self):
        temp = tempfile.TemporaryDirectory(prefix="local-repos-", dir=ARTIFACTS)
        self.addCleanup(temp.cleanup)
        self.root = Path(temp.name)
        self.workspace = self.root / "workspace"
        (self.workspace / "repos").mkdir(parents=True)
        self.config = dict(vmId="vm-a", generation=3, kind="primary", uid=os.getuid(), gid=os.getgid(),
                           workspace=str(self.workspace), storageLayout=2,
                           volume=dict(providerVolumeId="vol-1", filesystemUuid="fs-1",
                                       owner=dict(workspaceId="ws-1", providerVolumeId="vol-1")))
        self.commands = []

    def adapter(self, local, **changes):
        local.mkdir(exist_ok=True)
        adapter = rt.Linux(dict(self.config, **changes))
        for name, value in (("local_root", lambda: local), ("verify_repos_mount", lambda: None)):
            patcher = patch.object(adapter, name, side_effect=value)
            patcher.start()
            self.addCleanup(patcher.stop)
        return adapter

    def attach(self, adapter, restore=True):
        with patch.object(rt, "run", side_effect=lambda argv, **kwargs: self.commands.append(argv)):
            return adapter.attach_repos(restore=restore)

    def marker(self):
        return json.loads((self.workspace / rt.LAYOUT_MARKER).read_text())

    def test_new_volume_starts_empty_and_same_vm_reboot_keeps_newer_edits(self):
        local = self.root / "vm-a"
        adapter = self.adapter(local)
        genesis = self.attach(adapter)
        self.assertEqual("initialized", genesis["action"])
        self.assertEqual(genesis["checkpointId"], self.marker()["initialCheckpoint"])
        self.assertEqual(dict(schemaVersion=1, storageLayout=2, workspaceId="ws-1", providerVolumeId="vol-1",
                              filesystemUuid="fs-1"),
                         {key: value for key, value in self.marker().items() if key != "initialCheckpoint"})
        self.assertEqual([["mount", "--bind", str(local / "repos"), str(self.workspace / "repos")],
                          ["mount", "-o", "remount,bind,nodev,nosuid", str(self.workspace / "repos")]],
                         self.commands)
        (local / "repos/project").mkdir()
        (local / "repos/project/edit.txt").write_text("newer than any checkpoint")
        # Boot-time service recovery reattaches the kept tree and never restores over it.
        self.assertEqual("kept", self.attach(adapter, restore=False)["action"])
        self.assertEqual("newer than any checkpoint", (local / "repos/project/edit.txt").read_text())

    def test_replacement_vm_restores_and_never_publishes_an_empty_tree(self):
        local = self.root / "vm-a"
        adapter = self.adapter(local)
        self.attach(adapter)
        (local / "repos/project").mkdir()
        (local / "repos/project/edit.txt").write_text("uncommitted")
        binding = adapter.repos_binding()
        saved = rc.save(str(local / "repos"), str(self.workspace / rt.CHECKPOINT_STORE), binding,
                        str(local / "repos.lineage.json"), fenced=True)
        replacement = self.root / "vm-b"
        successor = self.adapter(replacement, vmId="vm-b", generation=4)
        with self.assertRaisesRegex(rt.Blocked, "must be restored"):
            self.attach(successor, restore=False)
        (replacement / "repos").mkdir(parents=True)
        with self.assertRaisesRegex(rt.Blocked, "not restored from the current checkpoint"):
            self.attach(successor)
        (replacement / "repos").rmdir()
        restored = self.attach(successor)
        self.assertEqual(("restored", saved["checkpointId"]), (restored["action"], restored["checkpointId"]))
        self.assertEqual("uncommitted", (replacement / "repos/project/edit.txt").read_text())

    def test_missing_checkpoint_and_unmarked_store_block_without_mounting(self):
        local = self.root / "vm-a"
        self.attach(self.adapter(local))
        shutil.rmtree(self.workspace / rt.CHECKPOINT_STORE)
        self.commands.clear()
        replacement = self.adapter(self.root / "vm-b", vmId="vm-b", generation=4)
        with self.assertRaisesRegex(rt.Blocked, "Checkpoint is missing"):
            self.attach(replacement)
        (self.workspace / rt.LAYOUT_MARKER).unlink()
        (self.workspace / rt.CHECKPOINT_STORE).mkdir()
        (self.workspace / rt.CHECKPOINT_STORE / "objects").mkdir()
        with self.assertRaisesRegex(rt.Blocked, "without a storage layout marker"):
            self.attach(replacement)
        self.assertEqual([], self.commands)
        self.assertFalse((self.root / "vm-b/repos").exists())

    def legacy_tree(self):
        """A layout 1 volume: checkouts directly on it, with Git state, edits and ignored outputs."""
        app = self.workspace / "repos/app"
        app.mkdir()
        git(app, "init", "-q", "-b", "main")
        (app / ".gitignore").write_text("node_modules/\n")
        (app / "package.json").write_text("{}\n")
        git(app, "add", "-A")
        git(app, "commit", "-qm", "initial")
        git(app, "worktree", "add", "-q", "-b", "feature", str(self.workspace / "repos/app-feature"))
        (app / "package.json").write_text('{"dirty": true}\n')
        (app / "untracked.txt").write_text("uncommitted\n")
        (app / "node_modules/pkg").mkdir(parents=True)
        (app / "node_modules/pkg/index.js").write_text("rebuildable\n")
        (self.workspace / "repos/notes.txt").write_text("non-Git data\n")
        return {str(path.relative_to(self.workspace / "repos")): path.read_bytes()
                for path in sorted((self.workspace / "repos").rglob("*")) if path.is_file()}

    def migrating(self, local, **changes):
        adapter = self.adapter(local, **changes)
        patcher = patch.object(adapter, "scratch_detach", return_value=dict(unmounted=1, lazy=0, lazyPaths=[]))
        patcher.start()
        self.addCleanup(patcher.stop)
        return adapter

    def tree(self, root):
        return {str(path.relative_to(root)): path.read_bytes() for path in sorted(root.rglob("*")) if path.is_file()}

    def test_layout_1_volume_migrates_through_a_proven_restore_and_keeps_uncommitted_edits(self):
        before = self.legacy_tree()
        local = self.root / "vm-a"
        adapter = self.migrating(local)
        journal = adapter.migrate_layout(True)
        record = journal["migration"]
        self.assertEqual("detached", journal["state"])
        self.assertEqual(record["checkpointId"], self.marker()["initialCheckpoint"])
        self.assertEqual(1, record["omittedCount"])
        self.assertEqual(1, record["scratchUnmounted"])
        for key in ("saveMs", "verifyMs", "restoreMs", "compareMs", "writersBlockedMs", "bytes",
                    "peakStoreGrowthBytes"):
            self.assertIsInstance(record[key], int)
        # The old tree is moved aside unchanged until the new mount is attached.
        self.assertEqual(before, self.tree(self.workspace / rt.MIGRATION_OLD))
        self.assertFalse((self.workspace / "repos").exists())
        expected = {path: data for path, data in before.items() if "node_modules" not in path}
        self.assertEqual(expected, self.tree(local / "repos"))
        (self.workspace / "repos").mkdir()  # layout() recreates the empty mount point.
        self.assertEqual("kept", self.attach(adapter)["action"])
        self.assertEqual(record, adapter.finish_layout_migration())
        for name in (rt.MIGRATION_OLD, rt.MIGRATION_JOURNAL, rt.MIGRATION_LINEAGE):
            self.assertFalse(os.path.lexists(self.workspace / name))
        self.assertIsNone(adapter.migrate_layout(True))
        self.assertIsNone(adapter.finish_layout_migration())
        # A replacement VM restores the migrated tree, with its uncommitted edits.
        successor = self.adapter(self.root / "vm-b", vmId="vm-b", generation=4)
        self.assertEqual(("restored", record["checkpointId"]),
                         (lambda result: (result["action"], result["checkpointId"]))(self.attach(successor)))
        self.assertEqual(expected, self.tree(self.root / "vm-b/repos"))

    def test_migration_failure_or_crash_before_cutover_rolls_back_to_layout_1(self):
        before = self.legacy_tree()
        local = self.root / "vm-a"
        adapter = self.migrating(local)
        with patch.object(rc, "compare_tree", side_effect=rc.Failed("Restored checkout differs from its checkpoint")), \
                self.assertRaisesRegex(rt.Blocked, "rolled back: Restored checkout differs"):
            adapter.migrate_layout(True)
        with patch.object(rc, "restore", side_effect=rc.Failed("Restore destination is full")), \
                self.assertRaisesRegex(rt.Blocked, "rolled back: Restore destination is full"):
            adapter.migrate_layout(True)

        def rolled_back():
            self.assertEqual(before, self.tree(self.workspace / "repos"))
            for name in (rt.LAYOUT_MARKER, rt.CHECKPOINT_STORE, rt.MIGRATION_JOURNAL, rt.MIGRATION_LINEAGE,
                         rt.MIGRATION_OLD):
                self.assertFalse(os.path.lexists(self.workspace / name), name)
            for name in ("repos", "repos.lineage.json", ".restore-staging"):
                self.assertFalse(os.path.lexists(local / name), name)

        rolled_back()
        # A crash after the proof, before the marker is written: the next bootstrap rolls back even
        # without the request (the worker no longer asks for layout 2).
        with patch.object(adapter, "layout_marker", side_effect=SystemExit("crash")), \
                self.assertRaises(SystemExit):
            adapter.migrate_layout(True)
        self.assertEqual("cutover", adapter.migration_journal()["state"])
        self.assertTrue((local / "repos/notes.txt").exists())
        legacy = self.adapter(local, storageLayout=1)
        self.assertIsNone(legacy.migrate_layout(False))
        rolled_back()

    def test_migration_resumes_after_a_crash_following_cutover(self):
        self.legacy_tree()
        local = self.root / "vm-a"
        adapter = self.migrating(local)
        real_rename = os.rename

        def crash(source, target, *args, **kwargs):
            if Path(target).name == rt.MIGRATION_OLD:
                raise SystemExit("crash")
            return real_rename(source, target, *args, **kwargs)

        with patch.object(rt.os, "rename", side_effect=crash), self.assertRaises(SystemExit):
            adapter.migrate_layout(True)
        self.assertTrue(self.marker()["initialCheckpoint"])
        self.assertEqual("cutover", adapter.migration_journal()["state"])
        self.assertTrue((self.workspace / "repos/notes.txt").exists())  # Past cutover: never rolled back.
        # A same-VM reboot that attached the tree hides the old one; the journal waits.
        with patch.object(rt.os.path, "ismount", return_value=True):
            self.assertEqual("cutover", adapter.migrate_layout(True)["state"])
        self.assertIsNone(adapter.finish_layout_migration())
        self.assertEqual("detached", adapter.migrate_layout(False)["state"])
        (self.workspace / "repos").mkdir()
        self.attach(adapter)
        self.assertEqual(self.marker()["initialCheckpoint"], adapter.finish_layout_migration()["checkpointId"])
        self.assertFalse((self.workspace / rt.MIGRATION_OLD).exists())

    def test_migration_refuses_unresolved_scratch_or_foreign_state_without_writing(self):
        local = self.root / "vm-a"
        adapter = self.migrating(local)
        self.assertIsNone(adapter.migrate_layout(True))  # Empty volume: layout 2 starts empty.
        self.legacy_tree()
        self.assertIsNone(adapter.migrate_layout(False))
        (self.workspace / rt.SCRATCH_TRASH).mkdir()
        with self.assertRaisesRegex(rt.Blocked, "relocation is unresolved"):
            adapter.migrate_layout(True)
        (self.workspace / rt.SCRATCH_TRASH).rmdir()
        (local / "repos").mkdir()
        with self.assertRaisesRegex(rt.Blocked, "already exists"):
            adapter.migrate_layout(True)
        (local / "repos").rmdir()
        (self.workspace / rt.CHECKPOINT_STORE / "objects").mkdir(parents=True)
        with self.assertRaisesRegex(rt.Blocked, "without a storage layout marker"):
            adapter.migrate_layout(True)
        shutil.rmtree(self.workspace / rt.CHECKPOINT_STORE)
        rt.atomic_json(self.workspace / rt.MIGRATION_JOURNAL, dict(
            schemaVersion=1, state="started", workspaceId="ws-1", providerVolumeId="other", filesystemUuid="fs-1"))
        with self.assertRaisesRegex(rt.Blocked, "belongs to another volume"):
            adapter.migrate_layout(True)
        self.assertTrue((self.workspace / "repos/notes.txt").exists())
        self.assertFalse((self.workspace / rt.LAYOUT_MARKER).exists())

    def test_offhost_backup_carries_the_final_checkpoint_and_restores_it_on_a_separate_volume(self):
        local = self.root / "vm-a"
        source = self.adapter(local)
        self.attach(source)
        (local / "repos/project").mkdir()
        (local / "repos/project/edit.txt").write_text("uncommitted edit")
        final = source.checkpoint_repos("stop-1")
        (self.workspace / "t3").mkdir()
        (self.workspace / "t3/state.sqlite").write_bytes(b"durable T3 state")
        store = self.workspace / rt.CHECKPOINT_STORE
        (store / "tmp/unfinished").write_bytes(b"partial object")
        (store / "staging/c-00000000000000000000-0000000000000000.json").write_text("{}")
        (store / "objects/ff").mkdir()
        (store / "objects/ff" / ("f" * 64)).write_bytes(b"unreferenced")
        # Stands for the live VM-disk tree mounted on the same device: never entered.
        (self.workspace / "repos/live.txt").write_text("live tree")
        runtime = rt.Runtime(dict(self.config, uid=2000, gid=2000, backup=dict(
            transport="management-ssh-stream", recipient="age1fixture", keyRecoveryId="external-key",
            retentionDays=7, destinationIdentity="operator")), self.root / "state-a", source, git=object())
        runtime.s.update(freezeCompleted=True, reposCheckpoint=final)
        archive = io.BytesIO()

        def encrypt(path, output, recipient, scope=None):
            rt.write_backup_archive(path, archive, scope)
            output.write_bytes(archive.getvalue())
        with patch.object(rt, "encrypt_archive", side_effect=encrypt):
            prepared = runtime.backup("stop-1")
        self.assertEqual((2, dict(checkpointId=final["checkpointId"], manifestSha256=final["manifestSha256"])),
                         (prepared["storageLayout"], prepared["checkpoint"]))
        names = set(rt.tarfile.open(fileobj=io.BytesIO(archive.getvalue())).getnames())
        prefix = "data/" + rt.CHECKPOINT_STORE + "/"
        self.assertEqual(set(rc.backup_files(str(store))["files"]),
                         {name[len(prefix):] for name in names
                          if name.startswith(prefix) and (store / name[len(prefix):]).is_file()})
        self.assertTrue({"data/repos", "data/t3/state.sqlite", prefix + "tmp", prefix + "staging"} <= names)
        self.assertFalse(any(name.startswith(("data/repos/", prefix + "tmp/", prefix + "staging/")) or
                             "f" * 64 in name for name in names))
        self.assertNotIn("data/" + rt.LAYOUT_MARKER, names)
        self.assertNotIn(prefix + "lock", names)
        self.assertNotIn(prefix + "status.json", names)

        receipt = {key: value for key, value in prepared.items() if key != "receiptId"}
        receipt.update(type="PrimaryBackup", freshness="VerifiedOffHost", verifiedAt=1)
        receipt["receiptId"] = rt.digest(receipt)
        target = self.root / "workspace-b"
        target.mkdir()
        (target / ".workspaces-volume.json").write_text("target ownership")
        volume = dict(providerVolumeId="vol-2", filesystemUuid="fs-2",
                      owner=dict(workspaceId="ws-1", providerVolumeId="vol-2"))
        changes = dict(workspace=str(target), volume=volume, vmId="vm-b", generation=4)
        successor = self.adapter(self.root / "vm-b", **changes)
        for name in ("mount", "verify_workspace_mount"):
            patcher = patch.object(successor, name)
            patcher.start()
            self.addCleanup(patcher.stop)
        restoring = rt.Runtime(dict(runtime.c, **changes), self.root / "state-b", successor, git=object())
        restoring.restore(dict(operationId="restore-1", authorizedRestore=True,
                               independentRecoveryKeyConfirmed=True, backupReceipt=receipt))
        with patch.object(rt.os, "chown") as chown:
            restored = restoring.restore_import("restore-1", io.BytesIO(archive.getvalue()))
        adopted = restored["checkpoint"]
        self.assertEqual(final["checkpointId"], adopted["adoptedFrom"]["checkpointId"])
        self.assertEqual(dict(schemaVersion=1, storageLayout=2, workspaceId="ws-1", providerVolumeId="vol-2",
                              filesystemUuid="fs-2", initialCheckpoint=adopted["checkpointId"]),
                         json.loads((target / rt.LAYOUT_MARKER).read_text()))
        self.assertFalse(any(rt.CHECKPOINT_STORE in str(call.args[0]) or rt.LAYOUT_MARKER in str(call.args[0])
                             for call in chown.call_args_list))
        chown.assert_any_call(target / "repos", 0, 0)
        self.assertEqual(b"durable T3 state", (target / "t3/state.sqlite").read_bytes())
        self.assertEqual([], list((target / "repos").iterdir()))
        self.assertEqual(0o700, (target / rt.CHECKPOINT_STORE).stat().st_mode & 0o777)
        # The bootstrap of the replacement VM restores the checkout tree from the adopted checkpoint.
        attached = self.attach(successor)
        self.assertEqual(("restored", adopted["checkpointId"]), (attached["action"], attached["checkpointId"]))
        self.assertEqual("uncommitted edit", (self.root / "vm-b/repos/project/edit.txt").read_text())

    def test_layout_2_backup_requires_the_current_final_checkpoint(self):
        local = self.root / "vm-a"
        source = self.adapter(local)
        self.attach(source)
        final = source.checkpoint_repos("stop-1")
        rc.save(str(local / "repos"), str(self.workspace / rt.CHECKPOINT_STORE), source.repos_binding(),
                str(local / "repos.lineage.json"), fenced=True, operation_id="later")
        runtime = rt.Runtime(dict(self.config, uid=2000, gid=2000, backup=dict(
            transport="management-ssh-stream", recipient="age1fixture", keyRecoveryId="external-key",
            retentionDays=7, destinationIdentity="operator")), self.root / "state-a", source, git=object())
        runtime.s.update(freezeCompleted=True)
        with patch.object(rt, "encrypt_archive") as encrypt:
            with self.assertRaisesRegex(rt.Blocked, "final checkout checkpoint"):
                runtime.backup("stop-1")
            runtime.s["reposCheckpoint"] = final
            with self.assertRaisesRegex(rt.Blocked, "no longer the current checkpoint"):
                runtime.backup("stop-1")
        encrypt.assert_not_called()

    def test_layout_keeps_the_mountpoint_root_owned_and_refuses_old_layout_data(self):
        adapter = rt.Linux(self.config)
        with patch.object(rt.os, "chown") as chown:
            adapter.layout()
        repos = self.workspace / "repos"
        chown.assert_any_call(repos, 0, 0)
        self.assertNotIn(repos, [call.args[0] for call in chown.call_args_list if call.args[1:] != (0, 0)])
        self.assertEqual(0o555, repos.stat().st_mode & 0o777)
        repos.chmod(0o755)
        (repos / "old-clone").mkdir()
        with patch.object(rt.os, "chown"), self.assertRaisesRegex(rt.Blocked, "requires migration"):
            adapter.layout()
        (repos / "old-clone").rmdir()
        (self.workspace / rt.LAYOUT_MARKER).write_text("{}")
        old = rt.Linux(dict(self.config, storageLayout=1))
        with patch.object(rt.os, "chown") as chown, self.assertRaisesRegex(rt.Blocked, "VM disk"):
            old.layout()
        chown.assert_not_called()

    def test_local_root_must_not_share_the_retained_volume(self):
        adapter = rt.Linux(self.config)
        with patch.object(rt, "LOCAL_ROOT", str(self.workspace / "local")), \
                patch.object(rt, "run", return_value="ext4"), \
                self.assertRaisesRegex(rt.Blocked, "must not use the retained volume"):
            adapter.local_root()

    def test_final_checkpoint_on_the_real_engine_restores_on_a_replacement_vm(self):
        local = self.root / "vm-a"
        adapter = self.adapter(local)
        self.attach(adapter)
        (local / "repos/project").mkdir()
        (local / "repos/project/edit.txt").write_text("final uncommitted edit")
        checkpoint = adapter.checkpoint_repos("stop-1")
        self.assertEqual(("stop-1", 3, 3), (checkpoint["operationId"], checkpoint["binding"]["generation"],
                                             checkpoint["binding"]["writerGeneration"]))
        adapter.verify_checkpoint(checkpoint, "stop-1")
        with self.assertRaisesRegex(rt.Blocked, "not taken by this operation"):
            adapter.verify_checkpoint(checkpoint, "stop-2")
        rc.save(str(local / "repos"), str(self.workspace / rt.CHECKPOINT_STORE), adapter.repos_binding(),
                str(local / "repos.lineage.json"), fenced=True, operation_id="later")
        with self.assertRaisesRegex(rt.Blocked, "no longer the current checkpoint"):
            adapter.verify_checkpoint(checkpoint, "stop-1")
        replacement = self.root / "vm-b"
        restored = self.attach(self.adapter(replacement, vmId="vm-b", generation=4))
        self.assertEqual("restored", restored["action"])
        self.assertEqual("final uncommitted edit", (replacement / "repos/project/edit.txt").read_text())

    def test_periodic_checkpoint_saves_only_while_ready_and_never_after_a_stop_fences(self):
        local = self.root / "vm-a"
        adapter = self.adapter(local)
        self.attach(adapter)
        patchers = (patch.object(adapter, "verify_workspace_mount"),
                    patch.object(rt, "load_config", side_effect=lambda path: json.loads(Path(path).read_text())))
        verify = [patcher.start() for patcher in patchers][0]
        for patcher in patchers:
            self.addCleanup(patcher.stop)
        store = str(self.workspace / rt.CHECKPOINT_STORE)
        state_dir = self.root / "state"
        state_dir.mkdir()

        def state(**changes):
            value = dict(identity=dict(vmId="vm-a", generation=3, kind="primary"),
                         configDigest=rt.digest(self.config), phase="Ready")
            rt.atomic_json(state_dir / "state.json", dict(value, **changes))

        def periodic(offset=0):
            return rt.checkpoint_periodic(self.config, state_dir, adapter, lambda: time.time_ns() + offset * 10**9)

        self.assertEqual(dict(action="skipped", reason="Workspace is not bootstrapped"), periodic())
        state()
        (local / "repos/project").mkdir()
        (local / "repos/project/edit.txt").write_text("periodic edit")
        # The volume's first checkpoint was just taken when the tree was attached.
        self.assertEqual("Checkpoint interval has not elapsed", periodic()["reason"])
        saved = periodic(301)
        self.assertEqual("saved", saved["action"])
        self.assertEqual(saved["checkpointId"], rc.status(store)["checkpointId"])
        self.assertEqual("Checkpoint interval has not elapsed", periodic(100)["reason"])
        (local / "repos/project/edit.txt").write_text("later edit")
        later = periodic(301)
        self.assertEqual("saved", later["action"])
        before = rc.status(store)
        for changes in (dict(phase="Fenced", fenceOperation="stop-1"), dict(phase="Resuming"),
                        dict(freezeCompleted=True), dict(configDigest="other-generation-config")):
            with self.subTest(changes=changes):
                state(**changes)
                self.assertEqual("skipped", periodic(1000)["action"])
        # A stop that fences after the first check but before the store lock still wins, and the
        # refused save records no attempt that could hide the final checkpoint's status.
        state()
        verify.side_effect = lambda: state(phase="Fenced", fenceOperation="stop-1")
        with self.assertRaisesRegex(rc.Deferred, "not admitting"):
            periodic(1000)
        self.assertEqual(before, rc.status(store))
        self.assertEqual(dict(action="disabled"),
                         rt.checkpoint_periodic(dict(self.config, storageLayout=1), state_dir, adapter))
        # The restored tree on a replacement VM has the saved edit.
        restored = self.attach(self.adapter(self.root / "vm-b", vmId="vm-b", generation=4))
        self.assertEqual(later["checkpointId"], restored["checkpointId"])
        self.assertEqual("later edit", (self.root / "vm-b/repos/project/edit.txt").read_text())

    def test_stopping_periodic_checkpoints_waits_for_the_running_save(self):
        adapter = rt.Linux(self.config)
        commands = []
        for state, error in (("inactive", None), ("deactivating", "did not stop")):
            with self.subTest(state=state), \
                    patch.object(rt, "run", side_effect=lambda argv, **kwargs: commands.append(argv) or state):
                if error:
                    with self.assertRaisesRegex(rt.Blocked, error):
                        adapter.stop_periodic_checkpoints()
                else:
                    adapter.stop_periodic_checkpoints()
        # Without --no-block, systemctl returns only after the save and its Git children exit.
        self.assertEqual(["systemctl", "stop", rt.CHECKPOINT_UNIT], commands[0])

    def test_periodic_units_run_outside_the_work_slice_and_stop_with_t3(self):
        units = Path(__file__).parents[1] / "systemd"
        t3 = (units / "workspaces-t3.service").read_text()
        self.assertRegex(t3, r"(?m)^Wants=.*\bworkspaces-repos-checkpoint\.timer\b")
        service = (units / "workspaces-repos-checkpoint.service").read_text()
        timer = (units / "workspaces-repos-checkpoint.timer").read_text()
        for text in (service, timer):
            self.assertIn("PartOf=workspaces-t3.service", text.splitlines())
        self.assertNotIn("Slice=", service)
        self.assertIn("ExecStart=/usr/bin/python3 -B /opt/workspaces-workspace/runtime.py checkpoint-periodic",
                      service.splitlines())
        self.assertIn("SuccessExitStatus=%d" % rc.EX_DEFERRED, service.splitlines())
        self.assertIn(f"ConditionPathExists=/workspace/{rt.LAYOUT_MARKER}", service.splitlines())
        self.assertIn("OnUnitInactiveSec=60", timer.splitlines())
        self.assertEqual(rt.CHECKPOINT_UNIT, "workspaces-repos-checkpoint.service")

    def test_readonly_and_writable_cover_the_checkout_mount(self):
        adapter = rt.Linux(self.config)
        commands = []
        repos = str(self.workspace / "repos")
        with patch.object(adapter, "probe_volume", return_value="/dev/sdb"), \
                patch.object(adapter, "verify_owner"), patch.object(adapter, "verify_repos_mount") as verify, \
                patch.object(rt.os, "sync"), patch.object(rt.os.path, "ismount", return_value=True), \
                patch.object(rt, "run", side_effect=lambda argv, **kwargs: commands.append(argv) or
                             ("/dev/sdb" if "SOURCE" in argv else "ro" if "OPTIONS" in argv else "")):
            adapter.readonly()
            remounts = [argv for argv in commands if argv[:2] == ["mount", "-o"]]
            self.assertEqual([["mount", "-o", "remount,bind,ro,nodev,nosuid", repos],
                              ["mount", "-o", "remount,ro", str(self.workspace)]], remounts)
            self.assertEqual(False, verify.call_args.kwargs["writable"])
            del commands[:]
            with patch.object(rt, "run", side_effect=lambda argv, **kwargs: commands.append(argv) or "rw"):
                adapter.writable()
            self.assertEqual(["mount", "-o", "remount,bind,rw,nodev,nosuid", repos], commands[-1])
        with patch.object(adapter, "probe_volume"), patch.object(adapter, "verify_owner"), \
                patch.object(rt.os.path, "ismount", return_value=False), \
                patch.object(rt, "run", return_value="rw"), \
                patch.object(adapter, "attach_repos") as attach:
            adapter.writable()
        attach.assert_called_once_with(restore=False)

    def test_checkout_mount_is_writable_and_visible_to_t3_tasks_and_folder_jobs(self):
        unit = (Path(__file__).parents[1] / "systemd/workspaces-t3.service").read_text().splitlines()
        values = lambda key: " ".join(line.split("=", 1)[1] for line in unit if line.startswith(key + "="))
        # T3 cannot start before the mount unit has verified the checkout mount.
        for key in ("Requires", "After"):
            self.assertIn("workspaces-workspace-mount.service", values(key).split())
        # ReadWritePaths= applies recursively to the mounts below it at unit start.
        self.assertIn("/workspace", values("ReadWritePaths").split())
        adapter = rt.Linux(dict(self.config, workspace="/workspace"))
        commands = []
        with patch.object(rt, "run", side_effect=lambda argv, **kwargs: commands.append(argv)):
            adapter.launch("task", ["true"])
            payload = dict(mode="delete", workspace="/workspace", path=["repos", "project"], localRepos=True,
                           vmId="vm-a", generation=3)
            with patch.object(adapter, "filesystem_prepare") as prepare:
                adapter.filesystem_launch("job", payload, self.root / "out")
            prepare.assert_not_called()
        self.assertIn("--property=ReadWritePaths=/workspace -" + rt.SCRATCH_ROOT, commands[0])
        writable = [arg for arg in commands[1] if arg.startswith("--property=ReadWritePaths=")]
        self.assertIn("-/workspace/repos", writable[0].split("=", 2)[2].split())

    def test_runtime_bootstrap_attaches_before_ready(self):
        fake = FakeLinux()
        config = dict(self.config, uid=2000, gid=2000,
                      volume=dict(device="/dev/disk/by-id/fake", providerDeviceId="fake",
                                  providerVolumeId="vol-1", filesystemUuid="fixture-fs",
                                  owner={"workspaceId": "workspace"}))
        runtime = rt.Runtime(config, self.root / "state", fake, git=object(), clock=lambda: 1000)
        request = dict(vmId="vm-a", generation=3, gatewayAdmissionsFenced=True, unobservableToolsAbsent=True,
                       t3SnapshotSequence=0)
        runtime.handle(dict(request, operationId="boot", action="bootstrap"))
        self.assertEqual(["mount", "migration-check", "layout", "attach-repos"], fake.events)
        self.assertEqual("Ready", runtime.s["phase"])
        # Migration runs only on request, before the layout, and its record reaches the receipt.
        fake.events.clear()
        fake.finish_layout_migration = lambda: dict(checkpointId="c-1", saveMs=5)
        migrating = rt.Runtime(dict(config, migrateStorageLayout=True), self.root / "migrate", fake,
                               git=object(), clock=lambda: 1000)
        receipt = migrating.handle(dict(request, operationId="boot", action="bootstrap"))
        self.assertEqual(["mount", "migrate-layout", "layout", "attach-repos"], fake.events)
        self.assertEqual(dict(checkpointId="c-1", saveMs=5), receipt["storageLayoutMigration"])
        rt.Runtime(dict(config, checkpointIntervalSeconds=60), self.root / "interval", fake, git=object())
        for change in (dict(kind="ephemeral", volume=None, ignoredDataAcknowledged=True),
                       dict(storageLayout=3), dict(storageLayout=True),
                       dict(checkpointIntervalSeconds=59), dict(checkpointIntervalSeconds=3601),
                       dict(checkpointIntervalSeconds=True), dict(checkpointIntervalSeconds=300.0),
                       dict(storageLayout=1, checkpointIntervalSeconds=300),
                       dict(migrateStorageLayout=1), dict(storageLayout=1, migrateStorageLayout=True)):
            with self.subTest(change=change), self.assertRaises(rt.Blocked):
                rt.Runtime(dict(config, **change), self.root / "invalid", fake, git=object())


class LinuxAdapterTests(unittest.TestCase):
    def test_wrong_uuid_rejected_without_mount_or_format(self):
        adapter = rt.Linux(dict(workspace="/workspace"))
        claim = dict(device="/dev/disk/by-id/scsi-volume", providerDeviceId="scsi-volume",
                     filesystemUuid="expected")
        fake_stat = type("Stat", (), {"st_mode": 0o060000})()
        calls = []
        def command(argv, **kwargs):
            calls.append(argv)
            return "ext4" if "TYPE" in argv else "wrong"
        with patch.object(Path, "stat", return_value=fake_stat), patch.object(rt, "run", side_effect=command):
            with self.assertRaisesRegex(rt.Blocked, "UUID"):
                adapter.mount(claim)
        self.assertTrue(all(argv[0] == "blkid" for argv in calls))

    def test_scratch_detach_stops_helper_units_before_unmounting_and_fails_closed(self):
        adapter = rt.Linux(dict(workspace="/workspace"))
        for failed in (0, 1):
            calls = []

            def command(argv, **kwargs):
                calls.append(argv[-1] if argv[0] != "systemctl" else " ".join(argv[:2]))
                if argv[-1] == "unmount-all":
                    return json.dumps(dict(unmounted=3, lazy=1, failed=failed, lazyPaths=["/w/x"]))
                return json.dumps(dict(ok=True))
            with self.subTest(failed=failed), patch.object(Path, "is_file", return_value=True), \
                    patch.object(rt, "run", side_effect=command):
                if failed:
                    with self.assertRaisesRegex(rt.Blocked, "remain"):
                        adapter.scratch_detach()
                else:
                    self.assertEqual(dict(unmounted=3, lazy=1, lazyPaths=["/w/x"]), adapter.scratch_detach())
                self.assertEqual(["suspend", "systemctl stop", "unmount-all"], calls)
        with patch.object(Path, "is_file", return_value=False), patch.object(rt, "run") as command:
            self.assertIsNone(adapter.scratch_detach())
            command.assert_not_called()

    def test_unexpected_filesystem_never_formatted(self):
        adapter = rt.Linux(dict(workspace="/workspace"))
        fake_stat = type("Stat", (), {"st_mode": 0o060000})()
        with patch.object(Path, "stat", return_value=fake_stat), patch.object(rt, "run", return_value="xfs") as command:
            with self.assertRaisesRegex(rt.Blocked, "never formats"):
                adapter.mount(dict(device="/dev/disk/by-id/a", providerDeviceId="a", filesystemUuid="fs"))
            self.assertEqual("blkid", command.call_args.args[0][0])

    def test_canonical_urls_disallow_helpers_tokens_and_local_sidechannels(self):
        for value in ("file:///workspace/primary", "ext::command", "git@example.com:org/repo",
                      "https://secret@example.com/repo", "https://host/../repo"):
            with self.subTest(value=value), self.assertRaises(rt.Blocked):
                rt.canonical_remote(value)
        self.assertEqual("https://example.invalid/org/repo.git",
                         rt.canonical_remote("https://example.invalid/org/repo.git"))


if __name__ == "__main__":
    unittest.main()
