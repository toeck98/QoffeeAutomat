#!/usr/bin/env python3
"""Validate docs/plan/tasks.yaml against docs/plan/PLAN.md (task T005).

Checks the schema of tasks.yaml, unique task ids, existing dependencies, an acyclic
dependency graph, milestone, requirement and section references against PLAN.md, the
allowed status values, that done tasks only depend on done tasks, and that every task
area has a matching `area:*` label in .github/labels.yml. Only PyYAML is required.

Usage:
    python scripts/plan/validate.py [--plan-dir DIR] [--labels-file FILE]

Exit status: 0 when the plan is valid, 1 when problems were found, 2 when a file
cannot be read.
"""

import argparse
import re
import sys
from pathlib import Path
from typing import Any

import yaml

REPO_ROOT = Path(__file__).resolve().parents[2]
DEFAULT_PLAN_DIR = REPO_ROOT / "docs" / "plan"
DEFAULT_LABELS_FILE = REPO_ROOT / ".github" / "labels.yml"

TASK_ID = re.compile(r"^T\d{3}$")
MILESTONE_ID = re.compile(r"^M\d$")
SECTION_ID = re.compile(r"^S\d{2}$")
REQUIREMENT_ID = re.compile(r"^R-\d{2}$")
SECTION_HEADING = re.compile(r"^## (S\d{2})\b", re.MULTILINE)
REQUIREMENT_ROW = re.compile(r"^\| (R-\d{2}) \|", re.MULTILINE)

ALLOWED_STATUSES = ["todo", "in_progress", "blocked", "done"]
ALLOWED_SIZES = {"S", "M", "L"}
ALLOWED_ACTORS = {"agent", "human", "agent+human"}
ALLOWED_PLATFORMS = {"any", "windows", "linux"}
ALLOWED_AREAS = {
    "repo",
    "ci",
    "github",
    "protocol",
    "contracts",
    "core",
    "agent",
    "api",
    "windows",
    "sessionhost",
    "ui",
    "integration",
    "packaging",
    "docs",
    "security",
    "linux",
    "hub",
}

TOP_LEVEL_KEYS = [
    "schema_version",
    "project",
    "spec_file",
    "status_values",
    "milestones",
    "tasks",
]
MILESTONE_KEYS = ["id", "title", "goal", "exit_criteria"]
STRING_FIELDS = [
    "title",
    "milestone",
    "size",
    "actor",
    "platform",
    "description",
    "status",
]
LIST_FIELDS = [
    "area",
    "depends_on",
    "requirements",
    "spec",
    "deliverables",
    "acceptance",
    "verify",
]
NON_EMPTY_LIST_FIELDS = ["area", "spec", "deliverables", "acceptance"]
OPTIONAL_FIELDS = ["human_verify", "detail", "blocked_reason"]


def _is_str_list(value: Any) -> bool:
    return isinstance(value, list) and all(isinstance(v, str) and v for v in value)


def _str_list(task: dict[str, Any], key: str) -> list[str]:
    """Return task[key] when it is a list of strings, otherwise an empty list."""
    value = task.get(key)
    return value if _is_str_list(value) else []


def _check_milestones(data: dict[str, Any], errors: list[str]) -> set[str]:
    ids: set[str] = set()
    milestones = data.get("milestones")
    if not isinstance(milestones, list):
        return ids
    for index, milestone in enumerate(milestones):
        where = f"milestones[{index}]"
        if not isinstance(milestone, dict):
            errors.append(f"{where} must be a mapping")
            continue
        missing = [k for k in MILESTONE_KEYS if k not in milestone]
        errors.extend(f"{where} is missing '{k}'" for k in missing)
        mid = milestone.get("id")
        if not isinstance(mid, str) or not MILESTONE_ID.match(mid):
            errors.append(f"{where} has an invalid id {mid!r}")
        elif mid in ids:
            errors.append(f"duplicate milestone id {mid}")
        else:
            ids.add(mid)
        if not _is_str_list(milestone.get("exit_criteria")):
            errors.append(f"{where} exit_criteria must be a list of strings")
    return ids


