## Summary

<!-- What does this change and why? Link the task or issue. -->

Task: T000 <!-- task id from docs/plan/tasks.yaml, or "none" for changes outside the plan -->

## Checklist

- [ ] The pull request title follows Conventional Commits and ends with the task id, for example `feat(agent): add metric engine (T205)`.
- [ ] I read the sections listed in the task's `spec` field and the code follows them.
- [ ] Every command in the task's `verify` list passes.
- [ ] The global checks from `docs/plan/PLAN.md` S14 pass (`dotnet format --verify-no-changes`, `dotnet build -warnaserror`, `dotnet test`, `ruff`, `mypy`, `pytest`, `python scripts/plan/validate.py`).
- [ ] All `acceptance` items of the task hold.
- [ ] The task's `status` is set to `done` in `docs/plan/tasks.yaml` (or `blocked` with a `blocked_reason`).
- [ ] No decision (`D-nnn`) or wire format changed silently; if it did, `PLAN.md` is updated in this pull request.
- [ ] No secrets, no network calls to external hosts, no variable values spliced into script text.
- [ ] Documentation is updated where behavior changed.

## Human checklist

<!-- Only for tasks with `actor: human` or `human_verify: true`: list what the owner still has to do or check. -->

- [ ] Not applicable
