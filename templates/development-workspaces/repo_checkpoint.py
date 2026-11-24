#!/usr/bin/env python3
"""repo_checkpoint: save and restore the primary checkout tree on the retained volume (#105).

A checkpoint is a complete, verified copy of the live repository tree. File contents are stored
once in a root-owned, content-addressed object store. Each save writes a separate staging manifest
that is verified and flushed before it is published by atomically replacing current.json. The
previous published checkpoint stays readable until the new one is published, so an interrupted
or disk-full save never loses it. One lock serializes every save, verification, and restore.

Data is copied by default: Git metadata, stashes, the index, new and ignored files and non-Git
directories. Only a directory named in REBUILDABLE is omitted, and only when Git run as the coding
user confirms that the directory is ignored and contains no tracked files. If Git fails, the
directory is copied. FIFOs and sockets hold no data and are reported as skipped. Device nodes or
other filesystems inside the tree fail the save.

The source is walked twice. The second pass rechecks every directory listing, inode, size,
timestamp, mode, and symlink target. It also rehashes files changed close to the first pass,
whose ctime might not have advanced. A periodic save that sees a change is deferred, and a save
under a writer fence fails. Paths in the tree are opened component by component relative to
verified directory descriptors with O_NOFOLLOW. Standard library only.

Commands: save, verify, restore, status. Each prints one JSON object. Exit status 0 means
success, 75 means deferred, and 1 means failed.
"""
import argparse
import ctypes
import errno
import fcntl
import hashlib
import json
import os
import re
import secrets
import shutil
import stat
import subprocess
import sys
import time

STORE = "/workspace/.workspaces-checkpoints"
SOURCE = "/workspace/repos"
LOGICAL_ROOT = "/workspace/repos"
CODING_UID = CODING_GID = 2000
SCHEMA_VERSION = 1
# The scratch build-output allowlist, reused as a backup filter.
REBUILDABLE = frozenset({
    "node_modules", "bin", "obj", "dist", "build", "out", "target", ".next", ".nuxt", ".svelte-kit",
    ".turbo", ".parcel-cache", ".angular", ".gradle", ".venv", "venv", "__pycache__", "coverage",
    ".pytest_cache", ".mypy_cache"})
KEEP = 2
MAX_ENTRIES = 2_000_000
MAX_DEPTH = 256
MAX_REPORTED = 50
MAX_REFERENCE_BYTES = 64 * 1024
RACY_NS = 1_000_000_000
TICK_MARGIN_NS = 100_000_000
MAX_SETTLE_NS = 2_000_000_000
GIT_SECONDS = 60
LOCK_SECONDS = 600
CHUNK = 1 << 20
DIRECTORY = os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW | os.O_CLOEXEC
READ_FILE = os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK | os.O_CLOEXEC
BINDING_FIELDS = {"workspaceId": str, "providerVolumeId": str, "filesystemUuid": str,
                  "vmId": str, "generation": int, "writerGeneration": int}
VOLUME_FIELDS = ("workspaceId", "providerVolumeId", "filesystemUuid")
IDENTIFIER = re.compile(r"[A-Za-z0-9][A-Za-z0-9._:-]{0,127}")
OPERATION = re.compile(r"[A-Za-z0-9_-]{1,128}")
CHECKPOINT_ID = re.compile(r"c-[0-9]{20}-[0-9a-f]{16}")
SHA256 = re.compile(r"[0-9a-f]{64}")
STORE_DIRS = ("objects", "tmp", "staging", "manifests")
EX_DEFERRED = 75


class Deferred(Exception):
    """A periodic save could not take a consistent copy now; the previous checkpoint stands."""
    def __init__(self, reason):
        super().__init__(reason)
        self.reason = reason


class Failed(Exception):
    """A sanitized failure. The previous published checkpoint is unchanged."""
    def __init__(self, reason):
        super().__init__(reason)
        self.reason = reason


class Changed(Exception):
    pass


def validate_binding(value):
    if not isinstance(value, dict) or set(value) != set(BINDING_FIELDS):
        raise Failed("Invalid checkpoint binding")
    for key, kind in BINDING_FIELDS.items():
        item = value[key]
        valid = (type(item) is int and 0 < item < 2 ** 63) if kind is int else \
            isinstance(item, str) and IDENTIFIER.fullmatch(item) is not None
        if not valid:
            raise Failed("Invalid checkpoint binding")
    return dict(value)


def same_volume(left, right):
    return all(left.get(key) == right.get(key) for key in VOLUME_FIELDS)


def canonical(value):
    return json.dumps(value, sort_keys=True, separators=(",", ":")).encode()


def sort_key(path):
    return tuple(os.fsencode(path).split(b"/")) if path else ()


def fsync_path(path):
    fd = os.open(path, os.O_RDONLY | os.O_CLOEXEC)
    try:
        os.fsync(fd)
    finally:
        os.close(fd)


def write_durable(path, data, mode=0o600):
    """Write a store file atomically and durably."""
    pending = path + ".pending"
    fd = os.open(pending, os.O_WRONLY | os.O_CREAT | os.O_TRUNC | os.O_NOFOLLOW | os.O_CLOEXEC, mode)
    try:
        view = memoryview(data)
        while view:
            view = view[os.write(fd, view):]
        os.fsync(fd)
    finally:
        os.close(fd)
    os.replace(pending, path)
    fsync_path(os.path.dirname(path))


def syncfs(fd):
    try:
        libc = ctypes.CDLL(None, use_errno=True)
        if libc.syncfs(fd) == 0:
            return
    except (OSError, AttributeError):
        pass
    os.sync()


