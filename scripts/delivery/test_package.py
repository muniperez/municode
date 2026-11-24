# SPDX-License-Identifier: MPL-2.0
import http.client
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import tarfile
import tempfile
import time
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

    def test_passkeys_bind_to_the_configured_dashboard_origin(self):
        bundle = ROOT / "packaging" / "bundle"
        self.assertIn(
            "    passkey:\n"
            "      enabled: true\n"
            "      config:\n"
            "        rp:\n"
            "          display_name: Municode\n",
            (bundle / "kratos.yml").read_text(),
        )
        for schema in (bundle / "identity.schema.json",
                       ROOT / "docs" / "setup" / "identity.schema.json"):
            with self.subTest(schema=schema.name):
                email = json.loads(schema.read_text())["properties"]["traits"]["properties"]["email"]
                self.assertEqual(
                    email["ory.sh/kratos"]["credentials"]["passkey"], {"display_name": True}
                )

        if shutil.which("docker") is None or subprocess.run(
                ["docker", "compose", "version"], capture_output=True).returncode != 0:
            self.skipTest("docker compose is unavailable")
        secrets = self.root / "secrets"
        for name in ("postgres-admin-password", "app-db-password", "kratos-db-password",
                     "workspace-gateway-credential", "ssh/worker-key", "openbao-server.crt",
                     "openbao-server.key", "openbao-ca.crt", "openbao-approle/role-id",
                     "openbao-approle/secret-id", "kratos.env"):
            (secrets / name).parent.mkdir(parents=True, exist_ok=True)
            (secrets / name).write_text("synthetic\n")
        (secrets / "bootstrap").mkdir()

        def render(**overrides):
            environment = {
                key: value for key, value in os.environ.items()
                if not key.startswith(("CONSOLE_", "WORKSPACES_"))
            }
            environment.update(WORKSPACES_SECRETS_DIR=str(secrets),
                               CONSOLE_HOSTNAME="dashboard.customer.test",
                               CONSOLE_URL="https://dashboard.customer.test", **overrides)
            rendered = subprocess.run(
                ["docker", "compose", "-f", str(bundle / "compose.yml"),
                 "--env-file", str(bundle / ".env.example"), "config", "--format", "json"],
                env=environment, check=True, capture_output=True, text=True,
            ).stdout
            services = json.loads(rendered)["services"]
            return services["kratos"]["environment"], services["api"]["environment"]

        kratos, api = render()
        self.assertEqual(kratos["SELFSERVICE_METHODS_PASSKEY_ENABLED"], "true")
        self.assertEqual(kratos["SELFSERVICE_METHODS_PASSKEY_CONFIG_RP_ID"], "dashboard.customer.test")
        self.assertEqual(kratos["SELFSERVICE_METHODS_PASSKEY_CONFIG_RP_ORIGINS"],
                         "https://dashboard.customer.test")
        self.assertEqual(api["Kratos__DashboardOrigin"], "https://dashboard.customer.test")
        self.assertEqual(api["Kratos__PasskeyEnabled"], "true")

        kratos, api = render(WORKSPACES_PASSKEYS_ENABLED="false")
        self.assertEqual(kratos["SELFSERVICE_METHODS_PASSKEY_ENABLED"], "false")
        self.assertEqual(api["Kratos__PasskeyEnabled"], "false")

    def test_archive_checksum_is_present(self):
        subprocess.run(self.command(), check=True, capture_output=True, text=True)
        lines = (self.output / "checksums.txt").read_text().splitlines()
        self.assertEqual(len(lines), 2)
        self.assertEqual({line.split("  ", 1)[1] for line in lines},
                         {"release-manifest.json", next(self.output.glob("*.tar.gz")).name})

    @unittest.skipUnless(os.environ.get("WORKSPACES_TEST_CADDY") == "1",
                         "Set WORKSPACES_TEST_CADDY=1 to run the isolated Caddy runtime test")
    def test_packaged_dashboard_revalidates_across_two_releases(self):
        image = json.loads((ROOT / "packaging" / "release.json").read_text())["images"]["caddy"]
        served = self.root / "served"
        served.mkdir()
        shells = []
        for number in (1, 2):
            dashboard = self.payloads["dashboard"]
            assets = dashboard / "assets"
            assets.mkdir(exist_ok=True)
            for asset in assets.iterdir():
                asset.unlink()
            shell = (
                '<!doctype html><html><script type="module" '
                f'src="/assets/index-release{number}.js"></script></html>\n'
            ).encode()
            shells.append(shell)
            (dashboard / "index.html").write_bytes(shell)
            (assets / f"index-release{number}.js").write_text(f"export default {number};\n")
            (assets / f"index-release{number}.css").write_text(f"body {{ opacity: .{number}; }}\n")
            command = self.command()
            command[command.index("--version") + 1] = f"0.0.0-test{number}"
            output = self.root / f"output{number}"
            command[command.index("--output") + 1] = str(output)
            subprocess.run(command, check=True, capture_output=True, text=True)
            with tarfile.open(output / f"municode-workspaces-0.0.0-test{number}-linux-x64.tar.gz") as bundle:
                bundle.extractall(self.root / f"release{number}", filter="data")
            release = self.root / f"release{number}" / f"municode-workspaces-0.0.0-test{number}"
            subprocess.run([sys.executable, "-B", str(release / "verify_bundle.py")],
                           check=True, capture_output=True, text=True)
            self.assertEqual((release / "dashboard" / "index.html").read_bytes(), shell)
        first = self.root / "release1" / "municode-workspaces-0.0.0-test1"
        second = self.root / "release2" / "municode-workspaces-0.0.0-test2"
        shutil.copytree(first / "dashboard", served, dirs_exist_ok=True)
        container = subprocess.run(
            ["docker", "run", "--detach", "--pull=never",
             "--read-only", "--tmpfs", "/data", "--tmpfs", "/config",
             "--publish", "127.0.0.1::80",
             "--mount", f"type=bind,src={first / 'Caddyfile'},dst=/etc/caddy/Caddyfile,readonly",
             "--mount", f"type=bind,src={served},dst=/srv/workspaces-dashboard,readonly",
             "--env", "CONSOLE_HOSTNAME=http://localhost",
             "--env", "API_HOSTNAME=http://api.localhost",
             "--env", "API_URL=https://api.localhost",
             "--env", "WORKSPACES_SOURCE_URL=https://github.com/muniperez/municode",
             "--env", "WORKSPACES_GATEWAY_DOMAIN=workspaces.localhost", image],
            check=True, capture_output=True, text=True,
        ).stdout.strip()
        self.addCleanup(subprocess.run, ["docker", "rm", "--force", "--volumes", container],
                        check=True, capture_output=True, text=True)
        address = subprocess.run(["docker", "port", container, "80/tcp"],
                                 check=True, capture_output=True, text=True).stdout.strip()
        client = http.client.HTTPConnection("127.0.0.1", int(address.rsplit(":", 1)[1]), timeout=5)
        self.addCleanup(client.close)

        def get(path, headers=None):
            client.request("GET", path, headers={"Host": "localhost",
                                                "Cookie": "ory_kratos_session=existing-client",
                                                **(headers or {})})
            response = client.getresponse()
            return response.status, response.headers, response.read()

        for attempt in range(50):
            try:
                status, _, _ = get("/")
                self.assertEqual(status, 200)
                break
            except (OSError, http.client.HTTPException):
                client.close()
                if attempt == 49:
                    logs = subprocess.run(["docker", "logs", container],
                                          capture_output=True, text=True)
                    self.fail(f"Caddy did not become ready:\n{logs.stdout}\n{logs.stderr}")
                time.sleep(0.1)

        paths = ("/", "/index.html", "/workspaces/existing-client",
                 "/settings/github/callback?code=private", "/runtime-config.json/not-config")
        status, headers, body = get("/assets/index-release1.js")
        self.assertEqual(status, 200)
        self.assertEqual(headers.get_content_type(), "application/javascript")
        self.assertEqual(body, b"export default 1;\n")
        for path in paths:
            status, headers, body = get(path)
            self.assertEqual(status, 200, path)
            self.assertEqual(headers.get_content_type(), "text/html", path)
            self.assertEqual(body, shells[0], path)
            self.assertEqual(headers["Cache-Control"],
                             "no-store" if path.startswith("/settings/github/") else "no-cache", path)
            self.assertIsNone(headers["ETag"], path)
            self.assertIsNone(headers["Last-Modified"], path)
            if path.startswith("/settings/github/"):
                self.assertEqual(headers["Referrer-Policy"], "no-referrer")

        # Both packaged shells have identical sizes and epoch mtimes: old validators must not yield 304.
        self.assertEqual(len(shells[0]), len(shells[1]))
        self.assertEqual((first / "dashboard/index.html").stat().st_mtime, 0)
        self.assertEqual((second / "dashboard/index.html").stat().st_mtime, 0)
        shutil.rmtree(served / "assets")
        shutil.copytree(second / "dashboard", served, dirs_exist_ok=True)
        for path in paths:
            status, headers, body = get(path, {"If-None-Match": "*",
                                             "If-Modified-Since": "Wed, 31 Dec 2099 23:59:59 GMT"})
            self.assertEqual(status, 200, path)
            self.assertEqual(headers.get_content_type(), "text/html", path)
            self.assertEqual(body, shells[1], path)
            self.assertEqual(headers["Cache-Control"],
                             "no-store" if path.startswith("/settings/github/") else "no-cache", path)
            if path.startswith("/settings/github/"):
                self.assertEqual(headers["Referrer-Policy"], "no-referrer")
        for extension, content_type in (("js", "application/javascript"), ("css", "text/css")):
            status, headers, body = get(f"/assets/index-release2.{extension}")
            self.assertEqual(status, 200)
            self.assertEqual(headers.get_content_type(), content_type)
            self.assertEqual(body, (second / "dashboard/assets" / f"index-release2.{extension}").read_bytes())
            self.assertIsNotNone(headers["ETag"])
            self.assertEqual(get(f"/assets/index-release2.{extension}",
                                 {"If-None-Match": headers["ETag"]})[0], 304)
        for path in ("/assets/index-release1.js", "/assets/missing.css", "/assets/nested/missing.js"):
            status, headers, body = get(path)
            self.assertEqual(status, 404, path)
            self.assertNotEqual(headers.get_content_type(), "text/html", path)
            self.assertNotEqual(body, shells[1], path)
        status, headers, body = get("/runtime-config.json")
        self.assertEqual(status, 200)
        self.assertEqual(headers.get_content_type(), "application/json")
        self.assertEqual(headers["Cache-Control"], "no-store")
        self.assertEqual(json.loads(body), {
            "apiBaseUrl": "https://api.localhost",
            "sourceUrl": "https://github.com/muniperez/municode",
            "auth": {"origin": "https://api.localhost",
                     "capabilitiesUrl": "https://api.localhost/api/account/capabilities"},
        })

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
