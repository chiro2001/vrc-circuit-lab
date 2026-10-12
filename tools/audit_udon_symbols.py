#!/usr/bin/env python3
"""Check that editor scripts only read Udon symbols that some product script declares.

Editor code reaches into running Udon programs with ``GetProgramVariable("name")``.
A misspelled name, or a name read from the wrong behaviour, makes Udon log
``Could not find symbol`` together with its entire symbol table - thousands of
characters of noise that bury the real message, and the read silently returns null.

This catches the "no such symbol anywhere" half of that mistake. It cannot catch a
read of a real symbol from the wrong object (for example ``deniedCommands`` read
from the simulator instead of the coordinator); the editor log remains the
authority for those.
"""

from __future__ import annotations

import argparse
import collections
import glob
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
PRODUCT_GLOBS = ("Assets/CircuitLab/Scripts/*.cs", "Assets/GpuNetlistSmoke/Scripts/*.cs")
CONSUMER_GLOBS = ("AgentScripts/*.cs",)


def declared_fields() -> set[str]:
    names: set[str] = set()
    for pattern in PRODUCT_GLOBS:
        for path in glob.glob(str(ROOT / pattern)):
            text = Path(path).read_text()
            # Strip attributes first: "[UdonSynced] public int foo = 1;" would
            # otherwise hide the declaration from the pattern below.
            text = re.sub(r"\[[^\]]*\]", " ", text)
            for match in re.finditer(
                r"^\s*(?:public|private|protected|internal)?\s*(?:static\s+)?(?:readonly\s+)?"
                r"(?:const\s+)?[\w\.<>\[\]]+\s+(\w+)\s*(?:=\s*[^;]+)?;",
                text,
                re.M,
            ):
                names.add(match.group(1))
    return names


def consumed_symbols() -> dict[str, set[str]]:
    reads: dict[str, set[str]] = collections.defaultdict(set)
    for pattern in CONSUMER_GLOBS:
        for path in glob.glob(str(ROOT / pattern)):
            text = Path(path).read_text()
            for match in re.finditer(r'GetProgramVariable\("([^"]+)"\)', text):
                reads[Path(path).name].add(match.group(1))
    return reads


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--quiet", action="store_true", help="Print only failures.")
    options = parser.parse_args()

    declared = declared_fields()
    reads = consumed_symbols()
    unknown = {
        script: sorted(name for name in names if name not in declared)
        for script, names in reads.items()
    }
    unknown = {script: names for script, names in unknown.items() if names}
    if unknown:
        for script, names in sorted(unknown.items()):
            print(f"{script}: reads undeclared symbols {names}")
        print("These would log 'Could not find symbol' and return null at runtime.")
        return 1
    if not options.quiet:
        total = sum(len(names) for names in reads.values())
        print(f"OK: {total} GetProgramVariable reads across {len(reads)} editor scripts, "
              f"all named like a declared product field ({len(declared)} fields scanned).")
    return 0


if __name__ == "__main__":
    sys.exit(main())
