#!/usr/bin/env python3
# SPDX-License-Identifier: MPL-2.0
"""Probe the dashboard served by the installed bundle's Caddy service."""

import argparse
import json
from urllib.request import Request, urlopen


def probe(console_url: str, api_origin: str, source_url: str) -> None:
    def get(path: str):
        request = Request(console_url + path, headers={"Origin": console_url})
        with urlopen(request, timeout=10) as response:
            assert response.status == 200, f"{path}: expected HTTP 200"
            return response.headers, response.read()

    headers, body = get("/runtime-config.json")
    assert headers.get_content_type() == "application/json", "Runtime config must be JSON"
    assert headers.get("Cache-Control") == "no-store", "Runtime config must not be cached"
    assert json.loads(body) == {
        "apiBaseUrl": api_origin,
        "sourceUrl": source_url,
        "auth": {
            "origin": api_origin,
            "capabilitiesUrl": api_origin + "/api/account/capabilities",
        },
    }, "Runtime config must match the exact public dashboard origin contract"

    headers, index = get("/")
    assert headers.get_content_type() == "text/html", "Dashboard must serve HTML"
    assert b"<html" in index.lower(), "Dashboard must serve its SPA index"
    for path in (
        "/workspaces/dashboard-probe",
        "/settings/github/callback",
        "/runtime-config.json/not-config",
    ):
        headers, body = get(path)
        assert headers.get_content_type() == "text/html", f"{path}: expected SPA HTML"
        assert body == index, f"{path}: expected the dashboard deep-link fallback"
        if path.startswith("/settings/github/"):
            assert headers.get("Referrer-Policy") == "no-referrer", "GitHub referrer policy"
            assert headers.get("Cache-Control") == "no-store", "GitHub callbacks must not be cached"


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--console-url", required=True)
    parser.add_argument("--api-origin", required=True)
    parser.add_argument("--source-url", required=True)
    args = parser.parse_args()
    probe(args.console_url, args.api_origin, args.source_url)
    print("Packaged dashboard JSON, exact origin contract, headers and deep links passed.")
