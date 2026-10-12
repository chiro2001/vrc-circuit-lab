#!/usr/bin/env python3
"""Align Windows Git with an already-pushed commit without rewriting Unity assets."""

import argparse
import hashlib
import json
from pathlib import Path
import subprocess
import tarfile
import tempfile
import uuid

from deploy_netlist_smoke import checked_pwsh, pipeline
from unity_remote import DEFAULT_HOST, DEFAULT_PROJECT, pwsh_literal

ROOT = Path(__file__).resolve().parents[1]
TASK_TMP = Path.home() / "tmp/vrc-cpu-map/github"


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--host", default=DEFAULT_HOST)
    parser.add_argument("--project-path", default=DEFAULT_PROJECT)
    options = parser.parse_args()
    state = pipeline(options, "editor_status")
    if state.get("playMode") != "stopped" or state.get("compiling"):
        raise RuntimeError("Stop Play Mode and finish compilation first.")
    if subprocess.check_output(["git", "status", "--porcelain"], cwd=ROOT):
        raise RuntimeError("Commit and push the local changes before aligning Windows Git.")
    head = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=ROOT, text=True).strip()
    url = subprocess.check_output(["git", "remote", "get-url", "origin"], cwd=ROOT, text=True).strip()
    remote_head = subprocess.check_output(["git", "ls-remote", "origin", "refs/heads/main"], cwd=ROOT, text=True).split()[0]
    if head != remote_head:
        raise RuntimeError("Push main to GitHub first.")
    paths = subprocess.check_output(["git", "ls-files", "-z"], cwd=ROOT).decode().strip("\0").split("\0")
    manifest = {path: hashlib.sha256((ROOT / path).read_bytes()).hexdigest() for path in paths}
    supplemental = [path for path in paths if not path.startswith(("Assets/", "Packages/", "ProjectSettings/", "AgentScripts/"))]
    TASK_TMP.mkdir(parents=True, exist_ok=True)
    name = "git-sync-" + uuid.uuid4().hex
    remote_stage = checked_pwsh(
        "$taskRoot = Join-Path ([Environment]::GetFolderPath('UserProfile')) 'tmp\\vrc-cpu-world\\github'; "
        f"$stage = Join-Path $taskRoot {pwsh_literal(name)}; New-Item -ItemType Directory -Path $stage -Force | Out-Null; Write-Output $stage",
        options.host,
    )
    try:
        with tempfile.TemporaryDirectory(prefix="git-sync-", dir=TASK_TMP) as temporary:
            stage = Path(temporary)
            (stage / "files.json").write_text(json.dumps(manifest))
            subprocess.run(["git", "bundle", "create", str(stage / "main.bundle"), "main"], cwd=ROOT, check=True)
            with tarfile.open(stage / "supplemental.tgz", "w:gz") as tar:
                for path in supplemental:
                    tar.add(ROOT / path, arcname=path)
            for file in stage.iterdir():
                subprocess.run(["scp", "-q", "-o", "BatchMode=yes", "-o", "LogLevel=ERROR", str(file), options.host + ":" + remote_stage.replace("\\", "/") + "/" + file.name], check=True)
        result = checked_pwsh(f"""
Set-Location -LiteralPath {pwsh_literal(options.project_path)}
$stage = {pwsh_literal(remote_stage)}
$expected = Get-Content -Raw (Join-Path $stage 'files.json') | ConvertFrom-Json -AsHashtable
# Refuse to adopt a commit whose Unity content differs from the live project.
foreach ($path in $expected.Keys) {{
    if ($path -match '^(Assets|Packages|ProjectSettings|AgentScripts)/') {{
        if (!(Test-Path -LiteralPath $path) -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expected[$path]) {{ throw "Live Unity content differs: $path. Capture and commit it first." }}
    }}
}}
if (Test-Path '.git') {{
    $dirty = @(& git -c core.quotepath=false diff HEAD --name-only)
    if ($LASTEXITCODE -ne 0) {{ throw 'Cannot inspect Windows Git changes' }}
    foreach ($path in $dirty) {{
        if (!$expected.ContainsKey($path) -or !(Test-Path -LiteralPath $path) -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expected[$path]) {{ throw "Uncaptured Windows edit: $path" }}
    }}
}}
& tar -xzf (Join-Path $stage 'supplemental.tgz')
if ($LASTEXITCODE -ne 0) {{ throw 'Supplemental source extraction failed' }}
if (!(Test-Path '.git')) {{
    & git init -b main | Out-Null
    if ($LASTEXITCODE -ne 0) {{ throw 'Windows Git init failed' }}
    & git remote add origin {pwsh_literal(url)}
    if ($LASTEXITCODE -ne 0) {{ throw 'Windows origin setup failed' }}
}} else {{
    $origin = & git remote get-url origin
    if ($origin -ne {pwsh_literal(url)}) {{ throw 'Windows Git has a different origin' }}
}}
& git config core.autocrlf false
if ((Test-Path '.gitattributes') -and (Select-String -LiteralPath '.gitattributes' -Pattern 'filter=lfs' -Quiet)) {{
    # Unity assets remain in place; the clean filter records their existing bytes as LFS objects.
    & git lfs install --local | Out-Null
    if ($LASTEXITCODE -ne 0) {{ throw 'Windows Git LFS setup failed' }}
}}
& git fetch (Join-Path $stage 'main.bundle') main 2>&1 | Out-Null
if ($LASTEXITCODE -ne 0) {{ throw 'Git bundle fetch failed' }}
# Only HEAD/index change. Scene, assets, meta files and settings are untouched.
& git reset --mixed FETCH_HEAD | Out-Null
if ($LASTEXITCODE -ne 0) {{ throw 'Windows Git index alignment failed' }}
& git update-ref refs/remotes/origin/main {pwsh_literal(head)}
& git branch --set-upstream-to=origin/main main | Out-Null
$actual = (& git rev-parse HEAD).Trim()
$status = @(& git status --porcelain)
if ($actual -ne {pwsh_literal(head)} -or $status.Count -ne 0) {{ throw "Windows Git is not clean: $($status -join ', ')" }}
[pscustomobject]@{{ synced=$true; windowsHead=$actual; githubHead={pwsh_literal(head)}; origin={pwsh_literal(url)}; unityAssetsRewritten=$false }} | ConvertTo-Json
""", options.host)
        print(result)
    finally:
        checked_pwsh(f"Remove-Item -LiteralPath {pwsh_literal(remote_stage)} -Recurse -Force", options.host)


if __name__ == "__main__":
    main()