def boot_id():
    try:
        with open("/proc/sys/kernel/random/boot_id", encoding="ascii") as stream:
            return stream.read().strip()[:64]
    except OSError:
        return None


def logical_external(logical_root, directory, target):
    """True when a link or Git reference points outside the supported workspace tree."""
    if not target:
        return True
    base = logical_root if not directory else logical_root + "/" + directory
    resolved = os.path.normpath(target if os.path.isabs(target) else os.path.join(base, target))
    return not (resolved == logical_root or resolved.startswith(logical_root.rstrip("/") + "/"))


def reference_kind(parts, in_git):
    """Git files whose content names another directory."""
    if not in_git:
        return "gitdir" if parts[-1] == ".git" else None
    if len(parts) >= 3 and parts[-1] == "gitdir" and parts[-3] == "worktrees":
        return "worktree"
    if len(parts) >= 3 and parts[-3:] == ["objects", "info", "alternates"]:
        return "alternates"
    return None


def reference_targets(kind, data):
    text = data.decode("utf-8", "surrogateescape")
    if kind == "gitdir":
        line = text.splitlines()[0] if text else ""
        return [line[len("gitdir:"):].strip()] if line.startswith("gitdir:") else [""]
    if kind == "worktree":
        return [text.strip()]
    return [line.strip() for line in text.splitlines() if line.strip() and not line.startswith("#")]


class Git:
    """Classifies rebuildable directories. Git runs as the coding user without hooks or config."""
    def __init__(self, uid=CODING_UID, gid=CODING_GID):
        self.privilege = dict(user=uid, group=gid, extra_groups=[]) if os.geteuid() == 0 else {}

    def ignored(self, repo_fd, paths):
        """Ignored subset of repository-relative directory paths, or None when Git fails."""
        try:
            real = os.readlink("/proc/self/fd/%d" % repo_fd)
        except OSError:
            return None
        env = dict(PATH="/usr/bin:/bin", HOME="/nonexistent", LC_ALL="C", GIT_TERMINAL_PROMPT="0",
                   GIT_CONFIG_NOSYSTEM="1", GIT_CONFIG_GLOBAL="/dev/null", GIT_OPTIONAL_LOCKS="0",
                   GIT_ATTR_NOSYSTEM="1", GIT_NO_REPLACE_OBJECTS="1",
                   GIT_CEILING_DIRECTORIES=os.path.dirname(real))
        base = ["git", "-c", "core.hooksPath=/dev/null", "-c", "core.fsmonitor=false",
                "-c", "core.untrackedCache=false", "-C", "/proc/self/fd/%d" % repo_fd]

        def git(arguments, data=None, **extra):
            try:
                return subprocess.run(base + arguments, input=data, capture_output=True,
                                      env=dict(env, **extra), timeout=GIT_SECONDS,
                                      pass_fds=(repo_fd,), cwd="/", **self.privilege)
            except (OSError, subprocess.SubprocessError):
                return None

        result = git(["check-ignore", "--stdin", "-z"],
                     b"".join(os.fsencode(path) + b"/\0" for path in paths))
        if result is None or result.returncode not in (0, 1):
            return None
        ignored = {os.fsdecode(item).rstrip("/") for item in result.stdout.split(b"\0") if item}
        if not ignored:
            return ignored
        tracked = git(["ls-files", "-z", "--cached", "--"] + sorted(ignored), GIT_LITERAL_PATHSPECS="1")
        if tracked is None or tracked.returncode != 0:
            return None
        for item in tracked.stdout.split(b"\0"):
            if item:
                name = os.fsdecode(item)
                ignored = {path for path in ignored if not (name == path or name.startswith(path + "/"))}
        return ignored


def stat_key(info):
    return (stat.S_IFMT(info.st_mode), info.st_ino, info.st_size, info.st_mtime_ns,
            info.st_ctime_ns, stat.S_IMODE(info.st_mode))


