# Contributing to QoffeeAutomat

Thank you for your interest. The project is specified in
[`docs/plan/PLAN.md`](docs/plan/PLAN.md) and broken down into tasks in
[`docs/plan/tasks.yaml`](docs/plan/tasks.yaml). Please read
[`AGENTS.md`](AGENTS.md) as well: it describes the repository map and the rules
that apply to human and AI contributors alike.

By participating you agree to the [Code of Conduct](CODE_OF_CONDUCT.md). Report
security problems privately as described in [`SECURITY.md`](SECURITY.md), never in a
public issue.

## Workflow

1. Pick a task: the task with the lowest id whose `status` is `todo`, whose `actor`
   includes `agent` (or `human`, if you are the maintainer) and whose `depends_on`
   tasks are all `done`. `python scripts/plan/next_task.py` prints it. For changes
   that are not part of the plan, open an issue first.
2. Read every section of `PLAN.md` that the task lists in its `spec` field.
3. Work on a branch named `task/<id>-<slug>`, for example `task/t205-metric-engine`.
   **One task equals one pull request.**
4. Run every command in the task's `verify` list and the global checks below.
5. In the same pull request set the task's `status` to `done` in `tasks.yaml`. If you
   are blocked, set `status: blocked` and add a `blocked_reason`.
6. Open the pull request. It is squash-merged, so the pull request title becomes the
   commit message on `main`.

Never change a decision (`D-nnn`) or a wire format silently. If a change requires it,
change `PLAN.md` in the same pull request, add or amend the decision row and bump the
protocol or schema version when the change is breaking.

## Commit messages and pull request titles

Titles follow [Conventional Commits](https://www.conventionalcommits.org/) and end
with the task id:

```
feat(agent): add metric engine (T205)
fix(integration): reconnect after a PC start (T501)
```

Allowed types: `feat`, `fix`, `docs`, `style`, `refactor`, `perf`, `test`, `build`,
`ci`, `chore`, `revert`.

Allowed scopes: `agent`, `core`, `contracts`, `protocol`, `ui`, `integration`,
`windows`, `linux`, `hub`, `ci`, `docs`, `deps`, `repo`.

release-please builds the changelog and the version number from these titles.

## Running the checks

You need the .NET SDK version from `global.json` and Python 3.14 or newer.

```bash
python -m venv .venv
. .venv/bin/activate            # Windows: .venv\Scripts\activate
python -m pip install -r requirements-dev.txt
```

The global checks that every pull request must pass:

```bash
dotnet format --verify-no-changes
dotnet build -warnaserror
dotnet test
ruff check . && ruff format --check .
mypy custom_components/qoffeeautomat
pytest tests_integration
python scripts/plan/validate.py
```

A development container with all tools is available in `.devcontainer/`.
`scripts/dev/run-agent.sh` starts the agent with the simulated platform, and
`scripts/dev/run-ha.sh` starts a Home Assistant development instance with the
integration mounted.

## Windows-only code

Code in `QoffeeAutomat.Platform.Windows` and `QoffeeAutomat.SessionHost` cannot run on
Linux. It is built everywhere and tested by the `windows-latest` CI job. Tasks marked
`human_verify: true` additionally need a check on a real Windows desktop session.

## Rules for the code base

- C#: nullable reference types, warnings as errors, file-scoped namespaces, async all
  the way, `System.Text.Json` source generation in `Contracts`, central package
  management.
- Python: full type hints, ruff, no blocking I/O in the event loop.
- Never splice user or Home Assistant supplied values into script text (D-012).
- Never add network calls to external hosts or embed secrets.
- Pin GitHub Actions by commit SHA with the version in a comment.

## Reporting bugs and proposing features

Use the issue forms: bug report, feature request, or command and sensor template
request. Questions and show-and-tell posts belong in
[Discussions](https://github.com/toeck98/QoffeeAutomat/discussions).
