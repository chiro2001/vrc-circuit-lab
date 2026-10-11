#!/usr/bin/env python3
"""Verify the hierarchical teaching CPU and export the schema-2 GPU fixture."""

import argparse
import collections
import csv
import json
import os
from pathlib import Path
import subprocess

ROOT = Path(__file__).resolve().parents[1]
TOP = "teaching_cpu"
OPCODES = {"CONST0": 0, "CONST1": 1, "INPUT": 2, "DFF": 3,
           "BUF": 4, "NOT": 5, "AND": 6, "OR": 7, "XOR": 8,
           "XNOR": 9, "MUX": 10, "NAND": 11, "NOR": 12,
           "ANDNOT": 13, "ORNOT": 14}
DEBUG_PORTS = ("pc", "instruction", "phase", "acc", "out", "zero", "carry", "halt",
               "terminal_data", "terminal_write", "terminal_sequence")


def run(command, directory, log):
    result = subprocess.run(command, cwd=directory, text=True, stdout=subprocess.PIPE,
                            stderr=subprocess.STDOUT, env={**os.environ, "TMPDIR": str(directory)})
    log.write_text(result.stdout)
    if result.returncode:
        raise RuntimeError(f"Failed: {command}\n{result.stdout[-10000:]}")
    return result.stdout


def read_rows(path):
    with path.open() as stream:
        return list(csv.DictReader(stream))


class Aliases:
    """Union hierarchy port aliases before assigning global signal IDs."""

    def __init__(self):
        self.parents = {"0": "0", "1": "1"}

    def add(self, key):
        self.parents.setdefault(key, key)
        return key

    def find(self, key):
        parent = self.parents[key]
        if parent != key:
            self.parents[key] = self.find(parent)
        return self.parents[key]

    def union(self, a, b):
        a, b = self.find(a), self.find(b)
        if a == b:
            return
        if a in ("0", "1") and b in ("0", "1"):
            raise ValueError("A hierarchy port shorts CONST0 to CONST1")
        if b in ("0", "1"):
            a, b = b, a
        self.parents[b] = a


