# Instructions for GitHub Copilot

All instructions for AI coding agents live in [`AGENTS.md`](../AGENTS.md) at the
repository root. Read it first, then `docs/plan/PLAN.md` and the task in
`docs/plan/tasks.yaml`.

Short version:

- One task from `docs/plan/tasks.yaml` equals one pull request; the title follows
  Conventional Commits and ends with the task id, for example
  `feat(agent): add metric engine (T205)`.
- Run the global checks from `AGENTS.md` (`dotnet format --verify-no-changes`,
  `dotnet build -warnaserror`, `dotnet test`, `ruff`, `mypy`, `pytest`,
  `python scripts/plan/validate.py`) and the task's `verify` commands.
- Set the task's `status` to `done` in the same pull request.
- Never splice variable values into scripts, never add external network calls or
  secrets, and never change decisions or wire formats silently.
- Pin GitHub Actions by full commit SHA with the version in a comment.
