#!/usr/bin/env python3
"""Verify the gate-level teaching CPU and export a schema-3 GPU fixture."""

from __future__ import annotations

import argparse
import collections
import csv
import json
import os
from pathlib import Path
import subprocess

from teaching_cpu_assembler import CpuReference, ISA, Program, load_programs
from verify_teaching_cpu_programs import verify_program
from build_verilog_sources import export_sources


ROOT = Path(__file__).resolve().parents[1]
TOP = "teaching_cpu"
GATE_OPS = {"CONST0": 0, "CONST1": 1, "INPUT": 2, "DFF": 3,
            "BUF": 4, "NOT": 5, "AND": 6, "OR": 7, "XOR": 8,
            "XNOR": 9, "MUX": 10, "NAND": 11, "NOR": 12,
            "ANDNOT": 13, "ORNOT": 14}
DEBUG_PORTS = ("pc", "instruction", "phase", "acc", "x", "sp", "out", "zero",
               "carry", "halt", "terminal_data", "terminal_write", "terminal_sequence", "bank")
TRACE_COLUMNS = ("pc", "instruction", "phase", "acc", "x", "sp", "out", "bank", "zero",
                 "carry", "halt", "terminal_data", "terminal_write", "terminal_sequence",
                 "rom_address", "rom_data", "ram_read_address", "ram_read_data",
                 "ram_write_address", "ram_write_data", "ram_write_enable")
WAVEFORM_COLUMNS = ("cycle", "half", "clk", *TRACE_COLUMNS)


def run(command: list[str], directory: Path, log_path: Path) -> str:
    result = subprocess.run(command, cwd=directory, text=True, stdout=subprocess.PIPE,
                            stderr=subprocess.STDOUT,
                            env={**os.environ, "TMPDIR": str(directory)})
    log_path.write_text(result.stdout)
    if result.returncode:
        raise RuntimeError(f"Failed: {' '.join(command)}\n{result.stdout[-12000:]}")
    return result.stdout


def write_program_inputs(directory: Path, program: Program) -> tuple[Path, Path]:
    program_path = directory / "active-program.hex"
    ram_path = directory / "active-initial-ram.hex"
    program_path.write_text("".join(f"{word:03x}\n" for word in program.words))
    ram_path.write_text("".join(f"{byte:02x}\n" for byte in program.initial_ram))
    return program_path, ram_path


def reset_plusargs(reset_indices: list[int]) -> list[str]:
    """Pass the sparse reset schedule without allocating a cycle-sized HDL array."""
    if len(reset_indices) > 2:
        raise ValueError("the teaching CPU testbench supports at most two reset pulses")
    slots = reset_indices + [-1] * (2 - len(reset_indices))
    return [f"+reset_cycle0={slots[0]}", f"+reset_cycle1={slots[1]}"]


def reference_run(program: Program, *, inject_reset_on_store: bool = False,
                  max_cycles: int = 200000, collect_rows: bool = True
                  ) -> tuple[list[dict[str, int]], CpuReference, list[int], int | None, int, list[int]]:
    cpu = CpuReference(program)
    rows: list[dict[str, int]] = []
    reset_indices: list[int] = []
    terminal_bytes: list[int] = []
    injected = False
    reset_write_attempt_cycle: int | None = None
    for cycle in range(max_cycles):
        reset = int(cycle == 0)
        if inject_reset_on_store and not injected and cycle > 0 and cpu.phase == 1:
            opcode, operand = cpu.instruction >> 8, cpu.instruction & 0xFF
            if opcode == 9 and operand == 32:
                before = cpu.ram[32]
                reset = 1
                injected = True
                reset_write_attempt_cycle = cycle
        if reset:
            reset_indices.append(cycle)
        terminal_write = bool(cpu.phase == 1 and not cpu.halt and cpu.instruction >> 8 == 5 and not reset)
        state = cpu.step(reset, capture_state=collect_rows)
        if terminal_write:
            terminal_bytes.append(cpu.out)
        if reset_write_attempt_cycle == cycle and cpu.ram[32] != before:
            raise AssertionError("CPU reset changed RAM during an attempted store")
        if collect_rows:
            rows.append({"cycle": cycle, "reset": reset, **state, **cpu.buses(reset)})
        if cpu.halt:
            cycles_including_reset = cycle + 1
            break
    else:
        if not inject_reset_on_store:
            # Deliberately running programs are captured as a small bounded fixture.
            cycles_including_reset = max_cycles
        else:
            raise RuntimeError(f"{program.id}: reset-injected program did not halt within {max_cycles} cycles")
    if inject_reset_on_store and not injected:
        raise RuntimeError("No pending RAM store was found for reset cancellation check")
    return rows, cpu, reset_indices, reset_write_attempt_cycle, cycles_including_reset, terminal_bytes


def parse_trace(path: Path) -> list[dict[str, int]]:
    with path.open() as stream:
        return [{key: int(value) for key, value in row.items()} for row in csv.DictReader(stream)]


def parse_waveform(path: Path) -> list[dict[str, str]]:
    with path.open() as stream:
        reader = csv.DictReader(stream)
        if tuple(reader.fieldnames or ()) != WAVEFORM_COLUMNS:
            raise AssertionError(f"{path.name}: malformed waveform header {reader.fieldnames}")
        rows = list(reader)
    if any(None in row or any(value is None for value in row.values()) for row in rows):
        raise AssertionError(f"{path.name}: waveform rows do not match the header")
    return rows


