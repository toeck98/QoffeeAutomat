# GitHub repository setup

This page is for the repository owner. It lists everything that cannot be stored as a
file in the repository and explains how to apply it with
[`scripts/github/setup-repo.sh`](../../scripts/github/setup-repo.sh) (PLAN.md S12, task
T009, open item O-01). The script is idempotent: run it as often as you like.

## Prerequisites

- The [GitHub CLI](https://cli.github.com/) (`gh`) 2.40 or newer, logged in as an
  administrator of the repository: `gh auth login`.
- A public repository named `toeck98/QoffeeAutomat` (rename the repository first; HACS
  needs a public repository).
- Bash: Linux, macOS, or Git Bash on Windows.

## Order of steps

1. **Dry run**, then apply without required checks. The branch ruleset cannot require a
   check that has never reported, so it is created without checks first:

   ```bash
   bash scripts/github/setup-repo.sh --dry-run
   bash scripts/github/setup-repo.sh
   ```

2. Open the pull request of milestone M0 and wait until the CI workflows have run once.
   Compare the names that GitHub shows (`gh pr checks <number>`) with the table below.
3. **Apply the required checks**. Copy this command as it is:

   ```bash
   bash scripts/github/setup-repo.sh \
     --required-check "dotnet ci passed" \
     --required-check "integration ci passed" \
     --required-check "meta ci passed" \
     --required-check "pr title"
   ```

   Add `--admin-bypass` to every run if repository administrators (you) should be able
   to merge in an emergency without the checks; without it the bypass is removed again.
   Later runs without `--required-check` keep the stored checks;
   `--clear-required-checks` removes them.
4. Confirm in the pull request that the script ran with the required checks (task T009
   acceptance), then merge.

## Check names

The status check that GitHub reports for a job is the job's `name`. The workflows are
built so that four always-running jobs summarize everything else; require exactly these:

| Required check | Workflow | Meaning |
|----------------|----------|---------|
| `dotnet ci passed` | `ci-dotnet.yml` | Green when the .NET build and tests passed on both operating systems, or were skipped because no .NET relevant file changed. Red when a job failed or was cancelled. |
| `integration ci passed` | `ci-integration.yml` | Same for ruff, mypy, pytest, hassfest and the HACS validation. |
| `meta ci passed` | `ci-meta.yml` | Plan validation, actionlint, workflow security lint (zizmor), shell script checks and the markdown link check all passed. |
| `pr title` | `pr-title.yml` | The pull request title is a Conventional Commit with an allowed scope. |

The summary jobs exist because a workflow that is skipped through a trigger path filter
never reports its checks, which would block every pull request that touches no matching
file. Instead the workflows always start and decide inside the `changes` job.

All job names that appear on a pull request (for reference; you can add any of them as an
additional `--required-check`):

| Workflow | Job names |
|----------|-----------|
| `ci-dotnet.yml` | `changes`, `build (ubuntu-latest)`, `build (windows-latest)`, `dotnet ci passed` |
| `ci-integration.yml` | `changes`, `python (ruff, mypy, pytest)`, `hassfest`, `hacs validation`, `integration ci passed` |
| `ci-meta.yml` | `plan validation`, `actionlint`, `workflow security lint`, `shell scripts`, `markdown links`, `meta ci passed` |
| `pr-title.yml` | `pr title` |
| `labeler.yml` | `label` |
| `codeql.yml` | `analyze (csharp)`, `analyze (python)`, `analyze (actions)` |
| `dependency-review.yml` | `dependency review` |

`changes` exists in two workflows and is therefore not usable as a required check.
`scorecard.yml` (`scorecard analysis`), `plan-sync.yml` (`sync plan tasks`) and
`copilot-setup-steps.yml` (`copilot-setup-steps`) do not run on pull requests.
Add `analyze (csharp)`, `analyze (python)`, `analyze (actions)` and `dependency review`
as required checks later if you want code scanning and dependency review to block merges.

## What the script does

| Step | Effect |
|------|--------|
| `metadata` | Repository description and topics (`home-assistant`, `hacs`, `integration`, `windows`, `dotnet`, ...). HACS validation needs both. |
| `merge` | Squash merge only, squash commit uses the pull request title and body, merged branches are deleted, auto-merge and "update branch" allowed, issues on, wiki off. |
| `security` | Secret scanning with push protection, Dependabot alerts and security updates, private vulnerability reporting (used by `SECURITY.md`). |
| `actions` | The workflow token is read-only by default and workflows may create pull requests (release-please needs it). |
| `labels` | Creates or updates the labels from `.github/labels.yml`. Other labels are left alone. |
| `milestones` | One milestone per plan milestone, titled `<id> <title>`, for example `M0 Foundation`. Existing milestones are left alone. |
| `discussions` | Enables Discussions and checks that the categories Q&A, Ideas and Show and tell exist. |
| `rulesets` | Ruleset `main protection` (pull request required, squash only, linear history, no force push, no deletion, review threads resolved, required checks) and ruleset `release tags` (tags `v*` cannot be moved or deleted). |
| `releases` | Enables immutable releases (assets must be attached while the release is a draft, see PLAN.md S13). |
| `pages` | Enables GitHub Pages with deployment from GitHub Actions. |
| `environments` | Environment `release` (required reviewer: you, deployments only from `v*` tags and the default branch) and environment `github-pages`. |

Use `--only STEP[,STEP]` or `--skip STEP[,STEP]` to run only some steps. The script has
a test with a fake `gh`: `bash tests/scripts/test_setup_repo.sh` (also run by `ci-meta.yml`).

## Settings that stay manual

| Setting | Where | Notes |
|---------|-------|-------|
| Discussion categories | Discussions, Categories | The API cannot create them. Q&A, Ideas and Show and tell (shared commands) are expected; the script warns when one is missing. |
| Project board | Your profile, Projects, New project | Create one project (v2). Then set the repository **variable** `PLAN_PROJECT_NUMBER` (Settings, Secrets and variables, Actions, Variables) to its number and the repository **secret** `PLAN_PROJECT_TOKEN` to a token that has the `project` scope. The workflow token cannot reach user-owned projects. Without both, `plan-sync.yml` still syncs the issues and skips the board. |
| Code scanning default setup | Settings, Advanced Security | Must stay **off**: `codeql.yml` is the advanced setup and GitHub does not allow both. |
| Dependency graph | Settings, Advanced Security | On by default for public repositories. While it is off, `dependency-review.yml` skips the review with a warning annotation; once on, the review runs strictly. |
| Social preview image | Settings, General | Optional; use `assets/brand/logo.png`. |
| Code signing | SignPath Foundation | Open item O-02: submit the application yourself. |
| HACS default store | HACS repository | Open item O-04: submit after 1.0. |
| Repository secrets for releases | Settings, Environments, `release` | Signing secrets are added in milestone M6. |

## After the first run of the workflows

- `plan-sync.yml` runs on every push to `main` that touches `docs/plan/tasks.yaml`.
  The first run can be started by hand (Actions, plan-sync, Run workflow; the default is
  a dry run that only prints the planned changes). Run it once without the dry-run
  option to create all issues.
- `hacs validation` ignores `brands`, `description`, `topics` and `license` on purpose
  (see the comment in `ci-integration.yml`): `brands` until the brand images are added to
  the integration (task T507), `description` and `topics` until the `metadata` step of
  `setup-repo.sh` has run, and `license` until `LICENSE` is on `main`. Remove the last
  three from `ignore` once the repository settings are applied and the license is merged.