class Snapshot:
    """Walks the source tree once to copy it and again to prove that it did not change."""
    def __init__(self, root_fd, store, logical_root, git, previous=None, clock=time.time_ns):
        self.root_fd = root_fd
        self.store = store
        self.logical_root = logical_root
        self.git = git
        self.clock = clock
        self.device = os.fstat(root_fd).st_dev
        self.entries = {}
        self.keys = {}
        self.omitted = {}
        self.skipped = []
        self.external = []
        self.racy = {}
        self.candidates = {}
        self.previous = previous or {}
        self.started_ns = clock()
        self.stats = dict(files=0, directories=0, symlinks=0, bytes=0, bytesWritten=0,
                          objectsWritten=0, bytesHashed=0)

    def open_dir(self, parts):
        """Reopen a directory recorded by the first pass, verifying each component."""
        fd = os.dup(self.root_fd)
        try:
            for index, name in enumerate(parts):
                child = os.open(name, DIRECTORY, dir_fd=fd)
                os.close(fd)
                fd = child
                expected = self.keys.get("/".join(parts[:index + 1])) or \
                    self.omitted.get("/".join(parts[:index + 1]))
                if expected is None or os.fstat(fd).st_ino != expected[1]:
                    raise Changed()
            return fd
        except FileNotFoundError as error:
            os.close(fd)
            raise Changed() from error
        except BaseException:
            os.close(fd)
            raise

    def copy(self):
        self.keys[""] = stat_key(os.fstat(self.root_fd))
        self.walk(os.dup(self.root_fd), [], None, False)
        while self.candidates:
            pending, self.candidates = self.candidates, {}
            for repo, paths in sorted(pending.items(), key=lambda item: sort_key(item[0])):
                self.classify(repo, paths)

    def classify(self, repo, paths):
        repo_parts = repo.split("/") if repo else []
        relative = {path[len(repo) + 1:] if repo else path: path for path in paths}
        ignored = None
        try:
            repo_fd = self.open_dir(repo_parts)
        except OSError as error:
            raise Changed() from error
        try:
            ignored = self.git.ignored(repo_fd, sorted(relative))
        finally:
            os.close(repo_fd)
        for name, path in sorted(relative.items(), key=lambda item: sort_key(item[1])):
            if ignored is not None and name in ignored:
                self.omitted[path] = self.keys.pop(path)
                del self.entries[path]
                self.stats["directories"] -= 1
                continue
            fd = self.open_dir(path.split("/"))
            if os.fstat(fd).st_dev != self.device:
                os.close(fd)
                raise Failed("Checkout tree contains another filesystem")
            self.walk(fd, path.split("/"), repo, False)

    def walk(self, fd, parts, repo, in_git):
        """Depth-first copy using a descriptor for each directory on the path."""
        stack = [(fd, parts, repo, in_git, None)]
        try:
            while stack:
                fd, parts, repo, in_git, names = stack[-1]
                if names is None:
                    if len(parts) > MAX_DEPTH:
                        raise Failed("Checkout tree is too deep to checkpoint")
                    listing = sorted(os.listdir(fd), key=os.fsencode)
                    if not in_git and ".git" in listing:
                        repo = "/".join(parts)
                    names = iter(listing)
                    stack[-1] = (fd, parts, repo, in_git, names)
                name = next(names, None)
                if name is None:
                    os.close(fd)
                    stack.pop()
                    continue
                child = self.record(fd, parts, name, repo, in_git)
                if child is not None:
                    stack.append(child + (None,))
        finally:
            for item in stack:
                os.close(item[0])

    def record(self, fd, parts, name, repo, in_git):
        child_parts = parts + [name]
        path = "/".join(child_parts)
        if len(self.keys) + len(self.omitted) >= MAX_ENTRIES:
            raise Failed("Checkout tree has too many entries to checkpoint")
        try:
            info = os.lstat(name, dir_fd=fd)
        except FileNotFoundError as error:
            raise Changed() from error
        kind = stat.S_IFMT(info.st_mode)
        key = stat_key(info)
        mode = stat.S_IMODE(info.st_mode)
        if info.st_ctime_ns + RACY_NS > self.started_ns:
            self.racy[path] = info.st_ctime_ns
        if kind == stat.S_IFDIR:
            self.keys[path] = key
            self.entries[path] = dict(type="dir", mode=mode, mtimeNs=info.st_mtime_ns)
            self.stats["directories"] += 1
            if not in_git and repo is not None and name in REBUILDABLE:
                self.candidates.setdefault(repo, []).append(path)
                return None
            if info.st_dev != self.device:
                raise Failed("Checkout tree contains another filesystem")
            try:
                child_fd = os.open(name, DIRECTORY, dir_fd=fd)
            except FileNotFoundError as error:
                raise Changed() from error
            if os.fstat(child_fd).st_ino != info.st_ino:
                os.close(child_fd)
                raise Changed()
            return (child_fd, child_parts, repo, in_git or name == ".git")
        if kind == stat.S_IFLNK:
            target = os.readlink(name, dir_fd=fd)
            self.keys[path] = key
            self.entries[path] = dict(type="symlink", target=target)
            self.stats["symlinks"] += 1
            if logical_external(self.logical_root, "/".join(parts), target):
                self.flag(path, "symlink")
            return None
        if kind in (stat.S_IFIFO, stat.S_IFSOCK):
            self.keys[path] = key
            if len(self.skipped) < MAX_REPORTED:
                self.skipped.append(dict(path=path, type="fifo" if kind == stat.S_IFIFO else "socket"))
            return None
        if kind != stat.S_IFREG:
            raise Failed("Checkout tree contains a device node")
        reference = reference_kind(child_parts, in_git)
        sha, captured = self.copy_file(fd, name, path, info, reference is not None)
        self.keys[path] = key
        self.entries[path] = dict(type="file", mode=mode, mtimeNs=info.st_mtime_ns, size=info.st_size,
                                  sha256=sha, ino=info.st_ino, ctimeNs=info.st_ctime_ns)
        self.stats["files"] += 1
        self.stats["bytes"] += info.st_size
        if reference is not None:
            # Alternates are relative to the objects directory, other references to their own.
            directory = "/".join(parts[:-1] if reference == "alternates" else parts)
            if any(logical_external(self.logical_root, directory, target)
                   for target in reference_targets(reference, captured)):
                self.flag(path, reference)
        return None

    def flag(self, path, kind):
        if len(self.external) < MAX_REPORTED:
            self.external.append(dict(path=path, kind=kind))

    def copy_file(self, fd, name, path, info, capture):
        previous = self.previous.get(path)
        if not capture and previous and previous.get("type") == "file" and \
                (previous.get("ino"), previous.get("size"), previous.get("mtimeNs"),
                 previous.get("ctimeNs")) == (info.st_ino, info.st_size, info.st_mtime_ns, info.st_ctime_ns) \
                and self.store.has_object(previous["sha256"], info.st_size):
            return previous["sha256"], b""
        try:
            source = os.open(name, READ_FILE | getattr(os, "O_NOATIME", 0), dir_fd=fd)
        except PermissionError:
            source = os.open(name, READ_FILE, dir_fd=fd)
        except FileNotFoundError as error:
            raise Changed() from error
        try:
            opened = os.fstat(source)
            if not stat.S_ISREG(opened.st_mode) or opened.st_ino != info.st_ino:
                raise Changed()
            sha, size, captured = self.store.put_object(source, capture)
            self.stats["bytesHashed"] += size
            if size != info.st_size:
                raise Changed()
            return sha, captured
        finally:
            os.close(source)

    def settle(self):
        """Wait until racy entries' ctimes are older than the filesystem timestamp tick."""
        if not self.racy:
            return
        deadline = max(self.racy.values()) + TICK_MARGIN_NS
        wait = deadline - self.clock()
        if wait > MAX_SETTLE_NS:
            raise Changed()
        if wait > 0:
            time.sleep(wait / 1e9)

    def recheck(self):
        """The second pass: every recorded entry and listing must be unchanged."""
        self.settle()
        if stat_key(os.fstat(self.root_fd)) != self.keys[""]:
            raise Changed()
        seen = {""}
        stack = [(os.dup(self.root_fd), [])]
        try:
            while stack:
                fd, parts = stack.pop()
                try:
                    listing = os.listdir(fd)
                    for name in listing:
                        path = "/".join(parts + [name])
                        seen.add(path)
                        try:
                            info = os.lstat(name, dir_fd=fd)
                        except FileNotFoundError as error:
                            raise Changed() from error
                        if path in self.omitted:
                            if (stat.S_IFMT(info.st_mode), info.st_ino) != self.omitted[path][:2]:
                                raise Changed()
                            continue
                        if self.keys.get(path) != stat_key(info):
                            raise Changed()
                        entry = self.entries.get(path)
                        if entry is None:
                            continue
                        if entry["type"] == "symlink" and os.readlink(name, dir_fd=fd) != entry["target"]:
                            raise Changed()
                        if entry["type"] == "file" and path in self.racy:
                            self.rehash(fd, name, entry)
                        if entry["type"] == "dir":
                            child = os.open(name, DIRECTORY, dir_fd=fd)
                            if os.fstat(child).st_ino != info.st_ino:
                                os.close(child)
                                raise Changed()
                            stack.append((child, parts + [name]))
                finally:
                    os.close(fd)
        finally:
            for fd, _ in stack:
                os.close(fd)
        if seen != set(self.keys) | set(self.omitted):
            raise Changed()

    def rehash(self, fd, name, entry):
        try:
            source = os.open(name, READ_FILE, dir_fd=fd)
        except FileNotFoundError as error:
            raise Changed() from error
        try:
            digest = hashlib.sha256()
            for chunk in iter(lambda: os.read(source, CHUNK), b""):
                digest.update(chunk)
        finally:
            os.close(source)
        if digest.hexdigest() != entry["sha256"]:
            raise Changed()


