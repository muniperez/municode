#!/usr/bin/env python3
"""Install ONLY inside an explicitly marked disposable Ubuntu image builder VM."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import stat
import subprocess
import sys
import tarfile
import urllib.request
import zipfile

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE.parent))
from runtime import Blocked, atomic_json, require, run
from copilot_provider import probe as probe_copilot


def cloud_image_serial(path=Path("/etc/cloud/build.info")):
    try:
        text = path.read_text()
    except FileNotFoundError:
        # Provider stock images (for example Vultr's) omit it; boot the verified pinned image instead.
        raise Blocked("Builder lacks /etc/cloud/build.info; it was not booted from the pinned "
                      "Ubuntu cloud image") from None
    build = {}
    for line in text.splitlines():
        match = re.fullmatch(r"([a-zA-Z0-9_]+)\s*[:=]\s*([^#]+?)\s*", line)
        if match:
            build[match[1]] = match[2].strip("'\"")
    return build.get("serial")


def builder_guard(acknowledgement):
    require(acknowledgement == "DESTROY-DISPOSABLE-IMAGE-BUILDER",
            "Explicit disposable-image-builder acknowledgement required")
    marker = Path("/etc/workspaces-disposable-image-builder")
    require(marker.is_file() and marker.stat().st_uid == 0 and
            marker.read_text().strip() == acknowledgement, "Disposable builder marker missing")
    require(os.geteuid() == 0 and os.uname().machine == "x86_64", "Root linux/amd64 builder required")
    require('VERSION_ID="24.04"' in Path("/etc/os-release").read_text(), "Ubuntu 24.04 required")
    manifest = json.loads((HERE / "manifest.lock.json").read_text())
    require(cloud_image_serial() == manifest["os"]["cloudImageSerial"],
            "Builder cloud-image serial does not match the pinned Ubuntu image")
    require(not Path("/etc/workspaces-workspace/runtime.json").exists(), "Already enrolled runtime")
    require(not os.path.ismount("/workspace") and
            not list(Path("/dev/disk/by-id").glob("*Volume*")), "Builder has workspace volume")
    require(run(["systemd-detect-virt", "--vm"]) not in ("", "none"), "A disposable VM is required")


def verify_size(item, target):
    require("size" not in item or target.stat().st_size == item["size"], "Pinned artifact size mismatch")


def download(item, target):
    with urllib.request.urlopen(item["url"], timeout=120) as source, target.open("wb") as output:
        shutil.copyfileobj(source, output)
    algorithm = "sha512" if "sha512" in item else "sha256"
    checksum = hashlib.new(algorithm)
    with target.open("rb") as stream:
        for data in iter(lambda: stream.read(1024 * 1024), b""):
            checksum.update(data)
    require(checksum.hexdigest() == item[algorithm], "Pinned artifact checksum mismatch")


RUNTIME_FILES = ("runtime.py", "bootstrap.py", "issue-t3-session.py", "git-credential.py",
                 "t3_connect.py", "t3-connect.html", "t3-connect.js", "t3-connect.css",
                 "copilot_provider.py", "disk_usage.py", "t3_project_defaults.py",
                 "storage_tiers.py", "scratch_mounts.py", "repo_checkpoint.py")
SYSTEMD_FILES = ("workspaces-workspace.service", "workspaces-workspace-mount.service",
                 "workspaces-t3.service", "workspaceswork.slice",
                 "workspaces-t3-connect.service", "workspaces-t3-connect-activate.service",
                 "workspaces-scratch-mounts.service", "workspaces-scratch-watch.service")
PINNED_EXECUTABLE_FILES = (
    "/opt/workspaces-node/bin/node", "/opt/workspaces-dotnet/dotnet",
    "/opt/workspaces-tools/node_modules/@openai/codex/bin/codex.js",
    "/opt/workspaces-copilot/copilot", "/opt/workspaces-muse/muse",
    "/opt/workspaces-antigravity/agy_acp_server.par",
    "/opt/workspaces-antigravity/localharness_external",
    "/usr/local/bin/cloudflared", "/usr/local/bin/gh", "/usr/local/bin/glab",
    "/usr/local/bin/copilot", "/usr/local/bin/claude", "/usr/local/bin/muse",
)


def install_systemd_units(source_directory, destination_directory):
    for source in source_directory.iterdir():
        destination = destination_directory / source.name
        shutil.copy2(source, destination)
        os.chown(destination, 0, 0)
        destination.chmod(0o644)


STORAGE_TIER_FILES = (("scratch.env", "{runtime}/scratch.env"),
                      ("tmpfiles.conf", "/etc/tmpfiles.d/workspaces-scratch.conf"),
                      ("profile.sh", "/etc/profile.d/workspaces-scratch.sh"))


def install_storage_tiers(source_directory, runtime, root=Path("/")):
    """Installs the shared scratch environment, the boot-time scratch layout and the login profile."""
    for name, target in STORAGE_TIER_FILES:
        destination = root / target.format(runtime=runtime).lstrip("/")
        destination.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(source_directory / name, destination)
        os.chown(destination, 0, 0)
        destination.chmod(0o644)


def write_image_manifest(lock, source_directory=HERE.parent, root=Path("/")):
    """Record actual immutable inputs, checking source-managed copies before publication."""
    runtime = "/opt/workspaces-workspace"
    copies = [(source_directory / name, runtime + "/" + name) for name in RUNTIME_FILES]
    copies += [(source_directory / "storage" / name, target.format(runtime=runtime))
               for name, target in STORAGE_TIER_FILES]
    copies += [(source_directory / "systemd" / name, "/etc/systemd/system/" + name)
               for name in SYSTEMD_FILES]
    copies += [(source_directory / "image/tools" / name, "/opt/workspaces-tools/" + name)
               for name in ("package.json", "package-lock.json")]
    files = {}

    def record(path, source=None):
        info = path.lstat()
        require(path.is_absolute() and path.resolve() == path and stat.S_ISREG(info.st_mode)
                and info.st_uid == 0 and not info.st_mode & 0o022,
                "Unsafe installed immutable input: " + str(path))
        with path.open("rb") as stream:
            digest = hashlib.file_digest(stream, "sha256").hexdigest()
        if source is not None:
            with source.open("rb") as stream:
                require(digest == hashlib.file_digest(stream, "sha256").hexdigest(),
                        "Installed immutable input differs from source: " + str(path))
        files[str(path)] = digest

    for source, target in copies:
        record(root / target.lstrip("/"), source)
    for target in (*PINNED_EXECUTABLE_FILES,
                   "/opt/workspaces-tools/" + lock["claude"]["executable"],
                   "/opt/workspaces-t3/" + lock["t3"]["resourceMonitor"]["path"]):
        record(root / target.lstrip("/"))
    # The release's T3 directory is discovered during installation; record the real file,
    # not the /usr/local/bin symlink which the native guard deliberately rejects.
    t3 = (root / "usr/local/bin/t3").resolve(strict=True)
    require(t3.is_relative_to(root / "opt/workspaces-t3"), "Unexpected installed T3 executable")
    record(t3)
    lock["provenance"]["installedImmutableInputs"] = {"files": files}
    atomic_json(root / runtime.lstrip("/") / "image-manifest.json", lock)


def install_copilot(lock, cache):
    artifact = cache / "copilot.tar.gz"
    download(lock["copilot"], artifact)
    destination = Path("/opt/workspaces-copilot")
    destination.mkdir(parents=True, exist_ok=True)
    destination.chmod(0o755)
    run(["tar", "--no-same-owner", "-xf", str(artifact), "-C", str(destination)])
    require((destination / "copilot").is_file(), "Unexpected Copilot release archive layout")
    (destination / "copilot").chmod(0o755)
    wrapper = Path("/usr/local/bin/copilot")
    wrapper.write_text("#!/bin/sh\nexport COPILOT_AUTO_UPDATE=false\n"
                       'exec /opt/workspaces-copilot/copilot "$@"\n')
    wrapper.chmod(0o755)
    return probe_copilot(expected_version=lock["copilot"]["version"],
                         environment=dict(PATH="/usr/local/bin:/usr/bin:/bin", HOME=str(cache)))


def write_wrapper(path, environment, executable):
    path.write_text("#!/bin/sh\nexport " + environment + "\nexec " + executable + ' "$@"\n')
    path.chmod(0o755)


def install_claude(lock, tools, wrapper=Path("/usr/local/bin/claude")):
    item = lock["claude"]
    binary = tools / item["executable"]
    require(binary.is_file() and not binary.is_symlink(), "Unexpected Claude Code package layout")
    # npm verified the lockfile integrity; the wrapper keeps the pinned build from self-updating.
    write_wrapper(wrapper, "DISABLE_AUTOUPDATER=1", str(binary))
    output = run([str(wrapper), "--version"],
                 env={"PATH": "/usr/local/bin:/usr/bin:/bin", "HOME": str(tools), "LC_ALL": "C"})
    require(re.match(re.escape(item["version"]) + r"(?:\s|$)", output), "Wrong Claude Code version")


def install_antigravity(lock, cache, destination=Path("/opt/workspaces-antigravity")):
    item = lock["antigravity"]
    artifact = cache / "antigravity.zip"
    download(item, artifact)
    verify_size(item, artifact)
    destination.mkdir(mode=0o755)
    # Copy only the pinned regular members by exact name; never extract archive paths or links.
    with zipfile.ZipFile(artifact) as archive:
        entries = {entry.filename: entry for entry in archive.infolist()}
        for name, size in item["members"].items():
            entry = entries.get(name)
            require(entry is not None and not entry.is_dir() and entry.file_size == size and
                    (entry.external_attr >> 16) & 0o170000 in (0, 0o100000),
                    "Unexpected Antigravity release archive layout")
            target = destination / name
            with archive.open(entry) as source, target.open("xb") as output:
                shutil.copyfileobj(source, output, 1024 * 1024)
            require(target.stat().st_size == size, "Antigravity member size mismatch")
            os.chown(target, 0, 0)
            target.chmod(0o755)
    artifact.unlink()


def install_muse(lock, cache, destination=Path("/opt/workspaces-muse"), wrapper=Path("/usr/local/bin/muse")):
    item = lock["muse"]
    destination.mkdir(mode=0o755)
    binary = destination / "muse"
    download(item, binary)
    verify_size(item, binary)
    os.chown(binary, 0, 0)
    binary.chmod(0o755)
    write_wrapper(wrapper, "MUSE_NO_AUTO_UPDATE=1", str(binary))


def install_source_control_clis(lock, cache):
    for name in ("gh", "glab"):
        item = lock[name]
        artifact = cache / (name + ".tar.gz")
        download(item, artifact)
        destination = Path("/usr/local/bin") / name
        # Copy only the pinned executable; never extract archive paths or follow links.
        with tarfile.open(artifact, "r:gz") as archive:
            members = [member for member in archive.getmembers() if member.name == item["executable"]]
            require(len(members) == 1 and members[0].isfile() and
                    0 < members[0].size <= 256 * 1024**2, "Unexpected source-control CLI archive layout")
            require(not destination.exists() and not destination.is_symlink(),
                    "Source-control CLI destination already exists")
            with archive.extractfile(members[0]) as source, destination.open("xb") as output:
                shutil.copyfileobj(source, output)
        os.chown(destination, 0, 0)
        destination.chmod(0o755)
        output = run([str(destination), "--version"],
                     env={"PATH": "/usr/local/bin:/usr/bin:/bin", "HOME": str(cache), "LC_ALL": "C"})
        require(re.search(r"(?:^|\s)v?" + re.escape(item["version"]) + r"(?:$|[\s,])", output),
                "Wrong source-control CLI version")


def install(source_revision, builder):
    require(re.fullmatch(r"[a-f0-9]{40}", source_revision), "Exact source commit required")
    require(builder, "Builder provenance identity required")
    lock = json.loads((HERE / "manifest.lock.json").read_text())
    cache = Path.cwd() / ".local/artifacts/runtime/image-build"
    cache.mkdir(mode=0o700, parents=True, exist_ok=True)
    snapshot = lock["os"]["aptSnapshot"]
    sources = Path("/etc/apt/sources.list.d")
    for path in sources.iterdir():
        if path.suffix in (".sources", ".list"):
            path.unlink()
    Path("/etc/apt/sources.list").write_text(
        "deb [check-valid-until=no] https://snapshot.ubuntu.com/ubuntu/" + snapshot +
        "/ noble main universe\n"
        "deb [check-valid-until=no] https://snapshot.ubuntu.com/ubuntu/" + snapshot +
        "/ noble-updates main universe\n"
        "deb [check-valid-until=no] https://snapshot.ubuntu.com/ubuntu/" + snapshot +
        "/ noble-security main universe\n")
    env = dict(os.environ, DEBIAN_FRONTEND="noninteractive")
    run(["apt-get", "-qq", "update"], env=env, timeout=1800)
    packages = ["python3", "git", "curl", "ca-certificates", "openssh-server", "openssh-client",
                "age", "util-linux", "e2fsprogs",
                "libicu74", "libssl3t64", "xz-utils", "build-essential", "pkg-config",
                "unzip", "zip", "jq", "ripgrep", "sqlite3", *lock["browserLibraries"]]
    run(["apt-get", "-y", "-qq", "install", "--no-install-recommends", *packages], env=env, timeout=1800)
    for name in ("node", "dotnet", "t3"):
        artifact = cache / (name + ".tar")
        download(lock[name], artifact)
        destination = Path("/opt") / ("workspaces-" + name)
        destination.mkdir(parents=True, exist_ok=True)
        argv = ["tar", "--no-same-owner", "-xf", str(artifact), "-C", str(destination)]
        if name == "node":
            argv += ["--strip-components=1"]
        run(argv)
    for executable in ("node", "npm", "npx"):
        Path("/usr/local/bin", executable).symlink_to("/opt/workspaces-node/bin/" + executable)
    Path("/usr/local/bin/dotnet").symlink_to("/opt/workspaces-dotnet/dotnet")
    t3 = list(Path("/opt/workspaces-t3").rglob("t3"))
    require(len(t3) == 1 and t3[0].is_file(), "Unexpected T3 release archive layout")
    Path("/usr/local/bin/t3").symlink_to(t3[0])
    relay = Path("/usr/local/bin/cloudflared")
    download(lock["t3"]["relayClient"], relay)
    relay.chmod(0o755)
    install_source_control_clis(lock, cache)
    monitor = Path("/opt/workspaces-t3") / lock["t3"]["resourceMonitor"]["path"]
    with monitor.open("rb") as binary:
        require(hashlib.file_digest(binary, "sha256").hexdigest() ==
                lock["t3"]["resourceMonitor"]["sha256"], "Pinned T3 telemetry helper checksum mismatch")
    tools = Path("/opt/workspaces-tools")
    tools.mkdir(exist_ok=True)
    for name in ("package.json", "package-lock.json"):
        shutil.copy2(HERE / "tools" / name, tools / name)
        os.chown(tools / name, 0, 0)
        (tools / name).chmod(0o644)
    run(["npm", "ci", "--prefix", str(tools), "--ignore-scripts", "--omit=dev"],
        env=dict(os.environ, HOME=str(cache), npm_config_cache=str(cache / "npm")), timeout=600)
    Path("/usr/local/bin/codex").symlink_to(tools / "node_modules/.bin/codex")
    install_claude(lock, tools)
    install_antigravity(lock, cache)
    install_muse(lock, cache)
    # ACP initialize needs no credentials and never creates a coding thread.
    copilot = install_copilot(lock, cache)
    run(["groupadd", "--gid", "2000", "coding"])
    run(["useradd", "--uid", "2000", "--gid", "2000", "--home-dir", "/workspace/home",
         "--no-create-home", "--shell", "/usr/sbin/nologin", "coding"])
    runtime = Path("/opt/workspaces-workspace")
    runtime.mkdir(exist_ok=True)
    for name in RUNTIME_FILES:
        shutil.copy2(HERE.parent / name, runtime / name)
        os.chown(runtime / name, 0, 0)
        (runtime / name).chmod(0o755)
    install_storage_tiers(HERE.parent / "storage", runtime)
    install_systemd_units(HERE.parent / "systemd", Path("/etc/systemd/system"))
    Path("/var/cache/workspaces-workspace/verification").mkdir(parents=True, exist_ok=True, mode=0o711)
    Path("/var/cache/workspaces-workspace").chmod(0o711)
    Path("/etc/workspaces-workspace").mkdir(mode=0o700, exist_ok=True)
    run(["systemctl", "mask", "apt-daily.service", "apt-daily-upgrade.service", "apt-daily.timer",
         "apt-daily-upgrade.timer", "unattended-upgrades.service"])
    Path("/etc/ssh/sshd_config.d/00-workspaces-workspace.conf").write_text(
        "PasswordAuthentication no\nKbdInteractiveAuthentication no\n"
        "PermitRootLogin prohibit-password\nAllowUsers root\n"
        "AllowAgentForwarding no\nPermitTunnel no\nX11Forwarding no\n"
        "GatewayPorts no\nAllowTcpForwarding local\n")
    run(["systemctl", "enable", "workspaces-workspace.service"])
    require(run(["node", "--version"]) == "v" + lock["node"]["version"], "Wrong Node version")
    require(run(["dotnet", "--version"]) == lock["dotnet"]["version"], "Wrong .NET version")
    require(lock["codex"]["version"] in run(["codex", "--version"]), "Wrong Codex version")
    require(lock["t3"]["version"] in run(["t3", "--version"]), "Wrong T3 version")
    # Do not assume auth CLI options merely because an upstream source revision supports them.
    help_text = run(["t3", "auth", "session", "issue", "--help"])
    require(all(option in help_text for option in
                ("--token-only", "--ttl", "--subject", "--scope", "--base-dir")),
            "Installed T3 lacks required headless session issuance")
    connect_help = run(["t3", "connect", "link", "--help"])
    require(all(option in connect_help for option in ("--headless", "--base-dir")),
            "Installed T3 lacks supported non-service-installing Connect enrollment")
    lock["provenance"].update(sourceRevision=source_revision, builderIdentity=builder,
                              installedPackages=run(["dpkg-query", "-W", "-f=${Package}=${Version}\n"]),
                              toolArtifactsVerified=True, headlessAuthOptionsChecked=True)
    lock["provenance"]["copilotStdioAcpVersionChecked"] = copilot["version"]
    lock["provenance"]["agentArtifactsVerified"] = [agent["id"] for agent in lock["agents"]]
    write_image_manifest(lock)
    shutil.rmtree(cache)


def generalize():
    # Guard is applied by main before either install or this destructive operation.
    os.chdir("/")
    for unit in ("workspaces-scratch-watch.service", "workspaces-scratch-mounts.service",
                 "workspaces-workspace-mount.service",
                 "workspaces-workspace.service", "workspaces-t3.service",
                 "workspaces-t3-connect.service", "workspaces-t3-connect-activate.service"):
        subprocess.run(["systemctl", "stop", unit], capture_output=True, timeout=120, check=False)
    require(not os.path.ismount("/workspace"), "Never erase an attached workspace")
    run(["systemctl", "disable", "workspaces-t3.service"])
    # Build-output bind mounts must go first: erasing through one would empty scratch, not the image.
    if Path("/opt/workspaces-workspace/scratch_mounts.py").is_file():
        subprocess.run(["/usr/bin/python3", "-I", "-B", "/opt/workspaces-workspace/scratch_mounts.py",
                        "unmount-all"], capture_output=True, timeout=180, check=False)
    mounts = Path("/proc/self/mountinfo").read_text().splitlines()
    require(not any(line.split()[4].startswith("/workspace/") for line in mounts),
            "Never erase through a mount below the workspace")
    # The helper self-binds scratch/repos (nosuid,nodev); ismount() misses a same-filesystem bind.
    scratch_repos = "/var/lib/workspaces-scratch/repos"
    for _ in range(4):
        if not any(line.split()[4] == scratch_repos
                   for line in Path("/proc/self/mountinfo").read_text().splitlines()):
            break
        run(["umount", scratch_repos])
    else:
        require(False, "Scratch repos is still mounted")
    for path in ("/workspace", "/root/.codex", "/root/.t3", "/root/.config", "/root/.cache",
                 "/root/.npm", "/root/.ssh", "/home/ubuntu/.ssh", "/var/lib/workspaces-workspace",
                 "/var/lib/workspaces-scratch",
                 "/var/cache/workspaces-workspace/verification",
                 "/etc/workspaces-workspace", "/var/log/journal"):
        target = Path(path)
        if target.is_dir() and not target.is_symlink():
            shutil.rmtree(target)
        else:
            target.unlink(missing_ok=True)
    for home in Path("/home").iterdir():
        if home.is_dir():
            shutil.rmtree(home)
    for path in Path("/root").iterdir():
        if path.is_dir() and not path.is_symlink():
            shutil.rmtree(path)
        else:
            path.unlink()
    staging = Path("/opt/workspaces-image-source")
    if staging.exists():
        shutil.rmtree(staging)
    for path in Path("/etc/ssh").glob("ssh_host_*"):
        path.unlink()
    for name in ("/root/.bash_history", "/root/.git-credentials", "/root/.gitconfig",
                 "/etc/ssh/ssh_host_ed25519_key", "/var/lib/dbus/machine-id",
                 "/var/lib/systemd/random-seed", "/var/lib/systemd/credential.secret"):
        Path(name).unlink(missing_ok=True)
    run(["cloud-init", "clean", "--logs", "--seed", "--machine-id"])
    Path("/etc/machine-id").write_text("")
    for path in Path("/var/log").rglob("*"):
        if path.is_file() and not path.is_symlink():
            with path.open("wb"):
                pass
    require(not list(Path("/etc/ssh").glob("ssh_host_*")), "SSH host keys remain")
    Path("/etc/workspaces-disposable-image-builder").unlink()
    os.sync()
    print(json.dumps({"type": "ImageGeneralized", "captureOnlyAfterPowerOff": True,
                      "providerVolumeEncryptionClaim": False}))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("action", choices=("install", "generalize"))
    parser.add_argument("--acknowledgement", required=True)
    parser.add_argument("--source-revision")
    parser.add_argument("--builder-identity")
    args = parser.parse_args()
    builder_guard(args.acknowledgement)
    if args.action == "install":
        install(args.source_revision or "", args.builder_identity)
    else:
        generalize()


if __name__ == "__main__":
    main()
