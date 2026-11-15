#!/usr/bin/env python3
"""Sync the tasks of docs/plan/tasks.yaml to GitHub issues (task T010).

For every task one issue titled `<id>: <title>` is created or updated: the body holds
the description, the acceptance list as checkboxes, the dependencies as issue links and
the verify commands; the labels are `task`, one `area:*` label per area, `human` and
`human-verify` where applicable; the milestone is `<id> <title>` (for example
`M0 Foundation`). Issues of tasks with status `done` are closed, all others are open.
Issues are matched by the task id at the start of the title, so running the sync twice
never creates duplicates. tasks.yaml stays the source of truth; this script never
writes to it. Only PyYAML is required, HTTP goes through urllib.

Environment:
    GITHUB_TOKEN           token with issues:write (not needed for --dry-run on a
                           public repository)
    GITHUB_REPOSITORY      OWNER/NAME (default: toeck98/QoffeeAutomat)
    PLAN_PROJECT_NUMBER    number of the GitHub project (v2) that receives the issues
                           (a repository variable); optional
    PLAN_PROJECT_TOKEN     token with the `project` scope for that project; optional

Usage:
    python scripts/plan/sync_issues.py [--dry-run] [--offline] [--repo OWNER/NAME]

--dry-run prints the planned changes and calls no write API. It reads the existing
issues when it can and plans as if there were none when it cannot. --offline never
touches the network (it implies --dry-run).
"""

import argparse
import json
import os
import re
import sys
import time
import urllib.error
import urllib.request
from pathlib import Path
from typing import Any

import yaml

API_URL = "https://api.github.com"
DEFAULT_REPO = "toeck98/QoffeeAutomat"
REPO_ROOT = Path(__file__).resolve().parents[2]
DEFAULT_TASKS_FILE = REPO_ROOT / "docs" / "plan" / "tasks.yaml"
TITLE_ID = re.compile(r"^(T\d{3}): ")
MANAGED_LABELS = {"task", "human", "human-verify", "blocked"}
MANAGED_PREFIX = "area:"
NOTE = (
    "> Managed by `scripts/plan/sync_issues.py` from `docs/plan/tasks.yaml`. "
    "Edit the task there; changes made here are overwritten."
)
ADD_TO_PROJECT = """
mutation($project: ID!, $content: ID!) {
  addProjectV2ItemById(input: {projectId: $project, contentId: $content}) {
    item { id }
  }
}
"""
FIND_PROJECT = """
query($login: String!, $number: Int!) {
  repositoryOwner(login: $login) {
    ... on User { projectV2(number: $number) { id } }
    ... on Organization { projectV2(number: $number) { id } }
  }
}
"""


class GitHubError(Exception):
    """Raised when the GitHub API reports an error."""


class GitHub:
    """Minimal GitHub REST and GraphQL client based on urllib."""

    def __init__(self, token: str | None) -> None:
        self.token = token

    def request(self, method: str, path: str, payload: Any = None) -> Any:
        """Send one request and return the decoded JSON answer (or None)."""
        data = None if payload is None else json.dumps(payload).encode("utf-8")
        request = urllib.request.Request(f"{API_URL}{path}", data=data, method=method)
        request.add_header("Accept", "application/vnd.github+json")
        request.add_header("X-GitHub-Api-Version", "2022-11-28")
        request.add_header("User-Agent", "qoffeeautomat-plan-sync")
        if self.token:
            request.add_header("Authorization", f"Bearer {self.token}")
        if data is not None:
            request.add_header("Content-Type", "application/json")
        try:
            with urllib.request.urlopen(request, timeout=30) as response:
                raw = response.read()
        except urllib.error.HTTPError as exc:
            detail = exc.read().decode("utf-8", errors="replace")
            raise GitHubError(f"{method} {path}: HTTP {exc.code} {detail}") from exc
        except OSError as exc:
            raise GitHubError(f"{method} {path}: {exc}") from exc
        return json.loads(raw) if raw else None

    def get_all(self, path: str) -> list[Any]:
        """Return all items of a paginated list endpoint."""
        separator = "&" if "?" in path else "?"
        items: list[Any] = []
        page = 1
        while True:
            chunk = self.request("GET", f"{path}{separator}per_page=100&page={page}")
            items.extend(chunk)
            if len(chunk) < 100:
                return items
            page += 1

    def graphql(self, query: str, variables: dict[str, Any]) -> Any:
        """Run a GraphQL query and return its data."""
        body = {"query": query, "variables": variables}
        answer = self.request("POST", "/graphql", body)
        if answer.get("errors"):
            raise GitHubError(f"GraphQL: {answer['errors']}")
        return answer["data"]


def issue_title(task: dict[str, Any]) -> str:
    return f"{task['id']}: {task['title']}"


