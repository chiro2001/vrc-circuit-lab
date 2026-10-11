#!/usr/bin/env python3
"""Copy the smoke's text sources and generated data, then refresh via Unity CLI."""

import argparse
import json
from pathlib import Path
import shutil
import subprocess
import sys
import tarfile
import tempfile
import time
import uuid

from unity_remote import DEFAULT_HOST, DEFAULT_PROJECT, pwsh_literal, run_pwsh

ROOT = Path(__file__).resolve().parents[1]
TASK_TMP = Path.home() / "tmp/vrc-cpu-map/netlist-smoke"


def copy_text_sources(source, target):
    """Deploy editable sources only; serialized assets and GUIDs stay Editor-owned."""
    for file in source.rglob("*"):
        if file.is_file() and file.suffix in (".cs", ".shader", ".json"):
            destination = target / file.relative_to(source)
            destination.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(file, destination)


def checked_pwsh(script, host):
    result = run_pwsh(script, host, True)
    if result.returncode:
        raise RuntimeError(result.stderr.decode("utf-8", errors="replace"))
    return result.stdout.decode("utf-8-sig").strip()


def pipeline(options, *arguments, allow_reload=False):
    result = subprocess.run([
        sys.executable, str(ROOT / "tools/unity_remote.py"), "--check-result",
        "--host", options.host, "--project-path", options.project_path, *arguments,
    ], text=True, capture_output=True)
    if result.returncode:
        if allow_reload:
            try:
                failure = json.loads(result.stdout)
                messages = " ".join(error.get("message", "") for error in failure.get("errors", [])).lower()
                if any(marker in messages for marker in ("connection reset", "connection refused", "econnrefused", "socket hang up", "pipeline_load_pending")):
                    return {"status": "reloading"}
            except ValueError:
                pass
        raise RuntimeError(result.stdout + result.stderr)
    return json.loads(result.stdout)["data"]["result"]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--host", default=DEFAULT_HOST)
    parser.add_argument("--project-path", default=DEFAULT_PROJECT)
    parser.add_argument("--data", type=Path, default=TASK_TMP / "hdl/gpu-netlist.json")
    options = parser.parse_args()
    if not options.data.is_file():
        parser.error("Generate the netlist first: python tools/build_netlist_smoke.py")
    data = json.loads(options.data.read_text())
    if data.get("schema") != 1 or len(data["expectedStates"]) != data["nodeCount"] * data["vectorCount"]:
        raise ValueError("Invalid GPU netlist fixture")
    state = pipeline(options, "editor_status")
    if state.get("playMode") != "stopped":
        raise RuntimeError("Stop Play Mode before deploying source updates.")
    pipeline(options, "set_autotick", "--enable", "true")
    TASK_TMP.mkdir(parents=True, exist_ok=True)
    remote_root = checked_pwsh(
        "$taskRoot = Join-Path ([Environment]::GetFolderPath('UserProfile')) 'tmp\\vrc-cpu-world\\netlist-smoke'; "
        "New-Item -ItemType Directory -Path $taskRoot -Force | Out-Null; Write-Output $taskRoot", options.host)
    name = "sources-" + uuid.uuid4().hex
    with tempfile.TemporaryDirectory(prefix="deploy-", dir=TASK_TMP) as temporary:
        stage = Path(temporary)
        sources = stage / "Assets/GpuNetlistSmoke"
        copy_text_sources(ROOT / "Assets/GpuNetlistSmoke", sources)
        (sources / "Data").mkdir(exist_ok=True)
        (sources / "Data/gpu-netlist.json").write_text(json.dumps(data, separators=(",", ":")) + "\n")
        (stage / "AgentScripts").mkdir()
        shutil.copy2(ROOT / "AgentScripts/BuildNetlistSmoke.cs", stage / "AgentScripts/BuildNetlistSmoke.cs")
        archive = stage / (name + ".tgz")
        with tarfile.open(archive, "w:gz") as tar:
            tar.add(stage / "Assets", arcname="Assets")
            tar.add(stage / "AgentScripts", arcname="AgentScripts")
        remote_archive = remote_root.replace("\\", "/") + "/" + archive.name
        subprocess.run(["scp", "-q", "-o", "BatchMode=yes", "-o", "LogLevel=ERROR", str(archive), options.host + ":" + remote_archive], check=True)
    script = f"""
$project = {pwsh_literal(options.project_path)}
$archive = {pwsh_literal(remote_archive)}
$stage = Join-Path {pwsh_literal(remote_root)} {pwsh_literal(name)}
New-Item -ItemType Directory -Path $stage | Out-Null
try {{
  tar -xzf $archive -C $stage
  if ($LASTEXITCODE -ne 0) {{ throw 'Source extraction failed' }}
  foreach ($file in Get-ChildItem -LiteralPath $stage -Recurse -File) {{
    $relative = $file.FullName.Substring($stage.Length + 1)
    if ($file.Extension -notin @('.cs', '.shader', '.json')) {{ throw "Unexpected source type: $relative" }}
    if ($relative -notlike 'Assets\\GpuNetlistSmoke\\*' -and $relative -ne 'AgentScripts\\BuildNetlistSmoke.cs') {{ throw "Unexpected source path: $relative" }}
    $target = Join-Path $project $relative
    New-Item -ItemType Directory -Path (Split-Path $target -Parent) -Force | Out-Null
    Copy-Item -LiteralPath $file.FullName -Destination $target -Force
  }}
}} finally {{
  Remove-Item -LiteralPath $stage -Recurse -Force
  Remove-Item -LiteralPath $archive -Force
}}
"""
    checked_pwsh(script, options.host)
    pipeline(options, "recompile")
    deadline = time.monotonic() + 60
    while time.monotonic() < deadline:
        state = pipeline(options, "recompile_status", allow_reload=True)
        if state.get("status") in ("completed", "up_to_date"):
            print(json.dumps({"sourcesDeployed": True, "compileStatus": state, "project": options.project_path}, indent=2))
            return
        time.sleep(1)
    raise TimeoutError("Compilation did not finish; inspect editor_status and recompile_status.")


if __name__ == "__main__":
    main()
