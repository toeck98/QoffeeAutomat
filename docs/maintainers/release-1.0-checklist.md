# Release 1.0 checklist

Task T606 (PLAN.md S13, S15, S18, S19). `1.0.0` ends milestone M6 and starts the compatibility promise of S13 (protocol v1 stays
backward compatible, config documents migrate forward). Work through the sections in order; tick a box only when you have seen
the result yourself. Items marked **(owner)** cannot be done by an agent; items marked **(Windows)** need a real Windows desktop
session (`human_verify`, open item O-06).

The security review itself is [../security/threat-model.md](../security/threat-model.md). The technical release procedure is
[releasing.md](releasing.md); signing is described there, too.

## 1. Preconditions in the repository

State of `docs/plan/tasks.yaml` when this checklist was written (2026-11-17). `scripts/plan/next_task.py --all` shows the current
state.

| Milestone | Tasks | State then | What is left |
|-----------|-------|------------|--------------|
| M0 Foundation | T001 to T011 | all `done` | nothing |
| M1 Protocol | T101 to T104 | all `done` | nothing |
| M2 Agent core | T201 to T217 | `done` | |
| M2 | **T218** performance budget tests | **`todo`** | T606 depends on it. The commit `62e2f56` mentions T218, but the task is not marked `done`. Run the benchmarks and the `verify` list of T218, then set `status: done`, or `blocked` with a reason. Do not release with an unclear state. |
| M3 Windows | T301 to T311 (there is no T308) | `done`, most `human_verify` | The owner's Windows check, section 4. |
| M4 UI | T401 to T408 | all `done` | nothing |
| M5 Integration | T501 to T508 | all `done` | nothing |
| M6 Release | **T601 to T605** | **`todo`** (the work is in the open pull request or in the working tree) | Merge each pull request, then set `status: done` in the same pull request after its `verify` list and the global checks passed. |
| M6 | T606 (this), T607 (winget) | `todo` | See sections 2 to 9; T607 follows after the release (section 10). |

- [ ] Every task of M0 to M6 except T606 and T607 is `done` in `docs/plan/tasks.yaml`, `python scripts/plan/validate.py` passes.
- [ ] No open pull request with the label `task` or `release` besides the release pull request.
- [ ] `main` is green: `ci-dotnet`, `ci-integration`, `ci-protocol`, `ci-meta`, `codeql`, `docs`, and the last nightly `e2e` run.
- [ ] No `TODO(pin)` and no placeholder SHA remains: `grep -rn "TODO(pin)\|0000000000000000000000000000000000000000" .github/workflows`
      returns nothing (T602 and T603 pinned their actions; T607 pins `winget.yml`, which uses no third-party action).
- [ ] The global checks of AGENTS.md pass on the release commit:

  ```bash
  dotnet format --verify-no-changes
  dotnet build -warnaserror
  dotnet test
  ruff check . && ruff format --check .
  mypy custom_components/qoffeeautomat
  pytest tests_integration
  python scripts/plan/validate.py
  mkdocs build --strict
  ```

## 2. Security review (T606)

- [ ] **Read [threat-model.md](../security/threat-model.md)** and the two fixes of this review. The author could not build or run
      anything, so first confirm that they compile and pass:
  - [ ] `AuthEndpointTests.A_burst_of_parallel_wrong_passwords_cannot_exceed_the_failure_limit` passes, and does so repeatedly
        (run `dotnet test --filter "FullyQualifiedName~A_burst_of_parallel"` in a loop of about 20 runs). If it is flaky on a
        slow runner, keep the gate in `AccountService` and relax the test, not the other way around.
  - [ ] A PowerShell run as `system` on Windows works with the new temp root (`%ProgramData%\QoffeeAutomat\data\tmp`) and leaves no
        `qa-ps-*` directory behind; `icacls "%ProgramData%\QoffeeAutomat\data\tmp"` shows only SYSTEM and Administrators.