class Store:
    """The runtime-owned checkpoint directory on the retained volume."""
    def __init__(self, path=STORE, clock=time.time_ns):
        self.path = path
        self.clock = clock
        self.lock_fd = None
        self.written = set()
        self.touched = set()
        self.free_before = self.free_min = None

    def prepare(self, create):
        if create:
            os.makedirs(self.path, mode=0o700, exist_ok=True)
        try:
            info = os.lstat(self.path)
        except FileNotFoundError:
            return False
        owner = 0 if os.geteuid() == 0 else os.geteuid()
        if not stat.S_ISDIR(info.st_mode) or info.st_uid != owner or stat.S_IMODE(info.st_mode) & 0o077:
            raise Failed("Unsafe checkpoint store")
        for name in STORE_DIRS:
            directory = os.path.join(self.path, name)
            if create:
                os.makedirs(directory, mode=0o700, exist_ok=True)
            if os.path.lexists(directory):
                child = os.lstat(directory)
                if not stat.S_ISDIR(child.st_mode) or child.st_uid != owner or \
                        stat.S_IMODE(child.st_mode) & 0o077:
                    raise Failed("Unsafe checkpoint store")
        return True

    def lock(self, wait_seconds):
        fd = os.open(os.path.join(self.path, "lock"), os.O_RDWR | os.O_CREAT | os.O_NOFOLLOW | os.O_CLOEXEC,
                     0o600)
        deadline = time.monotonic() + wait_seconds
        while True:
            try:
                fcntl.flock(fd, fcntl.LOCK_EX | fcntl.LOCK_NB)
                self.lock_fd = fd
                return
            except BlockingIOError:
                if time.monotonic() >= deadline:
                    os.close(fd)
                    raise Deferred("Another checkpoint operation is running")
                time.sleep(0.2)

    def unlock(self):
        if self.lock_fd is not None:
            os.close(self.lock_fd)
            self.lock_fd = None

    def object_path(self, sha):
        return os.path.join(self.path, "objects", sha[:2], sha)

    def has_object(self, sha, size):
        try:
            info = os.lstat(self.object_path(sha))
        except FileNotFoundError:
            return False
        return stat.S_ISREG(info.st_mode) and info.st_size == size

    def measure(self):
        info = os.statvfs(self.path)
        free = info.f_bavail * info.f_frsize
        self.free_min = free if self.free_min is None else min(self.free_min, free)
        return free

    def put_object(self, source, capture):
        temporary = os.path.join(self.path, "tmp", secrets.token_hex(16))
        target = os.open(temporary, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW | os.O_CLOEXEC, 0o600)
        digest = hashlib.sha256()
        size = 0
        captured = bytearray()
        try:
            try:
                for chunk in iter(lambda: os.read(source, CHUNK), b""):
                    digest.update(chunk)
                    size += len(chunk)
                    if capture and len(captured) < MAX_REFERENCE_BYTES:
                        captured += chunk[:MAX_REFERENCE_BYTES - len(captured)]
                    view = memoryview(chunk)
                    while view:
                        view = view[os.write(target, view):]
                sha = digest.hexdigest()
                if self.has_object(sha, size):
                    return sha, size, bytes(captured)
                os.fchmod(target, 0o400)
                os.fsync(target)
            finally:
                os.close(target)
            directory = os.path.dirname(self.object_path(sha))
            os.makedirs(directory, mode=0o700, exist_ok=True)
            os.rename(temporary, self.object_path(sha))
            temporary = None
            self.touched.add(directory)
            self.written.add(sha)
            if len(self.written) % 64 == 1:
                self.measure()
            return sha, size, bytes(captured)
        finally:
            if temporary is not None:
                try:
                    os.unlink(temporary)
                except FileNotFoundError:
                    pass

    def clear(self, name):
        directory = os.path.join(self.path, name)
        for entry in os.listdir(directory):
            path = os.path.join(directory, entry)
            if os.path.isdir(path) and not os.path.islink(path):
                shutil.rmtree(path)
            else:
                os.unlink(path)

    def read_json(self, path, limit=1 << 30):
        fd = os.open(path, os.O_RDONLY | os.O_NOFOLLOW | os.O_CLOEXEC)
        with os.fdopen(fd, "rb") as stream:
            data = stream.read(limit + 1)
        if len(data) > limit:
            raise Failed("Checkpoint metadata is too large")
        return data

    def current(self):
        path = os.path.join(self.path, "current.json")
        if not os.path.lexists(path):
            return None
        try:
            value = json.loads(self.read_json(path, 1 << 20))
        except (OSError, ValueError) as error:
            raise Failed("Checkpoint pointer is corrupt") from error
        history = value.get("history") if isinstance(value, dict) else None
        if value.get("schemaVersion") != SCHEMA_VERSION or not isinstance(history, list) or not history or \
                not all(isinstance(item, dict) and CHECKPOINT_ID.fullmatch(str(item.get("checkpointId"))) and
                        SHA256.fullmatch(str(item.get("manifestSha256"))) for item in history):
            raise Failed("Checkpoint pointer is corrupt")
        return value

    def manifest(self, checkpoint_id, manifest_sha):
        try:
            data = self.read_json(os.path.join(self.path, "manifests", checkpoint_id + ".json"))
        except FileNotFoundError as error:
            raise Failed("Checkpoint manifest is missing") from error
        if hashlib.sha256(data).hexdigest() != manifest_sha:
            raise Failed("Checkpoint manifest is corrupt")
        try:
            value = json.loads(data)
        except ValueError as error:
            raise Failed("Checkpoint manifest is corrupt") from error
        if not isinstance(value, dict) or value.get("schemaVersion") != SCHEMA_VERSION or \
                value.get("checkpointId") != checkpoint_id or not isinstance(value.get("entries"), dict):
            raise Failed("Checkpoint manifest is corrupt")
        validate_binding(value.get("binding"))
        return value

    def select(self, checkpoint_id=None):
        current = self.current()
        if current is None:
            raise Failed("No completed checkpoint")
        for item in current["history"][:KEEP]:
            if checkpoint_id is None or item["checkpointId"] == checkpoint_id:
                return self.manifest(item["checkpointId"], item["manifestSha256"]), item["manifestSha256"]
        raise Failed("Expected checkpoint is not retained")

    def check_objects(self, manifest, deep):
        for path, entry in manifest["entries"].items():
            if entry.get("type") != "file":
                continue
            sha = entry.get("sha256")
            if not isinstance(sha, str) or not SHA256.fullmatch(sha) or \
                    not self.has_object(sha, entry.get("size")):
                raise Failed("Checkpoint data is missing or corrupt")
            if deep or sha in self.written:
                digest = hashlib.sha256()
                with open(self.object_path(sha), "rb") as stream:
                    for chunk in iter(lambda: stream.read(CHUNK), b""):
                        digest.update(chunk)
                if digest.hexdigest() != sha:
                    raise Failed("Checkpoint data is missing or corrupt")

    def publish(self, manifest):
        data = canonical(manifest)
        manifest_sha = hashlib.sha256(data).hexdigest()
        checkpoint_id = manifest["checkpointId"]
        staged = os.path.join(self.path, "staging", checkpoint_id + ".json")
        write_durable(staged, data, 0o400)
        for directory in sorted(self.touched):
            fsync_path(directory)
        fsync_path(os.path.join(self.path, "objects"))
        self.check_objects(self.manifest_from(staged, manifest_sha), False)
        store_fd = os.open(self.path, DIRECTORY)
        try:
            syncfs(store_fd)
        finally:
            os.close(store_fd)
        os.rename(staged, os.path.join(self.path, "manifests", checkpoint_id + ".json"))
        fsync_path(os.path.join(self.path, "manifests"))
        fsync_path(os.path.join(self.path, "staging"))
        try:
            previous = self.current()
        except Failed:
            previous = None
        history = [dict(checkpointId=checkpoint_id, manifestSha256=manifest_sha)] + \
            [item for item in (previous or {}).get("history", []) if item["checkpointId"] != checkpoint_id]
        pointer = dict(schemaVersion=SCHEMA_VERSION, checkpointId=checkpoint_id, manifestSha256=manifest_sha,
                       binding=manifest["binding"], completedAt=manifest["completedAt"],
                       history=history[:KEEP])
        write_durable(os.path.join(self.path, "current.json"), canonical(pointer))
        return manifest_sha, pointer

    def manifest_from(self, path, manifest_sha):
        data = self.read_json(path)
        if hashlib.sha256(data).hexdigest() != manifest_sha:
            raise Failed("Checkpoint manifest is corrupt")
        return json.loads(data)

    def collect(self, pointer):
        """Remove unretained manifests and unreferenced objects after a publish."""
        keep = {item["checkpointId"] for item in pointer["history"]}
        referenced = set()
        for item in pointer["history"]:
            manifest = self.manifest(item["checkpointId"], item["manifestSha256"])
            referenced.update(entry["sha256"] for entry in manifest["entries"].values()
                              if entry.get("type") == "file")
        directory = os.path.join(self.path, "manifests")
        for name in os.listdir(directory):
            if name.removesuffix(".json") not in keep:
                os.unlink(os.path.join(directory, name))
        objects = os.path.join(self.path, "objects")
        for prefix in os.listdir(objects):
            folder = os.path.join(objects, prefix)
            for name in os.listdir(folder):
                if name not in referenced:
                    os.unlink(os.path.join(folder, name))

    def status_path(self):
        return os.path.join(self.path, "status.json")

    def record_attempt(self, attempt, success=None):
        try:
            status = json.loads(self.read_json(self.status_path(), 1 << 20))
            if not isinstance(status, dict):
                status = {}
        except (OSError, ValueError, Failed):
            status = {}
        status["lastAttempt"] = attempt
        if success is not None:
            status["lastSuccess"] = success
        try:
            write_durable(self.status_path(), canonical(status))
        except OSError:
            pass


