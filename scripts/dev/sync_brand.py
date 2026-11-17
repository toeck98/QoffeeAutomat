#!/usr/bin/env python3
"""Copy the brand images of assets/brand/ into the integration (task T507, O-05).

Home Assistant and HACS read the images of a custom integration from
``custom_components/qoffeeautomat/brand/``. ``assets/brand/`` is the single source:
this script copies the files Home Assistant uses and, with ``--check``, fails when
copies differ from their sources, are missing, or ``brand/`` holds anything else.

Usage:
    python scripts/dev/sync_brand.py            copy the files
    python scripts/dev/sync_brand.py --check    verify only, change nothing

Exit status: 0 when the folders are in sync (or were synchronised), 1 otherwise.
"""

import argparse
import shutil
import sys
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[2]
SOURCE_DIR = REPO_ROOT / "assets" / "brand"
TARGET_DIR = REPO_ROOT / "custom_components" / "qoffeeautomat" / "brand"

# The images Home Assistant looks for in a ``brand/`` folder.
BRAND_FILES = (
    "icon.png",
    "icon@2x.png",
    "logo.png",
    "logo@2x.png",
    "dark_logo.png",
    "dark_logo@2x.png",
)


def find_problems(source_dir: Path, target_dir: Path) -> list[str]:
    """Return what differs between the source and the target folder."""
    problems: list[str] = []
    for name in BRAND_FILES:
        source = source_dir / name
        target = target_dir / name
        if not source.is_file():
            problems.append(f"{source} is missing")
        elif not target.is_file():
            problems.append(f"{target} is missing")
        elif source.read_bytes() != target.read_bytes():
            problems.append(f"{target} differs from {source}")
    if target_dir.is_dir():
        problems.extend(
            f"{path} is not a brand file of the integration"
            for path in sorted(target_dir.iterdir())
            if path.name not in BRAND_FILES
        )
    return problems


def sync(source_dir: Path, target_dir: Path) -> None:
    """Copy the brand files and remove other files from the target folder."""
    target_dir.mkdir(parents=True, exist_ok=True)
    for path in target_dir.iterdir():
        if path.name not in BRAND_FILES and path.is_file():
            path.unlink()
    for name in BRAND_FILES:
        shutil.copyfile(source_dir / name, target_dir / name)


def main(argv: list[str] | None = None) -> int:
    """Run the script."""
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument(
        "--check", action="store_true", help="verify only, do not copy anything"
    )
    parser.add_argument("--source", type=Path, default=SOURCE_DIR)
    parser.add_argument("--target", type=Path, default=TARGET_DIR)
    args = parser.parse_args(argv)

    if args.check:
        problems = find_problems(args.source, args.target)
        for problem in problems:
            print(f"error: {problem}", file=sys.stderr)
        if problems:
            print(
                "run `python scripts/dev/sync_brand.py` and commit the result",
                file=sys.stderr,
            )
            return 1
        print("brand images are in sync")
        return 0

    sync(args.source, args.target)
    problems = find_problems(args.source, args.target)
    for problem in problems:
        print(f"error: {problem}", file=sys.stderr)
    if not problems:
        print(f"copied {len(BRAND_FILES)} files to {args.target}")
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main())
