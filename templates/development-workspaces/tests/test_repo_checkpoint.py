#!/usr/bin/env python3
"""Repository checkpoint engine checks with real Git on synthetic trees. No root, mounts or provider."""
import errno
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

HERE = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(HERE))
import repo_checkpoint as rc
import scratch_mounts as sm

GIT_ENV = dict(os.environ, GIT_CONFIG_GLOBAL="/dev/null", GIT_CONFIG_NOSYSTEM="1",
               GIT_AUTHOR_NAME="t", GIT_AUTHOR_EMAIL="t@example.invalid",
               GIT_COMMITTER_NAME="t", GIT_COMMITTER_EMAIL="t@example.invalid")
BINDING = dict(workspaceId="ws-1", providerVolumeId="vol-1", filesystemUuid="uuid-1",
               vmId="vm-1", generation=3, writerGeneration=3)


def git(cwd, *args):
    return subprocess.run(["git", *args], cwd=cwd, env=GIT_ENV, check=True, capture_output=True).stdout


def snapshot_tree(root):
    """Relative path -> (kind, mode, content or target) for comparison."""
    result = {}
    for directory, names, files in os.walk(root):
        for name in names + files:
            path = os.path.join(directory, name)
            info = os.lstat(path)
            relative = os.path.relpath(path, root)
            if os.path.islink(path):
                result[relative] = ("link", os.readlink(path))
            elif os.path.isdir(path):
                result[relative] = ("dir", info.st_mode & 0o7777, info.st_mtime_ns)
            else:
                result[relative] = ("file", info.st_mode & 0o7777, info.st_mtime_ns, Path(path).read_bytes())
    return result


class CheckpointTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.base = Path(os.path.realpath(self.temporary.name))
        self.source = self.base / "repos"
        self.store = str(self.base / "store")
        self.lineage = str(self.base / "repos.lineage.json")
        self.source.mkdir()
        for name, value in (("RACY_NS", 50_000_000), ("TICK_MARGIN_NS", 20_000_000)):
            patcher = patch.object(rc, name, value)
            patcher.start()
            self.addCleanup(patcher.stop)

    def tearDown(self):
        self.temporary.cleanup()

    def save(self, **extra):
        return rc.save(str(self.source), self.store, dict(BINDING), self.lineage, logical_root=str(self.source),
                       **extra)

    def restore(self, name="repos", binding=BINDING, **extra):
        lineage = self.lineage if name == "repos" else str(self.base / (name + ".lineage.json"))
        return rc.restore(self.store, str(self.base), name, dict(binding), lineage, **extra)

    def make_app(self):
        app = self.source / "app"
        app.mkdir()
        git(app, "init", "-q", "-b", "main")
        (app / ".gitignore").write_text("node_modules/\n.env\ndist/\n")
        (app / "package.json").write_text("{}\n")
        (app / "run.sh").write_text("#!/bin/sh\n")
        (app / "run.sh").chmod(0o755)
        (app / "dist").mkdir()
        (app / "dist" / "kept.js").write_text("tracked output\n")
        git(app, "add", "-A")
        git(app, "add", "-f", "dist/kept.js")
        git(app, "commit", "-qm", "initial")
        (app / "stash.txt").write_text("stash me\n")
        git(app, "add", "stash.txt")
        git(app, "stash", "-q")
        (app / "staged.txt").write_text("staged\n")
        git(app, "add", "staged.txt")
        (app / "package.json").write_text('{"dirty": true}\n')
        (app / "untracked.txt").write_text("new\n")
        (app / ".env").write_text("LOCAL=1\n")
        (app / "node_modules" / "pkg").mkdir(parents=True)
        (app / "node_modules" / "pkg" / "index.js").write_text("rebuildable\n")
        (app / "dist" / "bundle.js").write_text("ignored but holds a tracked sibling\n")
        (app / "build").mkdir()
        (app / "build" / "not-ignored.txt").write_text("not ignored\n")
        (app / "empty").mkdir()
        (app / "link").symlink_to("run.sh")
        git(app, "worktree", "add", "-q", "-b", "feature", str(self.source / "app-feature"))
        (self.source / "app-feature" / "wip.txt").write_text("worktree edit\n")
        notes = self.source / "notes"
        (notes / "bin").mkdir(parents=True)
        (notes / "bin" / "tool").write_text("non-Git project data\n")
        return app

    def test_round_trip_restores_git_state_and_omits_only_confirmed_outputs(self):
        app = self.make_app()
        before = {command: git(directory, *command.split())
                  for directory in (app, self.source / "app-feature")
                  for command in ("status --porcelain=v1", "stash list", "worktree list --porcelain")}
        expected = {path: item for path, item in snapshot_tree(self.source).items()
                    if not path.startswith("app/node_modules")}
        result = self.save()
        self.assertEqual(1, result["omittedCount"])
        self.assertEqual([], result["externalReferences"])

        self.source.rename(self.base / "repos.old")
        restored = self.restore("repos", dict(BINDING, vmId="vm-2", generation=4))
        self.assertEqual(result["checkpointId"], restored["checkpointId"])
        self.assertEqual(expected, snapshot_tree(self.source))
        after = {command: git(directory, *command.split())
                 for directory in (app, self.source / "app-feature")
                 for command in ("status --porcelain=v1", "stash list", "worktree list --porcelain")}
        self.assertEqual(before, after)
        self.assertFalse((app / "node_modules").exists())
        self.assertTrue((app / "dist" / "bundle.js").exists())

    def test_a_later_save_preserves_deletion_and_keeps_the_previous_checkpoint(self):
        self.make_app()
        first = self.save()
        shutil.rmtree(self.source / "notes")
        second = self.save()
        self.assertEqual(0, second["objectsWritten"])
        self.assertEqual("ready", rc.status(self.store)["state"])
        rc.verify(self.store, checkpoint_id=first["checkpointId"], deep=True)
        self.source.rename(self.base / "old")
        self.restore("repos", BINDING)
        self.assertFalse((self.source / "notes").exists())
        self.assertTrue((self.source / "app" / "untracked.txt").exists())

    def test_disk_full_and_interrupted_publish_keep_the_previous_checkpoint(self):
        self.make_app()
        first = self.save()
        (self.source / "notes" / "more.txt").write_text("x" * 1000)
        with patch.object(rc.Store, "put_object", side_effect=OSError(errno.ENOSPC, "full")):
            with self.assertRaisesRegex(rc.Failed, "destination is full"):
                self.save()
        with patch.object(rc, "syncfs", side_effect=OSError(errno.EIO, "interrupted")):
            with self.assertRaisesRegex(rc.Failed, "save failed"):
                self.save()
        status = rc.status(self.store)
        self.assertEqual((first["checkpointId"], "failed"),
                         (status["checkpointId"], status["lastAttempt"]["outcome"]))
        self.assertEqual(first["checkpointId"], rc.verify(self.store, BINDING, deep=True)["checkpointId"])
        self.assertEqual([], os.listdir(os.path.join(self.store, "tmp")))
        self.assertEqual([], os.listdir(os.path.join(self.store, "staging")))
        retry = self.save(fenced=True, operation_id="stop-1")
        self.assertEqual(retry["checkpointId"], rc.verify(self.store, BINDING, operation_id="stop-1")["checkpointId"])

    def test_change_during_copy_defers_periodic_and_fails_fenced_save(self):
        self.make_app()
        original = rc.Store.put_object

        def mutate(store, source, capture):
            (self.source / "notes" / "bin" / "tool").write_text("changed while copying\n")
            return original(store, source, capture)

        with patch.object(rc.Store, "put_object", mutate):
            with self.assertRaisesRegex(rc.Deferred, "changed during"):
                self.save()
            self.assertEqual("deferred", rc.status(self.store)["lastAttempt"]["outcome"])
            with self.assertRaisesRegex(rc.Failed, "writers were fenced"):
                self.save(fenced=True)
        self.assertEqual("empty", rc.status(self.store)["state"])

    def test_concurrent_periodic_save_defers(self):
        rc.Store(self.store).prepare(create=True)
        holder = rc.Store(self.store)
        holder.lock(0)
        try:
            with self.assertRaisesRegex(rc.Deferred, "Another checkpoint"):
                self.save()
        finally:
            holder.unlock()

    def test_binding_mismatch_is_refused(self):
        (self.source / "file.txt").write_text("data\n")
        self.save()
        with self.assertRaisesRegex(rc.Failed, "writer generation"):
            rc.verify(self.store, dict(BINDING, writerGeneration=4))
        with self.assertRaisesRegex(rc.Failed, "not taken by this operation"):
            rc.verify(self.store, BINDING, operation_id="stop-2")
        with self.assertRaisesRegex(rc.Failed, "another workspace volume"):
            self.restore("other", dict(BINDING, providerVolumeId="vol-2"))
        with self.assertRaisesRegex(rc.Failed, "another workspace volume"):
            rc.save(str(self.source), self.store, dict(BINDING, workspaceId="ws-2"), self.lineage)
        with self.assertRaisesRegex(rc.Failed, "Invalid checkpoint binding"):
            rc.verify(self.store, dict(BINDING, generation="3"))

    def test_restore_is_retryable_and_refuses_existing_target_or_corrupt_data(self):
        (self.source / "file.txt").write_text("data\n")
        result = self.save()
        staging = self.base / ".restore-staging"
        (staging / "junk").mkdir(parents=True)
        self.restore("copy", BINDING)
        self.assertEqual("data\n", (self.base / "copy" / "file.txt").read_text())
        self.assertFalse(staging.exists())
        with self.assertRaisesRegex(rc.Failed, "already exists"):
            self.restore("copy", BINDING)

        manifest = json.loads(Path(self.store, "manifests", result["checkpointId"] + ".json").read_bytes())
        sha = manifest["entries"]["file.txt"]["sha256"]
        corrupt = Path(self.store, "objects", sha[:2], sha)
        corrupt.chmod(0o600)
        corrupt.write_text("DATA\n")
        with self.assertRaisesRegex(rc.Failed, "missing or corrupt"):
            self.restore("second", BINDING)
        self.assertFalse((self.base / "second").exists())
        with self.assertRaisesRegex(rc.Failed, "missing or corrupt"):
            rc.verify(self.store, BINDING, deep=True)

    def test_status_distinguishes_empty_from_missing_or_corrupt_pointer(self):
        self.assertEqual("empty", rc.status(self.store)["state"])
        (self.source / "file.txt").write_text("data\n")
        self.save()
        pointer = Path(self.store, "current.json")
        pointer.write_text("{")
        self.assertEqual("corrupt", rc.status(self.store)["state"])
        with self.assertRaisesRegex(rc.Failed, "pointer is corrupt"):
            self.restore("copy", BINDING)
        pointer.unlink()
        self.assertEqual(("corrupt", "Checkpoint pointer is missing"),
                         tuple(rc.status(self.store)[key] for key in ("state", "reason")))

    def test_external_references_special_files_and_git_failure_are_reported_not_dropped(self):
        app = self.source / "app"
        app.mkdir()
        git(app, "init", "-q")
        (app / ".gitignore").write_text("node_modules/\n")
        (app / "node_modules").mkdir()
        (app / "node_modules" / "kept.js").write_text("kept when Git fails\n")
        (app / "outside").symlink_to("/etc/hostname")
        (app / "escape").symlink_to("../../elsewhere")
        os.mkfifo(app / "pipe")
        linked = self.source / "linked"
        linked.mkdir()
        (linked / ".git").write_text("gitdir: /srv/other/.git/worktrees/linked\n")

        class FailingGit:
            def ignored(self, repo_fd, paths):
                return None

        result = self.save(git=FailingGit())
        self.assertEqual(0, result["omittedCount"])
        self.assertEqual(1, result["skippedCount"])
        self.assertEqual([("app/escape", "symlink"), ("app/outside", "symlink"), ("linked/.git", "gitdir")],
                         [(item["path"], item["kind"]) for item in result["externalReferences"]])
        self.source.rename(self.base / "old")
        self.restore("repos", BINDING)
        self.assertTrue((self.source / "app" / "node_modules" / "kept.js").exists())
        self.assertFalse((self.source / "app" / "pipe").exists())

    def test_empty_replacement_tree_never_overwrites_the_checkpoint_to_restore(self):
        (self.source / "work.txt").write_text("uncommitted\n")
        first = self.save()["checkpointId"]
        self.source.rename(self.base / "lost-vm-disk")
        self.source.mkdir()
        with self.assertRaisesRegex(rc.Failed, "replaced after it was restored"):
            self.save()
        os.unlink(self.lineage)
        with self.assertRaisesRegex(rc.Failed, "not restored from the current checkpoint"):
            self.save(fenced=True)
        self.assertEqual(first, rc.verify(self.store, BINDING, deep=True)["checkpointId"])
        Path(self.store, "current.json").write_text("{")
        with self.assertRaisesRegex(rc.Failed, "pointer is corrupt"):
            self.save()
        Path(self.store, "current.json").unlink()
        with self.assertRaisesRegex(rc.Failed, "not restored from the current checkpoint"):
            self.save()
        self.assertFalse(os.path.exists(self.lineage))

    def test_restored_tree_saves_but_a_tree_from_an_older_checkpoint_does_not(self):
        (self.source / "work.txt").write_text("one\n")
        first = self.save()["checkpointId"]
        (self.source / "work.txt").write_text("two\n")
        self.save()
        self.restore("old", checkpoint_id=first)
        with self.assertRaisesRegex(rc.Failed, "not restored from the current checkpoint"):
            rc.save(str(self.base / "old"), self.store, dict(BINDING), str(self.base / "old.lineage.json"))
        self.source.rename(self.base / "lost-vm-disk")
        self.restore("repos")
        (self.source / "work.txt").write_text("three\n")
        self.save()
        self.save()
        self.assertEqual("three\n", (self.source / "work.txt").read_text())

    def test_prepare_tree_initializes_keeps_or_restores_and_detects_a_missing_checkpoint(self):
        def prepare(allow_empty=False):
            return rc.prepare_tree(self.store, str(self.base), "live", dict(BINDING), self.lineage, allow_empty,
                                   logical_root=str(self.base / "live"))

        with self.assertRaisesRegex(rc.Failed, "Checkpoint is missing"):
            prepare()
        self.assertFalse((self.base / "live").exists())
        initialized = prepare(allow_empty=True)
        self.assertEqual(("initialized", "ready"), (initialized["action"], rc.status(self.store)["state"]))
        live = self.base / "live"
        self.source = live
        (live / "saved.txt").write_text("saved\n")
        saved = self.save()["checkpointId"]
        (live / "newer.txt").write_text("not yet checkpointed\n")
        self.assertEqual(dict(action="kept", checkpointId=saved), prepare())
        self.assertTrue((live / "newer.txt").exists())

        live.rename(self.base / "lost-vm-disk")
        restored = prepare()
        self.assertEqual(("restored", saved), (restored["action"], restored["checkpointId"]))
        self.assertEqual(["saved.txt"], os.listdir(live))
        self.save()

        shutil.rmtree(live)
        live.mkdir()
        with self.assertRaisesRegex(rc.Failed, "replaced after it was restored"):
            prepare()
        shutil.rmtree(self.store)
        with self.assertRaisesRegex(rc.Failed, "Checkpoint is missing"):
            prepare()

    def test_rebuildable_names_match_the_scratch_output_allowlist(self):
        self.assertEqual(sm.ALLOWLIST, rc.REBUILDABLE)

    def test_cli_reports_deferred_exit_status(self):
        binding = self.base / "binding.json"
        binding.write_text(json.dumps(BINDING))
        (self.source / "file.txt").write_text("data\n")
        command = [sys.executable, "-B", str(HERE / "repo_checkpoint.py")]
        saved = subprocess.run(command + ["save", "--store", self.store, "--binding", str(binding),
                                          "--source", str(self.source), "--logical-root", str(self.source),
                                          "--lineage", self.lineage],
                               capture_output=True, text=True)
        self.assertEqual(0, saved.returncode, saved.stdout)
        self.assertTrue(json.loads(saved.stdout)["ok"])
        holder = rc.Store(self.store)
        holder.lock(0)
        try:
            deferred = subprocess.run(command + ["save", "--store", self.store, "--binding", str(binding),
                                                 "--source", str(self.source), "--lineage", self.lineage],
                                      capture_output=True, text=True)
        finally:
            holder.unlock()
        self.assertEqual((rc.EX_DEFERRED, "Deferred"), (deferred.returncode, json.loads(deferred.stdout)["state"]))


if __name__ == "__main__":
    unittest.main()