def new_checkpoint_id(clock):
    return "c-%020d-%s" % (clock(), secrets.token_hex(8))


def previous_entries(store, current):
    """File stat cache from the current checkpoint, valid only on the same boot and filesystem."""
    if current is None:
        return {}
    try:
        manifest = store.manifest(current["checkpointId"], current["manifestSha256"])
    except Failed:
        return {}
    source = manifest.get("source") or {}
    if source.get("bootId") is None or source.get("bootId") != boot_id():
        return {}
    started = manifest.get("startedAtNs", 0)
    return {path: entry for path, entry in manifest["entries"].items()
            if entry.get("type") == "file" and entry.get("ctimeNs", 0) + RACY_NS <= started}


def save(source, store_path, binding, fenced=False, operation_id=None, logical_root=LOGICAL_ROOT,
         git=None, clock=time.time_ns):
    """Copy the tree into the store and publish it. Returns the published summary."""
    binding = validate_binding(binding)
    if operation_id is not None and not OPERATION.fullmatch(operation_id):
        raise Failed("Invalid operation ID")
    mode = "fenced" if fenced else "periodic"
    store = Store(store_path, clock)
    store.prepare(create=True)
    store.lock(LOCK_SECONDS if fenced else 0)
    started = time.monotonic()
    attempt = dict(at=clock(), mode=mode, operationId=operation_id)
    try:
        store.clear("tmp")
        store.clear("staging")
        store.free_before = store.measure()
        try:
            current = store.current()
        except Failed:
            current = None
        if current is not None and not same_volume(current["binding"], binding):
            raise Failed("Checkpoint store belongs to another workspace volume")
        try:
            root_fd = os.open(source, DIRECTORY)
        except OSError as error:
            raise Failed("Checkpoint source is unavailable") from error
        try:
            snapshot = Snapshot(root_fd, store, logical_root, git or Git(), previous_entries(store, current), clock)
            try:
                try:
                    snapshot.copy()
                    snapshot.recheck()
                except OSError as error:
                    if error.errno in (errno.ENOENT, errno.ENOTDIR, errno.ELOOP):
                        raise Changed() from error
                    raise
            except Changed as error:
                if fenced:
                    raise Failed("Checkout changed while writers were fenced") from error
                raise Deferred("Checkout changed during the checkpoint") from error
            root = os.fstat(root_fd)
        finally:
            os.close(root_fd)
        checkpoint_id = new_checkpoint_id(clock)
        manifest = dict(
            schemaVersion=SCHEMA_VERSION, type="RepositoryCheckpoint", checkpointId=checkpoint_id,
            binding=binding, mode=mode, operationId=operation_id, logicalRoot=logical_root,
            startedAtNs=snapshot.started_ns, completedAt=clock(),
            source=dict(bootId=boot_id(), device=root.st_dev),
            root=dict(mode=stat.S_IMODE(root.st_mode), mtimeNs=root.st_mtime_ns),
            entries={path: snapshot.entries[path] for path in sorted(snapshot.entries, key=sort_key)},
            omitted=sorted(snapshot.omitted, key=sort_key)[:MAX_REPORTED], omittedCount=len(snapshot.omitted),
            skipped=snapshot.skipped, external=snapshot.external)
        manifest_sha, pointer = store.publish(manifest)
        try:
            store.collect(pointer)
        except (OSError, Failed):
            pass
        store.measure()
        result = dict(
            checkpointId=checkpoint_id, manifestSha256=manifest_sha, mode=mode, operationId=operation_id,
            binding=binding, completedAt=manifest["completedAt"],
            durationMs=int((time.monotonic() - started) * 1000),
            files=snapshot.stats["files"], directories=snapshot.stats["directories"],
            symlinks=snapshot.stats["symlinks"], bytes=snapshot.stats["bytes"],
            bytesHashed=snapshot.stats["bytesHashed"],
            bytesWritten=sum(os.lstat(store.object_path(sha)).st_size for sha in store.written),
            objectsWritten=len(store.written), omittedCount=len(snapshot.omitted),
            skippedCount=len(snapshot.skipped), externalReferences=snapshot.external,
            peakStoreGrowthBytes=max(0, store.free_before - store.free_min))
        store.record_attempt(dict(attempt, outcome="succeeded", checkpointId=checkpoint_id), result)
        return result
    except Deferred as error:
        store.record_attempt(dict(attempt, outcome="deferred", reason=error.reason))
        raise
    except Failed as error:
        store.record_attempt(dict(attempt, outcome="failed", reason=error.reason))
        raise
    except OSError as error:
        reason = "Checkpoint destination is full" if error.errno in (errno.ENOSPC, errno.EDQUOT) \
            else "Checkpoint save failed"
        store.record_attempt(dict(attempt, outcome="failed", reason=reason))
        raise Failed(reason) from error
    finally:
        try:
            store.clear("tmp")
            store.clear("staging")
        except OSError:
            pass
        store.unlock()


