#!/usr/bin/env python3
"""Refresh exported program metadata after catalog-only edits, without rerunning CPU simulations."""

from __future__ import annotations

import argparse
import json
from pathlib import Path
import sys

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "tools"))
from teaching_cpu_assembler import ISA, load_programs  # noqa: E402
from verify_teaching_cpu_programs import verify_program  # noqa: E402


def refresh(directory: Path) -> None:
    netlist_path = directory / "gpu-netlist.json"
    report_path = directory / "test-report.json"
    netlist = json.loads(netlist_path.read_text(encoding="utf-8"))
    report = json.loads(report_path.read_text(encoding="utf-8"))
    programs = load_programs()
    exported = netlist["programs"]
    if len(exported) != len(programs) or len(report.get("programRuns", [])) != len(programs):
        raise AssertionError("The verified export and current JSON catalog have different program counts")
    by_id = {item["id"]: item for item in report["programRuns"]}
    by_export = {item["id"]: item for item in exported}
    if len(by_id) != len(programs) or len(by_export) != len(programs):
        raise AssertionError("Program IDs are not unique in the verified export")

    for program in programs:
        record = by_export[program.id]
        run = by_id[program.id]
        if record["words"] != program.words or record["initialRam"] != program.initial_ram:
            raise AssertionError(f"{program.id}: catalog update changed verified ROM or initial RAM")
        if run["finalRam"] is None:
            raise AssertionError(f"{program.id}: verified run has no final RAM")
        verify_program(program, run["outputBytes"], run["finalRam"])
        if program.expected_text and run["outputText"] != program.expected_text:
            raise AssertionError(f"{program.id}: verified output no longer matches catalog text")
        if program.expected_bytes is not None and run["outputBytes"] != program.expected_bytes:
            raise AssertionError(f"{program.id}: verified output no longer matches expectedBytes")

        record.update({
            "name": program.name,
            "description": program.description,
            "source": program.source,
            "expectedText": program.expected_text,
            "nameTranslations": program.name_translations,
            "descriptionTranslations": program.description_translations,
            "expectedTextTranslations": program.expected_text_translations,
            "sourceComment": program.source_comment,
            "sourceCommentTranslations": program.source_comment_translations,
            "romWordCount": run["romWords"],
        })
        if program.expected_bytes is not None:
            record["expectedBytes"] = program.expected_bytes
        else:
            record.pop("expectedBytes", None)
        if program.display is not None:
            record["display"] = program.display
        else:
            record.pop("display", None)
        for key in ("digits", "frameCount", "oracle", "inputValues", "neighborMasks"):
            if program.metadata and key in program.metadata:
                record[key] = program.metadata[key]
            else:
                record.pop(key, None)

    netlist["isa"] = ISA
    netlist["programs"] = exported
    (directory / "gpu-netlist.json").write_text(json.dumps(netlist, separators=(",", ":")) + "\n", encoding="utf-8")
    (directory / "program-catalog.json").write_text(json.dumps(exported, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    (directory / "isa.json").write_text(json.dumps(ISA, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")

    pi_runs = []
    for digits in (64, 100):
        run = by_id[f"pi-spigot-{digits}"]
        pi_runs.append({"digits": digits, "algorithm": "Rabinowitz-Wagon integer spigot",
                        "romWords": run["romWords"], "runCycles": run["runCycles"],
                        "cyclesIncludingReset": run["cyclesIncludingReset"],
                        "outputBytes": len(run["outputBytes"]), "outputText": run["outputText"],
                        "estimatedHoursAt256Hz": run["cyclesIncludingReset"] / 256 / 3600,
                        "referenceOracle": "independent Chudnovsky Decimal computation"})
    report["piSpigotRuns"] = pi_runs
    report["independentProgramOraclesPassed"] = True
    (report_path).write_text(json.dumps(report, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    print(f"Refreshed {len(programs)} program records; verified ROM/RAM images stayed identical.")


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--data", type=Path, required=True)
    options = parser.parse_args()
    refresh(options.data.resolve())


if __name__ == "__main__":
    main()
