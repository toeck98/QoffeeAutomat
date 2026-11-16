"""Tests for scripts/plan/validate.py and scripts/plan/next_task.py (task T005)."""

import shutil
import subprocess
import sys
from collections.abc import Callable
from pathlib import Path
from typing import Any

import pytest
import yaml

ROOT = Path(__file__).resolve().parents[2]
VALIDATE = ROOT / "scripts" / "plan" / "validate.py"
NEXT_TASK = ROOT / "scripts" / "plan" / "next_task.py"
PLAN_DIR = ROOT / "docs" / "plan"
LABELS = ROOT / ".github" / "labels.yml"


def run(script: Path, *args: str) -> subprocess.CompletedProcess[str]:
    """Run a script with the current interpreter and capture its output."""
    return subprocess.run(
        [sys.executable, str(script), *args],
        capture_output=True,
        text=True,
        check=False,
    )


def load_tasks() -> dict[str, Any]:
    data: dict[str, Any] = yaml.safe_load(
        (PLAN_DIR / "tasks.yaml").read_text(encoding="utf-8")
    )
    return data


def find(data: dict[str, Any], task_id: str) -> dict[str, Any]:
    for task in data["tasks"]:
        if task["id"] == task_id:
            found: dict[str, Any] = task
            return found
    raise AssertionError(f"task {task_id} not found")


def write_plan(tmp_path: Path, mutate: Callable[[dict[str, Any]], None]) -> Path:
    """Copy PLAN.md and a modified copy of tasks.yaml into tmp_path/plan."""
    plan_dir = tmp_path / "plan"
    plan_dir.mkdir()
    shutil.copy(PLAN_DIR / "PLAN.md", plan_dir / "PLAN.md")
    data = load_tasks()
    mutate(data)
    text = yaml.safe_dump(data, sort_keys=False, allow_unicode=True)
    (plan_dir / "tasks.yaml").write_text(text, encoding="utf-8")
    return plan_dir


def validate(plan_dir: Path) -> subprocess.CompletedProcess[str]:
    """Validate the plan in plan_dir together with the real label file."""
    return run(VALIDATE, "--plan-dir", str(plan_dir), "--labels-file", str(LABELS))


def test_committed_plan_is_valid() -> None:
    result = run(VALIDATE)
    assert result.returncode == 0, result.stderr
    assert result.stdout.startswith("OK:")


@pytest.mark.parametrize(
    ("task_id", "field", "value", "expected"),
    [
        ("T002", "depends_on", ["T00l"], "unknown task 'T00l'"),
        ("T002", "depends_on", ["T002"], "depends on itself"),
        ("T002", "status", "finished", "invalid status"),
        ("T002", "spec", ["S99"], "unknown spec section 'S99'"),
        ("T002", "requirements", ["R-99"], "unknown requirement 'R-99'"),
        ("T002", "area", ["bogus"], "unknown area 'bogus'"),
        ("T002", "milestone", "M7", "does not match M7"),
        ("T002", "milestone", "M42", "unknown milestone M42"),
        ("T002", "size", "XL", "invalid size"),
        ("T002", "actor", "robot", "invalid actor"),
        ("T002", "platform", "beos", "invalid platform"),
        ("T002", "acceptance", [], "'acceptance' must not be empty"),
        ("T002", "status", "blocked", "needs a 'blocked_reason'"),
        ("T002", "blocked_reason", "waiting", "only allowed when blocked"),
        ("T002", "colour", "red", "unknown field 'colour'"),
    ],
)
def test_validate_reports_a_broken_plan(
    tmp_path: Path, task_id: str, field: str, value: Any, expected: str
) -> None:
    def mutate(data: dict[str, Any]) -> None:
        find(data, task_id)[field] = value

    result = validate(write_plan(tmp_path, mutate))
    assert result.returncode == 1
    assert expected in result.stderr


def test_validate_reports_done_depending_on_todo(tmp_path: Path) -> None:
    def mutate(data: dict[str, Any]) -> None:
        find(data, "T002")["status"] = "todo"

    result = validate(write_plan(tmp_path, mutate))
    assert result.returncode == 1
    assert "is done but T002 is todo" in result.stderr


