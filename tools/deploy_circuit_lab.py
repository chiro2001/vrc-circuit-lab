#!/usr/bin/env python3
"""Deploy CircuitLab text sources/data; Unity owns all generated assets."""

import argparse
import json
from pathlib import Path
import shutil
import subprocess
import tarfile
import tempfile
import time
import uuid

from deploy_netlist_smoke import checked_pwsh, copy_text_sources, pipeline
from unity_remote import DEFAULT_HOST, DEFAULT_PROJECT, pwsh_literal

ROOT = Path(__file__).resolve().parents[1]
TASK_TMP = Path.home() / "tmp/vrc-cpu-map/circuit-lab"


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--host", default=DEFAULT_HOST)
    parser.add_argument("--project-path", default=DEFAULT_PROJECT)
    parser.add_argument("--data", type=Path, default=TASK_TMP / "hdl/gpu-netlist.json")
    parser.add_argument("--builder-only", action="store_true")
    parser.add_argument("--no-recompile", action="store_true")
    options = parser.parse_args()
    state = pipeline(options, "editor_status")
    if state.get("playMode") != "stopped" and not options.builder_only:
        raise RuntimeError("Stop Play Mode before deploying sources.")
    pipeline(options, "set_autotick", "--enable", "true")
    data = None
    if not options.builder_only:
        data = json.loads(options.data.read_text())
        if data.get("schema") not in (2, 3):
            raise ValueError("Expected a hierarchy-preserving schema 2 or 3 fixture")
        if len(data["expectedStates"]) != data["nodeCount"] * data["vectorCount"]:
            raise ValueError("Reference state length differs from node/vector counts")
        if len(data["nodes"]) != data["nodeCount"] or not data.get("modules"):
            raise ValueError("Invalid node or module metadata")
        if data["schema"] == 3:
            required_ports = ("romAddress", "romData", "ramReadAddress", "ramReadData", "ramWriteAddress", "ramWriteData", "ramWriteEnable")
            if any(name not in data.get("memoryPorts", {}) for name in required_ports):
                raise ValueError("Schema 3 is missing its real memory-port signal mapping")
            for program in data.get("programs", []):
                if len(program["words"]) != 512 or any(not 0 <= word <= 4095 for word in program["words"]):
                    raise ValueError("Program image must contain 512 valid 12-bit words")
                if len(program["initialRam"]) != 1024 or any(not 0 <= value <= 255 for value in program["initialRam"]):
                    raise ValueError("Program RAM image must contain 1024 bytes")
            if not data.get("programs"):
                raise ValueError("Schema 3 needs at least one loadable program")
            for name in ("fixturePrograms", "fixtureRamReset"):
                if len(data.get(name, [])) != data["vectorCount"]:
                    raise ValueError(f"Schema 3 fixture length is invalid: {name}")
    TASK_TMP.mkdir(parents=True, exist_ok=True)
    remote_root = checked_pwsh(
        "$taskRoot = Join-Path ([Environment]::GetFolderPath('UserProfile')) 'tmp\\vrc-cpu-world\\circuit-lab'; "
        "New-Item -ItemType Directory -Path $taskRoot -Force | Out-Null; Write-Output $taskRoot",
        options.host,
    )
    deployment_name = "sources-" + uuid.uuid4().hex
    with tempfile.TemporaryDirectory(prefix="deploy-", dir=TASK_TMP) as temporary:
        stage = Path(temporary)
        if data is not None:
            source = stage / "Assets/CircuitLab"
            copy_text_sources(ROOT / "Assets/CircuitLab", source)
            # New font sources and locale dictionaries are copied; Unity imports them.
            locale = ROOT / "Assets/CircuitLab/Localization"
            for path in locale.rglob("*") if locale.exists() else ():
                if path.is_file() and path.suffix in (".ttf", ".otf", ".txt", ".tsv", ".md"):
                    target = source / "Localization" / path.relative_to(locale)
                    target.parent.mkdir(parents=True, exist_ok=True)
                    shutil.copy2(path, target)
            for path in source.rglob("*"):
                if path.is_file() and path.suffix not in (".cs", ".shader", ".json", ".ttf", ".otf", ".txt", ".tsv", ".md"):
                    raise ValueError(f"Only text sources may be deployed: {path}")
            (source / "Data").mkdir(exist_ok=True)
            (source / "Data/gpu-netlist.json").write_text(json.dumps(data, separators=(",", ":")) + "\n")
            for reference_name in ("waveform-reference.json", "test-report.json", "verilog-source.json"):
                reference = options.data.parent / reference_name
                if reference.is_file():
                    shutil.copy2(reference, source / "Data" / reference_name)
        scripts = stage / "AgentScripts"
        scripts.mkdir()
        for script in (ROOT / "AgentScripts").glob("*CircuitLab*.cs"):
            shutil.copy2(script, scripts / script.name)
        if not list(scripts.glob("*.cs")):
            raise RuntimeError("CircuitLab builder has not been created yet")
        archive = stage / (deployment_name + ".tgz")
        with tarfile.open(archive, "w:gz") as tar:
            if data is not None:
                tar.add(stage / "Assets", arcname="Assets")
            tar.add(scripts, arcname="AgentScripts")
        remote_archive = remote_root.replace("\\", "/") + "/" + archive.name
        subprocess.run(["scp", "-q", "-o", "BatchMode=yes", "-o", "LogLevel=ERROR", str(archive), options.host + ":" + remote_archive], check=True)
    checked_pwsh(f"""
$project = {pwsh_literal(options.project_path)}
$archive = {pwsh_literal(remote_archive)}
$stage = Join-Path {pwsh_literal(remote_root)} {pwsh_literal(deployment_name)}
New-Item -ItemType Directory -Path $stage | Out-Null
try {{
  tar -xzf $archive -C $stage
  if ($LASTEXITCODE -ne 0) {{ throw 'Source extraction failed' }}
  foreach ($file in Get-ChildItem -LiteralPath $stage -Recurse -File) {{
    $relative = $file.FullName.Substring($stage.Length + 1)
    if ($file.Extension -notin @('.cs', '.shader', '.json', '.ttf', '.otf', '.txt', '.tsv', '.md')) {{ throw "Unexpected source type: $relative" }}
    if ($relative -notlike 'Assets\\CircuitLab\\*' -and $relative -notlike 'AgentScripts\\*CircuitLab*.cs') {{ throw "Unexpected source path: $relative" }}
    $target = Join-Path $project $relative
    New-Item -ItemType Directory -Path (Split-Path $target -Parent) -Force | Out-Null
    Copy-Item -LiteralPath $file.FullName -Destination $target -Force
  }}
}} finally {{
  Remove-Item -LiteralPath $stage -Recurse -Force
  Remove-Item -LiteralPath $archive -Force
}}
""", options.host)
    if options.no_recompile or options.builder_only:
        print(json.dumps({"sourcesDeployed": True, "builderOnly": options.builder_only}))
        return
    pipeline(options, "recompile")
    deadline = time.monotonic() + 120
    while time.monotonic() < deadline:
        state = pipeline(options, "recompile_status", allow_reload=True)
        if state.get("failed") or state.get("status") == "failed":
            raise RuntimeError(json.dumps(state, indent=2))
        if state.get("status") in ("completed", "up_to_date"):
            print(json.dumps({"sourcesDeployed": True, "compileStatus": state, "project": options.project_path}, indent=2))
            return
        time.sleep(1)
    raise TimeoutError("Compilation did not finish; inspect recompile_status.")


if __name__ == "__main__":
    main()