def _check_fields(task: dict[str, Any], where: str) -> list[str]:
    """Check presence, type and optional fields of one task."""
    errors: list[str] = []
    known = {"id", *STRING_FIELDS, *LIST_FIELDS, *OPTIONAL_FIELDS}
    errors.extend(f"{where}: unknown field '{k}'" for k in task if k not in known)
    for key in STRING_FIELDS:
        value = task.get(key)
        if not isinstance(value, str) or not value.strip():
            errors.append(f"{where}: '{key}' must be a non-empty string")
    for key in LIST_FIELDS:
        if key not in task:
            errors.append(f"{where}: missing '{key}'")
        elif not _is_str_list(task[key]):
            errors.append(f"{where}: '{key}' must be a list of non-empty strings")
        elif key in NON_EMPTY_LIST_FIELDS and not task[key]:
            errors.append(f"{where}: '{key}' must not be empty")
    if "human_verify" in task and not isinstance(task["human_verify"], bool):
        errors.append(f"{where}: 'human_verify' must be true or false")
    if "detail" in task and task["detail"] != "epic":
        errors.append(f"{where}: 'detail' may only be 'epic'")
    reason = task.get("blocked_reason")
    if task.get("status") == "blocked" and not (isinstance(reason, str) and reason):
        errors.append(f"{where}: a blocked task needs a 'blocked_reason'")
    if task.get("status") != "blocked" and reason is not None:
        errors.append(f"{where}: 'blocked_reason' is only allowed when blocked")
    return errors


def _check_values(task: dict[str, Any], where: str, ctx: dict[str, Any]) -> list[str]:
    """Check enum values and references of one task."""
    errors: list[str] = []
    milestone = task.get("milestone")
    task_id = str(task.get("id"))
    if isinstance(milestone, str) and milestone not in ctx["milestones"]:
        errors.append(f"{where}: unknown milestone {milestone}")
    if TASK_ID.match(task_id) and milestone != f"M{task_id[1]}":
        errors.append(f"{where}: first digit of the id does not match {milestone}")
    if task.get("size") not in ALLOWED_SIZES:
        errors.append(f"{where}: invalid size {task.get('size')!r}")
    if task.get("actor") not in ALLOWED_ACTORS:
        errors.append(f"{where}: invalid actor {task.get('actor')!r}")
    if task.get("platform") not in ALLOWED_PLATFORMS:
        errors.append(f"{where}: invalid platform {task.get('platform')!r}")
    if task.get("status") not in ALLOWED_STATUSES:
        errors.append(f"{where}: invalid status {task.get('status')!r}")
    for area in _str_list(task, "area"):
        if area not in ALLOWED_AREAS:
            errors.append(f"{where}: unknown area '{area}'")
        elif ctx["area_labels"] is not None and area not in ctx["area_labels"]:
            errors.append(f"{where}: area '{area}' has no label 'area:{area}'")
    unknown_reqs = [r for r in _str_list(task, "requirements") if r not in ctx["reqs"]]
    errors.extend(f"{where}: unknown requirement '{r}'" for r in unknown_reqs)
    unknown_specs = [s for s in _str_list(task, "spec") if s not in ctx["sections"]]
    errors.extend(f"{where}: unknown spec section '{s}'" for s in unknown_specs)
    return errors


def _find_cycle(graph: dict[str, list[str]]) -> list[str] | None:
    """Return the ids of one dependency cycle, or None when the graph is acyclic."""
    white, grey, black = 0, 1, 2
    color = dict.fromkeys(graph, white)
    for start in sorted(graph):
        if color[start] != white:
            continue
        stack: list[tuple[str, int]] = [(start, 0)]
        path: list[str] = []
        while stack:
            node, index = stack.pop()
            if index == 0:
                color[node] = grey
                path.append(node)
            deps = graph[node]
            if index >= len(deps):
                color[node] = black
                path.pop()
                continue
            stack.append((node, index + 1))
            dep = deps[index]
            if color[dep] == grey:
                return [*path[path.index(dep) :], dep]
            if color[dep] == white:
                stack.append((dep, 0))
    return None