def build_waveform_reference(rtl_wave_path: Path, gate_wave_path: Path, program: Program,
                             reset_indices: list[int], reference_rows: list[dict[str, int]]) -> list[dict]:
    rtl_rows, gate_rows = parse_waveform(rtl_wave_path), parse_waveform(gate_wave_path)
    expected_count = len(reference_rows) * 2
    if len(rtl_rows) != expected_count or len(gate_rows) != expected_count:
        raise AssertionError(f"Waveform must have two actual RTL/gate phases per cycle: {len(rtl_rows)}/{len(gate_rows)} != {expected_count}")

    model = CpuReference(program)
    reset_set = set(reset_indices)
    samples: list[dict] = []
    for sample_index, (rtl_row, gate_row) in enumerate(zip(rtl_rows, gate_rows)):
        source_cycle = sample_index // 2
        half = sample_index & 1
        valid = not (source_cycle == 0 and half == 0)
        values: dict[str, int] = {}
        for key in WAVEFORM_COLUMNS:
            raw = rtl_row[key].strip()
            if any(char in raw.lower() for char in ("x", "z", "?")):
                if valid:
                    raise AssertionError(f"RTL waveform has an unresolved value at sample {sample_index} {key}={raw}")
                values[key] = 0
                continue
            values[key] = int(raw, 10)
        gate_values: dict[str, int] = {}
        for key in WAVEFORM_COLUMNS:
            raw = gate_row[key].strip()
            if any(char in raw.lower() for char in ("x", "z", "?")):
                if valid:
                    raise AssertionError(f"Gate waveform has an unresolved value at sample {sample_index} {key}={raw}")
                gate_values[key] = 0
            else:
                gate_values[key] = int(raw, 10)

        if values["cycle"] != source_cycle or gate_values["cycle"] != source_cycle:
            raise AssertionError(f"Waveform source-cycle sequence is invalid at sample {sample_index}")
        if values["half"] != half or gate_values["half"] != half:
            raise AssertionError(f"Waveform half-cycle order is invalid at sample {sample_index}")
        if values["clk"] != half or gate_values["clk"] != half:
            raise AssertionError(f"Waveform clock level is invalid at sample {sample_index}")
        if values != gate_values:
            changed = next(key for key in WAVEFORM_COLUMNS if values[key] != gate_values[key])
            raise AssertionError(f"RTL and synthesized waveforms differ at sample {sample_index} {changed}")

        if half == 0:
            if valid:
                expected = {**model.state(), **model.buses(int(source_cycle in reset_set))}
                for key in TRACE_COLUMNS:
                    if values[key] != expected[key]:
                        raise AssertionError(f"RTL low-phase waveform differs from reference at cycle {source_cycle} {key}: {values[key]} != {expected[key]}")
        else:
            reset = int(source_cycle in reset_set)
            state = model.step(reset)
            expected = {**state, **model.buses(reset)}
            for key in TRACE_COLUMNS:
                if values[key] != expected[key] or values[key] != reference_rows[source_cycle][key]:
                    raise AssertionError(f"RTL high-phase waveform differs from reference trace at cycle {source_cycle} {key}")

        sample = {"cycle": source_cycle + 1, "half": half, "clk": half,
                  **{key: values[key] for key in TRACE_COLUMNS}, "valid": valid}
        samples.append(sample)
    return samples


def check_trace(actual: list[dict[str, int]], expected: list[dict[str, int]], label: str) -> None:
    if len(actual) != len(expected):
        raise AssertionError(f"{label}: vector count differs {len(actual)} != {len(expected)}")
    keys = ("cycle", "reset", *TRACE_COLUMNS)
    for index, (got, want) in enumerate(zip(actual, expected)):
        for key in keys:
            if got[key] != want[key]:
                raise AssertionError(f"{label}: cycle {index} {key} differs {got[key]} != {want[key]}")


FINAL_STATE_FIELDS = ("pc", "instruction", "phase", "acc", "x", "sp", "bank", "out",
                      "zero", "carry", "halt", "terminal_data", "terminal_sequence")


def read_final_state(path: Path) -> dict[str, int]:
    values = [int(value) for value in path.read_text().strip().split(",")]
    if len(values) != len(FINAL_STATE_FIELDS):
        raise AssertionError(f"Malformed final CPU state in {path}")
    return dict(zip(FINAL_STATE_FIELDS, values))


class Aliases:
    """Union hierarchy port aliases before assigning global signal IDs."""

    def __init__(self):
        self.parents: dict[object, object] = {"0": "0", "1": "1"}

    def add(self, key):
        self.parents.setdefault(key, key)
        return key

    def find(self, key):
        parent = self.parents[key]
        if parent != key:
            self.parents[key] = self.find(parent)
        return self.parents[key]

    def union(self, left, right):
        left, right = self.find(left), self.find(right)
        if left == right:
            return
        if left in ("0", "1") and right in ("0", "1"):
            raise ValueError("A hierarchy port shorts CONST0 to CONST1")
        if right in ("0", "1"):
            left, right = right, left
        self.parents[right] = left


