# Backup and restore

Everything you author (commands, sensors, scripts, metric overrides) is a plain file under `%ProgramData%\QoffeeAutomat`, so it
is easy to back up. The page **Backup** of the web interface does it for you and adds revision history and a support bundle.

## What is where

| Content | Location | In the export? |
|---------|----------|----------------|
| Commands | `config\commands\<id>.json` | yes |
| Custom sensors | `config\sensors\<id>.json` | yes |
| Metric overrides (switched off, interval, deadband) | `config\metrics.json` | yes |
| Settings (name, ports, remote UI, retention) | `config\settings.json` | only on request |
| PowerShell scripts | `scripts\` | yes |
| Earlier revisions of documents | `config\.history\` | no (use *Revisions*) |
| Password hash, link token hashes, certificate and its key, other secrets | `secrets\` | **never** |
| Run history, audit log, recent events | `data\` (SQLite) | no |
| Logs | `logs\` | no |

## Export

Page **Backup**, **Download the configuration**. You get one zip file with all commands, sensors, metric overrides and scripts.
It contains **no password, token or key**, so it is safe to keep in a normal backup or a repository. Do it before every larger
change, before an upgrade, and now and then.

The same file is available from the management API: `GET /api/v1/config/export` (see the [API reference](reference/api.md)).

## Import

Page **Backup**, *Import*, choose the zip file of an export (up to 32 MiB).

1. The agent **checks the whole archive** and shows what would change: for every document *new*, *changed* or *unchanged*, or
   the errors. Nothing is written yet.
2. If any document is invalid, **nothing** is applied; fix the archive and choose it again.
3. *Also import the settings* is off by default, because name and ports belong to one PC. Switch it on to move the settings, too.
4. **Apply the import.** Documents with the same id are **replaced**; others stay. A replaced document gets a new revision, so the
   previous version is still in the history.

Home Assistant follows at once: new commands and sensors appear, removed ones disappear.

## Go back to an earlier version

The agent keeps the **last ten revisions** of every command, sensor and the settings. On the page **Backup**, *Revisions*, choose
the kind and the id, look at the list (newest first) and **restore** one. A restore creates a **new** revision with the old
content, so nothing is lost and you can undo the undo.

The API does the same: `GET /api/v1/config/revisions/{kind}/{id}` and
`POST /api/v1/config/revisions/{kind}/{id}/{revision}/restore`.

Scripts are plain files; keep them in a repository or in your normal file backup. The export contains them.

## Back up the whole installation

For a backup that includes the **pairing and the password** (so that after a restore Home Assistant stays connected and nobody
has to set up again), copy the entire folder while the service is stopped:

```powershell
Stop-Service QoffeeAutomat
Copy-Item -Recurse -Force "$env:ProgramData\QoffeeAutomat" "D:\Backups\QoffeeAutomat-$(Get-Date -Format yyyyMMdd)"
Start-Service QoffeeAutomat
```

This copy **contains secrets**: treat it like a password store. Restore it only onto the same PC (same Windows installation; the
secrets are bound to the machine), with the service stopped, and keep the folder permissions of the original (`SYSTEM` and
Administrators only). Restoring a whole-disk backup of the PC does this for you.

## Reinstall and move to another PC

* **Reinstall or upgrade on the same PC:** nothing to restore. The installer and the uninstaller keep the data folder (unless you
  chose to delete it).
* **New PC or after deleting the data:** install the agent, set a password, **import** the export (with the settings if you want
  them), and **pair Home Assistant again**. A fresh agent has a new identity and a new certificate, so Home Assistant adds it
  as a new device. Remove the old device from Home Assistant afterwards.

## Diagnostics bundle

For a bug report, download the **diagnostics bundle** from the page *Backup*: a zip with `diagnostics.json` (versions, operating
system, uptime, numbers of commands, sensors, scripts and paired clients, configuration files that were skipped as invalid),
`settings.json` and the tail of `logs/agent.log`. Everything passes a scrubber that removes tokens, passwords and secrets;
look through it anyway before you attach it to a public issue.

## Checklist before big changes

1. Download an export.
2. Make the change.
3. Use *Test run* and the history to check it.
4. If it goes wrong, restore the previous revision of the document, or import the export.
