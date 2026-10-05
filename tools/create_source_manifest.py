"""Create or verify hashes for the published prompts and animation source files."""
from __future__ import annotations

import argparse
import hashlib
from pathlib import Path
import sys

ROOT = Path(__file__).resolve().parents[1]
ASSET_DIRS = ("workflow/prompts", "workflow/source-videos", "workflow/reference-images")
MANIFEST = ROOT / "workflow/source-materials.sha256"


def expected_lines() -> list[str]:
    files = sorted(path for folder in ASSET_DIRS for path in (ROOT / folder).rglob("*") if path.is_file())
    lines = []
    for path in files:
        digest = hashlib.sha256(path.read_bytes()).hexdigest()
        lines.append(f"{digest}  {path.relative_to(ROOT).as_posix()}")
    return lines


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--check", action="store_true", help="verify the checked-in manifest instead of replacing it")
    args = parser.parse_args()
    lines = expected_lines()
    if args.check:
        actual = MANIFEST.read_text(encoding="utf-8").splitlines()
        if actual != lines:
            print("Source-material SHA-256 manifest does not match the files.", file=sys.stderr)
            return 1
        print(f"Verified {len(lines)} source-material files.")
        return 0
    MANIFEST.write_text("\n".join(lines) + "\n", encoding="utf-8")
    print(f"Wrote {len(lines)} source-material hashes to {MANIFEST.relative_to(ROOT).as_posix()}.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