def export_netlist(design: dict, programs: list[Program], fixture_rows: list[dict[str, int]],
                   fixture_resets: list[int], fixture_program_index: int,
                   fixture_ram_reset: list[bool], validated_runs: list[dict],
                   reset_write_attempt_cycle: int | None) -> dict:
    definitions = design["modules"]
    aliases = Aliases()
    instances: list[dict] = []
    primitive_cells: list[tuple[int, str, dict]] = []

    def local_key(module_id: int, bit):
        if bit in ("0", "1"):
            return bit
        if not isinstance(bit, int):
            raise ValueError(f"Unresolved four-state signal {bit!r} in module {module_id}")
        return aliases.add((module_id, bit))

    def instantiate(kind: str, name: str, parent_id: int, path: str, connections=None) -> int:
        definition = definitions[kind]
        module_id = len(instances)
        instance = {"id": module_id, "parentId": parent_id, "path": path, "type": kind,
                    "name": name, "children": [], "nodes": [], "definition": definition}
        instances.append(instance)
        for port in definition["ports"].values():
            for bit in port["bits"]:
                local_key(module_id, bit)
        for net in definition.get("netnames", {}).values():
            for bit in net["bits"]:
                local_key(module_id, bit)
        for cell in definition.get("cells", {}).values():
            for bits in cell["connections"].values():
                for bit in bits:
                    local_key(module_id, bit)
        if connections is not None:
            for port_name, port in definition["ports"].items():
                values = connections.get(port_name)
                if values is None or len(values) != len(port["bits"]):
                    raise ValueError(f"Port width mismatch at {path}/{port_name}")
                for child_bit, parent_bit in zip(port["bits"], values):
                    aliases.union(local_key(module_id, child_bit), local_key(parent_id, parent_bit))
        for cell_name, cell in sorted(definition.get("cells", {}).items()):
            if cell["type"] in definitions:
                child_id = instantiate(cell["type"], cell_name, module_id,
                                       path + "/" + cell_name, cell["connections"])
                instance["children"].append(child_id)
            else:
                primitive_cells.append((module_id, cell_name, cell))
        return module_id

    instantiate(TOP, TOP, -1, TOP)
    roots = {aliases.find(key) for key in aliases.parents}
    ids: dict[object, int] = {"0": 0, "1": 1}
    for key in sorted(roots - {"0", "1"}):
        ids[key] = len(ids)

    def signal_id(module_id: int, bit) -> int:
        return ids[aliases.find(local_key(module_id, bit))]

    nodes: list[dict | None] = [None] * len(ids)
    nodes[0] = {"id": 0, "op": 0, "a": 0, "b": 0, "c": 0, "name": "CONST0", "moduleId": 0}
    nodes[1] = {"id": 1, "op": 1, "a": 0, "b": 0, "c": 0, "name": "CONST1", "moduleId": 0}
    root_ports = definitions[TOP]["ports"]
    ports = {name: [signal_id(0, bit) for bit in port["bits"]]
             for name, port in root_ports.items()}
    input_offsets: dict[str, int] = {}
    input_offset = 0
    for name in ("clk", "reset"):
        input_offsets[name] = input_offset
        for bit_index, node_id in enumerate(ports[name]):
            if nodes[node_id] is not None:
                raise ValueError(f"Top input {name} bit {bit_index} already has a driver")
            nodes[node_id] = {"id": node_id, "op": 2, "a": input_offset, "b": 0, "c": 0,
                              "name": f"{TOP}/{name}", "moduleId": 0}
            input_offset += 1
    if input_offset > 32:
        raise ValueError("Input masks support at most 32 input bits")

    cell_counts = collections.Counter()
    dff_inputs: dict[int, int] = {}
    for module_id, cell_name, cell in primitive_cells:
        kind, conn = cell["type"], cell["connections"]
        cell_counts[kind] += 1
        if kind == "$_DFF_P_":
            if len(conn["C"]) != 1 or signal_id(module_id, conn["C"][0]) != ports["clk"][0]:
                raise ValueError("Only the top-level rising-edge clock is supported")
            node_id = signal_id(module_id, conn["Q"][0])
            op, a, b, c = 3, signal_id(module_id, conn["D"][0]), 0, 0
            dff_inputs[node_id] = a
        else:
            name = kind.removeprefix("$_").removesuffix("_")
            if not kind.startswith("$_") or name not in GATE_OPS or GATE_OPS[name] <= 3:
                raise ValueError(f"Unsupported synthesized cell {kind} at {instances[module_id]['path']}/{cell_name}")
            if any(len(bits) != 1 for bits in conn.values()):
                raise ValueError(f"Primitive {kind} has a nonscalar connection")
            node_id, op = signal_id(module_id, conn["Y"][0]), GATE_OPS[name]
            a = signal_id(module_id, conn["A"][0])
            b = signal_id(module_id, conn["B"][0]) if "B" in conn else 0
            c = signal_id(module_id, conn["S"][0]) if "S" in conn else 0
        if nodes[node_id] is not None:
            raise ValueError(f"Multiple drivers for signal {node_id}")
        nodes[node_id] = {"id": node_id, "op": op, "a": a, "b": b, "c": c,
                          "name": instances[module_id]["path"] + "/" + cell_name,
                          "moduleId": module_id}

    memory_ports: dict[str, list[int] | int] = {}
    for instance in instances:
        kind, module_id, path = instance["type"], instance["id"], instance["path"]
        if kind not in ("teaching_rom", "teaching_ram"):
            continue
        definition = definitions[kind]
        bit_ports = {name: [signal_id(module_id, bit) for bit in port["bits"]]
                     for name, port in definition["ports"].items()}
        instance["ports"] = [{"name": name, "direction": port["direction"],
                               "signals": bit_ports[name]}
                              for name, port in definition["ports"].items()]
        if kind == "teaching_rom":
            address = bit_ports["address"]
            data = bit_ports["data"]
            if len(address) != 9 or len(data) != 12:
                raise ValueError("ROM must expose 9 address and 12 data bits")
            memory_ports["romAddress"] = address
            memory_ports["romData"] = data
            for bit_index, node_id in enumerate(data):
                if nodes[node_id] is not None:
                    raise ValueError(f"ROM output bit {bit_index} already has a driver")
                nodes[node_id] = {"id": node_id, "op": 15, "a": bit_index, "b": 0, "c": 0,
                                  "name": f"{path}/data[{bit_index}]", "moduleId": module_id}
        else:
            read_address = bit_ports["read_address"]
            read_data = bit_ports["read_data"]
            write_address = bit_ports["write_address"]
            write_data = bit_ports["write_data"]
            write_enable = bit_ports["write_enable"]
            if (len(read_address) != 10 or len(read_data) != 8 or len(write_address) != 10 or
                    len(write_data) != 8 or len(write_enable) != 1):
                raise ValueError("RAM ports do not match the 1024 x 8 memory contract")
            memory_ports["ramReadAddress"] = read_address
            memory_ports["ramReadData"] = read_data
            memory_ports["ramWriteAddress"] = write_address
            memory_ports["ramWriteData"] = write_data
            memory_ports["ramWriteEnable"] = write_enable[0]
            for bit_index, node_id in enumerate(read_data):
                if nodes[node_id] is not None:
                    raise ValueError(f"RAM output bit {bit_index} already has a driver")
                nodes[node_id] = {"id": node_id, "op": 16, "a": bit_index, "b": 0, "c": 0,
                                  "name": f"{path}/read_data[{bit_index}]", "moduleId": module_id}

    required_memory_fields = {"romAddress", "romData", "ramReadAddress", "ramReadData",
                              "ramWriteAddress", "ramWriteData", "ramWriteEnable"}
    if set(memory_ports) != required_memory_fields:
        raise ValueError(f"Missing memory ports: {required_memory_fields - set(memory_ports)}")

    signals: dict[str, list[int]] = {}
    names_by_node: dict[int, list[tuple[int, bool, str]]] = collections.defaultdict(list)
    for instance in instances:
        module_id, definition, path = instance["id"], instance["definition"], instance["path"]
        instance["ports"] = instance.get("ports") or [
            {"name": name, "direction": port["direction"],
             "signals": [signal_id(module_id, bit) for bit in port["bits"]]}
            for name, port in definition["ports"].items()]
        for name, net in definition.get("netnames", {}).items():
            net_ids = [signal_id(module_id, bit) for bit in net["bits"]]
            signals[path + "/" + name] = net_ids
            for bit_index, node_id in enumerate(net_ids):
                suffix = f"[{bit_index + net.get('offset', 0)}]" if len(net_ids) > 1 else ""
                names_by_node[node_id].append((module_id, "$" in name, path + "/" + name + suffix))
        del instance["definition"]

    for node in nodes:
        if node is None:
            raise ValueError("Undriven retained net; refusing to create a fictitious signal")
        module_id = node["moduleId"]
        instances[module_id]["nodes"].append(node["id"])
        candidates = [entry for entry in names_by_node[node["id"]] if entry[0] == module_id]
        if candidates and node["op"] > 1:
            node["name"] = min(candidates, key=lambda entry: (entry[1], len(entry[2]), entry[2]))[2]
    if sorted(node for instance in instances for node in instance["nodes"]) != list(range(len(nodes))):
        raise ValueError("Every global signal must have exactly one owning module")

    def parents(node: dict) -> list[int]:
        op = node["op"]
        if op in (15, 16):
            return memory_ports["romAddress"] if op == 15 else memory_ports["ramReadAddress"]
        if op <= 3:
            return []
        result = [node["a"]]
        if op in (6, 7, 8, 9, 10, 11, 12, 13, 14):
            result.append(node["b"])
        if op == 10:
            result.append(node["c"])
        return result

    depths: dict[int, int] = {}
    visiting: set[int] = set()

    def depth(node_id: int) -> int:
        if node_id in depths:
            return depths[node_id]
        if node_id in visiting:
            raise ValueError("Combinational loop in synthesized netlist")
        visiting.add(node_id)
        deps = parents(nodes[node_id])
        depths[node_id] = 0 if not deps else 1 + max(depth(parent) for parent in deps)
        visiting.remove(node_id)
        return depths[node_id]

    for node in nodes:
        depth(node["id"])
    ordered = sorted(nodes, key=lambda node: (depths[node["id"]], node["id"]))
    for node in nodes:
        node["level"] = depths[node["id"]]

    def port_value(values: list[int], ids_for_port: list[int]) -> int:
        return sum(values[signal] << bit for bit, signal in enumerate(ids_for_port))

    def evaluate(mask: int, values: list[int], rom_words: list[int], ram_bytes: list[int]) -> None:
        for node in ordered:
            node_id, op, a, b, c = (node[key] for key in ("id", "op", "a", "b", "c"))
            if op == 0:
                values[node_id] = 0
            elif op == 1:
                values[node_id] = 1
            elif op == 2:
                values[node_id] = (mask >> a) & 1
            elif op == 3:
                continue
            elif op == 15:
                address = port_value(values, memory_ports["romAddress"])
                values[node_id] = (rom_words[address] >> a) & 1
            elif op == 16:
                address = port_value(values, memory_ports["ramReadAddress"])
                values[node_id] = (ram_bytes[address] >> a) & 1
            elif op == 4:
                values[node_id] = values[a]
            elif op == 5:
                values[node_id] = 1 - values[a]
            elif op == 6:
                values[node_id] = values[a] & values[b]
            elif op == 7:
                values[node_id] = values[a] | values[b]
            elif op == 8:
                values[node_id] = values[a] ^ values[b]
            elif op == 9:
                values[node_id] = 1 - (values[a] ^ values[b])
            elif op == 10:
                values[node_id] = values[b] if values[c] else values[a]
            elif op == 11:
                values[node_id] = 1 - (values[a] & values[b])
            elif op == 12:
                values[node_id] = 1 - (values[a] | values[b])
            elif op == 13:
                values[node_id] = values[a] & (1 - values[b])
            elif op == 14:
                values[node_id] = values[a] | (1 - values[b])
            else:
                raise ValueError(f"No evaluator for node op={op}")

    clk_offset, reset_offset = input_offsets["clk"], input_offsets["reset"]
    input_masks: list[int] = []
    expected_states: list[int] = []
    register_ids = sorted(dff_inputs)
    values = [0] * len(nodes)
    ram = list(programs[fixture_program_index].initial_ram)
    fixture_program_ids = [fixture_program_index] * len(fixture_rows)
    fixture_ram_reset = list(fixture_ram_reset)
    reset_set = set(fixture_resets)
    for index, row in enumerate(fixture_rows):
        reset = int(index in reset_set)
        program = programs[fixture_program_ids[index]]
        if fixture_ram_reset[index]:
            ram = list(program.initial_ram)
        low_mask = (0 << clk_offset) | (reset << reset_offset)
        evaluate(low_mask, values, program.words, ram)
        next_registers = {node_id: values[dff_inputs[node_id]] for node_id in register_ids}
        write_enable = values[memory_ports["ramWriteEnable"]]
        write_address = port_value(values, memory_ports["ramWriteAddress"])
        write_data = port_value(values, memory_ports["ramWriteData"])
        if write_enable:
            ram[write_address] = write_data
        for node_id, value in next_registers.items():
            values[node_id] = value
        mask = (1 << clk_offset) | (reset << reset_offset)
        evaluate(mask, values, program.words, ram)
        for name in DEBUG_PORTS:
            actual = port_value(values, ports[name])
            if actual != row[name]:
                raise AssertionError(f"Netlist interpreter differs from reference at vector {index} {name}: {actual} != {row[name]}")
        if ram != programs[fixture_program_index].initial_ram and index == 0 and fixture_ram_reset[index]:
            # Initial stores can happen on the first rising edge only if the
            # reset gate is broken; reset takes priority over the RAM write.
            if reset and values[memory_ports["ramWriteEnable"]]:
                raise AssertionError("Reset vector enabled a RAM write")
        if ram != _reference_ram_at(program, fixture_rows, index, fixture_resets):
            raise AssertionError(f"Netlist interpreter RAM differs from reference at vector {index}")
        input_masks.append(mask)
        expected_states.extend(values)

    max_depth = max(depths.values())
    dff_ids = sorted(dff_inputs)
    exported_programs = []
    for index, program in enumerate(programs):
        verified = validated_runs[index]
        record = {"id": program.id, "name": program.name, "description": program.description,
                  "source": program.source,
                  "words": program.words, "initialRam": program.initial_ram,
                  "expectedText": program.expected_text,
                  "nameTranslations": program.name_translations,
                  "descriptionTranslations": program.description_translations,
                  "expectedTextTranslations": program.expected_text_translations,
                  "sourceComment": program.source_comment,
                  "sourceCommentTranslations": program.source_comment_translations}
        if program.expected_bytes is not None:
            record["expectedBytes"] = program.expected_bytes
        if program.display is not None:
            record["display"] = program.display
        for key in ("digits", "frameCount", "oracle", "inputValues", "neighborMasks"):
            if program.metadata and key in program.metadata:
                record[key] = program.metadata[key]
        record["romWordCount"] = max((i + 1 for i, word in enumerate(program.words) if word), default=0)
        if verified["halted"]:
            record["expectedTerminalBytes"] = verified["terminalBytes"]
            record["expectedTerminalSequence"] = verified["terminalSequence"]
            record["expectedCycles"] = verified["cycles"]
            record["expectedCyclesIncludingReset"] = verified["cyclesIncludingReset"]
            record["expectedFinalRam"] = verified["finalRam"]
        else:
            record["terminalSampleBytes"] = verified["terminalBytes"]
        exported_programs.append(record)

    default_program_index = next(i for i, program in enumerate(programs) if program.id == "pi-355-113")
    return {"name": TOP, "schema": 3, "nodes": nodes, "nodeCount": len(nodes),
            "textureWidth": 1 << (len(nodes) - 1).bit_length(), "maxDepth": max_depth,
            "settlePasses": max_depth + 1, "registerCount": len(dff_ids),
            "ports": ports, "inputOffsets": input_offsets, "dffSignals": dff_ids,
            "outputWriteSignal": ports["terminal_write"][0], "outSignals": ports["out"],
            "terminalSequenceSignals": ports["terminal_sequence"],
            "phaseSignal": ports["phase"][0], "phaseSignals": ports["phase"],
            "xSignals": ports["x"], "spSignals": ports["sp"],
            "bankSignals": ports["bank"],
            "romAddressSignals": memory_ports["romAddress"],
            "ramReadAddressSignals": memory_ports["ramReadAddress"],
            "ramWriteAddressSignals": memory_ports["ramWriteAddress"],
            "ramWriteDataSignals": memory_ports["ramWriteData"],
            "ramWriteEnableSignal": memory_ports["ramWriteEnable"],
            "memoryPorts": memory_ports, "programs": exported_programs, "isa": ISA,
            "defaultProgramIndex": default_program_index, "defaultProgramId": programs[default_program_index].id,
            "modules": instances, "signals": signals, "inputMasks": input_masks,
            "expectedStates": expected_states, "vectorCount": len(fixture_rows),
            "fixturePrograms": fixture_program_ids, "fixtureRamReset": fixture_ram_reset,
            "fixtureRamResetSuppressedStoreCycle": -1 if reset_write_attempt_cycle is None else reset_write_attempt_cycle,
            "cellCounts": dict(sorted(cell_counts.items()))}


