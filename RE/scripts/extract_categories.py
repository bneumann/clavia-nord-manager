#!/usr/bin/env python3
"""
Cross-reference program category codes from parsed_protocol.txt with
category names from the Nord Stage 3 Program HTML export.

For each p2=0x1f message with file type "ns3f" (program):
  - extract name (offset 36, length at 32-35)
  - extract category code (offset 28-31)

Then look up the program name in the HTML to get the category string.
Output: sorted mapping of code -> category name.
"""

import re
import struct
import sys
from html.parser import HTMLParser
from pathlib import Path

PROTOCOL_FILE = Path(__file__).parent / "parsed_protocol.txt"
HTML_FILE     = Path(__file__).parent / "Export Nordmanager" / "Nord Stage 3 Program 2025-12-13.html"


# ── Parse HTML export ─────────────────────────────────────────────────────────

class ProgramTableParser(HTMLParser):
    """Extract (name, category) rows from the Nord program export table."""

    def __init__(self):
        super().__init__()
        self.name_to_category: dict[str, str] = {}
        self._in_td   = False
        self._cells: list[str] = []
        self._cur_row: list[str] = []
        self._in_table = False
        self._col_name = -1
        self._col_cat  = -1
        self._header_done = False

    def handle_starttag(self, tag, attrs):
        if tag == "table":
            self._in_table = True
        if tag == "tr":
            self._cur_row = []
        if tag in ("td", "th"):
            self._in_td = True
            self._cells.append("")

    def handle_endtag(self, tag):
        if tag in ("td", "th"):
            self._in_td = False
        if tag == "tr":
            if not self._header_done and self._cells:
                low = [c.strip().lower() for c in self._cells]
                if "name" in low and "category" in low:
                    self._col_name = low.index("name")
                    self._col_cat  = low.index("category")
                    self._header_done = True
            elif self._header_done and len(self._cells) > max(self._col_name, self._col_cat):
                name = self._cells[self._col_name].strip()
                cat  = self._cells[self._col_cat].strip()
                if name:
                    self.name_to_category[name] = cat
            self._cells = []

    def handle_data(self, data):
        if self._in_td and self._cells:
            self._cells[-1] += data


# ── Parse protocol dump ───────────────────────────────────────────────────────

def parse_1f_messages(protocol_path: Path) -> list[tuple[str, int]]:
    """
    Read parsed_protocol.txt and extract (name, category_code) for every
    p2=0x1f message whose file type is 'ns3f' (program).
    Returns list of (name, category_code).
    """
    results = []
    text = protocol_path.read_text(encoding="utf-8", errors="replace")

    # Split into per-message blocks.
    blocks = re.split(r"\nMessage \d+:", "\n" + text)

    for block in blocks:
        if "Param2: 0x0000001f" not in block:
            continue
        # Extract the hex payload line (first non-empty line after "Payload:")
        m = re.search(r"Payload:\s*\n\s+([0-9a-f ]+)", block, re.IGNORECASE)
        if not m:
            continue
        hex_str = m.group(1).replace(" ", "").strip()
        if len(hex_str) < 80:   # too short to have offsets 28-35+
            continue
        try:
            payload = bytes.fromhex(hex_str)
        except ValueError:
            continue

        if len(payload) < 40:
            continue

        file_type = payload[16:20].decode("ascii", errors="replace")
        if file_type != "ns3f":
            continue

        category_code = struct.unpack(">I", payload[28:32])[0]
        name_len      = struct.unpack(">I", payload[32:36])[0]
        if name_len == 0 or 36 + name_len > len(payload):
            continue
        name = payload[36:36 + name_len].decode("ascii", errors="replace")
        results.append((name, category_code))

    return results


# ── Main ──────────────────────────────────────────────────────────────────────

def main():
    if not PROTOCOL_FILE.exists():
        sys.exit(f"Not found: {PROTOCOL_FILE}")
    if not HTML_FILE.exists():
        sys.exit(f"Not found: {HTML_FILE}")

    parser = ProgramTableParser()
    parser.feed(HTML_FILE.read_text(encoding="utf-8", errors="replace"))
    name_to_cat = parser.name_to_category
    print(f"HTML: loaded {len(name_to_cat)} program entries\n")

    items = parse_1f_messages(PROTOCOL_FILE)
    print(f"Protocol dump: found {len(items)} ns3f entries\n")

    # Build code -> category mapping.
    code_to_cat: dict[int, str] = {}
    unmatched: list[tuple[str, int]] = []

    for name, code in items:
        if name in name_to_cat:
            cat = name_to_cat[name]
            if code in code_to_cat and code_to_cat[code] != cat:
                print(f"  CONFLICT: code {code} -> {code_to_cat[code]!r} vs {cat!r} (name={name!r})")
            else:
                code_to_cat[code] = cat
        else:
            unmatched.append((name, code))

    print("=" * 60)
    print("CONFIRMED MAPPINGS (sorted by code):")
    print("=" * 60)
    for code, cat in sorted(code_to_cat.items()):
        print(f"  0x{code:08x} = {code:3d}  →  {cat}")

    print()
    print(f"Unique categories resolved: {len(set(code_to_cat.values()))}/19")
    missing = sorted(set(name_to_cat.values()) - set(code_to_cat.values()))
    if missing:
        print(f"Missing categories (not seen in dump): {missing}")

    if unmatched:
        print(f"\nUnmatched names (in dump but not in HTML): {len(unmatched)}")
        for name, code in unmatched[:10]:
            print(f"  code=0x{code:08x}  name={name!r}")


if __name__ == "__main__":
    main()