def verify(store_path, binding=None, checkpoint_id=None, operation_id=None, deep=False):
    """Check that a retained checkpoint is complete and, when given, bound as expected."""
    store = Store(store_path)
    if not store.prepare(create=False):
        raise Failed("No completed checkpoint")
    store.lock(LOCK_SECONDS)
    try:
        manifest, manifest_sha = store.select(checkpoint_id)
        if binding is not None and manifest["binding"] != validate_binding(binding):
            raise Failed("Checkpoint belongs to another workspace, volume or writer generation")
        if operation_id is not None and manifest.get("operationId") != operation_id:
            raise Failed("Checkpoint was not taken by this operation")
        store.check_objects(manifest, deep)
        return dict(checkpointId=manifest["checkpointId"], manifestSha256=manifest_sha,
                    binding=manifest["binding"], mode=manifest["mode"], operationId=manifest.get("operationId"),
                    completedAt=manifest["completedAt"], files=sum(
                        1 for entry in manifest["entries"].values() if entry.get("type") == "file"))
    finally:
        store.unlock()


def restore(store_path, target_parent, name, binding, checkpoint_id=None, uid=CODING_UID, gid=CODING_GID):
    """Restore a checkpoint as target_parent/name through a staging directory. A retry after an
    interruption discards the unfinished staging copy. An existing target is never replaced."""
    binding = validate_binding(binding)
    if not name or "/" in name or name in (".", "..") or name.startswith(".restore"):
        raise Failed("Invalid restore target")
    store = Store(store_path)
    if not store.prepare(create=False):
        raise Failed("No completed checkpoint")
    store.lock(LOCK_SECONDS)
    started = time.monotonic()
    chown = os.geteuid() == 0
    staging = ".restore-staging"
    try:
        manifest, manifest_sha = store.select(checkpoint_id)
        if not same_volume(manifest["binding"], binding):
            raise Failed("Checkpoint belongs to another workspace volume")
        parent = os.open(target_parent, DIRECTORY)
        try:
            if chown:
                info = os.fstat(parent)
                if info.st_uid != 0 or stat.S_IMODE(info.st_mode) & 0o022:
                    raise Failed("Unsafe restore parent")
            try:
                os.lstat(name, dir_fd=parent)
                raise Failed("Restore target already exists")
            except FileNotFoundError:
                pass
            try:
                os.lstat(staging, dir_fd=parent)
                shutil.rmtree(staging, dir_fd=parent)
            except FileNotFoundError:
                pass
            os.mkdir(staging, 0o700, dir_fd=parent)
            root_fd = os.open(staging, DIRECTORY, dir_fd=parent)
            try:
                written = restore_entries(store, manifest, root_fd, uid, gid, chown)
                finish_dir(root_fd, manifest["root"], uid, gid, chown)
                syncfs(root_fd)
            finally:
                os.close(root_fd)
            try:
                os.lstat(name, dir_fd=parent)
                raise Failed("Restore target already exists")
            except FileNotFoundError:
                pass
            os.rename(staging, name, src_dir_fd=parent, dst_dir_fd=parent)
            os.fsync(parent)
        finally:
            os.close(parent)
        return dict(checkpointId=manifest["checkpointId"], manifestSha256=manifest_sha,
                    binding=manifest["binding"], files=written["files"], bytes=written["bytes"],
                    durationMs=int((time.monotonic() - started) * 1000))
    except OSError as error:
        reason = "Restore destination is full" if error.errno in (errno.ENOSPC, errno.EDQUOT) \
            else "Checkpoint restore failed"
        raise Failed(reason) from error
    finally:
        store.unlock()