def _check_graph(tasks: list[dict[str, Any]]) -> list[str]:
    """Check unique ids, dependencies, done-ness and cycles."""
    errors: list[str] = []
    by_id: dict[str, dict[str, Any]] = {}
    for task in tasks:
        task_id = str(task.get("id"))
        if not TASK_ID.match(task_id):
            continue
        if task_id in by_id:
            errors.append(f"duplicate task id {task_id}")
        by_id[task_id] = task

    graph: dict[str, list[str]] = {}
    for task_id, task in by_id.items():
        graph[task_id] = []
        for dep in _str_list(task, "depends_on"):
            if dep == task_id:
                errors.append(f"{task_id}: depends on itself")
            elif dep not in by_id:
                errors.append(f"{task_id}: depends_on unknown task '{dep}'")
            else:
                graph[task_id].append(dep)
                dep_status = by_id[dep].get("status")
                if task.get("status") == "done" and dep_status != "done":
                    errors.append(f"{task_id}: is done but {dep} is {dep_status}")

    cycle = _find_cycle(graph)
    if cycle is not None:
        errors.append("dependency cycle: " + " -> ".join(cycle))
    return errors


def _load_yaml(path: Path) -> Any:
    with path.open(encoding="utf-8") as handle:
        return yaml.safe_load(handle)


def _load_area_labels(labels_file: Path) -> set[str]:
    labels = _load_yaml(labels_file) or []
    names = [e["name"] for e in labels if isinstance(e, dict) and "name" in e]
    return {n.removeprefix("area:") for n in names if str(n).startswith("area:")}


def validate_plan(plan_dir: Path, labels_file: Path | None = None) -> list[str]:
    """Return the problems found in the plan in plan_dir (empty when it is valid)."""
    data = _load_yaml(plan_dir / "tasks.yaml")
    if not isinstance(data, dict):
        return ["tasks.yaml must contain a mapping at the top level"]

    errors = [f"missing top-level key '{k}'" for k in TOP_LEVEL_KEYS if k not in data]
    if data.get("schema_version") != 1:
        errors.append(f"unsupported schema_version {data.get('schema_version')!r}")
    if data.get("status_values") != ALLOWED_STATUSES:
        errors.append(f"status_values must be {ALLOWED_STATUSES}")

    spec_file = plan_dir / str(data.get("spec_file", "PLAN.md"))
    if not spec_file.is_file():
        return [*errors, f"spec_file {spec_file} does not exist"]
    plan_text = spec_file.read_text(encoding="utf-8")

    area_labels = None
    if labels_file is not None and labels_file.is_file():
        area_labels = _load_area_labels(labels_file)
    ctx: dict[str, Any] = {
        "milestones": _check_milestones(data, errors),
        "reqs": set(REQUIREMENT_ROW.findall(plan_text)),
        "sections": set(SECTION_HEADING.findall(plan_text)),
        "area_labels": area_labels,
    }

    tasks = data.get("tasks")
    if not isinstance(tasks, list) or not tasks:
        return [*errors, "'tasks' must be a non-empty list"]

    valid_tasks: list[dict[str, Any]] = []
    for index, task in enumerate(tasks):
        if not isinstance(task, dict):
            errors.append(f"tasks[{index}] must be a mapping")
            continue
        where = str(task.get("id", f"tasks[{index}]"))
        if not TASK_ID.match(where):
            errors.append(f"{where}: invalid task id (expected T and 3 digits)")
        errors.extend(_check_fields(task, where))
        errors.extend(_check_values(task, where, ctx))
        valid_tasks.append(task)

    errors.extend(_check_graph(valid_tasks))
    return errors


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Validate docs/plan/tasks.yaml.")
    parser.add_argument(
        "--plan-dir",
        type=Path,
        default=DEFAULT_PLAN_DIR,
        help="directory with tasks.yaml and PLAN.md (default: docs/plan)",
    )
    parser.add_argument(
        "--labels-file",
        type=Path,
        default=DEFAULT_LABELS_FILE,
        help="label definitions for the area check (default: .github/labels.yml)",
    )
    args = parser.parse_args(argv)

    try:
        errors = validate_plan(args.plan_dir, args.labels_file)
        data = _load_yaml(args.plan_dir / "tasks.yaml")
    except (OSError, yaml.YAMLError) as exc:
        print(f"ERROR: cannot read the plan: {exc}", file=sys.stderr)
        return 2

    if errors:
        for error in errors:
            print(f"ERROR: {error}", file=sys.stderr)
        print(f"{len(errors)} problem(s) found in {args.plan_dir}", file=sys.stderr)
        return 1

    tasks = data["tasks"]
    done = len([t for t in tasks if t["status"] == "done"])
    print(f"OK: {len(tasks)} tasks ({done} done), {len(data['milestones'])} milestones")
    return 0


if __name__ == "__main__":
    sys.exit(main())