def _reference_ram_at(program: Program, rows: list[dict[str, int]], index: int,
                      reset_indices: list[int]) -> list[int]:
    cpu = CpuReference(program)
    reset_set = set(reset_indices)
    for row_index in range(index + 1):
        cpu.step(int(row_index in reset_set))
    return cpu.ram


def validate_hierarchy(exported: dict) -> None:
    by_parent: dict[int, list[dict]] = collections.defaultdict(list)
    for instance in exported["modules"]:
        by_parent[instance["parentId"]].append(instance)
    top = {child["name"]: child["type"] for child in by_parent[0]}
    expected = {"control": "teaching_control", "pc_unit": "teaching_pc",
                "registers": "teaching_registers", "output_unit": "teaching_output",
                "alu": "teaching_alu", "rom": "teaching_rom", "ram": "teaching_ram"}
    if any(top.get(name) != kind for name, kind in expected.items()):
        raise AssertionError(f"Teaching module hierarchy changed: {top}")
    alu_id = next(i["id"] for i in exported["modules"] if i["path"] == "teaching_cpu/alu")
    alu_children = {child["name"]: child["type"] for child in by_parent[alu_id]}
    if alu_children != {"adder": "teaching_adder8", "bit_and": "teaching_and8",
                        "bit_or": "teaching_or8", "bit_xor": "teaching_xor8"}:
        raise AssertionError(f"ALU submodule hierarchy changed: {alu_children}")
    adder_id = next(i["id"] for i in exported["modules"] if i["path"] == "teaching_cpu/alu/adder")
    fa_names = {child["name"] for child in by_parent[adder_id]}
    if fa_names != {f"fa{i}" for i in range(8)}:
        raise AssertionError(f"Eight named full-adder stages were not preserved: {fa_names}")
    for instance in exported["modules"]:
        if "ports" not in instance:
            raise AssertionError(f"Module {instance['path']} omitted synthesized ports")
        for port in instance["ports"]:
            if set(port) != {"name", "direction", "signals"} or not port["signals"]:
                raise AssertionError(f"Malformed module port at {instance['path']}/{port.get('name')}")
            if port["direction"] not in ("input", "output", "inout"):
                raise AssertionError(f"Invalid port direction at {instance['path']}/{port['name']}")
    if not all(isinstance(node.get("level"), int) and node["level"] >= 0 for node in exported["nodes"]):
        raise AssertionError("Some synthesized nodes have no computed combinational depth")
    for op in (15, 16):
        memory_nodes = [node for node in exported["nodes"] if node["op"] == op]
        expected_width = 12 if op == 15 else 8
        if len(memory_nodes) != expected_width:
            raise AssertionError(f"Memory read primitive op={op} has {len(memory_nodes)} outputs")


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path,
                        default=Path.home() / "tmp/vrc-cpu-map/circuit-lab/hdl")
    parser.add_argument("--infinite-cycles", type=int, default=128)
    parser.add_argument("--reuse-existing-runs", action="store_true",
                        help="revalidate saved RTL/netlist run artifacts against the current reference and catalog")
    options = parser.parse_args()
    output = options.output.resolve()
    output.mkdir(parents=True, exist_ok=True)
    rtl = ROOT / "circuits/teaching_cpu.v"
    models = ROOT / "circuits/teaching_memory_models.v"
    programs = load_programs()
    for program in programs:
        if len(program.words) != 512 or len(program.initial_ram) != 1024:
            raise ValueError(f"{program.id} does not match the ROM/RAM dimensions")

    rtl_vvp = output / "rtl-cpu.vvp"
    gate_vvp = output / "gate-cpu.vvp"
    units_vvp = output / "rtl-units.vvp"
    gate_units_vvp = output / "gate-units.vvp"
    run(["iverilog", "-g2012", "-s", "teaching_cpu_tb", "-o", str(rtl_vvp),
         str(rtl), str(models), str(ROOT / "circuits/teaching_cpu_tb.sv")],
        output, output / "rtl-compile.log")
    run(["iverilog", "-g2012", "-s", "teaching_cpu_units_tb", "-o", str(units_vvp),
         str(rtl), str(ROOT / "circuits/teaching_cpu_units_tb.sv")],
        output, output / "rtl-units-compile.log")

    yosys_script = output / "synthesize.ys"
    yosys_script.write_text(f'read_verilog -DSYNTHESIS "{rtl}"\n'
        f'synth -top {TOP} -noabc\ndffunmap\nabc -g simple\nclean\ncheck -assert\nstat\n'
        f'write_json "{output / "netlist.json"}"\n'
        f'write_verilog -noattr "{output / "synthesized.v"}"\n')
    run(["yosys", "-Q", "-T", "-s", str(yosys_script)], output, output / "yosys.log")
    if "teaching_rom rom" not in (output / "synthesized.v").read_text() or \
       "teaching_ram ram" not in (output / "synthesized.v").read_text():
        raise AssertionError("Synthesis did not preserve ROM and RAM primitive boundaries")
    run(["iverilog", "-g2012", "-s", "teaching_cpu_tb", "-o", str(gate_vvp),
         str(output / "synthesized.v"), str(models), str(ROOT / "circuits/teaching_cpu_tb.sv")],
        output, output / "gate-compile.log")
    run(["iverilog", "-g2012", "-s", "teaching_cpu_units_tb", "-o", str(gate_units_vvp),
         str(output / "synthesized.v"), str(ROOT / "circuits/teaching_cpu_units_tb.sv")],
        output, output / "gate-units-compile.log")
    unit_result = run(["vvp", str(units_vvp)], output, output / "rtl-units.log")
    gate_unit_result = run(["vvp", str(gate_units_vvp)], output, output / "gate-units.log")
    if "TEACHING_UNITS_PASS" not in unit_result or "TEACHING_UNITS_PASS" not in gate_unit_result:
        raise AssertionError("Adder vectors failed in RTL or synthesized netlist")

    run_summaries = []
    verified_programs: list[dict] = []
    fixture_program_index = next(i for i, p in enumerate(programs) if p.id == "cpu-fixture")
    traced_program_count = 0
    for program_index, program in enumerate(programs):
        inject = False
        max_cycles = options.infinite_cycles if program.id in {"counter", "blinker", "alternating-55-aa", "step-count"} else 200000
        if program.id == "pi-spigot-64":
            max_cycles = 11_000_000
        elif program.id == "pi-spigot-100":
            max_cycles = 26_000_000
        max_cycles = int((program.metadata or {}).get("validationCycles", max_cycles))
        collect_rows = program.id not in {"life-8x8", "pi-spigot-64", "pi-spigot-100"} and not (program.metadata or {}).get("streamValidation", False)
        rows, final_cpu, reset_indices, reset_attempt_cycle, cycles_including_reset, output_bytes = reference_run(
            program, inject_reset_on_store=inject, max_cycles=max_cycles, collect_rows=collect_rows)
        halted = bool(final_cpu.halt)
        if halted:
            program.expected_cycles = cycles_including_reset - 1
        text = bytes(output_bytes).decode("latin1")
        if program.expected_text and text != program.expected_text:
            raise AssertionError(f"{program.id}: reference output {text!r} != {program.expected_text!r}")
        if program.expected_bytes is not None and output_bytes != program.expected_bytes:
            raise AssertionError(f"{program.id}: reference output bytes differ from expectedBytes")
        verify_program(program, output_bytes, final_cpu.ram)
        program_path, ram_path = write_program_inputs(output, program)

        actual_traces = {}
        actual_final_rams = {}
        actual_terminal = {}
        actual_final_states = {}
        for variant, executable in (("rtl", rtl_vvp), ("gate", gate_vvp)):
            trace_path = output / f"{variant}-{program.id}.csv"
            wave_path = output / f"{variant}-{program.id}-wave.csv"
            final_ram_path = output / f"{variant}-{program.id}-final-ram.hex"
            terminal_path = output / f"{variant}-{program.id}-terminal.bin"
            state_path = output / f"{variant}-{program.id}-final-state.csv"
            log_path = output / f"{variant}-{program.id}.log"
            if options.reuse_existing_runs:
                artifacts = (trace_path, final_ram_path, terminal_path, state_path, log_path)
                if any(not artifact.exists() for artifact in artifacts):
                    raise FileNotFoundError(f"{program.id}: missing saved {variant} validation artifacts")
                result = log_path.read_text()
            else:
                result = run(["vvp", str(executable), f"+program={program_path}", f"+ram={ram_path}",
                              *reset_plusargs(reset_indices), f"+terminal={terminal_path}", f"+state={state_path}",
                              f"+quiet={int(not collect_rows)}", f"+trace={trace_path}",
                              f"+waveform={wave_path}", f"+final_ram={final_ram_path}",
                              f"+cycles={cycles_including_reset}"], output, log_path)
            if "TEACHING_CPU_PASS" not in result:
                raise AssertionError(f"{variant} {program.id}: simulator did not finish")
            if collect_rows:
                trace = parse_trace(trace_path)
                check_trace(trace, rows, f"{variant} {program.id}")
                actual_traces[variant] = trace_path
                traced_program_count += int(variant == "rtl")
            final_ram = [int(line, 16) for line in final_ram_path.read_text().splitlines()]
            if final_ram != final_cpu.ram:
                raise AssertionError(f"{variant} {program.id}: final RAM differs from reference")
            actual_final_rams[variant] = final_ram
            actual_terminal[variant] = list(terminal_path.read_bytes())
            if actual_terminal[variant] != output_bytes:
                raise AssertionError(f"{variant} {program.id}: terminal file differs from reference")
            final_state = read_final_state(state_path)
            expected_state = final_cpu.state()
            for field in FINAL_STATE_FIELDS:
                if final_state[field] != expected_state[field]:
                    raise AssertionError(f"{variant} {program.id}: final state {field} differs: {final_state[field]} != {expected_state[field]}")
            actual_final_states[variant] = final_state
            if inject and reset_attempt_cycle is not None:
                log_text = (output / f"{variant}-{program.id}.log").read_text()
                expected_marker = f"RESET_WRITE_SUPPRESSED cycle={reset_attempt_cycle} raw=1 effective=0"
                if expected_marker not in log_text:
                    raise AssertionError(f"{variant} did not suppress reset-time RAM write")
        if collect_rows and actual_traces["rtl"].read_bytes() != actual_traces["gate"].read_bytes():
            raise AssertionError(f"RTL and synthesized traces differ for {program.id}")
        if actual_final_rams["rtl"] != actual_final_rams["gate"]:
            raise AssertionError(f"RTL and synthesized final RAM differ for {program.id}")
        if actual_terminal["rtl"] != actual_terminal["gate"] or actual_final_states["rtl"] != actual_final_states["gate"]:
            raise AssertionError(f"RTL and synthesized terminal/final state differ for {program.id}")
        verified_programs.append({"cycles": cycles_including_reset - 1,
                                  "cyclesIncludingReset": cycles_including_reset,
                                  "finalRam": actual_final_rams["rtl"],
                                  "terminalBytes": output_bytes,
                                  "terminalSequence": final_cpu.terminal_sequence,
                                  "finalState": actual_final_states["rtl"], "halted": halted,
                                  "traceVerified": collect_rows})

        run_summaries.append({"id": program.id, "cycles": cycles_including_reset,
                              "cyclesIncludingReset": cycles_including_reset,
                              "runCycles": cycles_including_reset - 1,
                              "halted": halted,
                              "outputBytes": output_bytes, "outputText": text,
                              "finalPc": final_cpu.pc, "finalAcc": final_cpu.acc,
                              "finalX": final_cpu.x, "finalSp": final_cpu.sp, "finalBank": final_cpu.bank,
                              "finalRam": list(final_cpu.ram),
                              "romWords": max((i + 1 for i, word in enumerate(program.words) if word), default=0),
                              "algorithm": (program.metadata or {}).get("oracle", ""),
                              "traceVerified": collect_rows,
                              "resetWriteSuppressedCycle": reset_attempt_cycle})

    fixture_program = programs[fixture_program_index]
    fixture_rows, fixture_cpu, fixture_reset_indices, reset_attempt_cycle, fixture_cycles, fixture_terminal = reference_run(
        fixture_program, inject_reset_on_store=True)
    fixture_input_paths = write_program_inputs(output, fixture_program)
    fixture_gate_results = {}
    for variant, executable in (("rtl", rtl_vvp), ("gate", gate_vvp)):
        trace_path = output / f"{variant}-cpu-fixture-reset-test.csv"
        wave_path = output / f"{variant}-cpu-fixture-reset-test-wave.csv"
        final_ram_path = output / f"{variant}-cpu-fixture-reset-test-final-ram.hex"
        terminal_path = output / f"{variant}-cpu-fixture-reset-test-terminal.bin"
        state_path = output / f"{variant}-cpu-fixture-reset-test-final-state.csv"
        result = run(["vvp", str(executable), f"+program={fixture_input_paths[0]}",
                      f"+ram={fixture_input_paths[1]}",
                      *reset_plusargs(fixture_reset_indices), f"+terminal={terminal_path}", f"+state={state_path}",
                      f"+trace={trace_path}", f"+waveform={wave_path}",
                      f"+final_ram={final_ram_path}", f"+cycles={fixture_cycles}"],
                     output, output / f"{variant}-cpu-fixture-reset-test.log")
        if "TEACHING_CPU_PASS" not in result:
            raise AssertionError(f"{variant} reset-injection fixture did not finish")
        check_trace(parse_trace(trace_path), fixture_rows, f"{variant} reset-injection fixture")
        if terminal_path.read_bytes() != bytes(fixture_terminal):
            raise AssertionError(f"{variant} reset-injection terminal bytes differ")
        final_state = read_final_state(state_path)
        expected_state = fixture_cpu.state()
        for field in FINAL_STATE_FIELDS:
            if final_state[field] != expected_state[field]:
                raise AssertionError(f"{variant} reset-injection state {field} differs: {final_state[field]} != {expected_state[field]}")
        log_text = (output / f"{variant}-cpu-fixture-reset-test.log").read_text()
        marker = f"RESET_WRITE_SUPPRESSED cycle={reset_attempt_cycle} raw=1 effective=0"
        if marker not in log_text:
            raise AssertionError(f"{variant} did not suppress reset-time RAM write")
        fixture_final_ram = [int(line, 16) for line in final_ram_path.read_text().splitlines()]
        if fixture_final_ram != fixture_cpu.ram:
            raise AssertionError(f"{variant} reset-injection fixture final RAM differs")
        fixture_gate_results[variant] = (trace_path, fixture_final_ram)
    if fixture_gate_results["rtl"][0].read_bytes() != fixture_gate_results["gate"][0].read_bytes():
        raise AssertionError("RTL and synthesized reset-injection fixture traces differ")
    if fixture_gate_results["rtl"][1] != fixture_gate_results["gate"][1]:
        raise AssertionError("RTL and synthesized reset-injection final RAM differs")
    fixture_ram_reset = [index == 0 for index in range(len(fixture_rows))]
    design = json.loads((output / "netlist.json").read_text())
    exported = export_netlist(design, programs, fixture_rows, fixture_reset_indices,
                              fixture_program_index, fixture_ram_reset, verified_programs,
                              reset_attempt_cycle)
    validate_hierarchy(exported)
    if exported["registerCount"] > len(exported["dffSignals"]):
        raise AssertionError("DFF snapshot export truncated register state")
    (output / "gpu-netlist.json").write_text(json.dumps(exported, separators=(",", ":")) + "\n")
    (output / "verilog-source.json").write_text(json.dumps(export_sources(exported), ensure_ascii=False, indent=2) + "\n")
    (output / "program-catalog.json").write_text(json.dumps(exported["programs"], indent=2, ensure_ascii=False) + "\n")
    (output / "isa.json").write_text(json.dumps(ISA, indent=2, ensure_ascii=False) + "\n")
    waveform_samples = build_waveform_reference(
        output / "rtl-cpu-fixture-reset-test-wave.csv",
        output / "gate-cpu-fixture-reset-test-wave.csv",
        programs[fixture_program_index], fixture_reset_indices, fixture_rows)
    if len(waveform_samples) != 2 * exported["vectorCount"]:
        raise AssertionError("Waveform reference must include low and high phases for every GPU fixture vector")
    (output / "waveform-reference.json").write_text(json.dumps({
        "name": TOP, "schema": 1,
        "cycleConvention": "one-based cycles; half=0 is low before the edge and half=1 is high after commit",
        "source": "independent RTL and synthesized Icarus waveform CSV traces",
        "programIndex": fixture_program_index,
        "samples": waveform_samples}, indent=2) + "\n")

    pi_index = next(i for i, p in enumerate(programs) if p.id == "pi-355-113")
    pi = run_summaries[pi_index]
    if pi["outputText"] != "3.141592\n" or pi["finalRam"][6:9] != [0, 16, 3]:
        raise AssertionError("CPU π program did not calculate quotient 3 and remainder 16")
    pi_spigot_runs = []
    for digits in (64, 100):
        run_record = next(item for item in run_summaries if item["id"] == f"pi-spigot-{digits}")
        pi_spigot_runs.append({"digits": digits, "algorithm": "Rabinowitz-Wagon integer spigot",
                               "romWords": run_record["romWords"], "runCycles": run_record["runCycles"],
                               "cyclesIncludingReset": run_record["cyclesIncludingReset"],
                               "outputBytes": len(run_record["outputBytes"]),
                               "outputText": run_record["outputText"],
                               "estimatedHoursAt256Hz": run_record["cyclesIncludingReset"] / 256 / 3600,
                               "referenceOracle": "independent Chudnovsky Decimal computation"})
    summary = {"name": TOP, "schema": exported["schema"], "nodeCount": exported["nodeCount"],
               "textureWidth": exported["textureWidth"], "maxDepth": exported["maxDepth"],
               "settlePasses": exported["settlePasses"], "registerCount": exported["registerCount"],
               "vectorCount": exported["vectorCount"], "moduleCount": len(exported["modules"]),
               "memoryPortCount": len(exported["memoryPorts"]), "cellCounts": exported["cellCounts"],
               "programCount": len(programs), "programRuns": run_summaries,
               "reusedProgramRunIds": [p.id for p in programs] if options.reuse_existing_runs else [],
               "adderUnitVectors": 131072, "xCountUnitVectors": 2048,
               "piSpigotRuns": pi_spigot_runs, "traceProgramCount": traced_program_count,
               "streamedProgramIds": [item["id"] for item in run_summaries if not item["traceVerified"]],
               "ramAndRomBlackboxesPreserved": True, "rtlPassed": True,
               "synthesizedPassed": True, "tracesEqual": True,
               "architecturalReferenceEqual": True, "netlistInterpreterEqual": True,
               "independentProgramOraclesPassed": True,
               "resetRetainsRam": True, "resetSuppressesPendingStore": True,
               "longAddressAndStackPassed": True,
               "waveformSamples": len(waveform_samples),
               "waveformBothPhasesPassed": True,
               "piOutput": pi["outputText"], "piCycles": pi["runCycles"],
               "piCyclesIncludingReset": pi["cyclesIncludingReset"],
               "piQuotient": pi["finalRam"][8], "piRemainder": pi["finalRam"][7],
               "piTerminalBytes": pi["outputBytes"],
               "hierarchy": ["control", "pc_unit", "registers", "output_unit", "alu", "rom", "ram"]}
    (output / "test-report.json").write_text(json.dumps(summary, indent=2, ensure_ascii=False) + "\n")
    concise = {key: value for key, value in summary.items() if key != "programRuns"}
    print(json.dumps(concise, indent=2, ensure_ascii=False))
    print("Export:", output / "gpu-netlist.json")


if __name__ == "__main__":
    main()