def wanted_labels(task: dict[str, Any]) -> list[str]:
    """Return the labels that the task requires."""
    labels = ["task", *(f"{MANAGED_PREFIX}{area}" for area in task["area"])]
    if task["actor"] in ("human", "agent+human"):
        labels.append("human")
    if task.get("human_verify"):
        labels.append("human-verify")
    if task["status"] == "blocked":
        labels.append("blocked")
    return labels


def is_managed(label: str) -> bool:
    return label in MANAGED_LABELS or label.startswith(MANAGED_PREFIX)


def render_body(task: dict[str, Any], issues: dict[str, dict[str, Any]]) -> str:
    """Render the issue body of a task; issues maps task ids to existing issues."""
    done = task["status"] == "done"
    facts = [
        f"**Milestone:** {task['milestone']}",
        f"**Size:** {task['size']}",
        f"**Actor:** {task['actor']}",
        f"**Platform:** {task['platform']}",
        f"**Status:** {task['status']}",
    ]
    lines = [NOTE, "", " | ".join(facts), ""]
    lines.append(f"**Specification:** {', '.join(task['spec'])}")
    lines.append(f"**Requirements:** {', '.join(task['requirements'])}")
    lines += ["", "## Description", "", str(task["description"]).strip(), ""]
    lines += ["## Acceptance", ""]
    mark = "x" if done else " "
    lines += [f"- [{mark}] {item}" for item in task["acceptance"]]
    lines += ["", "## Depends on", ""]
    if task["depends_on"]:
        for dep in task["depends_on"]:
            issue = issues.get(dep)
            lines.append(f"- #{issue['number']} ({dep})" if issue else f"- {dep}")
    else:
        lines.append("- none")
    lines += ["", "## Deliverables", ""]
    lines += [f"- `{item}`" for item in task["deliverables"]]
    lines += ["", "## Verify", "", "```bash", *task["verify"], "```", ""]
    return "\n".join(lines)


def task_milestone(task: dict[str, Any], titles: dict[str, str]) -> str:
    """Return the GitHub milestone title of a task, for example 'M0 Foundation'."""
    return f"{task['milestone']} {titles[task['milestone']]}"


def desired_fields(
    task: dict[str, Any],
    issue: dict[str, Any] | None,
    issues: dict[str, dict[str, Any]],
    milestone_number: int | None,
) -> dict[str, Any]:
    """Return the issue fields that the task wants (labels merged with manual ones)."""
    manual = []
    if issue is not None:
        manual = [lb["name"] for lb in issue["labels"] if not is_managed(lb["name"])]
    return {
        "title": issue_title(task),
        "body": render_body(task, issues),
        "labels": sorted({*manual, *wanted_labels(task)}),
        "milestone": milestone_number,
        "state": "closed" if task["status"] == "done" else "open",
    }


def changed_fields(issue: dict[str, Any], wanted: dict[str, Any]) -> dict[str, Any]:
    """Return the fields of wanted that differ from the existing issue."""
    changes: dict[str, Any] = {}
    if issue["title"] != wanted["title"]:
        changes["title"] = wanted["title"]
    current_body = (issue.get("body") or "").replace("\r\n", "\n")
    if current_body.strip() != wanted["body"].strip():
        changes["body"] = wanted["body"]
    if sorted(lb["name"] for lb in issue["labels"]) != wanted["labels"]:
        changes["labels"] = wanted["labels"]
    current_milestone = (issue.get("milestone") or {}).get("number")
    if wanted["milestone"] is not None and current_milestone != wanted["milestone"]:
        changes["milestone"] = wanted["milestone"]
    if issue["state"] != wanted["state"]:
        changes["state"] = wanted["state"]
        closing = wanted["state"] == "closed"
        changes["state_reason"] = "completed" if closing else "reopened"
    return changes


def index_issues(items: list[Any]) -> dict[str, dict[str, Any]]:
    """Map task ids to issues; pull requests and issues without an id are skipped."""
    issues: dict[str, dict[str, Any]] = {}
    for item in items:
        if "pull_request" in item:
            continue
        match = TITLE_ID.match(item["title"])
        if match and match.group(1) not in issues:
            issues[match.group(1)] = item
    return issues


def add_to_project(gh: GitHub, owner: str, number: int, issues: list[Any]) -> None:
    """Add the issues to the project; adding an issue twice is harmless."""
    data = gh.graphql(FIND_PROJECT, {"login": owner, "number": number})
    project = (data.get("repositoryOwner") or {}).get("projectV2")
    if not project:
        raise GitHubError(f"project {number} of {owner} not found or not accessible")
    for issue in issues:
        variables = {"project": project["id"], "content": issue["node_id"]}
        gh.graphql(ADD_TO_PROJECT, variables)
    print(f"project: {len(issues)} issue(s) are on project {number}")


