#!/usr/bin/env python3
"""Bounded, reproducible LCVEX full-RTL frontend and GPU feasibility probes.

Runs in a dedicated sibling worktree. Source files are never changed. Heavy
commands use the repository's global local resource lock. Output is an
elaborated RTL inventory, NOT a full-SoC synthesized gate-count claim.
"""

import argparse
import collections
from datetime import datetime
import hashlib
import json
import os
from pathlib import Path
import re
import signal
import shutil
import subprocess
import sys
import time
from zoneinfo import ZoneInfo

TASK = "T-20261011-vrc-gpu-eval"
MAIN = Path("/home/chiro/projects/mycpu/lcvex")
LOCK = Path("/home/chiro/projects/.resource-locks/resource-lock")
VERILATOR = Path.home() / "miniforge3/envs/lcvex/bin/verilator"


def now():
    return datetime.now(ZoneInfo("Asia/Shanghai")).isoformat(timespec="seconds")


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def walk(value):
    if isinstance(value, dict):
        yield value
        for child in value.values():
            if isinstance(child, (dict, list)):
                yield from walk(child)
    elif isinstance(value, list):
        for child in value:
            yield from walk(child)


def inventory(tree_path, top):
    tree = json.loads(tree_path.read_text())
    nodes = list(walk(tree))
    by_addr = {node["addr"]: node for node in nodes if "addr" in node}
    modules = {node["addr"]: node for node in tree["modulesp"] if node["type"] == "MODULE"}
    widths = {}

    def dimensions(dtype):
        text = dtype.get("range", dtype.get("declRange", ""))
        match = re.search(r"(-?\d+)\s*:\s*(-?\d+)", text)
        return abs(int(match[1]) - int(match[2])) + 1 if match else 1

    def bits(pointer):
        if pointer in widths:
            return widths[pointer]
        dtype = by_addr.get(pointer, {})
        kind = dtype.get("type", "")
        if kind == "BASICDTYPE":
            base = {"byte": 8, "shortint": 16, "int": 32, "integer": 32,
                    "longint": 64, "time": 64, "real": 64, "shortreal": 32}.get(dtype.get("keyword"), 1)
            result = dimensions(dtype) if dtype.get("range") else base
            if dtype.get("keyword") in ("string", "chandle", "event"):
                result = 0
        elif kind in ("UNPACKARRAYDTYPE", "PACKARRAYDTYPE"):
            result = dimensions(dtype) * bits(dtype["refDTypep"])
        elif kind in ("ENUMDTYPE", "REFDTYPE", "MEMBERDTYPE", "CONSTDTYPE"):
            result = bits(dtype.get("refDTypep", ""))
        elif kind in ("STRUCTDTYPE", "UNIONDTYPE"):
            members = [bits(member["refDTypep"]) for member in dtype.get("membersp", [])]
            result = (max(members, default=0) if kind == "UNIONDTYPE" else sum(members))
        else:
            result = 0
        widths[pointer] = result
        return result

    def unpacked(pointer):
        dtype = by_addr.get(pointer, {})
        if dtype.get("type") == "UNPACKARRAYDTYPE":
            return True
        if dtype.get("type") in ("REFDTYPE", "CONSTDTYPE", "PACKARRAYDTYPE"):
            return unpacked(dtype.get("refDTypep", ""))
        return False

    def cells(value, prefixes=()):
        if isinstance(value, dict):
            if value.get("type") == "CELL":
                yield value, prefixes
                return
            if value.get("type") == "GENBLOCK" and value.get("name"):
                prefixes = prefixes + (value["name"],)
            for child in value.values():
                if isinstance(child, (dict, list)):
                    yield from cells(child, prefixes)
        elif isinstance(value, list):
            for child in value:
                yield from cells(child, prefixes)

    instance_rows, arrays, sensitivities = [], [], []

    def visit(module, path, parent):
        local_nodes = list(walk(module.get("stmtsp", [])))
        variables = {node["addr"]: node for node in local_nodes if node.get("type") == "VAR"
                     and not node.get("isParam") and not node.get("lifetime", "").startswith("VAUTOM")
                     and not node.get("name", "").startswith("__V")}
        targets = set()
        for node in local_nodes:
            if node.get("type") == "ASSIGNDLY":
                targets.update(ref["varp"] for ref in walk(node.get("lhsp", []))
                               if ref.get("type") == "VARREF" and ref.get("access") in ("WR", "RW"))
            if node.get("type") == "SENITEM" and node.get("edgeType") in ("POS", "NEG"):
                sensitivities.extend({"path": path, "edge": node["edgeType"], "signal": ref["name"]}
                                     for ref in walk(node.get("sensp", [])) if ref.get("type") == "VARREF")
        array_bits, state_bits = 0, 0
        for addr, var in variables.items():
            width = bits(var.get("dtypep", ""))
            is_array = unpacked(var.get("dtypep", ""))
            if is_array:
                array_bits += width
                arrays.append({"path": path + "/" + var["name"], "bits": width,
                               "nonblockingTarget": addr in targets})
            if addr in targets:
                state_bits += width
        instance_id = len(instance_rows)
        instance_rows.append({"id": instance_id, "parentId": parent, "path": path,
                              "type": module.get("origName", module["name"]),
                              "specialization": module["name"],
                              "declaredUnpackedArrayBits": array_bits,
                              "nonblockingTargetDeclaredBits": state_bits,
                              "ports": [{"name": var["name"], "direction": var["direction"],
                                         "bits": bits(var.get("dtypep", ""))}
                                        for var in module.get("stmtsp", []) if var.get("type") == "VAR"
                                        and var.get("direction") in ("INPUT", "OUTPUT", "INOUT")]})
        for cell, prefixes in cells(module.get("stmtsp", [])):
            child = modules.get(cell.get("modp"))
            if child is None:
                raise ValueError(f"Unresolved real hierarchy cell {path}/{cell['name']}")
            visit(child, "/".join((path, *prefixes, cell["name"])), instance_id)

    root = next(module for module in modules.values() if module.get("origName") == top)
    visit(root, top, -1)
    type_counts = collections.Counter(row["type"] for row in instance_rows)
    port_bits = collections.Counter()
    for port in instance_rows[0]["ports"]:
        port_bits[port["direction"]] += port["bits"]
    return {"top": top, "instanceCount": len(instance_rows), "specializedModuleCount": len(modules),
            "topPortCount": len(instance_rows[0]["ports"]), "topPortBitsByDirection": dict(port_bits),
            "topParameters": {var["name"]: var["valuep"][0].get("name") for var in root.get("stmtsp", [])
                              if var.get("type") == "VAR" and var.get("isGParam") and var.get("valuep")},
            "moduleTypeCounts": dict(sorted(type_counts.items())), "instances": instance_rows,
            "declaredUnpackedArrays": sorted(arrays, key=lambda row: -row["bits"]),
            "declaredUnpackedArrayBits": sum(row["bits"] for row in arrays),
            "nonblockingTargetDeclaredBits": sum(row["nonblockingTargetDeclaredBits"] for row in instance_rows),
            "nonblockingTargetNonArrayDeclaredBits": sum(row["nonblockingTargetDeclaredBits"] for row in instance_rows)
                - sum(row["bits"] for row in arrays if row["nonblockingTarget"]),
            "edgeSensitivitySignals": sorted({(row["edge"], row["signal"]) for row in sensitivities}),
            "note": "Elaborated declarations and NBA target sizes; includes RTL memories and observability state. Not optimized FF/gate counts; unpacked port aliases may be counted more than once."}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--worktree", type=Path, default=MAIN.parent / ("lcvex-wt-" + TASK))
    parser.add_argument("--output", type=Path, default=Path.home() / "tmp/vrc-cpu-map/lcvex-gpu-eval")
    parser.add_argument("--verilator", type=Path, default=VERILATOR)
    options = parser.parse_args()
    worktree, output = options.worktree.resolve(), options.output.resolve()
    if worktree == MAIN or worktree.parent != MAIN.parent or not worktree.name.startswith("lcvex-wt-"):
        raise ValueError("Evaluation must run in its own direct sibling worktree, never the main checkout")
    if not (worktree / ".git").is_file():
        raise ValueError("Expected a linked Git worktree")
    output.mkdir(parents=True, exist_ok=True)
    scratch = output / "tmp"
    scratch.mkdir(exist_ok=True)
    env = {**os.environ, "TMPDIR": str(scratch)}
    records = []

    def run(label, command, heavy=False, timeout=300):
        payload = list(map(str, command))
        actual = ([str(LOCK), "run", "local", "vrc-cpu-map", TASK, "teaching_cpu",
                   "--min-local-available-mib", "4096", "--meta", "parallelism=1",
                   "--meta", "experiment=" + label, "--", *payload] if heavy else payload)
        start, started = time.monotonic(), now()
        process = subprocess.Popen(actual, cwd=worktree, env=env, text=True,
                                   stdout=subprocess.PIPE, stderr=subprocess.STDOUT, start_new_session=True)
        timed_out = False
        try:
            stdout, _ = process.communicate(timeout=timeout)
        except subprocess.TimeoutExpired:
            timed_out = True
            os.killpg(process.pid, signal.SIGTERM)
            try:
                stdout, _ = process.communicate(timeout=5)
            except subprocess.TimeoutExpired:
                os.killpg(process.pid, signal.SIGKILL)
                stdout, _ = process.communicate()
        log = output / (label + ".log")
        log.write_text(stdout)
        record = {"name": label, "command": actual, "cwd": str(worktree), "started_at": started,
                  "finished_at": now(), "wallSeconds": round(time.monotonic() - start, 3),
                  "exitCode": process.returncode, "timeout": timed_out, "log": str(log),
                  "logSha256": sha(log), "resourceLock": "local" if heavy else None}
        records.append(record)
        print(label, "exit=" + str(process.returncode), "seconds=" + str(record["wallSeconds"]), flush=True)
        return record, stdout

    source_sha = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=worktree, text=True).strip()
    status_before = subprocess.check_output(["git", "status", "--porcelain"], cwd=worktree, text=True)
    frontends = {name: shutil.which(name) for name in ("yosys", "sv2v", "slang", "surelog", "iverilog")}
    frontends["verilator"] = str(options.verilator) if options.verilator.exists() else None
    _, yosys_version = run("yosys-version", ["yosys", "-V"])
    _, verilator_version = run("verilator-version", [options.verilator, "--version"])
    plugin_record, _ = run("slang-plugin-probe", ["yosys", "-Q", "-T", "-p", "plugin -i slang; help read_slang"])

    source_files = [line.strip() for line in (worktree / "rtl/filelist.f").read_text().splitlines()
                    if line.strip() and not line.lstrip().startswith("#")]
    board_files = [line.strip() for line in (worktree / "fpga/catapult_a10/tb/filelist_soc.f").read_text().splitlines()
                   if line.strip() and not line.lstrip().startswith("#")]
    input_files = sorted(set(source_files + board_files + ["rtl/filelist.f", "fpga/catapult_a10/tb/filelist_soc.f"]))
    source_hash_before = {name: sha(worktree / name) for name in input_files}
    ys = output / "direct-full-soc.ys"
    ys.write_text("read_verilog -sv -formal " + " ".join(source_files) +
                  "\nhierarchy -check -top lcvex_catapult_soc_top\nstat -json\n")
    native_record, _ = run("direct-full-soc", ["yosys", "-Q", "-T", "-s", ys], heavy=True)

    leaf_json = output / "calibration-gate.json"
    abc_directory = scratch / "abc"
    abc_directory.mkdir(exist_ok=True)
    abc_executable = shutil.which("yosys-abc") or shutil.which("abc")
    if not abc_executable:
        raise ValueError("Expected the Yosys ABC executable")
    abc_wrapper = output / "abc-in-scratch.py"
    abc_wrapper.write_text(f"#!{sys.executable}\nimport os, sys\nos.chdir({str(abc_directory)!r})\n"
                           f"os.execv({abc_executable!r}, [{abc_executable!r}, *sys.argv[1:]])\n")
    abc_wrapper.chmod(0o700)
    leaf_script = output / "calibration-gate.ys"
    leaf_script.write_text("read_verilog -sv rtl/lcvex_calibration_gate.sv\n"
                          "synth -top lcvex_calibration_gate -noabc\ndffunmap\n"
                          f'abc -exe "{abc_wrapper}" -g simple\n'
                          "check -assert\nstat -json\nwrite_json " + str(leaf_json) + "\n")
    leaf_record, _ = run("calibration-gate", ["yosys", "-Q", "-T", "-s", leaf_script], heavy=True)
    leaf_counts = {}
    if leaf_record["exitCode"] == 0:
        leaf = json.loads(leaf_json.read_text())["modules"]["lcvex_calibration_gate"]
        leaf_counts = dict(collections.Counter(cell["type"] for cell in leaf["cells"].values()))

    inventories = {}
    for label, top, filelist, extra in (
        ("full-soc", "lcvex_catapult_soc_top", "rtl/filelist.f", []),
        ("full-fp-board-128m", "lcvex_catapult_soc_tb", "fpga/catapult_a10/tb/filelist_soc.f",
         ["--timing", "-GLINUX_BOOT_TEST=1", "-GA64_FP_SIMD=1", '-GBOOT_HEX_FILE=""'])):
        directory = output / label
        directory.mkdir(exist_ok=True)
        tree_path = directory / "design.tree.json"
        meta_path = directory / "design.meta.json"
        record, stdout = run(label, [options.verilator, "--json-only", "--json-only-output", tree_path,
                            "--json-only-meta-output", meta_path, "--assert", "--stats", "--stats-vars",
                            "-Wno-fatal", "--top-module", top, "-f", filelist, "-Mdir", directory, *extra], heavy=True)
        if record["exitCode"] == 0:
            data = inventory(tree_path, top)
            data["treeSha256"] = sha(tree_path)
            data["treeBytes"] = tree_path.stat().st_size
            data["metaSha256"] = sha(meta_path)
            match = re.search(r"allocated ([\d.]+) MB", stdout)
            data["verilatorAllocatedMB"] = float(match[1]) if match else None
            inventories[label] = data
            (output / (label + "-inventory.json")).write_text(json.dumps(data, indent=2) + "\n")

    board = inventories.get("full-fp-board-128m", {})
    ram = [row for row in board.get("declaredUnpackedArrays", []) if row["path"].endswith("/emif_model/mem")]
    ram_bits = ram[0]["bits"] if ram else None
    required = {"lcvex_core", "lcvex_fp_state", "lcvex_fp_exec", "lcvex_fp_scalar", "lcvex_neon_int",
                "lcvex_mmu", "lcvex_l1_i", "lcvex_l1_d_wb", "lcvex_l2_wb"}
    for name in ("full-soc", "full-fp-board-128m"):
        data = inventories.get(name, {})
        if not required.issubset(data.get("moduleTypeCounts", {})):
            raise ValueError(f"{name} did not retain the complete required CPU/cache hierarchy")
        if data.get("topParameters", {}).get("A64_FP_SIMD") != "1'h1":
            raise ValueError(f"{name} unexpectedly disabled FP/SIMD")
    if ram_bits != 128 * 1024 * 1024 * 8:
        raise ValueError("The actual elaborated DDR functional model must have exactly 128MiB capacity")
    status_after = subprocess.check_output(["git", "status", "--porcelain"], cwd=worktree, text=True)
    report = {"schema": 1, "task": TASK, "sourceSha": source_sha, "worktree": str(worktree),
              "branch": subprocess.check_output(["git", "branch", "--show-current"], cwd=worktree, text=True).strip(),
              "reported_at": now(), "frontends": frontends,
              "toolVersions": {"yosys": yosys_version.strip(), "verilator": verilator_version.strip()},
              "runs": records, "slangAvailable": plugin_record["exitCode"] == 0,
              "nativeFullSoCReadPassed": native_record["exitCode"] == 0,
              "fullHierarchyChecksPassed": True,
              "fullSoCGateCount": None, "calibrationGateLeafCellCounts": leaf_counts,
              "inventories": {name: {key: value for key, value in data.items()
                                      if key not in ("instances", "declaredUnpackedArrays")}
                              for name, data in inventories.items()},
              "ram128MiB": {"elaboratedBits": ram_bits, "bytes": ram_bits // 8 if ram_bits else None,
                             "doubleBufferedRFloatBytes": ram_bits * 8 if ram_bits else None,
                             "doubleBufferedRGBAFloatBytes": ram_bits * 32 if ram_bits else None,
                             "bitPackedBytes": ram_bits // 8 if ram_bits else None},
              "sourceUnchanged": source_hash_before == {name: sha(worktree / name) for name in input_files},
              "worktreeUnchanged": status_before == status_after, "gitStatusBefore": status_before,
              "gitStatusAfter": status_after,
              "sourceFileSha256": source_hash_before,
              "conclusions": {
                  "hierarchyBrowsing": "Real full-FP SoC elaboration provides a browseable RTL instance tree; a complete synthesized gate hierarchy still needs a capable SV synthesis frontend and memory/IP handling.",
                  "bareMetal": "Requires multi-clock edge scheduling, async reset, memory primitives, board bus functional models, image loading and commit/UART checks. Current teaching schema cannot directly execute this SoC.",
                  "linuxBoot": "Not demonstrated on the GPU. Requires real 128MiB functional RAM, interrupts/timer/flash/UART models and long-run differential validation. NoFP board Linux evidence does not validate current full-FP GPU behavior."}}
    (output / "evaluation.json").write_text(json.dumps(report, indent=2) + "\n")
    print(json.dumps({"sourceSha": source_sha, "sourceUnchanged": report["sourceUnchanged"],
                      "nativeFullSoCReadPassed": report["nativeFullSoCReadPassed"],
                      "calibrationGateLeafCellCounts": leaf_counts, "ram128MiB": report["ram128MiB"],
                      "inventories": {name: {key: data[key] for key in ("instanceCount", "topPortCount",
                          "topPortBitsByDirection", "nonblockingTargetNonArrayDeclaredBits", "verilatorAllocatedMB")}
                                      for name, data in inventories.items()}}, indent=2))


if __name__ == "__main__":
    main()
