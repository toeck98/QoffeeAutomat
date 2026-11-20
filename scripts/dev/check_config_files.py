#!/usr/bin/env python3
"""Fail on a config.yaml, config.yml or config.json where it must not be (D-034, T918).

The Home Assistant Supervisor looks for app definitions with these file names at any
depth of an app repository, so this repository (which is also a HACS repository) may
contain exactly one of them: the app definition of the hub,
``packaging/hub/ha-app/config.yaml``. Files in hidden folders (any path component that
starts with a dot, such as ``.github`` or ``.venv``) are ignored. In a git checkout the
files that git knows (tracked, or untracked and not ignored) are checked, so build
output and virtual environments cannot cause a false alarm; without git the tree is
walked.

Usage:
    python scripts/dev/check_config_files.py [--root DIR]

Exit status: 0 when the tree is clean, 1 when a forbidden file exists.
"""

import argparse
import os
import subprocess  # git is called with fixed arguments, no shell
import sys
from pathlib import Path, PurePosixPath

FORBIDDEN_NAMES = frozenset({"config.yaml", "config.yml", "config.json"})
ALLOWED_PATHS = frozenset({"packaging/hub/ha-app/config.yaml"})
DEFAULT_ROOT = Path(__file__).resolve().parents[2]
GIT_LIST_FILES = ("ls-files", "-z", "--cached", "--others", "--exclude-standard")


def _is_hidden(path: PurePosixPath) -> bool:
    """Tell whether a relative path lies in (or is) a hidden folder or file."""
    return any(part.startswith(".") for part in path.parts)


def _git_files(root: Path) -> list[str] | None:
    """List the files git knows below root, or None without a git checkout."""
    try:
        result = subprocess.run(
            ["git", "-C", str(root), *GIT_LIST_FILES],
            check=True,
            capture_output=True,
        )
    except OSError, subprocess.CalledProcessError:
        return None
    return [name for name in result.stdout.decode("utf-8").split("\0") if name]


def _walked_files(root: Path) -> list[str]:
    """List every file below root, without descending into hidden folders."""
    files: list[str] = []
    for directory, subdirectories, names in os.walk(root):
        subdirectories[:] = [d for d in subdirectories if not d.startswith(".")]
        base = Path(directory).relative_to(root)
        files.extend((base / name).as_posix() for name in names)
    return files


def find_violations(root: Path) -> list[str]:
    """Return the relative paths of forbidden files below root, sorted."""
    names = _git_files(root)
    if names is None:
        names = _walked_files(root)
    violations = []
    for name in names:
        path = PurePosixPath(name)
        if path.name not in FORBIDDEN_NAMES or _is_hidden(path):
            continue
        if path.as_posix() in ALLOWED_PATHS:
            continue
        violations.append(path.as_posix())
    return sorted(violations)


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Check for forbidden config files.")
    parser.add_argument(
        "--root",
        type=Path,
        default=DEFAULT_ROOT,
        help="repository root (default: this checkout)",
    )
    args = parser.parse_args(argv)

    allowed = ", ".join(sorted(ALLOWED_PATHS))
    violations = find_violations(args.root)
    for violation in violations:
        print(
            f"::error file={violation}::{violation}: a file named config.yaml, "
            f"config.yml or config.json is only allowed in a hidden folder or at "
            f"{allowed} (D-034).",
        )
    if violations:
        return 1
    print("No forbidden config.yaml, config.yml or config.json files (D-034).")
    return 0


if __name__ == "__main__":
    sys.exit(main())
