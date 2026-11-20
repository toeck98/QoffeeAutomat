# Windows packaging

Task T601 (PLAN.md S13, D-019). Everything here builds the Windows distribution of the agent and the SessionHost.

| File | Purpose |
|------|---------|
| `publish.ps1` | Publishes agent and SessionHost for `win-x64` or `win-arm64` with the publish profiles (self-contained, single file, ReadyToRun, version stamped, icon `assets/brand/app.ico`), stages them and writes the portable zip. |
| `build-installer.ps1` | Compiles `QoffeeAutomat.iss` with Inno Setup (6.3 or newer) from the staged files. |
| `QoffeeAutomat.iss` | The installer: Program Files, service, data directories and ACLs (through `service install`), firewall rule, Start menu shortcut, setup token page, in-place upgrade, uninstall with the option to keep the data. |
| `test-installer.ps1` | Install, health check, upgrade, uninstall test; CI runs it on `windows-latest` (job `installer` of `ci-dotnet.yml`). |
| `README-portable.txt` | Goes into the portable zip. |
| `winget/` | Templates of the winget manifest (`*.yaml.in`) and `New-WingetManifest.ps1`, which renders them for the first submission to `microsoft/winget-pkgs`; later versions are submitted by `.github/workflows/winget.yml` (T607, `docs/maintainers/winget.md`). |

The publish profiles are in `src/QoffeeAutomat.Agent/Properties/PublishProfiles/` and
`src/QoffeeAutomat.SessionHost/Properties/PublishProfiles/`.

## Build locally

Needs PowerShell 7, the .NET SDK of `global.json` and, for the installer, Inno Setup 6.3 or newer.

```powershell
pwsh packaging/windows/publish.ps1 -Arch x64           # artifacts/packaging/stage/win-x64 and the zip
pwsh packaging/windows/build-installer.ps1 -Arch x64    # artifacts/packaging/QoffeeAutomat-Setup-<version>-x64.exe
```

`-Version` overrides the version of `Directory.Build.props`. Output names follow PLAN.md S13:
`QoffeeAutomat-Setup-<version>-<arch>.exe` and `QoffeeAutomat-<version>-win-<arch>.zip`.

## Install

Interactive: run the installer and accept the elevation prompt. The last page shows where the setup token is
(`%ProgramData%\QoffeeAutomat\secrets\setup-token.txt`, readable by administrators only).

Silent (elevated):

```powershell
.\QoffeeAutomat-Setup-<version>-x64.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /LOG=setup.log
```

| Option | Effect |
|--------|--------|
| `/LINKPORT=<port>` | QoffeeLink port in the service command line and the firewall rule (default 8481). |
| `/UIPORT=<port>` | Loopback UI port (default 8480); also used by the Start menu shortcut. |
| `/HUBENROLL=<string>` | Enrollment string of the central hub (`qahub1.…`); the installer runs `qoffeeautomat hub join` once the service runs (T905). A failed join does not fail the installation. |

Without a port option an upgrade keeps the existing service and firewall rule untouched. An explicit port
re-registers the service and rewrites the rule. If you change the ports later in the settings, adjust the firewall rule
(`QoffeeAutomat QoffeeLink`) too.

The installer refuses a directory outside Program Files (`/DIR=...`): the service runs as LocalSystem and starts every
file in its directory, so standard users must not be able to change it.

## Upgrade

Run a newer installer over the old one. It stops the service and all SessionHosts, replaces the files (the `wwwroot`
folder is cleared first), starts the service again. Configuration, secrets, data and logs are not touched.

## Uninstall

"Apps and features", or silently:

```powershell
& "$env:ProgramFiles\QoffeeAutomat\unins000.exe" /VERYSILENT /SUPPRESSMSGBOXES /NORESTART
```

It removes the service, its Event Log source, the firewall rule, the shortcut and the files. The interactive uninstaller
asks whether to delete `%ProgramData%\QoffeeAutomat` (default: keep). Silently the data is kept unless you add
`/PURGEDATA`.

## Notes

* Native libraries (SQLite) and `wwwroot` stay next to the single-file executables on purpose: nothing is extracted to a
  temporary folder at start-up, which matters for a service that runs as LocalSystem.
* Local builds are not signed. Release builds are signed through SignPath when the credentials exist (D-020,
  `.github/workflows/release.yml`, `docs/maintainers/releasing.md`); an unsigned installer makes Windows SmartScreen
  warn (`docs/user/verify-download.md`).
* `QoffeeAutomat.SessionHost.exe` must stay next to `QoffeeAutomat.Agent.exe` (PLAN.md S09 item 7).
