# Releasing

Tasks T602 and T603 (PLAN.md S12, S13, D-002, D-020). One version for the whole repository: the agent, the installer and
the Home Assistant integration are released together.

## How a release works

```text
Conventional Commits on main
        |
        v
release-please.yml  -- maintains the release pull request ("chore: release X.Y.Z"):
        |                CHANGELOG.md, <Version> in Directory.Build.props, version in manifest.json
        | (you merge the pull request)
        v
tag vX.Y.Z + DRAFT release (release-please-config.json: draft, force-tag-creation)
        |
        v
release.yml (called by release-please.yml)
  prepare      checks the tag, the versions and the draft
  windows      x64 and arm64: publish, sign (optional), portable zip, installer      [environment: release]
  integration  qoffeeautomat.zip (contents of custom_components/qoffeeautomat at the archive root, as HACS expects)
  publish      SBOM, SHA256SUMS.txt, attestations, upload to the draft, checks, PUBLISH (last step)
  winget       stable releases only: starts winget.yml, which submits the version to winget-pkgs (winget.md)
```

Releases are immutable (PLAN.md S13): after publication nothing can be attached or changed, and the tag cannot be moved.
Everything is therefore attached to the draft, and publishing is the last step of `release.yml`. A run that fails
earlier leaves an unpublished draft.

### Assets

| Asset | Content |
|-------|---------|
| `QoffeeAutomat-Setup-<version>-x64.exe`, `...-arm64.exe` | Inno Setup installers (Authenticode signed when signing is configured). |
| `QoffeeAutomat-<version>-win-x64.zip`, `...-win-arm64.zip` | Portable distribution. |
| `qoffeeautomat.zip` | The integration for HACS (`zip_release`, D-003). |
| `SHA256SUMS.txt` | SHA-256 of every other asset, readable by `sha256sum -c`. |
| `sbom.spdx.json` | SPDX SBOM. |

Every asset, `SHA256SUMS.txt` and the SBOM included, has a GitHub build provenance attestation. Users verify them as
described in [verify-download.md](../user/verify-download.md).

### Version rules

* The tag `vX.Y.Z` equals `<Version>` in `Directory.Build.props`, `version` in `manifest.json` and the file version of
  the executables. For stable releases `prepare` fails when the files and the tag differ.
* Before 1.0, breaking changes bump the minor version (`bump-minor-pre-major`). `1.0.0` is the end of milestone M6; use
  a commit with the footer `Release-As: 1.0.0` to force it.
* `release-please-config.json` uses the strategy `simple`, so release-please also keeps a `version.txt`. Do not delete it.

## One-time setup

1. Run `scripts/github/setup-repo.sh` (see [github-setup.md](github-setup.md)): immutable releases, the `release`
   environment (required reviewer) and the ruleset for `v*` tags.
2. Optional but recommended: create a fine-grained personal access token or a GitHub App token with `contents` and
   `pull requests` write access to this repository and store it as the repository secret `RELEASE_PLEASE_TOKEN`. Pull
   requests that are created with the default `GITHUB_TOKEN` do not start the CI workflows, so the release pull request
   would have no checks. Without the secret the workflow still works with the default token.