- [ ] **File the issues** for the open findings F-03, F-04, F-06, F-07 (and F-09 if you want it): label `type:security` (or the
      closest label) and `area:security`, milestone M7. Suggested titles:
  - F-03 `Remove retention.auditLogDays or implement a real audit log`
  - F-04 `Support a user-supplied TLS certificate (or correct S09.3)`
  - F-06 `Warn in the command editor when a placeholder reaches a shell interpreter`
  - F-07 `Scrub secrets from GET /api/v1/logs`
  - F-09 `Reject unknown Host headers on the loopback listener`
- [ ] **No open finding of severity high**, in the register of the threat model and in the GitHub Security tab.
- [ ] **CodeQL** (`csharp`, `python`, `actions`): open Security, Code scanning; fix or dismiss with a reason every open alert. **(owner)**
- [ ] **Scorecard**: open the last `scorecard` run and Security, Code scanning; note the score, fix findings that are cheap (for
      example pinned dependencies, token permissions), record the rest. **(owner)**
- [ ] **Dependabot alerts and the dependency graph**: no open alert of severity high or critical for a shipped package. **(owner)**
- [ ] **Vulnerable packages**, run on the release commit:

  ```bash
  dotnet list package --vulnerable --include-transitive
  python -m pip install pip-audit
  pip-audit -r requirements-dev.txt -r requirements-docs.txt
  ```

- [ ] **Licences**: open `sbom.spdx.json` of the release candidate (section 7) and check that no shipped package is GPL, AGPL or
      SSPL; the NOTICE-worthy ones (Apache-2.0, MIT) need no action. The repository itself is MIT (`LICENSE`).
- [ ] **Secret scanning** has no open alert, and push protection is on (`scripts/github/setup-repo.sh`). **(owner)**
- [ ] **Private vulnerability reporting** is enabled and `SECURITY.md` is current (supported versions now say `1.x`).
- [ ] **Fresh-install permissions (Windows)**: the commands of section 6.2 of the threat model, on a clean Windows VM after
      installing the release candidate. Record the `icacls` output in the pull request.
- [ ] **Pipe and SessionHost (Windows)**: as a standard user, start a second `QoffeeAutomat.SessionHost.exe` from a copy outside
      Program Files and confirm in the agent log that the pipe rejects it (`PeerRejected`).
- [ ] **Setup token (Windows)**: after the first start only administrators can read `secrets\setup-token.txt`; after the password
      is set, the file is gone.

## 3. Brand assets (O-05)

Either replace the placeholders or confirm them. One of the two boxes must be ticked (acceptance of T606). **(owner)**

- [ ] **Replaced.** Final artwork is in `assets/brand/`, with the file names unchanged (table in `assets/brand/README.md`):
      `icon.svg`, `icon.png`, `icon@2x.png`, `logo.svg`, `logo.png`, `logo@2x.png`, `dark_logo.svg`, `dark_logo.png`,
      `dark_logo@2x.png`, `app.ico`. Then
  - [ ] copy `icon.svg`, `icon.png`, `icon@2x.png` and `app.ico` (as `favicon.ico`) into `src/QoffeeAutomat.Ui/wwwroot/`
        (`BrandAssetTests` in `tests/QoffeeAutomat.Ui.Tests` fails while a copy differs),
  - [ ] copy the PNGs into `custom_components/qoffeeautomat/brand/` (Home Assistant 2026.3 or newer reads them from there),
  - [ ] check the documentation site (`docs/assets/`, favicon and logo) and the README banner,
  - [ ] `app.ico` contains 16 to 256 px, so the installer, the executables and the tray show it,
  - [ ] update the placeholder notes in `assets/brand/README.md`, the README licence paragraph, and open item O-05 in PLAN.md S18.
- [ ] **Placeholder stays.** Write in the release pull request: "The placeholder artwork in assets/brand/ stays for 1.0" and keep
      the note in `README.md` that the artwork is a placeholder.