def safe_mode(mode):
    return mode & 0o1777


def finish_dir(fd, entry, uid, gid, chown):
    if chown:
        os.fchown(fd, uid, gid)
    os.fchmod(fd, safe_mode(entry["mode"]))
    os.utime(fd, ns=(entry["mtimeNs"], entry["mtimeNs"]))


def restore_entries(store, manifest, root_fd, uid, gid, chown):
    counts = dict(files=0, bytes=0)
    stack = [((), root_fd, None)]
    try:
        for path in sorted(manifest["entries"], key=sort_key):
            entry = manifest["entries"][path]
            parts = sort_key(path)
            if not parts or any(part in (b"", b".", b"..") for part in parts):
                raise Failed("Checkpoint manifest is corrupt")
            while stack[-1][0] != parts[:-1]:
                if len(stack) == 1:
                    raise Failed("Checkpoint manifest is corrupt")
                _, fd, pending = stack.pop()
                finish_dir(fd, pending, uid, gid, chown)
                os.close(fd)
            parent = stack[-1][1]
            name = os.fsdecode(parts[-1])
            kind = entry.get("type")
            if kind == "dir":
                os.mkdir(name, 0o700, dir_fd=parent)
                stack.append((parts, os.open(name, DIRECTORY, dir_fd=parent), entry))
            elif kind == "symlink":
                os.symlink(entry["target"], name, dir_fd=parent)
                if chown:
                    os.chown(name, uid, gid, dir_fd=parent, follow_symlinks=False)
            elif kind == "file":
                counts["bytes"] += restore_file(store, entry, parent, name, uid, gid, chown)
                counts["files"] += 1
            else:
                raise Failed("Checkpoint manifest is corrupt")
        while len(stack) > 1:
            _, fd, pending = stack.pop()
            finish_dir(fd, pending, uid, gid, chown)
            os.close(fd)
    finally:
        for _, fd, _ in stack[1:]:
            os.close(fd)
    return counts


