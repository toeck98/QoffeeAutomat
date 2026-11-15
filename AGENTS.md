# AGENTS.md

Instructions for AI coding agents (and a useful summary for human contributors).
`CLAUDE.md` and `.github/copilot-instructions.md` point here.

## Project summary

QoffeeAutomat is a PC agent (Windows first, Linux later) that lets Home Assistant
monitor a PC and run user-defined commands on it, plus a matching Home Assistant custom
integration (`custom_components/qoffeeautomat`). They talk over **QoffeeLink**, a
WebSocket protocol with JSON frames. The agent serves a Blazor WebAssembly UI for
authoring commands and sensors. The specification is `docs/plan/PLAN.md`; the work
breakdown is `docs/plan/tasks.yaml`.

Naming: the project is spelled **QoffeeAutomat** (domain `qoffeeautomat`, protocol
QoffeeLink). Use exactly this spelling everywhere (see PLAN.md, Naming).

## Repository map

| Path | Content |
|------|---------|
| `docs/plan/` | `PLAN.md` (specification), `tasks.yaml` (tasks). Read these first. |
| `src/QoffeeAutomat.Contracts` | Protocol messages, DTOs, config document models. BCL only. |
| `src/QoffeeAutomat.Core` | Config store, engines, platform interfaces. References Contracts only. |
| `src/QoffeeAutomat.Agent` | ASP.NET Core host, Kestrel, management API, QoffeeLink server. |
| `src/QoffeeAutomat.Platform.Windows` | Windows providers (`net10.0-windows`). |
| `src/QoffeeAutomat.Platform.Simulated` | Deterministic providers for tests, CI and Linux. |
| `src/QoffeeAutomat.SessionHost` | Per-user-session helper (`net10.0-windows`). |
| `src/QoffeeAutomat.Ui`, `src/QoffeeAutomat.Ui.Components` | Blazor shell and Razor class library. |
| `tests/` | xUnit test projects (`QoffeeAutomat.*.Tests`), `tests/scripts/` for script tests. |
| `custom_components/qoffeeautomat/` | Home Assistant integration. Must stay at the repository root. |
| `tests_integration/` | pytest tests for the integration. |
| `protocol/` | JSON Schema and fixtures, the single source of truth for QoffeeLink (from M1). |
| `scripts/plan/` | `validate.py`, `next_task.py`, `sync_issues.py`. |
| `scripts/github/`, `scripts/dev/` | Repository setup, development helpers. |
| `.github/` | Workflows, issue forms, labels, Dependabot, CODEOWNERS. |
| `assets/brand/` | Placeholder icon and logo; the single source for all icons. |

## Workflow (PLAN.md S01)

1. Read this file, then `docs/plan/PLAN.md`, then `docs/plan/tasks.yaml`.
2. Pick the task with the lowest id whose `status` is `todo`, whose `actor` includes
   `agent` and whose `depends_on` tasks are all `done`:
   `python scripts/plan/next_task.py` (`--all` lists every eligible task).
3. Read every section listed in the task's `spec` field before writing code.
4. Work on a branch named `task/<id>-<slug>`. One task equals one pull request.
5. The pull request title follows Conventional Commits and ends with the task id, for
   example `feat(agent): add metric engine (T205)`. Allowed scopes: `agent`, `core`,
   `contracts`, `protocol`, `ui`, `integration`, `windows`, `linux`, `hub`, `ci`,
   `docs`, `deps`, `repo`.
6. Run every command in the task's `verify` list plus the global checks below. A task
   is `done` only when all `acceptance` items hold and CI is green.
7. In the same pull request set the task's `status` to `done` in `tasks.yaml`. If you
   are blocked, set `status: blocked` and add a `blocked_reason`.
8. Never change a decision (S03) or a wire format (S07) silently. Change the spec in
   the same pull request, add or amend a decision row, and bump the protocol or schema
   version when the change is breaking.
9. Version numbers of NuGet packages, Python packages, GitHub Actions, Home Assistant
   and .NET patch levels are **not** fixed by the plan. Look up the current stable
   release when implementing and pin it (actions by commit SHA, with the version in a
   comment).
10. Tasks with `actor: human` need the repository owner. Prepare everything you can and
    leave a checklist in the pull request description.
11. A task with `detail: epic` is a placeholder whose first step is to refine it into
    concrete tasks in `tasks.yaml` through a pull request.

## Build and test commands

The SDK version is pinned in `global.json`. Python 3.14 or newer is required; install
the tools with `python -m pip install -r requirements-dev.txt` (a virtual environment in
`.venv/` is git-ignored).

Global checks that every pull request must pass (PLAN.md S14):

```bash
dotnet format --verify-no-changes
dotnet build -warnaserror
dotnet test
ruff check . && ruff format --check .
mypy custom_components/qoffeeautomat
pytest tests_integration
python scripts/plan/validate.py
```

Also useful: `pytest tests/scripts` (tests of the plan tooling),
`bash tests/scripts/test_setup_repo.sh`, `actionlint`, `shellcheck scripts/**/*.sh`.

## Conventions (PLAN.md S14)

- C#: nullable reference types on, warnings as errors, analyzers at the latest
  recommended level, file-scoped namespaces, async all the way, `System.Text.Json` source
  generation (no reflection-based serialization in Contracts), central package
  management (`Directory.Packages.props`), no `InternalsVisibleTo` across product
  assemblies.
- Project references: Contracts has none; Core references Contracts only; `Ui.Components`
  references neither Agent nor Core.
- Python: full type hints, ruff rule set aligned with Home Assistant core, no blocking
  I/O in the event loop. `client.py` of the integration has no Home Assistant imports.
- JSON on disk and in the API uses camelCase; QoffeeLink frames use snake_case.
- Do not add a file named `config.yaml`, `config.yml` or `config.json` anywhere outside
  hidden folders (D-034).
- Commit messages and pull request titles use Conventional Commits.

## Windows-only code

`Platform.Windows`, `SessionHost` and `Platform.Windows.Tests` target `net10.0-windows`.
They build on Linux (`EnableWindowsTargeting`) but cannot be executed there. They are
verified by the `windows-latest` CI job. Tasks marked `human_verify: true` additionally
need a check on a real Windows desktop session; leave a checklist for the owner.

## Things you must not do

- Change a decision or a wire format silently (see workflow step 8).
- Add network calls to external hosts to the product (agent, UI, integration). The UI is
  served with local assets only; no CDNs.
- Embed secrets, tokens or passwords in the repository, logs, fixtures or tests.
- Bypass parameter binding: variable values are never spliced into script text or
  command lines (D-012). PowerShell gets bound named parameters, processes get separate
  argv entries.
- Weaken authentication or the file permission rules (D-017, S09), not even on loopback.
- Pin GitHub Actions by tag or branch; use the full commit SHA with a version comment.
- Mark a task `done` without running its `verify` commands and the global checks.
- Push to `main` directly or merge your own pull request.
