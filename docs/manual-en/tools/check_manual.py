"""Checks the manual against the deployed panel and itself.

1. Every control in data/inventory.json (slider, toggle, choice, button) is named somewhere in the manual.
2. Every local link points to an existing page and, with #anchor, to an existing heading id.
3. Every image referenced by a page exists in img/ (images are local only, so this is reported, not fatal,
   when --allow-missing-images is given, for example in a fresh clone).
4. No page loads scripts, styles or fonts from the network.
5. Every page is under 800 lines.
Usage: python -I docs/manual-en/tools/check_manual.py [--allow-missing-images]
"""
from __future__ import annotations

import argparse
import html
import json
import re
import sys
from html.parser import HTMLParser
from pathlib import Path

MANUAL = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(MANUAL / "tools"))  # python -I does not add the script folder
from build_index import slug  # noqa: E402  same anchor rule as manual.js
MAX_LINES = 800


def normalise(text: str) -> str:
    return re.sub(r"\s+", " ", html.unescape(text)).strip().lower()


class Page(HTMLParser):
    def __init__(self) -> None:
        super().__init__(convert_charrefs=True)
        self.text: list[str] = []
        self.ids: set[str] = set()
        self.links: list[str] = []
        self.images: list[str] = []
        self.resources: list[str] = []
        self._heading: str | None = None
        self._heading_text: list[str] = []
        self._in_control = 0
        self._depth = 0

    def handle_starttag(self, tag: str, attrs: list[tuple[str, str | None]]) -> None:
        values = dict(attrs)
        self._depth += 1
        if values.get("id"):
            self.ids.add(values["id"] or "")
        if tag == "div" and "control" in (values.get("class") or "").split():
            self._in_control = self._depth
        if tag == "h2" or (tag == "h3" and self._in_control):
            self._heading, self._heading_text = tag, []
            if values.get("id"):
                self._heading = None  # explicit id already recorded
        if tag == "a" and values.get("href"):
            self.links.append(values["href"] or "")
        if tag == "img" and values.get("src"):
            self.images.append(values["src"] or "")
        if tag in ("script", "link") and (values.get("src") or values.get("href")):
            self.resources.append(values.get("src") or values.get("href") or "")

    def handle_endtag(self, tag: str) -> None:
        if self._heading and tag == self._heading:
            self.ids.add(slug("".join(self._heading_text)))
            self._heading = None
        if tag == "div" and self._in_control == self._depth:
            self._in_control = 0
        self._depth -= 1

    def handle_data(self, data: str) -> None:
        self.text.append(data)
        if self._heading:
            self._heading_text.append(data)


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--allow-missing-images", action="store_true")
    args = parser.parse_args()
    problems: list[str] = []
    pages: dict[str, Page] = {}
    for path in sorted(MANUAL.glob("*.html")):
        source = path.read_text(encoding="utf-8")
        if source.count("\n") + 1 > MAX_LINES:
            problems.append(f"{path.name}: more than {MAX_LINES} lines")
        page = Page()
        page.feed(source)
        pages[path.name] = page
    corpus = normalise(" ".join(" ".join(p.text) for p in pages.values()))

    inventory = json.loads((MANUAL / "data" / "inventory.json").read_text(encoding="utf-8"))
    missing = []
    for control in inventory["controls"]:
        if control["Kind"] not in ("slider", "toggle", "choice", "button") or not control.get("Label"):
            continue
        label = normalise(control["Label"].replace("…", ""))
        # Channel suffixes ("Particle scale · R") are documented as one group ("Particle scale · R, · G, · B").
        base = re.sub(r"\s*·\s*[rgb]$", "", label)
        if label not in corpus and base not in corpus:
            missing.append(f"{control['Section'] or 'top'} › {control['Label']}")
    for entry in sorted(set(missing)):
        problems.append("control not described: " + entry)

    missing_images = []
    for name, page in pages.items():
        for href in page.links:
            if href.startswith(("http://", "https://", "mailto:")):
                continue
            target, _, anchor = href.partition("#")
            target = target or name
            if target.startswith("img/"):
                if not (MANUAL / target).is_file():
                    missing_images.append(f"{name}: {target}")
                continue
            if target not in pages:
                problems.append(f"{name}: link to missing page {href}")
            elif anchor and anchor not in pages[target].ids:
                problems.append(f"{name}: link to missing anchor {href}")
        for src in page.images:
            if not (MANUAL / src).is_file():
                missing_images.append(f"{name}: {src}")
        for resource in page.resources:
            if re.match(r"^(https?:)?//", resource):
                problems.append(f"{name}: network resource {resource}")
    for entry in sorted(set(missing_images)):
        (print if args.allow_missing_images else problems.append)("image missing: " + entry)

    for line in problems:
        print(line)
    controls = sum(1 for c in inventory["controls"] if c["Kind"] in ("slider", "toggle", "choice", "button"))
    print(f"{len(pages)} pages, {controls} controls checked, {len(problems)} problem(s)")
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main())