def restore_file(store, entry, parent, name, uid, gid, chown):
    sha = entry.get("sha256")
    if not isinstance(sha, str) or not SHA256.fullmatch(sha):
        raise Failed("Checkpoint manifest is corrupt")
    try:
        source = os.open(store.object_path(sha), os.O_RDONLY | os.O_NOFOLLOW | os.O_CLOEXEC)
    except FileNotFoundError as error:
        raise Failed("Checkpoint data is missing or corrupt") from error
    try:
        target = os.open(name, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW | os.O_CLOEXEC, 0o600,
                         dir_fd=parent)
        try:
            digest = hashlib.sha256()
            size = 0
            for chunk in iter(lambda: os.read(source, CHUNK), b""):
                digest.update(chunk)
                size += len(chunk)
                view = memoryview(chunk)
                while view:
                    view = view[os.write(target, view):]
            if digest.hexdigest() != sha or size != entry.get("size"):
                raise Failed("Checkpoint data is missing or corrupt")
            if chown:
                os.fchown(target, uid, gid)
            os.fchmod(target, safe_mode(entry["mode"]))
            os.utime(target, ns=(entry["mtimeNs"], entry["mtimeNs"]))
        finally:
            os.close(target)
    finally:
        os.close(source)
    return size


def status(store_path):
    """Pointer and last-attempt state. "empty" means no checkpoint was ever published."""
    store = Store(store_path)
    if not store.prepare(create=False):
        return dict(state="empty")
    result = dict(state="empty")
    try:
        current = store.current()
        if current is not None:
            result = dict(state="ready", checkpointId=current["checkpointId"],
                          completedAt=current["completedAt"], binding=current["binding"])
    except Failed as error:
        result = dict(state="corrupt", reason=error.reason)
    if result["state"] == "empty" and os.listdir(os.path.join(store.path, "manifests")):
        result = dict(state="corrupt", reason="Checkpoint pointer is missing")
    try:
        recorded = json.loads(store.read_json(store.status_path(), 1 << 20))
        if isinstance(recorded, dict):
            result.update(lastAttempt=recorded.get("lastAttempt"), lastSuccess=recorded.get("lastSuccess"))
    except (OSError, ValueError, Failed):
        pass
    return result


def load_binding(path):
    try:
        with open(path, encoding="utf-8") as stream:
            return json.load(stream)
    except (OSError, ValueError) as error:
        raise Failed("Invalid checkpoint binding") from error


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    commands = parser.add_subparsers(dest="command", required=True)
    for name in ("save", "verify", "restore", "status"):
        command = commands.add_parser(name)
        command.add_argument("--store", default=STORE)
        if name != "status":
            command.add_argument("--binding", required=True, help="JSON file with the checkpoint binding")
        if name == "save":
            command.add_argument("--source", default=SOURCE)
            command.add_argument("--logical-root", default=LOGICAL_ROOT)
            command.add_argument("--fenced", action="store_true",
                                 help="writers are fenced; a change fails instead of deferring")
            command.add_argument("--operation-id")
        if name in ("verify", "restore"):
            command.add_argument("--checkpoint-id")
        if name == "verify":
            command.add_argument("--operation-id")
            command.add_argument("--deep", action="store_true")
        if name == "restore":
            command.add_argument("--target", required=True, help="absent directory to create")
    arguments = parser.parse_args(argv)
    try:
        if arguments.command == "status":
            result = status(arguments.store)
        else:
            binding = load_binding(arguments.binding)
            if arguments.command == "save":
                result = save(arguments.source, arguments.store, binding, arguments.fenced,
                              arguments.operation_id, arguments.logical_root)
            elif arguments.command == "verify":
                result = verify(arguments.store, binding, arguments.checkpoint_id, arguments.operation_id,
                                arguments.deep)
            else:
                target = os.path.normpath(os.path.abspath(arguments.target))
                result = restore(arguments.store, os.path.dirname(target), os.path.basename(target),
                                 binding, arguments.checkpoint_id)
        print(json.dumps(dict(ok=True, result=result), sort_keys=True))
        return 0
    except Deferred as error:
        print(json.dumps(dict(ok=False, state="Deferred", reason=error.reason), sort_keys=True))
        return EX_DEFERRED
    except Failed as error:
        print(json.dumps(dict(ok=False, state="Failed", reason=error.reason), sort_keys=True))
        return 1


if __name__ == "__main__":
    sys.exit(main())
