# Publishing to winget

Task T607 (PLAN.md S12, S19, requirements R-08 and R-11). Every stable release is offered through the Windows Package Manager:

```powershell
winget install toeck98.QoffeeAutomat
```

The manifest lives in the community repository [microsoft/winget-pkgs](https://github.com/microsoft/winget-pkgs), not in this
repository. This repository holds the **templates** of the manifest, the script that renders them for the first submission, and the
workflow that submits every later version.

| What | Where |
|------|-------|
| Package identifier | `toeck98.QoffeeAutomat` (publisher `toeck98`, as in the installer's `AppPublisher`) |
| Manifest templates (version, installer, default locale) | `packaging/windows/winget/*.yaml.in` |
| Renders the templates for a published release | `packaging/windows/winget/New-WingetManifest.ps1` |
| Submits a new version after each stable release | `.github/workflows/winget.yml` |
| Starts that workflow | the job `winget` at the end of `.github/workflows/release.yml` |
| Needed secret | `WINGET_TOKEN` (repository secret), see below |

## How a version reaches winget

```text
release.yml  publish   -> the release is published, immutable, with the installers and SHA256SUMS.txt
release.yml  winget    -> gh workflow run winget.yml --ref vX.Y.Z -f tag=vX.Y.Z   (stable releases only)
winget.yml   submit    -> check tag and release, check that the package exists upstream,
                          verify checksum and attestation of both installers,
                          wingetcreate update toeck98.QoffeeAutomat --urls <x64> <arm64> --version X.Y.Z --submit
microsoft/winget-pkgs  -> automated validation, moderators, merge; then winget install offers X.Y.Z
```

Why a dispatch and not `on: release`: a release that `release.yml` publishes with the default `GITHUB_TOKEN` does not start workflows
that listen for `release: published` (GitHub does not let `GITHUB_TOKEN` events trigger workflows, except `workflow_dispatch`). The
workflow file of the **tag** is used, so the submission runs the code that was released.

`winget.yml` uses no third-party action (nothing to pin by commit SHA): it downloads `wingetcreate.exe` from Microsoft
(`https://aka.ms/wingetcreate/latest`) and refuses to run it unless its Authenticode signature is valid and issued to Microsoft
Corporation. Everything else is the `gh` CLI that the runner has.

Without the secret `WINGET_TOKEN` the job started by `release.yml` only prints a warning (the release stays green). Started by hand it
fails, so you notice.

## One-time setup (owner)

Nothing here can be done by an agent: it needs your GitHub account.

1. **Fork `microsoft/winget-pkgs`** with the account that will own the token (Fork on GitHub, keep the name). `wingetcreate` pushes a
   branch to that fork and opens the pull request from it.
2. **Create a personal access token.** Settings, Developer settings, Personal access tokens, *Tokens (classic)*: scope
   **`public_repo`**, an expiry (one year), a note such as `QoffeeAutomat winget`. A classic token is the choice because the pull request
   goes to a repository you do not own; check the `wingetcreate` documentation if you want to try a fine-grained token.
3. **Store it** as the repository secret `WINGET_TOKEN` (Settings, Secrets and variables, Actions, New repository secret).
4. Note the expiry date in your calendar. An expired token makes `winget.yml` fail with an authentication error; create a new one and
   replace the secret.

> **The token is powerful.** A classic token with `public_repo` can write to **every public repository of its account**, not only
> to the fork. Prefer a separate GitHub account that owns nothing but the fork of `winget-pkgs` and use its token. If you use your own
> account, treat the secret as sensitive: a repository secret can be read by any workflow that someone with write access to this
> repository can run, so keep the `main protection` ruleset (pull requests and reviews required) and do not add collaborators lightly.
> `winget.yml` runs no code from pull requests and hands the token only to `wingetcreate`. Revoke the token when you stop
> publishing.

## First submission (once, after 1.0.0 is published)

`wingetcreate update` can only create a new version of a package that already exists upstream. The first version is submitted from
the templates. Run this on a Windows machine with PowerShell 7, the GitHub CLI and `winget` (App Installer):

```powershell
# 1. The checksums of the release that is published.
gh release download v1.0.0 --repo toeck98/QoffeeAutomat --pattern SHA256SUMS.txt --dir artifacts/winget

# 2. Render the three manifest files (version, installer, locale) into artifacts/winget/1.0.0.
pwsh packaging/windows/winget/New-WingetManifest.ps1 -Version 1.0.0 -ChecksumsFile artifacts/winget/SHA256SUMS.txt

# 3. Validate. Fix the template, not the output, if this complains.
winget validate --manifest artifacts/winget/1.0.0

# 4. Install from the local manifest on a clean VM or a machine that may run the installer (this really installs 1.0.0).
winget settings --enable LocalManifestFiles
winget install --manifest artifacts/winget/1.0.0
winget uninstall toeck98.QoffeeAutomat      # keeps %ProgramData%\QoffeeAutomat

# 5. Submit (opens the pull request in microsoft/winget-pkgs from your fork).
wingetcreate submit --token <your WINGET_TOKEN> artifacts/winget/1.0.0
```

Alternatively open the pull request yourself: copy the three files to `manifests/t/toeck98/QoffeeAutomat/1.0.0/` in your fork and
use the pull request template of `winget-pkgs`.

Then watch the pull request:

* The automated validation installs the package on Windows images (silent), scans the installer and checks the manifest. Fix what it
  reports by changing the template here (in a pull request of this repository) and pushing the corrected files to the same branch.
* **The installer is unsigned until SignPath signing is set up** (T603, O-02). Microsoft Defender SmartScreen may flag an unsigned
  installer during validation; moderators decide. A signed installer makes this much easier. If validation reports a detection, do not
  argue with the scanner: submit the file to Microsoft for analysis (<https://www.microsoft.com/wdsi/filesubmission>) and answer in
  the pull request.
* Reviews take from hours to a few days. After the merge, `winget search toeck98.QoffeeAutomat` finds the package.

Check the acceptance of T607 on a clean machine:

```powershell
winget install --id toeck98.QoffeeAutomat --exact
winget list --id toeck98.QoffeeAutomat      # version 1.0.0
```

A `winget install --silent` runs the installer with `/VERYSILENT`, which skips the page that shows where the setup token is. The
user then reads `%ProgramData%\QoffeeAutomat\secrets\setup-token.txt` as administrator, as described in `docs/user/first-run.md`.

## Every later release (automatic)

Nothing to do for a stable release: `release.yml` starts `winget.yml`, which opens a pull request for the new version. Check the pull
request in `winget-pkgs` once; the validation is the same as above.

Re-run it for a tag that is already published (after fixing the token, or when `wingetcreate` failed):

```bash
gh workflow run winget.yml --ref main -f tag=v1.0.1      # --ref main uses the current workflow file; --ref v1.0.1 the tagged one
```

Prereleases (`-rc`) are never submitted: the workflow rejects any tag that is not `vMAJOR.MINOR.PATCH`.

## Troubleshooting

| Message in the run | Meaning and remedy |
|--------------------|--------------------|
| `The repository secret WINGET_TOKEN does not exist` | Create the secret (above). Started from `release.yml` this is only a warning. |
| `There is no published release for vX.Y.Z` / `is a draft or a prerelease` | The release is not published yet, or the tag is wrong. Wait for `release.yml` to finish. |
| `The release has no asset ...` | The release is incomplete. Releases are immutable; fix forward with the next patch version. |
| `The package ... was not found in microsoft/winget-pkgs` | The first submission has not been merged yet (section above), or the lookup failed; run again later. |
| `has SHA-256 ..., SHA256SUMS.txt says ...` | Never submit. Something is wrong with the release assets; investigate before anything else (compare with the attestation). |
| `has no valid build provenance attestation` | The asset was not built by `release.yml`, or the attestation service is unreachable (re-run). |
| `wingetcreate.exe is not validly signed by Microsoft Corporation` | Do not use that file. The download was changed or the URL moved; check `https://github.com/microsoft/winget-create` for the current download. |
| `wingetcreate failed with exit code ...` | Read the log above the message. Typical: bad or expired token (401), no fork of `winget-pkgs` under the token's account, a pull request for this version already exists, or the installer URLs are not reachable. |
| The pull request fails validation | Read the checks of the pull request in `winget-pkgs`; common causes are an installer that does not install silently, a failing Defender scan (unsigned), a wrong `ProductCode`, or a schema version that the repository no longer accepts. |

## Facts to re-check before the first submission

The author of these files had no network access when writing them, so these points come from memory of the tools and are **not**
verified against their current documentation:

* `ManifestVersion: 1.10.0` in the templates. `wingetcreate update` writes the schema version it knows for every later version, but
  the first manifest comes from the templates. Look at <https://github.com/microsoft/winget-cli/tree/master/schemas/JSON/manifests>
  and raise the version in the three templates (the `$schema` comments, too) if `winget validate` asks for it.
* The command line of `wingetcreate` (`update <id> --urls ... --version ... --token ... --submit`, `submit --token <dir>`) and the
  download URL `https://aka.ms/wingetcreate/latest`. See `wingetcreate update --help` and the
  [wingetcreate README](https://github.com/microsoft/winget-create).
* The token type (classic with `public_repo`) and the layout `manifests/t/toeck98/QoffeeAutomat/<version>` of the community repository.
* The rules of the winget community repository for names and the installer, in its
  [submission documentation](https://learn.microsoft.com/windows/package-manager/package/).

If a command line changed, adapt `winget.yml` and this page in the same pull request.

## Owner checklist

- [ ] Fork of `microsoft/winget-pkgs` exists under the account of the token.
- [ ] `WINGET_TOKEN` is stored as repository secret; expiry noted.
- [ ] The facts of the previous section are checked and the templates are current.
- [ ] 1.0.0 is published (checklist: [release-1.0-checklist.md](release-1.0-checklist.md)); first manifest rendered, validated,
      installed locally, submitted.
- [ ] The pull request in `winget-pkgs` is merged; `winget install toeck98.QoffeeAutomat` installs 1.0.0 on a clean machine.
- [ ] After the next stable release the workflow `winget` ran and opened its pull request without manual work.
- [ ] `status: done` for T607 in `docs/plan/tasks.yaml`.
