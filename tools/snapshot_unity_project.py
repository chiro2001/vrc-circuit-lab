#!/usr/bin/env python3
"""Capture the saved Windows Unity project into this Git worktree, preserving GUIDs."""

import argparse
import hashlib
import json
from pathlib import Path
import shutil
import subprocess
import tarfile
import tempfile
import uuid

from deploy_netlist_smoke import checked_pwsh, pipeline
from unity_remote import DEFAULT_HOST, DEFAULT_PROJECT, pwsh_literal

ROOT = Path(__file__).resolve().parents[1]
TASK_TMP = Path.home() / "tmp/vrc-cpu-map/github"
TREES = ("Assets", "AgentScripts", "ProjectSettings", "Packages/com.unity.pipeline", "Packages/com.vrchat.core.vpm-resolver")
MANIFESTS = ("Packages/.gitignore", "Packages/manifest.json", "Packages/packages-lock.json", "Packages/vpm-manifest.json")


def digest(path):
    return hashlib.sha256(path.read_bytes()).digest() if path.is_file() else None


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--host", default=DEFAULT_HOST)
    parser.add_argument("--project-path", default=DEFAULT_PROJECT)
    options = parser.parse_args()
    state = pipeline(options, "editor_status")
    if state.get("playMode") != "stopped" or state.get("compiling"):
        raise RuntimeError("Stop Play Mode and finish compilation before taking a snapshot.")
    scenes = pipeline(options, "eval", "return Enumerable.Range(0, UnityEngine.SceneManagement.SceneManager.sceneCount).Select(i => new { path = UnityEngine.SceneManagement.SceneManager.GetSceneAt(i).path, dirty = UnityEngine.SceneManagement.SceneManager.GetSceneAt(i).isDirty }).ToArray();")
    if not scenes.get("success") or any(scene["dirty"] for scene in scenes["result"]):
        raise RuntimeError("Save the intended scene edits before taking a snapshot; existing unsaved edits are preserved.")
    TASK_TMP.mkdir(parents=True, exist_ok=True)
    name = "unity-snapshot-" + uuid.uuid4().hex + ".tgz"
    remote_archive = checked_pwsh(
        "$taskRoot = Join-Path ([Environment]::GetFolderPath('UserProfile')) 'tmp\\vrc-cpu-world\\github'; "
        "New-Item -ItemType Directory -Path $taskRoot -Force | Out-Null; "
        f"$archive = Join-Path $taskRoot {pwsh_literal(name)}; "
        f"& tar -czf $archive -C {pwsh_literal(options.project_path)} "
        + " ".join(pwsh_literal(path) for path in (*TREES, *MANIFESTS))
        + "; if ($LASTEXITCODE -ne 0) { throw 'Unity snapshot archive failed' }; Write-Output $archive",
        options.host,
    )
    try:
        with tempfile.TemporaryDirectory(prefix="snapshot-", dir=TASK_TMP) as temporary:
            stage = Path(temporary)
            archive = stage / name
            subprocess.run(["scp", "-q", "-o", "BatchMode=yes", "-o", "LogLevel=ERROR", options.host + ":" + remote_archive.replace("\\", "/"), str(archive)], check=True)
            unpacked = stage / "project"
            with tarfile.open(archive, "r:gz") as tar:
                tar.extractall(unpacked, filter="data")
            changed = []
            if (ROOT / ".git").exists():
                commands = [("ls-files", "--others", "--exclude-standard", "-z")]
                if subprocess.run(["git", "rev-parse", "--verify", "HEAD"], cwd=ROOT, capture_output=True).returncode == 0:
                    commands.append(("diff", "HEAD", "--name-only", "-z"))
                for args in commands:
                    changed.extend(subprocess.check_output(["git", *args], cwd=ROOT).decode().split("\0"))
            conflicts = [path for path in changed if path and any(path == base or path.startswith(base + "/") for base in (*TREES, *MANIFESTS)) and digest(ROOT / path) != digest(unpacked / path)]
            if conflicts:
                raise RuntimeError("Local edits differ from the Editor snapshot; deploy or resolve them first: " + ", ".join(conflicts))
            copied = removed = 0
            for base in TREES:
                source, target = unpacked / base, ROOT / base
                for old in target.rglob("*") if target.exists() else ():
                    if old.is_file() and not (source / old.relative_to(target)).is_file():
                        old.unlink(); removed += 1
                for file in source.rglob("*"):
                    if not file.is_file():
                        continue
                    destination = target / file.relative_to(source)
                    if digest(file) != digest(destination):
                        destination.parent.mkdir(parents=True, exist_ok=True)
                        shutil.copy2(file, destination); copied += 1
            for name in MANIFESTS:
                destination = ROOT / name
                destination.parent.mkdir(parents=True, exist_ok=True)
                shutil.copy2(unpacked / name, destination)
            print(json.dumps({"captured": True, "copiedFiles": copied, "removedStaleMirrorFiles": removed, "scenePaths": [scene["path"] for scene in scenes["result"]], "preservedUnityMeta": True, "vrchatPackagesRestoredByVCC": True}, indent=2))
    finally:
        checked_pwsh(f"Remove-Item -LiteralPath {pwsh_literal(remote_archive)} -Force", options.host)


if __name__ == "__main__":
    main()