3. Pin the actions that are still marked `TODO(pin)` in `release.yml` and `release-please.yml` (see
   [Pinning](#pinning-the-actions)). The workflows cannot run before that.
4. Code signing is optional, see [Code signing](#code-signing-signpath).

### Pinning the actions

Every action is pinned by the full 40 character commit SHA with the version in a comment (AGENTS.md). The actions that were
added with T602 and T603 carry the placeholder `0000000000000000000000000000000000000000` until a maintainer has looked up
the current release:

```bash
grep -rn "TODO(pin)" .github/workflows
gh api repos/<owner>/<action>/releases/latest --jq .tag_name
gh api repos/<owner>/<action>/git/ref/tags/<tag> --jq .object        # a tag object: follow .object.url once more
```

The actions are `googleapis/release-please-action`, `actions/download-artifact`, `actions/attest` and
`SignPath/github-action-submit-signing-request`. Dependabot keeps them up to date afterwards.

## Normal release

1. Merge pull requests with Conventional Commit titles into `main`. Types `feat` and `fix` (and `perf`, `revert`, `docs`,
   `refactor`) appear in the changelog; `build`, `ci`, `test` and `chore` do not.
2. Open the release pull request that release-please maintains. Check the version and `CHANGELOG.md`, let CI run, merge it
   (squash).
3. `release-please.yml` creates the tag and the draft release and calls `release.yml`. Approve the deployment to the
   environment `release` when GitHub asks (once per run; both architectures wait for the same approval).
4. Wait for the run. The release is published automatically when everything is attached and verified.
5. Check the published release: the assets of the table above are there, and the quick check from
   [verify-download.md](../user/verify-download.md) succeeds for the installer.
6. HACS picks up the new release as the integration version. Check an installation or update through HACS.

## Prerelease or test release

Use it to test the pipeline or to publish a release candidate. Prereleases never become "latest".

1. Open Actions, `release`, "Run workflow". Select the branch `main` (the environment `release` only deploys from the
   default branch and `v*` tags) and enter a tag with a prerelease suffix, for example `v0.2.0-rc.1`.
2. The workflow creates a draft prerelease for the selected commit and runs like a normal release. There is no pull
   request: the version is stamped into the executables, the installer and the `manifest.json` inside `qoffeeautomat.zip`;
   the files in the repository keep the last release version.
3. Publishing creates the tag. A published prerelease cannot be removed or reused (immutable), so use a new suffix
   (`-rc.2`) for the next try.

## When a run fails

The release is still a draft. Read the log and find the cause.

* The cause is outside the code (runner, network, SignPath, attestation service): re-run the failed jobs of the run.
  Assets of the failed attempt are replaced. If the run is gone, start `release` by hand with "Use workflow from: Tag
  vX.Y.Z" and the same tag; for an existing draft the workflow reuses it.
* The cause is in the code: the tag of a stable draft already exists (release-please creates it with the draft) and
  tags `v*` cannot be moved or deleted. Delete the draft, fix `main` and release the next patch version. For a prerelease
  draft created by hand there is no tag yet: delete the draft and start again.

A failed `attest` or `Verify the attestations` step usually means that Sigstore or the attestation service was
unreachable; re-run the job.

## Code signing (SignPath)

Decision D-020 and open item O-02: the project applies to the
[SignPath Foundation](https://signpath.org/) for free signing of open-source projects. Until the application is accepted
and the secrets exist, releases are unsigned. Nothing else changes: checksums, SBOM and attestations are produced anyway.

The signing steps of `release.yml` run only when both of these exist in the environment `release`:

| Kind | Name | Meaning |
|------|------|---------|
| secret | `SIGNPATH_API_TOKEN` | API token of the SignPath user that submits signing requests. |
| variable | `SIGNPATH_ORGANIZATION_ID` | SignPath organization id. |
| variable (optional) | `SIGNPATH_PROJECT_SLUG` | Default `QoffeeAutomat`. |
| variable (optional) | `SIGNPATH_SIGNING_POLICY` | Default `release-signing`. Use `test-signing` for a dry run (the certificate is not trusted, so the signature check only reports). |
| variable (optional) | `SIGNPATH_CONFIG_EXECUTABLES` | Artifact configuration for the executables, default `executables`. |
| variable (optional) | `SIGNPATH_CONFIG_INSTALLER` | Artifact configuration for the installer, default `installer`. |

Owner checklist (the repository owner does this, an agent cannot):

- [ ] Submit the application at SignPath Foundation (project page, license, build on GitHub-hosted runners, code signing
      policy and privacy statement in the repository documentation as SignPath requires).
- [ ] After acceptance, create the SignPath project and connect it to the repository (trusted build system GitHub.com).
- [ ] Create two artifact configurations. The zip files that `release.yml` uploads contain the files at their root:
      `executables` signs `QoffeeAutomat.Agent.exe` and `QoffeeAutomat.SessionHost.exe`, `installer` signs
      `QoffeeAutomat-Setup-*.exe`. Both are PE files, so use one `pe-file` element each inside a `zip-file` root.
- [ ] Create the policies `release-signing` (with manual approval by the owner) and, for tests, `test-signing`.
- [ ] Store the token and the variables in the environment `release` (Settings, Environments).
- [ ] Run a prerelease (`v0.x.y-rc.1`) and check the signature of the installer and both executables:
      `Get-AuthenticodeSignature` reports `Valid`.

What is signed: the two executables (before they go into the zip and the installer) and the installer. The signing
request waits for the approval in SignPath, which can take a while; the job timeout is 60 minutes. The embedded
uninstaller (`unins000.exe`) is generated by Inno Setup and stays unsigned; so do third-party DLLs next to the
executables.

If the application is declined, keep the unsigned path (nothing to change) and keep the SmartScreen notice in
[verify-download.md](../user/verify-download.md).

## Known limits

* The SBOM is the SPDX document of the GitHub dependency graph of the repository (`dependency-graph/sbom`), not one per
  binary. It requires the dependency graph to be enabled (it is by default for public repositories).
* Publishing with the default `GITHUB_TOKEN` does not start workflows that listen for `release: published`. That is why the
  last job of `release.yml` starts `winget.yml` for stable releases with `workflow_dispatch` (T607, see
  [winget.md](winget.md)); the `release` job of `release-please.yml` therefore grants `actions: write`.
* The workflow attests in the final job, so the attestation names `release.yml` as the build workflow; the Windows
  builds are done by the `windows` jobs of the same run.

## HACS

* Until 1.0 the integration is installed as a HACS custom repository; how to verify that against a real Home Assistant
  instance, and the checklist and pull request text for the HACS default store (open item O-04), are in
  [hacs-default-submission.md](hacs-default-submission.md).
* The default store needs a release that was created **after** a `ci-integration` run without ignored HACS checks. Remove
  the `ignore:` line of the HACS action (it exists until `scripts/github/setup-repo.sh` has set the description and the
  topics of the repository) before the release that you want to submit.