def sync(
    data: dict[str, Any],
    gh: GitHub,
    repo: str,
    *,
    dry_run: bool,
    fetch: bool = True,
    delay: float = 0.0,
) -> tuple[dict[str, dict[str, Any]], dict[str, int]]:
    """Create and update the issues; return the issues by task id and the counters."""
    tasks = sorted(data["tasks"], key=lambda t: t["id"])
    titles = {m["id"]: m["title"] for m in data["milestones"]}
    issues: dict[str, dict[str, Any]] = {}
    milestones: dict[str, int] = {}
    if fetch:
        found = gh.get_all(f"/repos/{repo}/issues?state=all&labels=task")
        issues = index_issues(found)
        listed = gh.get_all(f"/repos/{repo}/milestones?state=all")
        milestones = {m["title"]: m["number"] for m in listed}
    counters = {"create": 0, "update": 0, "unchanged": 0}
    warned: set[str] = set()

    def milestone_of(task: dict[str, Any]) -> int | None:
        title = task_milestone(task, titles)
        if fetch and title not in milestones and title not in warned:
            warned.add(title)
            print(f"warning: milestone '{title}' does not exist; run setup-repo.sh")
        return milestones.get(title)

    # Pass 1: create the missing issues, so that pass 2 can link every dependency.
    for task in tasks:
        if task["id"] in issues:
            continue
        fields = desired_fields(task, None, issues, milestone_of(task))
        counters["create"] += 1
        print(f"create  {fields['title']}  labels={fields['labels']}")
        if dry_run:
            continue
        payload = {k: v for k, v in fields.items() if k != "state" and v is not None}
        issues[task["id"]] = gh.request("POST", f"/repos/{repo}/issues", payload)
        time.sleep(delay)

    # Pass 2: bring every issue in line with its task.
    for task in tasks:
        issue = issues.get(task["id"])
        if issue is None:
            continue
        wanted = desired_fields(task, issue, issues, milestone_of(task))
        changes = changed_fields(issue, wanted)
        if not changes:
            counters["unchanged"] += 1
            continue
        counters["update"] += 1
        print(f"update  {wanted['title']}  ({', '.join(sorted(changes))})")
        if not dry_run:
            path = f"/repos/{repo}/issues/{issue['number']}"
            issues[task["id"]] = gh.request("PATCH", path, changes)
            time.sleep(delay)
    return issues, counters


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Sync plan tasks to GitHub issues.")
    parser.add_argument("--dry-run", action="store_true", help="plan only, no writes")
    parser.add_argument("--offline", action="store_true", help="no network (dry run)")
    parser.add_argument(
        "--repo",
        default=os.environ.get("GITHUB_REPOSITORY", DEFAULT_REPO),
        help="OWNER/NAME of the repository",
    )
    parser.add_argument(
        "--tasks-file",
        type=Path,
        default=DEFAULT_TASKS_FILE,
        help="path of tasks.yaml (default: docs/plan/tasks.yaml)",
    )
    parser.add_argument(
        "--delay",
        type=float,
        default=1.0,
        help="seconds between write requests (default: 1.0)",
    )
    parser.add_argument(
        "--no-project",
        action="store_true",
        help="do not add the issues to the project board",
    )
    args = parser.parse_args(argv)
    dry_run = args.dry_run or args.offline

    try:
        with args.tasks_file.open(encoding="utf-8") as handle:
            data = yaml.safe_load(handle)
    except (OSError, yaml.YAMLError) as exc:
        print(f"ERROR: cannot read {args.tasks_file}: {exc}", file=sys.stderr)
        return 2

    token = os.environ.get("GITHUB_TOKEN")
    if not dry_run and not token:
        print("ERROR: GITHUB_TOKEN is not set.", file=sys.stderr)
        return 2
    gh = GitHub(token)

    try:
        try:
            issues, counters = sync(
                data,
                gh,
                args.repo,
                dry_run=dry_run,
                fetch=not args.offline,
                delay=args.delay,
            )
        except GitHubError as exc:
            if not dry_run:
                raise
            print(f"warning: cannot read {args.repo} ({exc}); planning without it")
            issues, counters = sync(
                data, gh, args.repo, dry_run=True, fetch=False, delay=0.0
            )
        number = os.environ.get("PLAN_PROJECT_NUMBER", "").strip()
        if number and not args.no_project:
            if dry_run:
                print(f"project: would add all issues to project {number}")
            else:
                project_gh = GitHub(os.environ.get("PLAN_PROJECT_TOKEN") or token)
                owner = args.repo.split("/")[0]
                add_to_project(project_gh, owner, int(number), list(issues.values()))
        elif not dry_run:
            print("project: PLAN_PROJECT_NUMBER is not set, skipping the project board")
    except (GitHubError, ValueError) as exc:
        print(f"ERROR: {exc}", file=sys.stderr)
        return 1

    mode = "dry run: " if dry_run else ""
    print(
        f"{mode}{counters['create']} to create, {counters['update']} to update, "
        f"{counters['unchanged']} unchanged"
    )
    return 0


if __name__ == "__main__":
    sys.exit(main())
