#!/usr/bin/env python3
"""Annotate a teaching-CPU netlist with the settle depth each half-phase needs.

The simulator settles the combinational cone once per half phase:

* clock low: the only input that changes is the clock itself, so the settle only
  has to propagate through the clock's cone of influence.
* clock high: the DFF commit changes every register output at once, so the settle
  must cover the deepest cone reachable from any DFF (the exported max depth).

Computing the clock cone from the netlist keeps the shallow settle conservative
instead of guessing, and the result is verified against the reference model by
the GPU validation suite. Run this after regenerating a netlist, or pass a path.
"""

from __future__ import annotations

import argparse
import collections
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
DEFAULT_NETLIST = ROOT / "Assets" / "CircuitLab" / "Data" / "gpu-netlist.json"
# One extra pass so the per-cycle state pass count stays even: the display and
# wire materials only need rebinding when the state texture parity changes.
CLOCK_SETTLE_MARGIN = 2


def settle_dependencies(node: dict, rom_address: list[int], ram_address: list[int]) -> list[int]:
    """Node ids a node reads while the state settles (DFFs hold their value)."""
    op = node["op"]
    if op in (0, 1, 2, 3):
        return []
    if op == 15:
        return rom_address
    if op == 16:
        return ram_address
    return [node[key] for key in ("a", "b", "c") if node.get(key, -1) not in (-1, None)]


def clock_cone(exported: dict) -> tuple[int, int]:
    """Return (max level, node count) of the clock's settle cone of influence."""
    nodes = {node["id"]: node for node in exported["nodes"]}
    memory = exported["memoryPorts"]
    rom_address = [int(signal) for signal in memory["romAddress"]]
    ram_address = [int(signal) for signal in memory["ramReadAddress"]]
    successors: dict[int, list[int]] = collections.defaultdict(list)
    for node in exported["nodes"]:
        for source in settle_dependencies(node, rom_address, ram_address):
            successors[source].append(node["id"])
    clock = int(exported["ports"]["clk"][0])
    seen = {clock}
    queue = collections.deque([clock])
    depth = 0
    while queue:
        current = queue.popleft()
        depth = max(depth, int(nodes[current].get("level", 0)))
        for successor in successors.get(current, []):
            if successor not in seen:
                seen.add(successor)
                queue.append(successor)
    return depth, len(seen)


def settle_passes_for(depth: int) -> int:
    passes = max(CLOCK_SETTLE_MARGIN, depth + 1)
    return passes if passes % 2 == 0 else passes + 1


def annotate(path: Path) -> dict:
    exported = json.loads(path.read_text())
    depth, cone_nodes = clock_cone(exported)
    passes = settle_passes_for(depth)
    if passes > exported["settlePasses"]:
        raise AssertionError("Clock settle depth exceeds the full settle depth")
    exported["clockSettlePasses"] = passes
    exported["clockSettleDepth"] = depth
    path.write_text(json.dumps(exported, separators=(",", ":")) + "\n")
    return {
        "netlist": str(path),
        "clockConeDepth": depth,
        "clockConeNodes": cone_nodes,
        "clockSettlePasses": passes,
        "settlePasses": exported["settlePasses"],
        "statePassesPerCycle": passes + 1 + exported["settlePasses"],
    }


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("netlist", nargs="?", type=Path, default=DEFAULT_NETLIST)
    options = parser.parse_args()
    print(json.dumps(annotate(options.netlist), indent=2))


if __name__ == "__main__":
    main()