def test_validate_reports_a_dependency_cycle(tmp_path: Path) -> None:
    def mutate(data: dict[str, Any]) -> None:
        find(data, "T002")["depends_on"] = ["T003"]
        find(data, "T003")["depends_on"] = ["T002"]

    result = validate(write_plan(tmp_path, mutate))
    assert result.returncode == 1
    assert "dependency cycle" in result.stderr


def test_validate_reports_a_duplicate_id(tmp_path: Path) -> None:
    def mutate(data: dict[str, Any]) -> None:
        find(data, "T003")["id"] = "T002"

    result = validate(write_plan(tmp_path, mutate))
    assert result.returncode == 1
    assert "duplicate task id T002" in result.stderr


def test_validate_reports_a_missing_top_level_key(tmp_path: Path) -> None:
    def mutate(data: dict[str, Any]) -> None:
        del data["milestones"]

    result = validate(write_plan(tmp_path, mutate))
    assert result.returncode == 1
    assert "missing top-level key 'milestones'" in result.stderr


def test_validate_requires_a_label_for_every_area(tmp_path: Path) -> None:
    labels = tmp_path / "labels.yml"
    labels.write_text('- name: "area:repo"\n  color: "1d76db"\n', encoding="utf-8")
    plan_dir = write_plan(tmp_path, lambda data: None)
    result = run(VALIDATE, "--plan-dir", str(plan_dir), "--labels-file", str(labels))
    assert result.returncode == 1
    assert "has no label 'area:ci'" in result.stderr


def test_validate_fails_for_a_missing_plan(tmp_path: Path) -> None:
    result = run(VALIDATE, "--plan-dir", str(tmp_path))
    assert result.returncode == 2


def test_next_task_returns_a_dependency_free_task_when_only_t001_is_done(
    tmp_path: Path,
) -> None:
    data = load_tasks()
    for task in data["tasks"]:
        task["status"] = "done" if task["id"] == "T001" else "todo"
    tasks_file = tmp_path / "tasks.yaml"
    tasks_file.write_text(yaml.safe_dump(data, sort_keys=False), encoding="utf-8")

    first = run(NEXT_TASK, "--tasks-file", str(tasks_file))
    assert first.returncode == 0
    assert yaml.safe_load(first.stdout)["id"] == "T002"

    listing = run(NEXT_TASK, "--all", "--ids", "--tasks-file", str(tasks_file))
    ids = listing.stdout.split()
    assert ids[0] == "T002"
    assert {"T003", "T004", "T005"} <= set(ids)
    for task_id in ids:
        assert set(find(data, task_id)["depends_on"]) <= {"T001"}


def test_next_task_skips_human_tasks_and_unfinished_dependencies(
    tmp_path: Path,
) -> None:
    def task(task_id: str, actor: str, status: str, deps: list[str]) -> dict[str, Any]:
        return {"id": task_id, "actor": actor, "status": status, "depends_on": deps}

    tasks = [
        task("T001", "agent", "done", []),
        task("T002", "human", "todo", []),
        task("T003", "agent", "todo", ["T002"]),
        task("T004", "agent+human", "todo", ["T001"]),
        task("T005", "agent", "in_progress", []),
    ]
    tasks_file = tmp_path / "tasks.yaml"
    tasks_file.write_text(yaml.safe_dump({"tasks": tasks}), encoding="utf-8")

    result = run(NEXT_TASK, "--all", "--ids", "--tasks-file", str(tasks_file))
    assert result.stdout.split() == ["T004"]


def test_next_task_exits_with_one_when_nothing_is_eligible(tmp_path: Path) -> None:
    done = {"id": "T001", "actor": "agent", "status": "done", "depends_on": []}
    tasks_file = tmp_path / "tasks.yaml"
    tasks_file.write_text(yaml.safe_dump({"tasks": [done]}), encoding="utf-8")

    assert run(NEXT_TASK, "--tasks-file", str(tasks_file)).returncode == 1
    assert run(NEXT_TASK, "--all", "--tasks-file", str(tasks_file)).returncode == 0


def test_next_task_runs_on_the_committed_plan() -> None:
    result = run(NEXT_TASK, "--all")
    assert result.returncode == 0, result.stderr
