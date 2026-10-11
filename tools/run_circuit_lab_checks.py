#!/usr/bin/env python3
"""Drive meaningful CircuitLab checks through the real Editor Udon VM."""

import argparse
import json
from pathlib import Path
import time

from deploy_netlist_smoke import pipeline
from unity_remote import DEFAULT_HOST, DEFAULT_PROJECT


def script(options, entry):
    name = entry.split(".", 1)[0]
    result = pipeline(options, "run_script", "--timeout", "65", "--file", f"AgentScripts/{name}.cs", "--entry", entry, "--timeout_ms", "60000")
    if result.get("success") is not True:
        raise RuntimeError(json.dumps(result, indent=2))
    return result["result"]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--host", default=DEFAULT_HOST)
    parser.add_argument("--project-path", default=DEFAULT_PROJECT)
    parser.add_argument("--suite", choices=("all", "ram", "presentation", "gpu", "finish-gpu", "controls", "interactions", "freeze", "programs", "deep-probes", "probe-lifecycle", "optimizations", "scale"), default="ram")
    options = parser.parse_args()
    state = pipeline(options, "editor_status")
    if state.get("playMode") != "playing":
        raise RuntimeError("Enter Play Mode before running CircuitLab checks.")
    output = Path(__file__).resolve().parents[1] / "results/circuit-lab"
    output.mkdir(parents=True, exist_ok=True)
    if options.suite == "presentation":
        script(options, "ValidateCircuitLab.WaitForInitialization")
        for entry, name in (
            ("ValidateCircuitLabPresentation.BrowseAndLoad", "presentation-browse-load"),
            ("ValidateCircuitLabPresentation.SharedViewAndLayout", "presentation-shared-layout"),
        ):
            result = script(options, entry)
            (output / (name + ".json")).write_text(json.dumps(result, indent=2, ensure_ascii=False) + "\n")
            print(entry + " passed", flush=True)
        return
    if options.suite == "ram":
        for entry, name in (
            ("ValidateCircuitLabRam.Initialization", "ram-initialization"),
            ("ValidateCircuitLabRam.EditingAndReset", "ram-edit-reset"),
            ("ValidateCircuitLabRam.VirtualKeyboard", "ram-keyboard"),
            ("ValidateCircuitLabRam.RamProbe", "ram-probe"),
            ("ValidateCircuitLabRam.PrepareReplica", None),
            ("ValidateCircuitLabRamReplica.SharedGpuRestoreAndCatchup", "ram-replica"),
            ("ValidateCircuitLabRam.RunPi", "ram-pi-gpu"),
            ("ValidateCircuitLabRam.AuthoringLayout", "ram-ui-layout"),
        ):
            result = script(options, entry)
            if name:
                (output / (name + ".json")).write_text(json.dumps(result, indent=2, ensure_ascii=False) + "\n")
            print(entry + " passed", flush=True)
        return
    script(options, "ValidateCircuitLab.IsolatePlayMode")
    script(options, "ValidateCircuitLab.WaitForInitialization")
    script(options, "ValidateCircuitLabOptimizations.ClaimControl")
    if options.suite in ("all", "controls"):
        result = script(options, "ValidateCircuitLab.Controls")
        (output / "controls.json").write_text(json.dumps(result, indent=2) + "\n")
        print("Controls passed", flush=True)
    if options.suite in ("all", "gpu", "finish-gpu"):
        if options.suite != "finish-gpu":
            script(options, "ValidateCircuitLabOptimizations.PrepareGpuReference")
            result = script(options, "ValidateCircuitLab.StartSelfTest")
            previous = result["previousRun"]
        else:
            previous = -1
        deadline = time.monotonic() + 180
        while True:
            state = script(options, "ValidateCircuitLab.SimulationStatus")
            print(json.dumps(state, separators=(",", ":")), flush=True)
            if state["runId"] > previous and not state["testRunning"] and state["status"] in (3, 4, 5):
                if state["status"] != 3:
                    raise RuntimeError("GPU self-test failed: " + json.dumps(state))
                break
            if time.monotonic() >= deadline:
                raise TimeoutError("GPU reference self-test did not complete.")
            time.sleep(3)
        result = script(options, "ValidateCircuitLab.CompletedSelfTestAndWaveform")
        for name in ("gpu", "waveform"):
            (output / (name + ".json")).write_text(json.dumps(result[name], indent=2) + "\n")
        print("GPU state and independent RTL waveform checks passed", flush=True)
    if options.suite in ("all", "freeze"):
        result = script(options, "ValidateCircuitLab.FreezeAndBrowse")
        (output / "freeze-browse.json").write_text(json.dumps(result, indent=2) + "\n")
        print("Frozen view, ongoing capture and browsing passed", flush=True)
    if options.suite in ("all", "interactions"):
        result = script(options, "ValidateCircuitLabOptimizations.ModuleAndProbeInteractions")
        (output / "modules-probes.json").write_text(json.dumps(result, indent=2) + "\n")
        print("Module extraction, folding and probe event chain passed", flush=True)
    if options.suite in ("all", "programs"):
        script(options, "ValidateCircuitLabOptimizations.PrepareProgramRegression")
        result = script(options, "ValidateCircuitLab.ProgramsAndBurst")
        (output / "programs-burst.json").write_text(json.dumps(result, indent=2) + "\n")
        print("Queued steps, both programs, HALT and synchronous reset passed", flush=True)
    if options.suite in ("all", "deep-probes"):
        result = script(options, "ValidateCircuitLab.DeepProbeAndWires")
        (output / "deep-probe-wires.json").write_text(json.dumps(result, indent=2) + "\n")
        print("Deep extraction, boundary wires and hidden internal signal capture passed", flush=True)
    if options.suite in ("all", "probe-lifecycle"):
        result = script(options, "ValidateCircuitLab.ProbeLifecycleAndFormats")
        (output / "probe-lifecycle-formats.json").write_text(json.dumps(result, indent=2) + "\n")
        print("Pickup, preselection, Use, clock capture, formats, bit views and drop passed", flush=True)
    if options.suite in ("all", "optimizations"):
        for entry, name in (
            ("SceneStructure", "optimization-structure"),
            ("GeometryAndTree", "optimization-geometry-tree"),
            ("TerminalOutput", "optimization-terminal"),
            ("SharedGpuReplica", "optimization-shared-gpu"),
            ("ProbeResetPreservesFreeze", "optimization-probe-reset"),
        ):
            result = script(options, "ValidateCircuitLabOptimizations." + entry)
            (output / (name + ".json")).write_text(json.dumps(result, indent=2) + "\n")
            print(name + " passed", flush=True)
    if options.suite == "scale":
        result = script(options, "BenchmarkCircuitLab.Run")
        (output / "scale-benchmark.json").write_text(json.dumps(result, indent=2) + "\n")
        print("Independent 2D GPU scale experiment passed", flush=True)


if __name__ == "__main__":
    main()
