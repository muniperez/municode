# SPDX-License-Identifier: MPL-2.0
from copy import deepcopy
from email.message import Message
import json
import unittest
from unittest.mock import MagicMock, patch

from probe_dashboard import probe


CONSOLE = "http://localhost:18080"
API = "https://api.localhost"
SOURCE = "https://github.com/muniperez/municode"


class DashboardProbeTests(unittest.TestCase):
    def setUp(self):
        self.config = {
            "apiBaseUrl": API,
            "sourceUrl": SOURCE,
            "auth": {
                "origin": API,
                "capabilitiesUrl": API + "/api/account/capabilities",
            },
        }
        self.index = b"<!doctype html><html><body>dashboard</body></html>"
        self.responses = {
            "/runtime-config.json": (
                {"Content-Type": "application/json", "Cache-Control": "no-store"},
                json.dumps(self.config).encode(),
            ),
            "/": ({"Content-Type": "text/html; charset=utf-8"}, self.index),
            "/workspaces/dashboard-probe": ({"Content-Type": "text/html"}, self.index),
            "/settings/github/callback": (
                {
                    "Content-Type": "text/html",
                    "Cache-Control": "no-store",
                    "Referrer-Policy": "no-referrer",
                },
                self.index,
            ),
            "/runtime-config.json/not-config": ({"Content-Type": "text/html"}, self.index),
        }

    def run_probe(self):
        def respond(request, timeout):
            self.assertEqual(request.get_header("Origin"), CONSOLE)
            headers, body = self.responses[request.full_url.removeprefix(CONSOLE)]
            response = MagicMock()
            response.status = 200
            response.headers = Message()
            for name, value in headers.items():
                response.headers[name] = value
            response.read.return_value = body
            response.__enter__.return_value = response
            return response

        with patch("probe_dashboard.urlopen", side_effect=respond) as fetch:
            probe(CONSOLE, API, SOURCE)
            return fetch.call_count

    def test_accepts_packaged_dashboard_contract_and_deep_links(self):
        self.assertEqual(self.run_probe(), 5)

    def test_rejects_old_caddy_spa_response_and_invalid_json(self):
        for content_type in ("text/html", "application/json"):
            with self.subTest(content_type=content_type):
                self.responses["/runtime-config.json"] = (
                    {"Content-Type": content_type, "Cache-Control": "no-store"},
                    self.index,
                )
                with self.assertRaises((AssertionError, json.JSONDecodeError)):
                    self.run_probe()

    def test_rejects_non_exact_origin_contract(self):
        for field in ("apiBaseUrl", "origin", "capabilitiesUrl", "extra"):
            with self.subTest(field=field):
                config = deepcopy(self.config)
                if field == "apiBaseUrl":
                    config[field] += "/"
                elif field == "extra":
                    config[field] = "unexpected"
                else:
                    config["auth"][field] += "/"
                self.responses["/runtime-config.json"] = (
                    self.responses["/runtime-config.json"][0],
                    json.dumps(config).encode(),
                )
                with self.assertRaisesRegex(AssertionError, "exact public dashboard origin"):
                    self.run_probe()

    def test_rejects_lost_headers_or_deep_link_fallback(self):
        cases = (
            ("/runtime-config.json", "Cache-Control"),
            ("/settings/github/callback", "Cache-Control"),
            ("/settings/github/callback", "Referrer-Policy"),
            ("/workspaces/dashboard-probe", None),
            ("/runtime-config.json/not-config", None),
        )
        original = deepcopy(self.responses)
        for path, header in cases:
            with self.subTest(path=path, header=header):
                self.responses = deepcopy(original)
                if header:
                    del self.responses[path][0][header]
                else:
                    self.responses[path] = (self.responses[path][0], b"<html>not index</html>")
                with self.assertRaises(AssertionError):
                    self.run_probe()
