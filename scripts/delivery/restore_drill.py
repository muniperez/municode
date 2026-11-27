#!/usr/bin/env python3
# SPDX-License-Identifier: MPL-2.0
"""Exercise the actual host backup and restore on a disposable rootful Docker host."""

import argparse
import json
import os
from pathlib import Path
import shutil
import sys
import tempfile


ROOT = Path(__file__).resolve().parents[2]
BUNDLE = ROOT / "packaging/bundle"
sys.path.insert(0, str(BUNDLE))
import deploy_host as deploy
from install_host import assignments, create_secrets, write_json


def drill(root, run):
    host = deploy.Host(root)
    release = host.releases / "municode-v0.0.0-drill"
    installation = "synthetic-restore-drill"
    release.parent.mkdir(parents=True)
    (host.state / "backups").mkdir(parents=True)
    (host.state / "deploys").mkdir()
    host.config.mkdir(parents=True)
    create_secrets(host.secrets)
    shutil.copytree(BUNDLE, release, ignore=shutil.ignore_patterns("__pycache__"))
    (release / "dashboard").mkdir()
    policy = host.config / "municode.json"
    policy.write_bytes((release / "municode.json").read_bytes())
    old_policy = policy.read_bytes()
    (host.config / "operator.env").write_text("MUNICODE_INSTALLATION_ID=synthetic-restore-drill\n")
    deploy.write_environment(release, assignments(release / ".env.example") | {
        "MUNICODE_INSTALLATION_ID": installation,
        "MUNICODE_POLICY_FILE": str(policy),
        "MUNICODE_SECRETS_DIR": str(host.secrets),
    })
    previous = {"releaseId": release.name, "activatedAt": deploy.now()}
    write_json(host.state / "active-release.json", previous)

    def compose(step, *args, **options):
        return deploy.compose_at(run, release, step, *args, **options)

    def sql(database, query):
        return deploy.sql(run, release, "run drill SQL", database, query)

    def initialize_bao(timeout):
        try:
            deploy.bao_exec(run, release, "check drill OpenBao", "status", *deploy.BAO_OPTIONS,
                            allowed=(0, 2), timeout=timeout)
            return True
        except ValueError:
            return False

    try:
        compose("start drill PostgreSQL", "up", "-d", "--no-deps", "--wait", "--wait-timeout", "180", "postgres",
                timeout=300)
        compose("initialize drill databases", "run", "--rm", "--no-deps", "-T", "database-initialize")
        compose("start drill OpenBao", "up", "-d", "--no-deps", "openbao", timeout=300)
        compose("create real Caddy volume", "create", "--no-deps", "caddy", timeout=300)
        deploy.await_probe("wait for drill OpenBao", initialize_bao, 120)
        initialized = json.loads(deploy.bao_exec(run, release, "initialize drill OpenBao", "operator", "init",
                                                 *deploy.BAO_OPTIONS, "-key-shares=1", "-key-threshold=1",
                                                 "-format=json"))
        key = initialized["unseal_keys_b64"][0].encode()
        deploy.start_openbao(run, release, key)

        def bao(*args):
            return compose("run drill OpenBao command", "exec", "-T", "-e", "BAO_TOKEN=" + initialized["root_token"],
                           "openbao", "bao", *args[:2], *deploy.BAO_OPTIONS, *args[2:])

        bao("secrets", "enable", "-path=drill", "kv-v2")
        bao("kv", "put", "-mount=drill", "fixture", "value=before")
        for database in deploy.DATABASES:
            sql(database, f"SET ROLE {database}; CREATE TABLE drill (value text); INSERT INTO drill VALUES ('before')")
        caddy_data = deploy.volume_path(run, installation, "caddy-data") / "drill"
        caddy_data.write_text("before")
        extra_secret = host.secrets / "synthetic-drill"
        extra_secret.write_text("before")
        deploy.create_backup(host, "restore-drill", previous, installation, release, run)

        bao("kv", "put", "-mount=drill", "fixture", "value=after")
        for database in deploy.DATABASES:
            sql(database, "UPDATE drill SET value = 'after'; CREATE TABLE not_in_snapshot (value text)")
        policy.write_text('{"after":true}\n')
        extra_secret.unlink()
        caddy_data.write_text("after")
        compose("stop drill OpenBao", "stop", "openbao")
        deploy.restore_backup(host, "restore-drill", previous, installation, release, run)
        deploy.start_openbao(run, release, key)

        for database in deploy.DATABASES:
            assert sql(database, f"SET ROLE {database}; SELECT value FROM drill").splitlines()[-1] == "before"
            assert sql(database, "SELECT to_regclass('public.not_in_snapshot') IS NULL") == "t"
        assert bao("kv", "get", "-mount=drill", "-field=value", "fixture") == "before"
        assert policy.read_bytes() == old_policy
        assert extra_secret.read_text() == "before"
        assert caddy_data.read_text() == "before"
        print("Matched restore round-tripped both PostgreSQL databases, the OpenBao secret, policy, secrets and Caddy volume.")
    finally:
        compose("remove disposable drill stack", "down", "--volumes", "--remove-orphans", timeout=240)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--disposable-docker", action="store_true", required=True,
                        help="Confirm Docker is isolated and the municode project is disposable.")
    parser.parse_args()
    if os.geteuid() != 0:
        parser.error("Run as root to exercise the agent's real local volume access.")
    run = deploy.Runner()
    for kind, args in (("containers", ("ps", "-aq")), ("volumes", ("volume", "ls", "-q")),
                       ("networks", ("network", "ls", "-q"))):
        if run(f"check existing {kind}", "docker", *args, "--filter", "label=com.docker.compose.project=municode"):
            parser.error(f"Existing Municode {kind} found; refusing to run the drill.")
    artifacts = ROOT / ".local/artifacts"
    artifacts.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="restore-drill-", dir=artifacts) as temporary:
        try:
            drill(Path(temporary), run)
        except deploy.DEPLOY_ERRORS as error:
            parser.exit(1, deploy.error_message(error) + "\n")


if __name__ == "__main__":
    main()
