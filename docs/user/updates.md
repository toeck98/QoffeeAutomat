# Updates

The agent tells Home Assistant when a newer release exists, and Home Assistant can install it on the PC. This page says what the
agent contacts, what it checks before it installs, and how to switch the check off.

## What you see in Home Assistant

The device of the PC gets an **Agent** entity of the type *update* (the *update* platform). It shows the installed version, the
newest release, a one-line summary and a link to the release page. **Release notes** in its dialog shows the full notes of the
release. **Install** installs the newest release on the PC.

* The entity exists while the agent announces the feature `update`. That is the case for the Windows agent; the Linux agent
  and older agents do not have it.
* The state is **unknown** while the agent does not know the newest release: the check is switched off, has not run yet (it
  runs two minutes after the agent started) or failed. The entity never claims *up to date* without having checked.
* While **Install** runs, Home Assistant shows the update as in progress. The agent downloads the installer before it answers,
  which can take a few minutes. The entity then becomes unavailable while the agent restarts, and shows the new version when it
  is connected again.
* The four metrics behind it, `agent.version`, `agent.latest_version`, `agent.release_url` and `agent.release_notes`, are also
  sensors of the device. They are **disabled** by default; the update entity follows them by itself.

An automation can start the same installation:

```yaml
actions:
  - action: update.install
    target:
      entity_id: update.gaming_pc_agent
```

## The update check

Once a day the agent asks the GitHub releases page of the project for the newest release:

```
GET https://api.github.com/repos/<owner>/<project>/releases/latest
```

* It is **the only request the agent makes to the internet on its own**. It sends no cookie, no token and no identifier of the
  PC; the request carries `User-Agent: QoffeeAutomat/<version>`, as GitHub requires. GitHub sees the public address of your
  network and the agent version, like for any download.
* The first check runs two minutes after the agent started, then every 24 hours. A failed check (no network) is logged and tried
  again at the next interval.
* Drafts, pre-releases and tags that are not a version number are never offered.
* Nothing is downloaded by the check. It only reads the version, the release page and the release notes.

### Switch the check off

In the web interface open **Settings** and switch off **Check for a new release once a day**. Or set it in the settings document
(`settings.json`, also available through the API `PUT /api/v1/settings`):

```json
{
  "updates": { "check": false, "intervalHours": 24 }
}
```

| Member | Meaning |
|--------|---------|
| `check` | `false` switches the check off. Absent or `true`: on. |
| `intervalHours` | Hours between two checks, 1 to 720. Absent: 24. |

With the check off the agent makes **no request** and reports no newest version, so the update entity stays *unknown*. The
setting takes effect within a few minutes without a restart. **Install** still works: the action asks GitHub for the release
itself when someone runs it, because a person asked.

## What happens on Install

The action `agent.update` (the command `agent_update`) runs as the system, because the installer needs administrator rights. It
takes no parameters, so nothing that Home Assistant sends can change what runs. In this order:

1. It asks GitHub for the newest release and stops with *already at the newest release* when the agent is not older.
2. It downloads `SHA256SUMS.txt` and the installer of this PC's architecture (`QoffeeAutomat-Setup-<version>-x64.exe` or
   `-arm64.exe`) from `github.com` or `githubusercontent.com` over HTTPS (also after redirects), with a size limit and a time
   limit, into a new folder below `%ProgramData%\QoffeeAutomat\updates` that only the system and administrators can write to.
3. It compares the **SHA-256 checksum** of the installer with its line in `SHA256SUMS.txt`. A different checksum, a missing line
   or a missing checksum file **refuses the installer**: it is deleted and nothing is started.
4. When the **running** agent is signed, the installer must carry a trusted **Authenticode signature** too, otherwise it is
   refused and deleted. An unsigned build (a development build, a portable archive) compares the checksum only.
5. It tells every connected client `bye` with the reason `update`.
6. It starts the installer silently (`/VERYSILENT /SUPPRESSMSGBOXES /NORESTART`, log in `%ProgramData%\QoffeeAutomat\logs\update-installer.log`)
   and does not wait for it. The installer stops the service, replaces the files and starts it again; Home Assistant reconnects.

A refusal is shown in Home Assistant as an error with the reason, for example *The checksum of QoffeeAutomat-Setup-1.2.0-x64.exe
does not match SHA256SUMS.txt; the installer is refused and deleted.* The agent keeps running as it was.

!!! warning "What the checksum does and does not protect"
    `SHA256SUMS.txt` comes from the same release as the installer. It detects a damaged or swapped download, but it cannot detect
    a release that was manipulated as a whole. The signature can, and it is checked while the running build is signed. Releases
    are immutable and built by a public workflow; see [Verify your download](verify-download.md) to check one by hand. If you do
    not want a PC to install anything from the internet, switch the check off and update by hand.

## Updating by hand

Download the installer from the release page, [verify it](verify-download.md) and run it. The agent keeps its configuration. The
integration in Home Assistant is updated through HACS; update the agent and the integration to the same version.

## Troubleshooting

| Symptom | Cause and remedy |
|---------|------------------|
| The update entity is *unknown* | The check is off, the agent started less than two minutes ago, or the PC cannot reach `api.github.com`. See the agent log (`Update check ...`). |
| No update entity at all | The agent does not announce the feature `update` (not a Windows agent, or an agent older than this feature). |
| *The installer is refused* | The message names the check that failed. Do not install by hand until you know why; [verify the download](verify-download.md). |
| *The release has no installer for arm64* | The release has no installer for this PC's architecture. |
| The agent does not come back | See `%ProgramData%\QoffeeAutomat\logs\update-installer.log` and start the service from the Services app. |
