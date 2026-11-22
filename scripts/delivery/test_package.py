# SPDX-License-Identifier: MPL-2.0
import json
from pathlib import Path
import re
import subprocess
import sys
import tarfile
import tempfile
import unittest


ROOT = Path(__file__).resolve().parents[2]
PACKAGE = ROOT / "scripts" / "delivery" / "package.py"


class PackageTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.root = Path(self.temporary.name)
        self.payloads = {}
        for name, binary in (
            ("api", "Workspaces.Api.dll"),
            ("worker", "Workspaces.Worker.dll"),
            ("gateway", "Workspaces.Gateway.dll"),
            ("dashboard", "index.html"),
            ("cli", "municode"),
        ):
            payload = self.root / name
            payload.mkdir()
            target = payload / binary
            target.write_text(f"synthetic {name}\n")
            if name == "cli":
                target.unlink()
                platform = payload / "linux-x64"
                platform.mkdir()
                target = platform / "municode"
                target.write_text(f"synthetic {name}\n")
                target.chmod(0o755)
            self.payloads[name] = payload
        self.output = self.root / "output"

    def tearDown(self):
        self.temporary.cleanup()

    def command(self):
        return [
            sys.executable,
            "-B",
            str(PACKAGE),
            "--version",
            "0.0.0-test",
            "--source",
            "a" * 40,
            "--rid",
            "linux-x64",
            "--api",
            str(self.payloads["api"]),
            "--worker",
            str(self.payloads["worker"]),
            "--gateway",
            str(self.payloads["gateway"]),
            "--dashboard",
            str(self.payloads["dashboard"]),
            "--cli",
            str(self.payloads["cli"]),
            "--output",
            str(self.output),
        ]

    def test_package_contains_all_product_payloads_and_installer(self):
        subprocess.run(self.command(), check=True, capture_output=True, text=True)
        archive = next(self.output.glob("*.tar.gz"))
        with tarfile.open(archive, "r:gz") as bundle:
            names = {member.name for member in bundle.getmembers()}
            self.assertTrue(any(name.endswith("/runtime/api/Workspaces.Api.dll") for name in names))
            self.assertTrue(any(name.endswith("/runtime/worker/Workspaces.Worker.dll") for name in names))
            self.assertTrue(any(name.endswith("/runtime/gateway/Workspaces.Gateway.dll") for name in names))
            self.assertTrue(any(name.endswith("/dashboard/index.html") for name in names))
            self.assertTrue(any(name.endswith("/cli/linux-x64/municode") for name in names))
            self.assertTrue(any(name.endswith("/cli/linux-x64/workspaces") for name in names))
            self.assertTrue(any(name.endswith("/install.sh") for name in names))
            seed_installer = next(member for member in bundle.getmembers()
                                  if member.name.endswith("/seed-start.sh"))
            self.assertTrue(seed_installer.mode & 0o100)
            self.assertTrue(all(not member.issym() and not member.islnk() for member in bundle.getmembers()))

    def test_manifest_records_component_digests_and_no_floating_dependencies(self):
        subprocess.run(self.command(), check=True, capture_output=True, text=True)
        manifest = json.loads((self.output / "release-manifest.json").read_text())
        self.assertEqual(
            set(manifest["components"]), {"api", "worker", "gateway", "dashboard", "cli"}
        )
        for component in manifest["components"].values():
            self.assertRegex(component["sha256"], r"^[0-9a-f]{64}$")
            self.assertTrue(component["files"])
        release = json.loads((ROOT / "packaging" / "release.json").read_text())
        for value in release["images"].values():
            self.assertRegex(value, r"@sha256:[0-9a-f]{64}$")
        self.assertNotIn("0" * 64, (ROOT / "packaging" / "release.json").read_text())
        env = (ROOT / "packaging" / "bundle" / ".env.example").read_text()
        for value in release["images"].values():
            self.assertIn(value, env)
        self.assertNotIn("latest", env)

    def test_bundle_declares_only_independent_workspaces_services(self):
        compose = (ROOT / "packaging" / "bundle" / "compose.yml").read_text().lower()
        caddy = (ROOT / "packaging" / "bundle" / "Caddyfile").read_text().lower()
        self.assertNotIn("minicloud", compose)
        self.assertNotIn("minicloud", caddy)
        for service in ("api:", "worker:", "workspace-gateway:", "caddy:", "postgres:",
                        "openbao:", "kratos:", "dashboard"):
            self.assertIn(service, compose + caddy)

    def test_packaged_caddy_pins_gateway_routes_to_dedicated_proxy_network(self):
        subprocess.run(self.command(), check=True, capture_output=True, text=True)
        archive = next(self.output.glob("*.tar.gz"))
        with tarfile.open(archive, "r:gz") as bundle:
            prefix = "municode-workspaces-0.0.0-test/"
            caddy = bundle.extractfile(prefix + "Caddyfile").read().decode()
            compose = bundle.extractfile(prefix + "compose.yml").read().decode()

        self.assertEqual(
            re.findall(r"(?m)^\s*ask\s+(\S+)\s*$", caddy),
            ["http://172.31.20.3:8080/internal/certificate"],
        )
        workspace_route = caddy.split("https:// {", 1)[1]
        self.assertEqual(
            re.findall(r"(?m)^\s*reverse_proxy\s+(\S+)", workspace_route),
            ["172.31.20.3:8080"],
        )
        self.assertNotIn("workspace-gateway:8080", caddy)
        self.assertIn("on_demand_tls {", caddy)
        self.assertIn("\ttls {\n\t\ton_demand\n\t}", workspace_route)

        gateway = compose.split("\n  workspace-gateway:\n", 1)[1].split("\n  caddy:\n", 1)[0]
        proxy = compose.split("\n  caddy:\n", 1)[1].split("\nsecrets:\n", 1)[0]
        for service, address in ((gateway, "172.31.20.3"), (proxy, "172.31.20.2")):
            with self.subTest(address=address):
                self.assertIn(
                    "    networks:\n"
                    "      private: {}\n"
                    "      workspace-gateway-proxy:\n"
                    f"        ipv4_address: {address}\n",
                    service,
                )
        self.assertIn(
            "      DevelopmentWorkspaceGateway__TrustedProxyAddress: 172.31.20.2\n",
            gateway,
        )
        networks = compose.split("\nnetworks:\n", 1)[1]
        self.assertIn(
            "  workspace-gateway-proxy:\n"
            "    labels: *installation-labels\n"
            "    driver: bridge\n"
            "    ipam:\n"
            "      config:\n"
            "        - subnet: 172.31.20.0/24\n",
            networks,
        )

    def test_archive_checksum_is_present(self):
        subprocess.run(self.command(), check=True, capture_output=True, text=True)
        lines = (self.output / "checksums.txt").read_text().splitlines()
        self.assertEqual(len(lines), 2)
        self.assertEqual({line.split("  ", 1)[1] for line in lines},
                         {"release-manifest.json", next(self.output.glob("*.tar.gz")).name})

    def test_missing_dashboard_output_fails_without_partial_archive(self):
        (self.payloads["dashboard"] / "index.html").unlink()
        result = subprocess.run(self.command(), capture_output=True, text=True)
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("Dashboard payload lacks index.html", result.stderr)
        self.assertEqual(list(self.output.glob("*.tar.gz")), [])

    def test_unsafe_package_version_fails_before_creating_output(self):
        command = self.command()
        command[command.index("--version") + 1] = "../../release"
        result = subprocess.run(command, capture_output=True, text=True)
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("exact semantic version", result.stderr)
        self.assertFalse(self.output.exists())

    def test_installer_verifies_and_stages_a_fresh_release(self):
        subprocess.run(self.command(), check=True, capture_output=True, text=True)
        archive = next(self.output.glob("*.tar.gz"))
        extracted = self.root / "extracted"
        extracted.mkdir()
        with tarfile.open(archive, "r:gz") as bundle:
            bundle.extractall(extracted, filter="data")
        release_root = next(extracted.iterdir())
        install_root = self.root / "installed"
        secrets = self.root / "secrets"
        subprocess.run(
            [
                str(release_root / "install.sh"),
                "--install-root",
                str(install_root),
                "--secrets-dir",
                str(secrets),
            ],
            check=True,
            capture_output=True,
            text=True,
        )
        self.assertTrue((install_root / "runtime" / "api" / "Workspaces.Api.dll").is_file())
        self.assertTrue((secrets / "workspace-gateway-credential").is_file())
        self.assertTrue((secrets / "ssh" / "worker-key").is_file())
        self.assertTrue((secrets / "openbao-approle" / "role-id").is_file())
        self.assertEqual(secrets.stat().st_mode & 0o777, 0o700)
        self.assertEqual((install_root / ".env").stat().st_mode & 0o777, 0o600)
        env = (install_root / ".env").read_text()
        self.assertNotIn("synthetic-contract", env)
        self.assertEqual(env.count("WORKSPACES_SECRETS_DIR="), 1)

        kratos_env = secrets / "kratos.env"
        cipher_values = [
            line.partition(b"=")[2]
            for line in kratos_env.read_bytes().splitlines()
            if line.startswith(b"SECRETS_CIPHER=")
        ]
        self.assertEqual(len(cipher_values), 1)
        self.assertEqual(len(cipher_values[0]), 32)
        self.assertTrue(all(byte in b"ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_"
                            for byte in cipher_values[0]))
        self.assertEqual(kratos_env.stat().st_mode & 0o7777, 0o600)


if __name__ == "__main__":
    unittest.main()