## 4. End-to-end and Windows verification

- [ ] **E2E is green** on the release pull request: label it `release`, so `e2e.yml` runs against Home Assistant `stable` and
      `beta`; both matrix jobs pass. Locally: `pytest tests/e2e` (needs Docker).
- [ ] **Windows `human_verify` tasks (O-06)** on a real desktop: T301 (service), T302 and T303 (session commands), T305 (session
      metrics), T306 (built-in actions), T309 (display profiles and modes), T310 (tray icon), T311 (elevated desktop commands),
      T216 (zeroconf from another device). The per-task checklists are in the pull requests of those tasks; copy the results
      into the release pull request.
- [ ] **Installer test** passed in CI (`installer` job of `ci-dotnet.yml`), and by hand on a clean VM: install, UI reachable at
      `http://127.0.0.1:8480/`, setup token page, firewall rule only on private and domain profiles
      (`(Get-NetFirewallRule -DisplayName "QoffeeAutomat QoffeeLink").Profile` prints `Domain, Private`), uninstall keeps the
      data.
- [ ] **Pairing with a real Home Assistant** (not only the container): pair, entities appear, a button runs a command, the
      fingerprint shown by the agent equals the one in the config flow.

## 5. Documentation

- [ ] `docs` workflow is green on `main` and **the site is published**: <https://toeck98.github.io/QoffeeAutomat/> loads, the
      reference pages (metrics, protocol, API) exist, `Verify a download` is reachable. Pages is configured with source
      "GitHub Actions" (`scripts/github/setup-repo.sh`). **(owner)**
- [ ] `README.md` is final: install steps, badges, link to the documentation, security note, licence, the artwork note (section 3).
- [ ] `CHANGELOG.md` is generated by release-please; the release pull request shows the notes you want.
- [ ] `docs/user/security.md` and [../security/threat-model.md](../security/threat-model.md) agree; the user page points to the
      threat model.
- [ ] `docs/maintainers/` is complete: [releasing.md](releasing.md), [github-setup.md](github-setup.md),
      [hacs-default-submission.md](hacs-default-submission.md), [winget.md](winget.md).

## 6. Upgrade from the last prerelease

There is no prerelease yet when this is written (`.release-please-manifest.json` says `0.1.0`). Create one first, and test the
upgrade from it. **(owner, Windows)**

- [ ] Start the `release` workflow by hand with a tag such as `v0.9.0-rc.1` (releasing.md, "Prerelease or test release"), approve
      the environment, wait for the draft to be published as a prerelease.
- [ ] On a clean Windows VM install `QoffeeAutomat-Setup-0.9.0-rc.1-x64.exe`. Set the password, create a command with a secret
      variable and a sensor, pair Home Assistant (a real one, or the e2e stack), run the command.
- [ ] Note the link token's client in the UI (page *Home Assistant*) and the `rev` shown in Home Assistant.
- [ ] Install the release candidate of 1.0 (section 7) **over it** with the installer (`/VERYSILENT` once, interactive once).
  - [ ] The service restarts by itself, the UI works, the password is unchanged, commands, sensors and scripts are there.
  - [ ] Home Assistant reconnects without pairing again; entities keep their ids; the run history still lists the old runs.
  - [ ] `icacls` of the data directories is unchanged; after the first PowerShell run as `system`, `data\tmp` exists and is
        protected.
  - [ ] The configuration documents were migrated or left alone (`schemaVersion`, `revision` in a command file).
- [ ] Update the integration through HACS (custom repository) from the prerelease to the release candidate (Part A of
      [hacs-default-submission.md](hacs-default-submission.md)); the integration keeps its config entry.
- [ ] Uninstall, then confirm `%ProgramData%\QoffeeAutomat` is kept unless `/PURGEDATA` was given.

## 7. The release

