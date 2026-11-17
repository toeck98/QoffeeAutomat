# Submitting QoffeeAutomat to the HACS default store

Task T605, open item O-04 (PLAN.md S18): the integration is installed as a **HACS custom repository** until 1.0 and is submitted
to the **HACS default store after 1.0**. This page is the checklist and the ready-to-paste pull request text, so the owner
only has to open the pull request. Facts below were checked against the HACS documentation (*Publish, Include default
repositories*), the pull request template and the validators of `hacs/integration` on 2026-11-17; re-read the
[HACS publishing documentation](https://hacs.xyz/docs/publish/include) before you submit, because the rules change.

## Part A: verify the installation as a custom repository (T605 acceptance)

Do this once with a published release (a pre-release is enough) on a real Home Assistant instance. It cannot be automated.

- [ ] **Add the repository.** HACS, three dots, *Custom repositories*, `https://github.com/toeck98/QoffeeAutomat`, type
      *Integration*. (Or the My Home Assistant button in [Install the integration](../user/install-integration.md).) HACS accepts it
      without an error.
- [ ] **The zip release is found.** In the HACS entry *Download* offers the **release version** (not a branch or a commit) and the
      installed files are those of `qoffeeautomat.zip`: `config/custom_components/qoffeeautomat/manifest.json` exists and its
      `version` equals the release tag without the `v`.
- [ ] Restart Home Assistant; the integration appears under *Add integration* and the config flow starts.
- [ ] **The version is shown.** In HACS the installed version equals the release; *Settings, Devices & services, QoffeeAutomat*
      shows the same version on the device (`sw_version` comes from the agent, the integration version from `manifest.json`).
- [ ] **An update is detected.** Publish the next (pre-)release; HACS shows *Update available* for it after its next refresh (or
      the three dots, *Update information*). Download it, restart, and check the version again.
- [ ] Pair with a real agent ([Pairing](../user/pairing.md)) and confirm that entities appear.

Record the result in the pull request of T605 (version, date, Home Assistant and HACS versions).

## Part B: prerequisites for the submission

The HACS team and its automation check the following. Every item must be true **before** the pull request is opened; a pull
request that fails them is closed.

### Repository settings (owner, open item O-01)

- [ ] **Description and topics are set.** The check *repository* of the default store, and the HACS validation, need a
      description, **topics** and enabled issues. On 2026-11-17 the repository had **no description and no topics**
      (`ignore: description topics` in `ci-integration.yml` exists only because of that). Run
      `scripts/github/setup-repo.sh` (`--dry-run` first; see [github-setup.md](github-setup.md)). It sets the description and the
      topics `home-assistant, hacs, integration, windows, dotnet, powershell, automation`.
- [ ] Issues are enabled. The repository is public, not archived and not a fork.
- [ ] The license is recognised by GitHub as an OSI-approved one (MIT is; the API reports `mit`).

### Workflows (agent can prepare, owner confirms)

- [ ] **Remove the `ignore:` line** of the HACS action in `.github/workflows/ci-integration.yml` (currently
      `ignore: description topics`). The default store requires a run **without any ignored check**.
- [ ] `ci-integration` is green on `main` with that change: the job *hacs validation* and the job *hassfest* both pass.
- [ ] Note the URLs of those two runs (Actions, *ci-integration*, the run on `main`, the job). They go into the pull request.

### Release

- [ ] **A full GitHub release (not just a tag) exists that was created *after* the validation actions passed without ignores.**
      If 1.0.0 was published while the ignore was still in place, publish a following release (for example 1.0.1) first.
- [ ] The release contains `qoffeeautomat.zip` (the content of `custom_components/qoffeeautomat` at the archive root), and
      `hacs.json` has `"zip_release": true` and `"filename": "qoffeeautomat.zip"`.
- [ ] Part A passed.

### Content checks of the default store

| Check | State |
|-------|-------|
| Brands: `custom_components/qoffeeautomat/brand/icon.png` exists | Yes (kept equal to `assets/brand/` by `scripts/dev/sync_brand.py --check`). **Replace the placeholder artwork before 1.0** (open item O-05, T606). |
| Core domain conflict: `qoffeeautomat` is not a Home Assistant core domain | Yes (the name is unique). |
| `manifest.json` has `domain`, `documentation`, `issue_tracker`, `codeowners`, `name`, `version` | Yes. Consider pointing `documentation` at the published documentation site `https://toeck98.github.io/QoffeeAutomat/` once it is live (release-please does not touch this key). |
| `hacs.json` has at least `name` | Yes (`name`, `zip_release`, `filename`, `homeassistant`). |
| One integration in `custom_components/` | Yes (`qoffeeautomat`). |
| Not archived; at least one release | See above. |
| Submitter is the owner or a major contributor | Yes, if the owner of `toeck98/QoffeeAutomat` opens the pull request. **Not** from an organisation account (the PR must be editable). |
| `country` in `hacs.json` | Not needed: the integration is not country-specific. |
| Not a core override or a test build of a core integration | Correct. |

## Part C: open the pull request

1. Fork [`hacs/default`](https://github.com/hacs/default) to the personal account `toeck98` (**not** an organisation).
2. In the fork create a **new branch from `master`** (for example `add-qoffeeautomat`). Do not use `master` directly.
3. Edit the file **`integration`** in the root of the fork. It is a JSON array of `"owner/repo"` strings, sorted alphabetically
   without regard to case. Add the line for this repository at the right place, **not** at the end. On 2026-11-17 the neighbours
   were `"tobiasvoll/varta_pulse_neo",` and `"toggm/askoheat",`, so the line goes between them:

   ```diff
      "tobiasvoll/varta_pulse_neo",
   +  "toeck98/QoffeeAutomat",
      "toggm/askoheat",
   ```

   Check the neighbours again in the current file; the file is JSON, so keep the commas right (the `lint jq` and `lint sorted`
   checks of the repository verify it).
4. Commit with the message `Add toeck98/QoffeeAutomat`, push, and open the pull request against `hacs/default` `master`.
5. Use the title and the body below; **replace the three links** in the body. Fill the template exactly (an incorrect template is
   closed without notice).
6. **Do not request reviews and do not ping the maintainers.** The queue takes months; the pull request is drafted for minor issues
   (set it to *ready for review* when you have fixed them) and closed for major ones.

### Title

```text
Add toeck98/QoffeeAutomat
```

### Body

Paste this into the pull request description (it follows the current template of `hacs/default`; if the template differs when you
open the pull request, use the template that GitHub shows and carry over the answers).

```markdown
## Checklist

- [x] I've read the [publishing documentation](https://hacs.xyz/docs/publish/start).
- [x] I've added the [HACS action](https://hacs.xyz/docs/publish/action) to my repository.
- [x] (For integrations only) I've added the [hassfest action](https://developers.home-assistant.io/blog/2020/04/16/hassfest/) to my repository.
- [x] The actions are passing without any disabled checks in my repository.
- [x] I've added a link to the action run on my repository below in the links section.
- [x] I've created a new release of the repository after the validation actions were run successfully.

## Links

Link to current release: <https://github.com/toeck98/QoffeeAutomat/releases/tag/v1.0.0>
Link to successful HACS action (without the `ignore` key): <PASTE THE URL OF THE hacs validation JOB OF ci-integration ON main>
Link to successful hassfest action (if integration): <PASTE THE URL OF THE hassfest JOB OF ci-integration ON main>

## About the integration

QoffeeAutomat connects Home Assistant to a PC that runs the QoffeeAutomat agent (Windows first): sensors for the PC, buttons,
switches and other controls for commands the user defines in the agent, events, and an action to run commands with variables.
The integration talks to the agent over a local WebSocket (`local_push`, no cloud); it is installed from a release asset
(`zip_release` in `hacs.json`).

- Repository: https://github.com/toeck98/QoffeeAutomat (one integration, `custom_components/qoffeeautomat`, brand images in `brand/`)
- Documentation: https://toeck98.github.io/QoffeeAutomat/
- I am the owner of the repository.
- It has been installed and updated as a HACS custom repository on a real Home Assistant instance.

<!-- tid:73253df5-5376-4e68-8c16-b234da6a2de3 -->
```

!!! note "The line with `tid:`"
    The current template ends with an HTML comment that contains an id. Keep it exactly as the template shows it when you open the
    pull request; do not invent one. If GitHub pre-fills the body from the template, edit that text instead of pasting this block
    over it.

Replace the release link with the real tag and make sure the two action links point to runs **without** the `ignore` key.

## Part D: after the pull request

- [ ] Wait. Answer questions politely and fix findings on the same branch.
- [ ] When it is merged, HACS includes the repository in its next scheduled scan; check that *QoffeeAutomat* is found in the HACS
      store search without adding a custom repository.
- [ ] Update the documentation: the *Custom repository today* note in `docs/user/install-integration.md` and the quick start
      (the custom repository steps are no longer needed), the HACS badge in `README.md` (from *Custom* to the default-store
      badge or remove it), and O-04 in `docs/plan/PLAN.md` S18 (set the status to done).
- [ ] The My Home Assistant link of the documentation (`hacs_repository` redirect) keeps working for the default store.

## If the pull request is closed

Read the reason in the comments. Typical ones: the HACS action had ignored checks, the release was older than the passing run, the
submitter is not the owner, the template was not filled, the list is not sorted, or the description or topics are missing. Fix it
and open a new pull request; custom repository installation keeps working in the meantime.
