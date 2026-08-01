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

# Must be a real line comment carrying a reason, not the text "delete-ok:" appearing
# anywhere. A string literal or an unrelated neighbouring line must not approve a delete.
JUSTIFICATION = re.compile(r"^\s*//\s*delete-ok:\s*\S")

# Any line comment, used to walk the contiguous comment block above a delete.
COMMENT_LINE = re.compile(r"^\s*//")


def is_justified(lines, index: int) -> bool:
    """True when a `// delete-ok:` comment sits in the comment block directly above."""
    # A trailing comment on the delete line itself also counts.
    trailing = lines[index].split("//", 1)
    if len(trailing) == 2 and re.match(r"\s*delete-ok:\s*\S", trailing[1]):
        return True

    # Walk upwards only while the lines are still comments. The first non-comment line
    # ends the block, so a justification further up cannot reach across real code.
    i = index - 1
    while i >= 0 and COMMENT_LINE.match(lines[i]):
        if JUSTIFICATION.match(lines[i]):
            return True
        i -= 1

    return False


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
            if is_justified(lines, i):
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
        "this plugin wrote, or carry a justification in the comment block directly above it\n"
        "(or trailing on the same line):\n"
        "\n    // delete-ok: <why this cannot touch user content>\n"
        "\nIf it can touch the STRM library, route it through StrmOwnership instead. See ADR-014."
    )
    return 1


if __name__ == "__main__":
    sys.exit(main())
