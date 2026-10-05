"""Exercise the real WPF pet at the current desktop DPI using an isolated pack.

Run against a Debug app PID. Captures only its window, and restores the cursor.
"""
import argparse
import ctypes
import json
import time
from ctypes import wintypes
from pathlib import Path

from PIL import ImageGrab

user32 = ctypes.windll.user32
user32.SetProcessDpiAwarenessContext(ctypes.c_void_p(-4))
user32.GetDpiForWindow.argtypes = [wintypes.HWND]
user32.GetDpiForWindow.restype = wintypes.UINT


class RECT(ctypes.Structure):
    _fields_ = [(name, ctypes.c_long) for name in ("left", "top", "right", "bottom")]


class POINT(ctypes.Structure):
    _fields_ = [("x", ctypes.c_long), ("y", ctypes.c_long)]


def locate(pid):
    found = []
    callback_type = ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)

    def inspect(hwnd, _):
        owner = wintypes.DWORD()
        user32.GetWindowThreadProcessId(hwnd, ctypes.byref(owner))
        if owner.value == pid and title(hwnd).startswith("DesktopPet"):
            found.append(hwnd)
        return True

    # A self-contained release can take longer on its first cold startup.
    deadline = time.monotonic() + 10
    while not found and time.monotonic() < deadline:
        user32.EnumWindows(callback_type(inspect), 0)
        if not found:
            time.sleep(.1)
    assert len(found) == 1, f"Expected one pet for PID {pid}, got {found}"
    # Start-Process uses Hidden for helpers; explicitly reveal only the target
    # WPF window during this desktop verification.
    user32.ShowWindow(found[0], 5)
    return found[0]


def title(hwnd):
    value = ctypes.create_unicode_buffer(256)
    user32.GetWindowTextW(hwnd, value, len(value))
    return value.value


def rect(hwnd):
    value = RECT()
    user32.GetWindowRect(hwnd, ctypes.byref(value))
    return value.left, value.top, value.right, value.bottom


def wait_for(hwnd, action, seconds):
    end = time.monotonic() + seconds
    while time.monotonic() < end:
        if title(hwnd) == f"DesktopPet [{action}]":
            return
        time.sleep(.01)
    raise AssertionError(f"Expected {action}, got {title(hwnd)}")


def click(hwnd, count=1):
    left, top, right, bottom = rect(hwnd)
    user32.SetCursorPos((left + right) // 2, (top + bottom) // 2)
    time.sleep(.1)
    for _ in range(count):
        user32.SetCursorPos((left + right) // 2, (top + bottom) // 2)
        user32.mouse_event(0x0002, 0, 0, 0, 0)
        user32.mouse_event(0x0004, 0, 0, 0, 0)
        time.sleep(.07)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("pid", type=int)
    parser.add_argument("--output", type=Path)
    parser.add_argument("--close-only", action="store_true")
    parser.add_argument("--show-only", action="store_true")
    args = parser.parse_args()
    hwnd = locate(args.pid)
    if args.show_only:
        print(f"Shown pet for PID {args.pid}, HWND {hwnd}, rect={rect(hwnd)}")
        return
    if args.close_only:
        user32.PostMessageW(hwnd, 0x0010, 0, 0)
        print(f"Requested graceful close for PID {args.pid}, HWND {hwnd}")
        return
    if args.output is None:
        parser.error("--output is required for verification")
    args.output.mkdir(parents=True, exist_ok=True)
    original_cursor = POINT()
    user32.GetCursorPos(ctypes.byref(original_cursor))
    captures = []

    def capture(name):
        # Cursor is moved out of the pet before capturing its small window only.
        left, top, right, bottom = rect(hwnd)
        user32.SetCursorPos(left - 15, top)
        time.sleep(.03)
        ImageGrab.grab(bbox=(left, top, right, bottom), all_screens=True).save(args.output / f"{name}.png")
        captures.append(name)

    try:
        wait_for(hwnd, "sleep_sit", 2)
        initial = rect(hwnd)
        dpi = user32.GetDpiForWindow(hwnd)
        capture("startup-sleep-zzz")
        time.sleep(.6)
        capture("sleep-zzz-next-phase")
        click(hwnd)
        wait_for(hwnd, "awake_idle", .3)
        capture("single-click-standing")
        assert rect(hwnd) == initial, "Window moved on waking"
        wait_for(hwnd, "blink", 4.5)
        capture("standing-blink")
        assert rect(hwnd) == initial, "Window moved during blink"
        wait_for(hwnd, "awake_idle", .3)
        wait_for(hwnd, "wave", 5)
        capture("automatic-wave")
        wait_for(hwnd, "awake_idle", 1)
        assert rect(hwnd) == initial, "Window moved during automatic wave"
        click(hwnd, 2)
        wait_for(hwnd, "wave", .3)
        capture("double-click-wave")
        wait_for(hwnd, "awake_idle", 1)
        wait_for(hwnd, "sleep_sit", 12)
        assert rect(hwnd) == initial, "Window moved on returning to sleep"
        capture("timeout-sleep")
        time.sleep(4.7)
        assert title(hwnd) == "DesktopPet [sleep_sit]", "Random check woke sleeping pet"
        # Dragging sleeping pet must move its window without changing its pose.
        left, top, right, bottom = rect(hwnd)
        x, y = (left + right) // 2, (top + bottom) // 2
        user32.SetCursorPos(x, y)
        time.sleep(.1)
        user32.mouse_event(0x0002, 0, 0, 0, 0)
        for delta in (10, 20, 30, 40):
            time.sleep(.04)
            user32.SetCursorPos(x + delta, y - 25)
        time.sleep(.06)
        user32.mouse_event(0x0004, 0, 0, 0, 0)
        time.sleep(.15)
        after = rect(hwnd)
        assert after[0] - left == 40 and after[1] - top == -25, (initial, after)
        assert title(hwnd) == "DesktopPet [sleep_sit]", "Drag changed sleeping pose"
        report = dict(status="PASS", dpi=dpi, initialRect=initial, afterDragRect=after,
                      captures=captures, checks=["startup", "immediate single click", "standing blink", "automatic wave",
                      "double click", "11-second sleep", "no random waking", "fixed ground", "drag"])
        (args.output / "window-probe.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
        print(json.dumps(report))
    finally:
        user32.SetCursorPos(original_cursor.x, original_cursor.y)


if __name__ == "__main__":
    main()
