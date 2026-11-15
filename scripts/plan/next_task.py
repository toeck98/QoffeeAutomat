#!/usr/bin/env python3
"""Print the next eligible task of docs/plan/tasks.yaml as YAML (task T005).

A task is eligible when its status is `todo`, its `actor` includes `agent`, and every
task in its `depends_on` list is `done` (PLAN.md S01, rule 2). Without options the
eligible task with the lowest id is printed; `--all` lists every eligible task, which
is useful for parallel work. Only PyYAML is required.

Usage:
    python scripts/plan/next_task.py [--all] [--ids] [--tasks-file FILE]

Exit status: 0 when a task was printed (or --all was given), 1 when no task is
eligible, 2 when the file cannot be read.
"""

import argparse
import sys
from pathlib import Path
from typing import Any

import yaml

REPO_ROOT = Path(__file__).resolve().parents[2]
DEFAULT_TASKS_FILE = REPO_ROOT / "docs" / "plan" / "tasks.yaml"
YAML_OPTIONS: dict[str, Any] = {"sort_keys": False, "allow_unicode": True, "width": 100}


def eligible_tasks(tasks: list[dict[str, Any]]) -> list[dict[str, Any]]:
    """Return the eligible tasks sorted by id."""
    status = {t["id"]: t["status"] for t in tasks}
    result = [
        t
        for t in tasks
        if t["status"] == "todo"
        and "agent" in t["actor"]
        and all(status.get(dep) == "done" for dep in t["depends_on"])
    ]
    return sorted(result, key=lambda t: t["id"])


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Print the next eligible plan task.")
    parser.add_argument(
        "--all",
        action="store_true",
        help="list every eligible task instead of only the first one",
    )
    parser.add_argument(
        "--ids",
        action="store_true",
        help="print only the task ids, one per line",
    )
    parser.add_argument(
        "--tasks-file",
        type=Path,
        default=DEFAULT_TASKS_FILE,
        help="path of tasks.yaml (default: docs/plan/tasks.yaml)",
    )
    args = parser.parse_args(argv)

    try:
        with args.tasks_file.open(encoding="utf-8") as handle:
            data = yaml.safe_load(handle)
        eligible = eligible_tasks(data["tasks"])
    except (OSError, yaml.YAMLError, KeyError, TypeError) as exc:
        print(f"ERROR: cannot read the task file: {exc}", file=sys.stderr)
        return 2

    selected = eligible if args.all else eligible[:1]
    if args.ids:
        for task in selected:
            print(task["id"])
    elif args.all or selected:
        document = selected if args.all else selected[0]
        print(yaml.safe_dump(document, **YAML_OPTIONS))

    if not selected and not args.all:
        print("No eligible task: all are done, blocked or waiting.", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
