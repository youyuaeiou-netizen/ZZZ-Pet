"""Validate the shipped ZIP and exercise manual updates in isolated user data.

Runs only owned test processes. Never reads/writes normal DesktopPet settings.
This is same-machine deployment verification, not another-computer acceptance.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import shutil
import subprocess
import time
import zipfile

import ctypes
from ctypes import wintypes
from PIL import ImageGrab
from sit_wave_window_probe import locate, rect, user32

user32.EnableWindow.argtypes = [wintypes.HWND, wintypes.BOOL]


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def wait_until(test, seconds=5):
    deadline = time.monotonic() + seconds
    while time.monotonic() < deadline:
        if test():
            return
        time.sleep(.05)
    raise AssertionError("Timed out waiting for deployment check")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--archive", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    args.output.mkdir(parents=True, exist_ok=False)
    expected_hash = Path(str(args.archive) + ".sha256").read_text(encoding="utf-8").split()[0]
    assert digest(args.archive) == expected_hash, "ZIP SHA256 mismatch"
    with zipfile.ZipFile(args.archive) as archive:
        names = archive.namelist()
        for name in names:
            parts = PurePosixPath(name).parts
            assert not name.startswith(("/", "\\")) and ".." not in parts and ":" not in name, name
        roots = {PurePosixPath(name).parts[0] for name in names}
        assert len(roots) == 1, roots
        root_name = roots.pop()
        first = args.output / "第一份解压"
        second = args.output / "新版程序路径 with spaces"
        archive.extractall(first)
        archive.extractall(second)
    first, second = first / root_name, second / root_name
    inventory = json.loads((first / "release-manifest.json").read_text(encoding="utf-8"))
    for item in inventory["files"]:
        path = first / item["path"]
        assert digest(path) == item["sha256"] and path.stat().st_size == item["bytes"], item["path"]
    actual_files = {p.relative_to(first).as_posix() for p in first.rglob("*") if p.is_file()}
    assert actual_files == {item["path"] for item in inventory["files"]} | {"release-manifest.json"}
    assert not any("masters/" in name or name.endswith(".pdb") for name in actual_files)
    referenced = set()
    for manifest in (first / "characters").glob("*/character.json"):
        config = json.loads(manifest.read_text(encoding="utf-8"))
        referenced.add(manifest.relative_to(first).as_posix())
        referenced.update((manifest.parent / frame["path"]).relative_to(first).as_posix()
                          for action in config["actions"] for frame in action["frames"])
    assert referenced == {name for name in actual_files if name.startswith("characters/")}, "Unused runtime assets"
    assert root_name == f"DesktopPet-{inventory['version']}-win-x64" and inventory["selfContained"]
    assert (first / "coreclr.dll").exists() and (first / "朋友试用说明.txt").exists()

    data = args.output / "独立用户数据"
    data.mkdir()
    settings_path = data / "settings.json"
    settings_path.write_text(json.dumps(dict(WindowLeft=350, WindowTop=350, Scale=.9,
        SelectedCharacter="ellen-flat2d", AlwaysOnTop=False, FuturePreference={"enabled": True})), encoding="utf-8")
    user_pack = data / "characters/guest-role"
    (user_pack / "animations").mkdir(parents=True)
    approved_manifest = first / "characters/ellen-flat2d/character.json"
    approved_config = json.loads(approved_manifest.read_text(encoding="utf-8"))
    default_action = next(action for action in approved_config["actions"]
                          if action["id"] == approved_config["defaultActionId"])
    shutil.copyfile(approved_manifest.parent / default_action["frames"][0]["path"], user_pack / "animations/idle.png")
    config = dict(schemaVersion=1, id="guest-role", name="用户动作示例", minimumAppVersion="0.1.0",
        immediateClick=False, defaultActionId="resting", canvasWidth=160, canvasHeight=160, anchorX=80, anchorY=150,
        sleepThresholdMs=0, actions=[dict(id="resting", loop=True, interruptibleBy=["click"],
            frames=[dict(path="animations/idle.png", durationMs=300)]),
            dict(id="greeting", triggers=["click"], priority=10, nextActionId="resting",
            frames=[dict(path="animations/idle.png", durationMs=500)])])
    (user_pack / "character.json").write_text(json.dumps(config), encoding="utf-8")
    broken_pack = data / "characters/bad-role"
    broken_pack.mkdir()
    (broken_pack / "character.json").write_text("{broken", encoding="utf-8")
    user_hashes = {p.relative_to(data).as_posix(): digest(p) for p in (data / "characters").rglob("*") if p.is_file()}

    environment = os.environ.copy()
    system_root = environment.get("SystemRoot", "C:/Windows")
    environment["PATH"] = f"{system_root}/System32;{system_root}"
    environment["DOTNET_ROOT"] = str(args.output / "no-installed-dotnet")
    environment["DOTNET_ROOT_X64"] = environment["DOTNET_ROOT"]
    environment["DOTNET_MULTILEVEL_LOOKUP"] = "0"
    processes = []

    def launch(folder, extra=()):
        process = subprocess.Popen([str(folder / "DesktopPet.exe"), "--data-dir", str(data), *extra],
                                   env=environment, cwd=folder)
        processes.append(process)
        hwnd = locate(process.pid)
        # This deployment probe sends no clicks. Ignore physical desktop input
        # so incidental dragging cannot alter the isolated position fixture.
        user32.EnableWindow(hwnd, False)
        time.sleep(.3)
        return process, hwnd

    def close(process, hwnd):
        user32.PostMessageW(hwnd, 0x0010, 0, 0)
        assert process.wait(timeout=10) == 0, "Test process did not exit normally"

    try:
        process, hwnd = launch(first)
        initial_rect = rect(hwnd)
        ImageGrab.grab(bbox=initial_rect, all_screens=True).save(args.output / "zip-startup.png")
        log_path = data / "logs/diagnostics.log"
        wait_until(lambda: log_path.exists() and "bad-role" in log_path.read_text(encoding="utf-8-sig"))
        duplicate = subprocess.Popen([str(second / "DesktopPet.exe"), "--data-dir", str(data)], env=environment, cwd=second)
        processes.append(duplicate)
        assert duplicate.wait(timeout=10) == 0, "Duplicate launch failed to exit"
        wait_until(lambda: "已处理重复启动" in log_path.read_text(encoding="utf-8-sig"))
        assert process.poll() is None and rect(hwnd) == initial_rect, "Duplicate altered primary"
        close(process, hwnd)
        saved = json.loads(settings_path.read_text(encoding="utf-8"))
        assert saved["WindowLeft"] == 350 and saved["WindowTop"] == 350 and saved["Scale"] == .9, saved
        assert saved["AlwaysOnTop"] is False and saved["FuturePreference"]["enabled"] is True

        process, hwnd = launch(second)
        assert rect(hwnd) == initial_rect, "New application folder changed saved position or scale"
        close(process, hwnd)
        process, hwnd = launch(second, ["--character", "guest-role"])
        guest_rect = rect(hwnd)
        assert guest_rect[2] - guest_rect[0] < initial_rect[2] - initial_rect[0], "User role not selected"
        ImageGrab.grab(bbox=guest_rect, all_screens=True).save(args.output / "user-role.png")
        close(process, hwnd)
        assert json.loads(settings_path.read_text(encoding="utf-8"))["SelectedCharacter"] == "guest-role"
        process, hwnd = launch(first)
        assert rect(hwnd) == guest_rect, "User role or scale not retained across application folders"
        close(process, hwnd)
        assert all(digest(data / path) == value for path, value in user_hashes.items()), "Update modified user roles"
        assert {p.relative_to(first).as_posix() for p in first.rglob("*") if p.is_file()} == actual_files, "Runtime wrote in application folder"
        missing_assets = args.output / "缺少角色的发布文件夹"
        shutil.copytree(first, missing_assets, ignore=shutil.ignore_patterns("characters"))
        failure_data = args.output / "启动失败独立数据"
        failure = subprocess.Popen([str(missing_assets / "DesktopPet.exe"), "--data-dir", str(failure_data)],
                                   env=environment, cwd=missing_assets)
        processes.append(failure)
        dialog = locate(failure.pid)
        ImageGrab.grab(bbox=rect(dialog), all_screens=True).save(args.output / "startup-error.png")
        user32.PostMessageW(dialog, 0x0010, 0, 0)
        assert failure.wait(timeout=10) == 1, "Missing characters did not report startup failure"
        assert "没有可播放的角色" in (failure_data / "logs/diagnostics.log").read_text(encoding="utf-8-sig"), "Startup failure not logged"
        report = dict(status="PASS", version=inventory["version"], zipSha256=expected_hash,
            runtimeCharacterFiles=len(referenced), files=len(actual_files), initialRect=initial_rect,
            userRoleRect=guest_rect, otherComputerAcceptance="pending",
            checks=["ZIP inventory", "referenced frames only", "no masters", "self-contained launch without SDK PATH",
                    "Chinese and spaced paths", "same-data duplicate across release folders", "legacy settings upgrade",
                    "unknown settings retained", "position and scale retained", "user role discovery and persistence",
                    "invalid role skipped and logged", "no writes to application folder", "startup error dialog and log"])
        (args.output / "release-probe.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
        print(json.dumps(report))
    finally:
        for process in processes:
            if process.poll() is None:
                try:
                    hwnd = locate(process.pid)
                    user32.PostMessageW(hwnd, 0x0010, 0, 0)
                    process.wait(timeout=5)
                except (AssertionError, subprocess.TimeoutExpired):
                    # Only a test-owned process with isolated fixture data.
                    process.terminate()
                    process.wait(timeout=5)


if __name__ == "__main__":
    main()
