"""Compare EXE icons with the canonical ICO and capture shortcut rendering.

Only reads the supplied files and writes reports below --output.
"""
import argparse
import ctypes as c
from ctypes import wintypes as w
import json
from pathlib import Path

from PIL import Image

user32 = c.WinDLL("user32", use_last_error=True)
gdi32 = c.WinDLL("gdi32", use_last_error=True)
shell32 = c.WinDLL("shell32", use_last_error=True)
user32.LoadImageW.argtypes = [w.HINSTANCE, w.LPCWSTR, w.UINT, c.c_int, c.c_int, w.UINT]
user32.LoadImageW.restype = w.HANDLE
user32.PrivateExtractIconsW.argtypes = [w.LPCWSTR, c.c_int, c.c_int, c.c_int,
                                      c.POINTER(w.HICON), c.POINTER(w.UINT), w.UINT, w.UINT]
user32.PrivateExtractIconsW.restype = w.UINT
user32.DestroyIcon.argtypes = [w.HICON]
user32.DrawIconEx.argtypes = [w.HDC, c.c_int, c.c_int, w.HICON, c.c_int, c.c_int,
                            w.UINT, w.HBRUSH, w.UINT]
gdi32.CreateCompatibleDC.argtypes = [w.HDC]
gdi32.CreateCompatibleDC.restype = w.HDC
gdi32.SelectObject.argtypes = [w.HDC, w.HANDLE]
gdi32.SelectObject.restype = w.HANDLE
gdi32.DeleteObject.argtypes = [w.HANDLE]
gdi32.DeleteDC.argtypes = [w.HDC]


class BitmapInfo(c.Structure):
    _fields_ = [("size", w.DWORD), ("width", w.LONG), ("height", w.LONG),
                ("planes", w.WORD), ("bits", w.WORD), ("compression", w.DWORD),
                ("image_size", w.DWORD), ("xppm", w.LONG), ("yppm", w.LONG),
                ("used", w.DWORD), ("important", w.DWORD), ("color", w.DWORD)]


class ShellFileInfo(c.Structure):
    _fields_ = [("icon", w.HICON), ("index", c.c_int), ("attributes", w.DWORD),
                ("display_name", w.WCHAR * 260), ("type_name", w.WCHAR * 80)]


gdi32.CreateDIBSection.argtypes = [w.HDC, c.POINTER(BitmapInfo), w.UINT,
                                 c.POINTER(c.c_void_p), w.HANDLE, w.DWORD]
gdi32.CreateDIBSection.restype = w.HBITMAP
shell32.SHGetFileInfoW.argtypes = [w.LPCWSTR, w.DWORD, c.POINTER(ShellFileInfo), w.UINT, w.UINT]
shell32.SHGetFileInfoW.restype = c.c_size_t


def render(icon, size, background):
    dc = gdi32.CreateCompatibleDC(None)
    info = BitmapInfo(size=40, width=size, height=-size, planes=1, bits=32)
    pixels = c.c_void_p()
    bitmap = gdi32.CreateDIBSection(dc, c.byref(info), 0, c.byref(pixels), None, 0)
    assert bitmap and pixels.value, "Cannot create icon test bitmap"
    old = gdi32.SelectObject(dc, bitmap)
    try:
        c.memset(pixels, background, size * size * 4)
        assert user32.DrawIconEx(dc, 0, 0, icon, size, size, 0, None, 3), "Cannot draw icon"
        return Image.frombytes("RGB", (size, size), c.string_at(pixels, size * size * 4),
                               "raw", "BGRX")
    finally:
        gdi32.SelectObject(dc, old)
        gdi32.DeleteObject(bitmap)
        gdi32.DeleteDC(dc)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--exe", type=Path, required=True)
    parser.add_argument("--ico", type=Path, required=True)
    parser.add_argument("--shortcut", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    assert all(p.is_file() for p in (args.exe, args.ico, args.shortcut))
    args.output.mkdir(parents=True, exist_ok=False)
    checks = []
    for size in (16, 20, 24, 32, 40, 48, 64, 128, 256):
        expected = user32.LoadImageW(None, str(args.ico.resolve()), 1, size, size, 0x10)
        actual = w.HICON()
        icon_id = w.UINT()
        count = user32.PrivateExtractIconsW(str(args.exe.resolve()), 0, size, size,
                                           c.byref(actual), c.byref(icon_id), 1, 0)
        assert expected and count == 1 and actual.value, f"Cannot extract EXE icon at {size}px"
        try:
            for background in (0, 255):
                source = render(expected, size, background)
                exe_image = render(actual, size, background)
                assert source.tobytes() == exe_image.tobytes(), f"EXE icon differs at {size}px"
            exe_image.save(args.output / f"exe-{size}.png")
            checks.append(f"exe-{size}: identical")
        finally:
            user32.DestroyIcon(expected)
            user32.DestroyIcon(actual)
    for size, flag in ((16, 1), (32, 0)):
        info = ShellFileInfo()
        assert shell32.SHGetFileInfoW(str(args.shortcut.resolve()), 0, c.byref(info),
                                     c.sizeof(info), 0x100 | flag) and info.icon
        try:
            # The Shell adds an arrow and may resample its cached icon frame.
            # Record this for visual review; byte equality applies to the EXE.
            render(info.icon, size, 255).save(args.output / f"shortcut-{size}.png")
            checks.append(f"shortcut-{size}: Windows rendering captured for visual review")
        finally:
            user32.DestroyIcon(info.icon)
    report = dict(status="PASS", executable=str(args.exe.resolve()), checks=checks,
                  tray="Shared embedded ICO verified in DesktopPet.Smoke")
    (args.output / "icon-probe.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
    print("PASS: EXE icons 16-256px match the shared ICO; Windows shortcut rendering captured")


if __name__ == "__main__":
    main()
