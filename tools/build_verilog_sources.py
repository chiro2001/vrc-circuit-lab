#!/usr/bin/env python3
"""Index the authored Verilog definitions against exported module instances."""

import argparse
import json
from pathlib import Path
import re

ROOT = Path(__file__).resolve().parents[1]


def export_sources(netlist: dict) -> dict:
    path = ROOT / "circuits/teaching_cpu.v"
    source = path.read_text(encoding="utf-8")
    definitions = []
    for match in re.finditer(r"(?m)^(?:\(\*[^\n]*\*\)\s*)?module\s+(\w+)\b[\s\S]*?^endmodule\b[^\n]*", source):
        definitions.append({"name": match[1], "fileIndex": 0,
            "startLine": source.count("\n", 0, match.start()) + 1,
            "endLine": source.count("\n", 0, match.end()) + 1,
            "text": match[0] + "\n", "externalMemory": match[1] in {"teaching_rom", "teaching_ram"}})
    indices = {definition["name"]: i for i, definition in enumerate(definitions)}
    if len(indices) != len(definitions):
        raise ValueError("Verilog definition names must be unique")
    modules = [{"id": module["id"], "path": module["path"], "definitionIndex": indices[module["type"]]}
               for module in netlist["modules"]]
    if len({module["id"] for module in modules}) != len(modules):
        raise ValueError("Module instance IDs must be unique")
    return {"schema": 1, "files": [{"path": "circuits/teaching_cpu.v", "text": source}],
            "definitions": definitions, "modules": modules}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--netlist", type=Path, default=ROOT / "Assets/CircuitLab/Data/gpu-netlist.json")
    parser.add_argument("--output", type=Path, default=ROOT / "Assets/CircuitLab/Data/verilog-source.json")
    options = parser.parse_args()
    result = export_sources(json.loads(options.netlist.read_text()))
    options.output.parent.mkdir(parents=True, exist_ok=True)
    options.output.write_text(json.dumps(result, ensure_ascii=False, indent=2) + "\n")
    print(f"Indexed {len(result['modules'])} instances against {len(result['definitions'])} Verilog definitions.")


if __name__ == "__main__":
    main()