- [ ] **Brand decision** of section 3 is recorded in the release pull request.
- [ ] **Code signing (O-02, T603).** **(owner)** Either SignPath is set up (`SIGNPATH_API_TOKEN`, `SIGNPATH_ORGANIZATION_ID`, the
      artifact configurations and policies of releasing.md) and a prerelease was signed and `Get-AuthenticodeSignature` says
      `Valid`; or you accept an unsigned 1.0 with the SmartScreen notice (`docs/user/verify-download.md`) and write that in the
      pull request.
- [ ] **Environment `release`** has the required reviewer; **immutable releases** are on; the ruleset for `v*` tags exists
      (`scripts/github/setup-repo.sh`, release step in [github-setup.md](github-setup.md)).
- [ ] **`RELEASE_PLEASE_TOKEN`** exists (a fine-grained token with `contents` and `pull requests` write access), otherwise the
      release pull request has no checks (releasing.md, one-time setup).
- [ ] **Force the version `1.0.0`**: release-please computes `0.2.0` from the commits while the version is below 1.0. Push an
      empty commit to `main` through a pull request whose squash message has the footer `Release-As: 1.0.0`. The release pull
      request must then be titled `chore: release 1.0.0`, and change `Directory.Build.props`, `manifest.json` and `version.txt`.
- [ ] Review the release pull request: version, `CHANGELOG.md`, label it `release` so that `e2e` runs, wait for all checks.
- [ ] **Merge the release pull request** (squash). `release-please.yml` creates the tag `v1.0.0` and the draft, `release.yml` runs.
- [ ] Approve the deployment to the environment `release` when GitHub asks (once; both architectures wait for it).
- [ ] The release is **published** (the last step of `release.yml`) and has **all assets**:
      `QoffeeAutomat-Setup-1.0.0-x64.exe`, `...-arm64.exe`, `QoffeeAutomat-1.0.0-win-x64.zip`, `...-win-arm64.zip`,
      `qoffeeautomat.zip`, `SHA256SUMS.txt`, `sbom.spdx.json`.
- [ ] Verify the download as a user would ([verify-download.md](../user/verify-download.md)): `sha256sum -c SHA256SUMS.txt`,
      `gh attestation verify <installer> --repo toeck98/QoffeeAutomat`.
- [ ] Install the published 1.0.0 on the clean VM once more (smoke test: UI, setup, one command, Home Assistant pairing).
- [ ] HACS shows 1.0.0 for the custom repository and installs it.

## 8. After publishing

- [ ] Set `status: done` for **T606** in `docs/plan/tasks.yaml` (a small pull request, title
      `chore(docs): mark T606 done (T606)`), only when its three acceptance items hold: no open high finding, brand decision,
      release 1.0.0 published with all assets.
- [ ] Update PLAN.md S18: O-05 (artwork), O-02 (signing, if accepted), O-06 (Windows verification) get their final state.
- [ ] Close the milestone `M6 Release`; M7 stays open.
- [ ] Announce: GitHub Discussions (Show and tell / Announcements), Home Assistant community forum if you wish.

## 9. Rollback

Releases are immutable: a published release cannot be changed or removed, and the tag cannot be moved. If 1.0.0 turns out to be
broken, ship `1.0.1` with a fix as fast as the pipeline allows (conventional commit `fix:`), and mark 1.0.0 in its release notes as
superseded. The winget and HACS entries follow the newest release by themselves.

## 10. Hand-over to T607 (winget)

Do this after 1.0.0 is published; [winget.md](winget.md) has the details. **(owner)**

- [ ] Create the token and the repository secret `WINGET_TOKEN`.
- [ ] Submit the first manifest of `1.0.0` (the workflow can only update packages that exist upstream).
- [ ] After the first pull request in `microsoft/winget-pkgs` is merged, `winget install toeck98.QoffeeAutomat` installs 1.0.0
      on a clean machine, and the next stable release is submitted by `winget.yml` without manual work.
- [ ] Set `status: done` for **T607**.
