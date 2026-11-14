"""Tests for scripts/plan/sync_issues.py (task T010) with an in-memory GitHub."""

import importlib.util
import subprocess
import sys
from pathlib import Path
from types import ModuleType
from typing import Any

import yaml

ROOT = Path(__file__).resolve().parents[2]
SCRIPT = ROOT / "scripts" / "plan" / "sync_issues.py"
TASKS_FILE = ROOT / "docs" / "plan" / "tasks.yaml"
REPO = "toeck98/QoffeeAutomat"


def load_module() -> ModuleType:
    spec = importlib.util.spec_from_file_location("sync_issues", SCRIPT)
    assert spec is not None
    assert spec.loader is not None
    module = importlib.util.module_from_spec(spec)
    sys.modules["sync_issues"] = module
    spec.loader.exec_module(module)
    return module


sync_issues = load_module()


def load_plan() -> dict[str, Any]:
    data: dict[str, Any] = yaml.safe_load(TASKS_FILE.read_text(encoding="utf-8"))
    return data


class FakeGitHub:
    """Implements the part of the GitHub client that sync() uses."""

    def __init__(self, plan: dict[str, Any]) -> None:
        self.issues: list[dict[str, Any]] = []
        self.requests: list[tuple[str, str]] = []
        self.milestones = [
            {"title": f"{m['id']} {m['title']}", "number": index + 1}
            for index, m in enumerate(plan["milestones"])
        ]

    def writes(self) -> list[tuple[str, str]]:
        return [r for r in self.requests if r[0] in ("POST", "PATCH")]

    def get_all(self, path: str) -> list[Any]:
        self.requests.append(("GET", path))
        if "/milestones" in path:
            return list(self.milestones)
        assert "/issues" in path
        return list(self.issues)

    def request(self, method: str, path: str, payload: Any = None) -> Any:
        self.requests.append((method, path))
        if method == "POST":
            return self.create(payload)
        assert method == "PATCH"
        return self.update(int(path.rsplit("/", 1)[1]), payload)

    def create(self, payload: dict[str, Any]) -> dict[str, Any]:
        number = len(self.issues) + 1
        milestone = payload.get("milestone")
        issue = {
            "number": number,
            "node_id": f"node{number}",
            "title": payload["title"],
            "body": payload["body"],
            "labels": [{"name": name} for name in payload["labels"]],
            "milestone": {"number": milestone} if milestone else None,
            "state": "open",
        }
        self.issues.append(issue)
        return issue

    def update(self, number: int, payload: dict[str, Any]) -> dict[str, Any]:
        issue = self.issues[number - 1]
        for key, value in payload.items():
            if key == "labels":
                issue["labels"] = [{"name": name} for name in value]
            elif key == "milestone":
                issue["milestone"] = {"number": value}
            elif key != "state_reason":
                issue[key] = value
        return issue


def run_sync(
    plan: dict[str, Any], gh: FakeGitHub, *, dry_run: bool = False
) -> tuple[dict[str, dict[str, Any]], dict[str, int]]:
    return sync_issues.sync(plan, gh, REPO, dry_run=dry_run)


def by_id(gh: FakeGitHub, task_id: str) -> dict[str, Any]:
    for issue in gh.issues:
        if issue["title"].startswith(f"{task_id}: "):
            return issue
    raise AssertionError(f"no issue for {task_id}")


def label_names(issue: dict[str, Any]) -> set[str]:
    return {label["name"] for label in issue["labels"]}


def test_dry_run_plans_every_task_and_writes_nothing() -> None:
    plan = load_plan()
    gh = FakeGitHub(plan)
    issues, counters = run_sync(plan, gh, dry_run=True)
    assert counters == {"create": len(plan["tasks"]), "update": 0, "unchanged": 0}
    assert issues == {}
    assert gh.writes() == []
    assert gh.issues == []


def test_first_run_creates_one_issue_per_task() -> None:
    plan = load_plan()
    gh = FakeGitHub(plan)
    _, counters = run_sync(plan, gh)
    assert len(gh.issues) == len(plan["tasks"])
    assert counters["create"] == len(plan["tasks"])
    titles = [issue["title"] for issue in gh.issues]
    assert len(set(titles)) == len(titles)
    assert "T002: Create the .NET solution skeleton" in titles


