#!/usr/bin/env python3
"""Guard every filesystem delete in the plugin.

The plugin deletes files on the user's disk, and three separate paths once destroyed
content it had not written (BUG-031). Ownership verification now lives in
``StrmOwnership``, but that is only an agreement until something enforces it: a new
delete added anywhere else silently skips the check.

So every ``File.Delete`` / ``Directory.Delete`` must either live in ``StrmOwnership``,
or carry a ``delete-ok:`` comment saying why it is not touching library content. The
comment is the point. It makes "I am deleting something the user did not give me"
a decision someone had to write down.

Run: python3 scripts/check-delete-sites.py
Exits non-zero and prints every unjustified site.
"""

import re
import sys
from pathlib import Path

PLUGIN_ROOT = Path(__file__).resolve().parent.parent / "Emby.Xtream.Plugin"

# Ownership verification lives here; deletes in this file are the sanctioned ones.
SANCTIONED_FILE = "Service/StrmOwnership.cs"

DELETE_CALL = re.compile(r"\b(?:File|Directory)\.Delete\s*\(")
JUSTIFICATION = re.compile(r"delete-ok:\s*\S")

# How many lines above a delete we will look for its justification.
LOOKBACK = 4


def find_unjustified(root: Path):
    problems = []

    for path in sorted(root.rglob("*.cs")):
        rel = path.relative_to(root).as_posix()
        if rel == SANCTIONED_FILE:
            continue
        # Build output is not source.
        if rel.startswith(("obj/", "bin/")):
            continue

        lines = path.read_text(encoding="utf-8", errors="replace").splitlines()
        for i, line in enumerate(lines):
            if not DELETE_CALL.search(line):
                continue
            window = lines[max(0, i - LOOKBACK): i + 1]
            if any(JUSTIFICATION.search(w) for w in window):
                continue
            problems.append((rel, i + 1, line.strip()))

    return problems


def main() -> int:
    if not PLUGIN_ROOT.is_dir():
        print(f"error: plugin root not found at {PLUGIN_ROOT}", file=sys.stderr)
        return 2

    problems = find_unjustified(PLUGIN_ROOT)
    if not problems:
        print("delete-site check: all delete calls are sanctioned or justified")
        return 0

    print("Unjustified filesystem delete(s) found.\n")
    for rel, line_no, text in problems:
        print(f"  Emby.Xtream.Plugin/{rel}:{line_no}")
        print(f"    {text}")
    print(
        "\nEvery delete outside Service/StrmOwnership.cs must be reachable only for content\n"
        "this plugin wrote, or carry a justification comment within"
        f" {LOOKBACK} lines above it:\n"
        "\n    // delete-ok: <why this cannot touch user content>\n"
        "\nIf it can touch the STRM library, route it through StrmOwnership instead. See ADR-014."
    )
    return 1


if __name__ == "__main__":
    sys.exit(main())