def export(design, rows, waveform_rows):
    definitions = design["modules"]
    aliases = Aliases()
    instances, primitive_cells = [], []

    def local_key(module_id, bit):
        if bit in ("0", "1"):
            return bit
        if not isinstance(bit, int):
            raise ValueError(f"Unresolved four-state signal {bit!r} in module {module_id}")
        return aliases.add((module_id, bit))

    def instantiate(kind, name, parent_id, path, connections=None):
        definition = definitions[kind]
        module_id = len(instances)
        instance = {"id": module_id, "parentId": parent_id, "path": path,
                    "type": kind, "name": name, "children": [], "nodes": [],
                    "definition": definition}
        instances.append(instance)
        for port in definition["ports"].values():
            for bit in port["bits"]:
                local_key(module_id, bit)
        for net in definition["netnames"].values():
            for bit in net["bits"]:
                local_key(module_id, bit)
        for cell in definition["cells"].values():
            for values in cell["connections"].values():
                for bit in values:
                    local_key(module_id, bit)
        if connections is not None:
            for port_name, port in definition["ports"].items():
                values = connections.get(port_name)
                if values is None or len(values) != len(port["bits"]):
                    raise ValueError(f"Port width mismatch at {path}/{port_name}")
                for child_bit, parent_bit in zip(port["bits"], values):
                    aliases.union(local_key(module_id, child_bit), local_key(parent_id, parent_bit))
        for cell_name, cell in sorted(definition["cells"].items()):
            if cell["type"] in definitions:
                child_id = instantiate(cell["type"], cell_name, module_id,
                                       path + "/" + cell_name, cell["connections"])
                instance["children"].append(child_id)
            else:
                primitive_cells.append((module_id, cell_name, cell))
        return module_id

    instantiate(TOP, TOP, -1, TOP)
    roots = {aliases.find(key) for key in aliases.parents}
    ids = {"0": 0, "1": 1}
    for key in sorted(roots - {"0", "1"}):
        ids[key] = len(ids)

    def signal_id(module_id, bit):
        return ids[aliases.find(local_key(module_id, bit))]

    nodes = [None] * len(ids)
    nodes[0] = {"id": 0, "op": 0, "a": 0, "b": 0, "c": 0, "name": "CONST0", "moduleId": 0}
    nodes[1] = {"id": 1, "op": 1, "a": 0, "b": 0, "c": 0, "name": "CONST1", "moduleId": 0}
    root_ports = definitions[TOP]["ports"]
    ports = {name: [signal_id(0, bit) for bit in port["bits"]] for name, port in root_ports.items()}
    offsets, offset = {}, 0
    for name in ("clk", "reset", "program_select"):
        offsets[name] = offset
        for bit_index, node_id in enumerate(ports[name]):
            if nodes[node_id] is not None:
                raise ValueError(f"Top input {name} has another driver")
            nodes[node_id] = {"id": node_id, "op": 2, "a": offset, "b": 0, "c": 0,
                              "name": TOP + "/" + name, "moduleId": 0}
            offset += 1
    if offset > 32:
        raise ValueError("GPU input masks support at most 32 input bits")

    registers = []
    cell_counts = collections.Counter()
    for module_id, cell_name, cell in primitive_cells:
        kind, conn = cell["type"], cell["connections"]
        cell_counts[kind] += 1
        if kind == "$_DFF_P_":
            if len(conn["C"]) != 1 or signal_id(module_id, conn["C"][0]) != ports["clk"][0]:
                raise ValueError("Only the top-level, single rising-edge clock is supported")
            node_id = signal_id(module_id, conn["Q"][0])
            op, a, b, c = 3, signal_id(module_id, conn["D"][0]), 0, 0
            registers.append(node_id)
        else:
            name = kind.removeprefix("$_").removesuffix("_")
            if not kind.startswith("$_") or name not in OPCODES or OPCODES[name] <= 3:
                raise ValueError(f"Unsupported synthesized cell {kind} at {instances[module_id]['path']}/{cell_name}")
            if any(len(values) != 1 for values in conn.values()):
                raise ValueError(f"Primitive {kind} has a nonscalar connection")
            node_id, op = signal_id(module_id, conn["Y"][0]), OPCODES[name]
            a = signal_id(module_id, conn["A"][0])
            b = signal_id(module_id, conn["B"][0]) if "B" in conn else 0
            c = signal_id(module_id, conn["S"][0]) if "S" in conn else 0
        if nodes[node_id] is not None:
            raise ValueError(f"Multiple drivers for signal {node_id}")
        nodes[node_id] = {"id": node_id, "op": op, "a": a, "b": b, "c": c,
                          "name": instances[module_id]["path"] + "/" + cell_name,
                          "moduleId": module_id}

    signals, names_by_node = {}, collections.defaultdict(list)
    for instance in instances:
        module_id, definition, path = instance["id"], instance["definition"], instance["path"]
        instance["ports"] = [{"name": name, "direction": port["direction"],
                              "signals": [signal_id(module_id, bit) for bit in port["bits"]]}
                             for name, port in definition["ports"].items()]
        for name, net in definition["netnames"].items():
            net_ids = [signal_id(module_id, bit) for bit in net["bits"]]
            signals[path + "/" + name] = net_ids
            for bit_index, node_id in enumerate(net_ids):
                suffix = f"[{bit_index + net.get('offset', 0)}]" if len(net_ids) > 1 else ""
                names_by_node[node_id].append((module_id, "$" in name, path + "/" + name + suffix))
        del instance["definition"]
    for node in nodes:
        if node is None:
            raise ValueError("Undriven retained net; cannot create a fictitious probe signal")
        module_id = node["moduleId"]
        instances[module_id]["nodes"].append(node["id"])
        candidates = [entry for entry in names_by_node[node["id"]] if entry[0] == module_id]
        if candidates and node["op"] > 1:
            node["name"] = min(candidates, key=lambda entry: (entry[1], len(entry[2]), entry[2]))[2]
    if len(instances) != 19 or sum(m["type"] == "teaching_full_adder" for m in instances) != 8:
        raise ValueError("Teaching CPU hierarchy must preserve all 19 instances and eight full adders")
    if sorted(i for instance in instances for i in instance["nodes"]) != list(range(len(nodes))):
        raise ValueError("Every global signal must belong to exactly one driver instance")

    depths, visiting = {}, set()

    def depth(node_id):
        if node_id in depths:
            return depths[node_id]
        if node_id in visiting:
            raise ValueError("Combinational loop")
        visiting.add(node_id)
        node, parents = nodes[node_id], []
        if node["op"] > 3:
            parents.append(node["a"])
            if node["op"] >= 6:
                parents.append(node["b"])
            if node["op"] == 10:
                parents.append(node["c"])
        depths[node_id] = 0 if not parents else 1 + max(depth(parent) for parent in parents)
        visiting.remove(node_id)
        return depths[node_id]

    for node in nodes:
        node["level"] = depth(node["id"])
    ordered = sorted(nodes, key=lambda node: (node["level"], node["id"]))
    values = [0] * len(nodes)

    def evaluate(mask):
        for node in ordered:
            i, op, a, b, c = (node[key] for key in ("id", "op", "a", "b", "c"))
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

    def check_outputs(row, phase_name):
        for name in DEBUG_PORTS:
            actual = sum(values[i] << bit_index for bit_index, i in enumerate(ports[name]))
            if actual != int(row[name]):
                raise ValueError(f"Interpreter differs from Icarus at cycle {row['cycle']} {phase_name} {name}: {actual} != {row[name]}")

    waves = {(int(row["cycle"]), int(row["half"])): row for row in waveform_rows}
    masks, expected_states = [], []
    for index, row in enumerate(rows):
        mask = sum(int(row[name]) << offsets[name] for name in ("reset", "program_select"))
        evaluate(mask)
        if index:
            check_outputs(waves[(int(row["cycle"]), 0)], "low")
        mask |= 1 << offsets["clk"]
        evaluate(mask)
        next_q = [(i, values[nodes[i]["a"]]) for i in registers]
        for i, value in next_q:
            values[i] = value
        evaluate(mask)
        check_outputs(row, "high")
        check_outputs(waves[(int(row["cycle"]), 1)], "waveform-high")
        masks.append(mask)
        expected_states.extend(values)

    max_depth = max(depths.values())
    text_indices = [index for index, row in enumerate(rows) if row["segment"] == "text"]
    text_bytes = [int(rows[index]["terminal_data"]) for index in text_indices
                  if int(rows[index]["terminal_write"])]
    if text_bytes != list(b"HELLO\n"):
        raise ValueError("Text fixture must retain six real writes, including both L bytes")
    if len(registers) > 64:
        raise ValueError("Teaching CPU snapshot requires at most 64 flip-flop bits")
    return {"name": TOP, "schema": 2, "nodes": nodes,
            "nodeCount": len(nodes), "textureWidth": 1 << (len(nodes) - 1).bit_length(),
            "maxDepth": max_depth, "settlePasses": max_depth + 1,
            "registerCount": len(registers), "ports": ports, "inputOffsets": offsets,
            "dffSignals": sorted(registers), "outputWriteSignal": ports["terminal_write"][0],
            "outSignals": ports["out"], "terminalSequenceSignals": ports["terminal_sequence"],
            "terminalTextFixture": {"firstVector": min(text_indices), "lastVector": max(text_indices),
                                    "bytes": text_bytes},
            "modules": instances, "signals": signals,
            "inputMasks": masks, "expectedStates": expected_states, "vectorCount": len(rows),
            "cellCounts": dict(sorted(cell_counts.items()))}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path,
                        default=Path.home() / "tmp/vrc-cpu-map/circuit-lab/hdl")
    options = parser.parse_args()
    output = options.output.resolve()
    output.mkdir(parents=True, exist_ok=True)
    rtl = ROOT / "circuits/teaching_cpu.v"
    tests = [("teaching_cpu_units_tb", "TEACHING_UNITS_PASS"),
             ("teaching_cpu_tb", "TEACHING_CPU_PASS")]

    def test(source, variant):
        for top, marker in tests:
            stem = variant + ("-units" if top.endswith("units_tb") else "-cpu")
            run(["iverilog", "-g2012", "-s", top, "-o", stem + ".vvp",
                 str(source), str(ROOT / "circuits" / (top + ".sv"))],
                output, output / (stem + "-compile.log"))
            result = run(["vvp", stem + ".vvp", "+trace=" + stem + ".csv",
                          "+waveform=" + stem + "-waveform.csv"], output, output / (stem + "-test.log"))
            if marker not in result:
                raise RuntimeError(f"Testbench {top} did not finish successfully")
            print(variant, result.strip(), flush=True)
            if top == "teaching_cpu_tb":
                result = run(["vvp", stem + ".vvp", "+count_cycles=512",
                              "+trace=" + variant + "-fixture.csv",
                              "+waveform=" + variant + "-fixture-waveform.csv"],
                             output, output / (variant + "-fixture-test.log"))
                if marker not in result:
                    raise RuntimeError("Fixture test did not finish successfully")

    test(rtl, "rtl")
    script = output / "synthesize.ys"
    script.write_text(f'read_verilog "{rtl}"\nsynth -top {TOP} -noabc\n'
                      'dffunmap\nabc -g simple\nclean\ncheck -assert\nstat\n'
                      'write_json netlist.json\nwrite_verilog -noattr synthesized.v\n')
    run(["yosys", "-Q", "-T", "-s", str(script)], output, output / "yosys.log")
    test(output / "synthesized.v", "gate")
    for suffix in ("cpu.csv", "cpu-waveform.csv", "fixture.csv", "fixture-waveform.csv"):
        if (output / ("rtl-" + suffix)).read_bytes() != (output / ("gate-" + suffix)).read_bytes():
            raise RuntimeError(f"RTL and synthesized traces differ: {suffix}")
    rows = read_rows(output / "rtl-fixture.csv")
    waveform_rows = read_rows(output / "rtl-fixture-waveform.csv")
    design = json.loads((output / "netlist.json").read_text())
    exported = export(design, rows, waveform_rows)
    (output / "gpu-netlist.json").write_text(json.dumps(exported, separators=(",", ":")) + "\n")
    samples = [{**{key: int(value) for key, value in row.items()},
                "valid": not (int(row["cycle"]) == 1 and int(row["half"]) == 0)} for row in waveform_rows]
    (output / "waveform-reference.json").write_text(json.dumps({"name": TOP, "schema": 1,
        "cycleConvention": "one-based; half=0 low before edge; half=1 high after commit",
        "samples": samples}, indent=2) + "\n")
    summary = {key: exported[key] for key in ("name", "schema", "nodeCount", "textureWidth", "maxDepth",
               "settlePasses", "registerCount", "vectorCount", "cellCounts")}
    summary.update({"moduleCount": len(exported["modules"]), "signalCount": len(exported["signals"]),
                    "hierarchyPreserved": True, "fullAdderInstanceCount": 8, "completeNodeOwnership": True,
                    "rtlPassed": True, "synthesizedPassed": True, "tracesEqual": True,
                    "interpreterOutputsEqualRTL": True, "interpreterLowHighEqualRTL": True,
                    "allNodeReferenceGenerated": True, "waveformSamples": len(samples),
                    "aluVectorsPerImplementation": 327680, "adderVectorsPerImplementation": 131072,
                    "controlVectorsPerImplementation": 32, "pcVectorsPerImplementation": 256,
                    "cpuVectorsPerImplementation": len(read_rows(output / "rtl-cpu.csv")),
                    "terminalText": "HELLO\n", "terminalTextBytes": 6, "repeatedCharacterEventsPassed": True,
                    "instructionEncoding": {"LDI": 0, "ADDI": 1, "ANDI": 2, "ORI": 3, "XORI": 4,
                                            "OUT": 5, "JMP": 6, "JZ": 7, "HALT": 15}})
    (output / "test-report.json").write_text(json.dumps(summary, indent=2) + "\n")
    print(json.dumps(summary, indent=2))
    print("Export:", output / "gpu-netlist.json")


if __name__ == "__main__":
    main()