def test_running_the_sync_twice_creates_no_duplicates() -> None:
    plan = load_plan()
    gh = FakeGitHub(plan)
    run_sync(plan, gh)
    count = len(gh.issues)
    gh.requests.clear()

    _, counters = run_sync(plan, gh)
    assert counters == {"create": 0, "update": 0, "unchanged": len(plan["tasks"])}
    assert len(gh.issues) == count
    assert gh.writes() == []


def test_issues_are_matched_by_the_task_id_in_the_title() -> None:
    plan = load_plan()
    gh = FakeGitHub(plan)
    gh.create(
        {
            "title": "T002: an outdated title",
            "body": "old",
            "labels": ["task"],
        }
    )
    run_sync(plan, gh)
    assert len(gh.issues) == len(plan["tasks"])
    assert by_id(gh, "T002")["title"] == "T002: Create the .NET solution skeleton"
    assert gh.issues[0]["number"] == 1


def test_done_tasks_are_closed_and_open_tasks_stay_open() -> None:
    plan = load_plan()
    gh = FakeGitHub(plan)
    run_sync(plan, gh)
    for task in plan["tasks"]:
        expected = "closed" if task["status"] == "done" else "open"
        assert by_id(gh, task["id"])["state"] == expected, task["id"]


def test_a_task_that_becomes_done_is_closed_by_the_next_sync() -> None:
    plan = load_plan()
    plan["tasks"][-1]["status"] = "todo"
    gh = FakeGitHub(plan)
    run_sync(plan, gh)
    last = plan["tasks"][-1]
    assert by_id(gh, last["id"])["state"] == "open"

    last["status"] = "done"
    _, counters = run_sync(plan, gh)
    assert by_id(gh, last["id"])["state"] == "closed"
    assert counters["update"] >= 1


def test_labels_cover_task_area_actor_and_human_verify() -> None:
    plan = load_plan()
    gh = FakeGitHub(plan)
    run_sync(plan, gh)
    labels = label_names(by_id(gh, "T009"))
    assert {"task", "area:github", "human"} <= labels
    verify_task = next(t for t in plan["tasks"] if t.get("human_verify"))
    assert "human-verify" in label_names(by_id(gh, verify_task["id"]))
    agent_only = label_names(by_id(gh, "T002"))
    assert "human" not in agent_only
    assert "area:repo" in agent_only


def test_manual_labels_survive_the_sync() -> None:
    plan = load_plan()
    gh = FakeGitHub(plan)
    run_sync(plan, gh)
    by_id(gh, "T002")["labels"].append({"name": "needs-triage"})

    _, counters = run_sync(plan, gh)
    assert counters["update"] == 0
    assert "needs-triage" in label_names(by_id(gh, "T002"))


def test_body_has_acceptance_checkboxes_and_dependency_links() -> None:
    plan = load_plan()
    gh = FakeGitHub(plan)
    run_sync(plan, gh)
    t009 = by_id(gh, "T009")
    t010 = by_id(gh, "T010")
    assert f"#{t009['number']} (T009)" in t010["body"]
    assert "- [x] " in t010["body"]
    assert "## Description" in t010["body"]
    todo_task = next(t for t in plan["tasks"] if t["status"] == "todo")
    assert "- [ ] " in by_id(gh, todo_task["id"])["body"]


def test_milestone_number_follows_the_milestone_title() -> None:
    plan = load_plan()
    gh = FakeGitHub(plan)
    run_sync(plan, gh)
    foundation = next(m for m in gh.milestones if m["title"] == "M0 Foundation")
    assert by_id(gh, "T002")["milestone"] == {"number": foundation["number"]}


def test_offline_dry_run_from_the_command_line() -> None:
    result = subprocess.run(
        [sys.executable, str(SCRIPT), "--dry-run", "--offline"],
        capture_output=True,
        text=True,
        check=False,
    )
    plan = load_plan()
    assert result.returncode == 0, result.stderr
    created = [ln for ln in result.stdout.splitlines() if ln.startswith("create  ")]
    assert len(created) == len(plan["tasks"])
    assert "dry run:" in result.stdout.splitlines()[-1]


def test_a_real_run_without_a_token_is_refused() -> None:
    result = subprocess.run(
        [sys.executable, str(SCRIPT)],
        capture_output=True,
        text=True,
        check=False,
        env={"PATH": "/nonexistent"},
    )
    assert result.returncode == 2
    assert "GITHUB_TOKEN" in result.stderr
