#!/usr/bin/env python3
"""Verify RTL and a Yosys/ABC netlist, then export data for the Unity GPU smoke."""

import argparse
import collections
import csv
import json
import os
from pathlib import Path
import subprocess

ROOT = Path(__file__).resolve().parents[1]
OPCODES = {"CONST0": 0, "CONST1": 1, "INPUT": 2, "DFF": 3,
           "BUF": 4, "NOT": 5, "AND": 6, "OR": 7, "XOR": 8,
           "XNOR": 9, "MUX": 10, "NAND": 11, "NOR": 12,
           "ANDNOT": 13, "ORNOT": 14}


def run(command, directory, log):
    result = subprocess.run(command, cwd=directory, text=True, stdout=subprocess.PIPE,
                            stderr=subprocess.STDOUT, env={**os.environ, "TMPDIR": str(directory)})
    log.write_text(result.stdout)
    if result.returncode:
        raise RuntimeError(f"Failed: {command}\n{result.stdout[-10000:]}")
    return result.stdout


def export(module, rows):
    bits = sorted({bit for port in module["ports"].values() for bit in port["bits"] if isinstance(bit, int)} |
                  {bit for cell in module["cells"].values() for values in cell["connections"].values()
                   for bit in values if isinstance(bit, int)})
    ids = {bit: i + 2 for i, bit in enumerate(bits)}
    ids.update({"0": 0, "1": 1})
    nodes = [{"id": 0, "op": 0, "a": 0, "b": 0, "c": 0, "name": "0"},
             {"id": 1, "op": 1, "a": 0, "b": 0, "c": 0, "name": "1"}]
    nodes += [None] * len(bits)
    ports = {name: [ids[bit] for bit in port["bits"]] for name, port in module["ports"].items()}
    input_order = ["clk", "reset", "load", "enable", "data"]
    offset = 0
    offsets = {}
    for name in input_order:
        offsets[name] = offset
        for index, node_id in enumerate(ports[name]):
            nodes[node_id] = {"id": node_id, "op": 2, "a": offset, "b": 0, "c": 0,
                              "name": name if len(ports[name]) == 1 else f"{name}[{index}]"}
            offset += 1
    registers = []
    for cell_name, cell in module["cells"].items():
        kind = cell["type"]
        conn = cell["connections"]
        if kind == "$_DFF_P_":
            if conn["C"] != module["ports"]["clk"]["bits"]:
                raise ValueError("Only a single rising-edge clock is supported")
            node_id, a = ids[conn["Q"][0]], ids[conn["D"][0]]
            op, b, c = OPCODES["DFF"], 0, 0
            registers.append(node_id)
        else:
            name = kind.removeprefix("$_").removesuffix("_")
            if name not in OPCODES or name in ("INPUT", "DFF", "CONST0", "CONST1"):
                raise ValueError(f"Unsupported synthesized cell {kind}")
            node_id, op = ids[conn["Y"][0]], OPCODES[name]
            a = ids[conn["A"][0]]
            b = ids[conn["B"][0]] if "B" in conn else 0
            c = ids[conn["S"][0]] if "S" in conn else 0
        if nodes[node_id] is not None:
            raise ValueError(f"Multiple drivers for signal {node_id}")
        nodes[node_id] = {"id": node_id, "op": op, "a": a, "b": b, "c": c,
                          "name": f"{node_id}: {kind[2:-1]}"}
    if any(node is None for node in nodes):
        raise ValueError("Undriven net")
    for name, port in module["ports"].items():
        if port["direction"] == "output":
            for index, node_id in enumerate(ports[name]):
                nodes[node_id]["name"] = name if len(port["bits"]) == 1 else f"{name}[{index}]"

    depths, visiting = {}, set()
    def depth(node_id):
        if node_id in depths:
            return depths[node_id]
        if node_id in visiting:
            raise ValueError("Combinational loop")
        visiting.add(node_id)
        node = nodes[node_id]
        op = node["op"]
        parents = [] if op <= 3 else [node["a"]]
        if op >= 6: parents.append(node["b"])
        if op == 10: parents.append(node["c"])
        depths[node_id] = 0 if not parents else 1 + max(depth(p) for p in parents)
        visiting.remove(node_id)
        return depths[node_id]
    for node in nodes:
        depth(node["id"])
    ordered = sorted(nodes, key=lambda node: depths[node["id"]])
    max_depth = max(depths.values())
    groups = collections.defaultdict(list)
    for node in nodes:
        groups[depths[node["id"]]].append(node)
    for level, group in groups.items():
        for rank, node in enumerate(group):
            node["level"] = level
            node["x"] = -4.3 + 8.6 * level / max(1, max_depth)
            node["y"] = 1.9 - 3.8 * rank / max(1, len(group) - 1)

    values = [0] * len(nodes)
    masks, expected_states, expected_q = [], [], []
    def evaluate(mask):
        for node in ordered:
            i, op, a, b, c = (node[k] for k in ("id", "op", "a", "b", "c"))
            if op == 0: values[i] = 0
            elif op == 1: values[i] = 1
            elif op == 2: values[i] = (mask >> a) & 1
            elif op == 3: continue
            elif op == 4: values[i] = values[a]
            elif op == 5: values[i] = 1 - values[a]
            elif op == 6: values[i] = values[a] & values[b]
            elif op == 7: values[i] = values[a] | values[b]
            elif op == 8: values[i] = values[a] ^ values[b]
            elif op == 9: values[i] = 1 - (values[a] ^ values[b])
            elif op == 10: values[i] = values[b] if values[c] else values[a]
            elif op == 11: values[i] = 1 - (values[a] & values[b])
            elif op == 12: values[i] = 1 - (values[a] | values[b])
            elif op == 13: values[i] = values[a] & (1 - values[b])
            elif op == 14: values[i] = values[a] | (1 - values[b])
    for row in rows:
        mask = 1 << offsets["clk"]
        for name in ("reset", "load", "enable", "data"):
            mask |= int(row[name]) << offsets[name]
        evaluate(mask)
        next_q = {i: values[nodes[i]["a"]] for i in registers}
        for i, value in next_q.items():
            values[i] = value
        evaluate(mask)
        for name in ("q", "zero", "parity"):
            result = sum(values[i] << index for index, i in enumerate(ports[name]))
            if result != int(row[name]):
                raise ValueError(f"Netlist interpreter differs from Icarus at cycle {row['cycle']} port {name}")
        masks.append(mask)
        expected_states.extend(values)
        expected_q.append(int(row["q"]))
    return {"name": "smoke_accumulator", "schema": 1, "nodes": nodes,
            "nodeCount": len(nodes), "textureWidth": 1 << (len(nodes) - 1).bit_length(),
            "maxDepth": max_depth, "settlePasses": max_depth + 1,
            "registerCount": len(registers), "ports": ports, "inputOffsets": offsets,
            "inputMasks": masks, "expectedStates": expected_states, "expectedQ": expected_q,
            "vectorCount": len(rows), "cellCounts": dict(collections.Counter(c["type"] for c in module["cells"].values()))}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, default=Path.home() / "tmp/vrc-cpu-map/netlist-smoke/hdl")
    options = parser.parse_args()
    output = options.output.resolve()
    output.mkdir(parents=True, exist_ok=True)
    rtl = ROOT / "circuits/smoke_accumulator.v"
    tb = ROOT / "circuits/smoke_accumulator_tb.sv"
    def test(source, name):
        run(["iverilog", "-g2012", "-s", "smoke_accumulator_tb", "-o", name + ".vvp", str(source), str(tb)], output, output / (name + "-compile.log"))
        result = run(["vvp", name + ".vvp", "+trace=" + name + ".csv"], output, output / (name + "-test.log"))
        if "HDL_SMOKE_PASS" not in result:
            raise RuntimeError("Testbench did not finish successfully")
        print(result.strip())
    test(rtl, "rtl")
    script = output / "synthesize.ys"
    script.write_text(f'read_verilog "{rtl}"\nsynth -top smoke_accumulator -flatten -noabc\ndffunmap\nabc -g simple\nclean\ncheck -assert\nstat\nwrite_json netlist.json\nwrite_verilog -noattr synthesized.v\n')
    run(["yosys", "-Q", "-T", "-s", str(script)], output, output / "yosys.log")
    test(output / "synthesized.v", "gate")
    if (output / "rtl.csv").read_bytes() != (output / "gate.csv").read_bytes():
        raise RuntimeError("RTL and synthesized traces differ")
    with (output / "rtl.csv").open() as stream:
        rows = list(csv.DictReader(stream))
    module = json.loads((output / "netlist.json").read_text())["modules"]["smoke_accumulator"]
    exported = export(module, rows)
    (output / "gpu-netlist.json").write_text(json.dumps(exported, indent=2) + "\n")
    summary = {key: exported[key] for key in ("name", "nodeCount", "maxDepth", "settlePasses", "registerCount", "vectorCount", "cellCounts")}
    summary.update({"rtlPassed": True, "synthesizedPassed": True, "tracesEqual": True, "allNodeReferencePassed": True})
    (output / "hdl-validation.json").write_text(json.dumps(summary, indent=2) + "\n")
    print(json.dumps(summary, indent=2))
    print("Export:", output / "gpu-netlist.json")


if __name__ == "__main__":
    main()
