#!/usr/bin/env python3
# SPDX-License-Identifier: MPL-2.0
"""Verify static GitHub Pages site links, fragments, SVG labels, and assets."""

from __future__ import annotations

from html.parser import HTMLParser
from pathlib import Path
import sys
from urllib.parse import urldefrag, urlparse


ROOT = Path(__file__).resolve().parents[1]
SITE = ROOT / "site"


class PageInspector(HTMLParser):
    def __init__(self) -> None:
        super().__init__()
        self.ids: set[str] = set()
        self.links: list[tuple[str, int]] = []
        self.assets: list[tuple[str, int]] = []
        self.h1_count = 0
        self.has_color_scheme_meta = False
        self.svg_labelled_by: list[tuple[str, int]] = []

    def handle_starttag(self, tag: str, attrs: list[tuple[str, str | None]]) -> None:
        attr_map = dict(attrs)
        line = self.getpos()[0]

        element_id = attr_map.get("id")
        if element_id:
            self.ids.add(element_id)

        if tag == "h1":
            self.h1_count += 1
        elif tag == "meta" and attr_map.get("name") == "color-scheme":
            if attr_map.get("content") in ("dark", "light dark"):
                self.has_color_scheme_meta = True
        elif tag == "a" and attr_map.get("href"):
            self.links.append((attr_map["href"] or "", line))
        elif tag in ("link", "script", "img") and (attr_map.get("href") or attr_map.get("src")):
            target = attr_map.get("href") or attr_map.get("src") or ""
            self.assets.append((target, line))
        elif tag == "svg" and attr_map.get("role") == "img":
            self.svg_labelled_by.append((attr_map.get("aria-labelledby") or "", line))


def main() -> int:
    errors: list[str] = []

    if not (SITE / ".nojekyll").is_file():
        errors.append("Missing site/.nojekyll")

    for path in SITE.rglob("*"):
        if path.is_symlink():
            errors.append(f"Symlink forbidden in GitHub Pages bundle: {path}")

    html_files = sorted(SITE.rglob("*.html"))
    if not html_files:
        errors.append("No HTML files found under site/")

    parsed: dict[Path, PageInspector] = {}
    for html_path in html_files:
        inspector = PageInspector()
        inspector.feed(html_path.read_text(encoding="utf-8"))
        parsed[html_path] = inspector

        rel = html_path.relative_to(ROOT)
        if inspector.h1_count != 1:
            errors.append(f"{rel}: expected exactly 1 <h1>, found {inspector.h1_count}")
        if not inspector.has_color_scheme_meta:
            errors.append(f"{rel}: missing <meta name='color-scheme' content='light dark'>")
        for label_id, line in inspector.svg_labelled_by:
            if not label_id or label_id not in inspector.ids:
                errors.append(f"{rel}:{line}: <svg role='img'> missing matching title id '{label_id}'")

    for html_path, inspector in parsed.items():
        rel = html_path.relative_to(ROOT)
        for target, line in inspector.assets:
            parsed_url = urlparse(target)
            if parsed_url.scheme or parsed_url.netloc:
                continue
            if target.startswith("/"):
                errors.append(f"{rel}:{line}: root-relative asset '{target}' breaks GitHub Project Pages subpaths")
                continue
            resolved = (html_path.parent / parsed_url.path).resolve()
            if not resolved.is_file():
                errors.append(f"{rel}:{line}: missing asset '{target}'")

        for href, line in inspector.links:
            clean, fragment = urldefrag(href)
            parsed_url = urlparse(clean)
            if parsed_url.scheme or parsed_url.netloc:
                continue
            if clean.startswith("/"):
                errors.append(f"{rel}:{line}: root-relative link '{href}' breaks GitHub Project Pages subpaths")
                continue

            target_file = html_path if clean == "" else (html_path.parent / parsed_url.path).resolve()
            if not target_file.is_file():
                errors.append(f"{rel}:{line}: broken link '{href}'")
                continue

            if fragment and target_file in parsed and fragment not in parsed[target_file].ids:
                errors.append(f"{rel}:{line}: missing fragment '#{fragment}' in {target_file.relative_to(ROOT)}")

    if errors:
        for error in errors:
            print(f"ERROR: {error}", file=sys.stderr)
        return 1

    print(f"Verified {len(html_files)} HTML pages under {SITE.relative_to(ROOT)}/ with 0 broken links or fragments.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
